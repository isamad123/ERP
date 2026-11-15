using System.Text.RegularExpressions;

namespace Erp.CodeGen.Design;

/// <summary>
/// Reads docs/data-design/*.yaml into the <see cref="DesignModel"/>: expands traits (README "Traits"), maps types
/// (README "Types"), reads keys, indexes and notes, applies the reviewed translations of rules.yaml and resolves
/// foreign keys (README rules 2, 11 and 12). Anything it cannot read without guessing raises a DesignException.
/// </summary>
public static partial class DesignLoader
{
    public const string NilUuid = "'00000000-0000-0000-0000-000000000000'::uuid";

    private static readonly string[] KnownKeys =
        ["table", "purpose", "traits", "status", "columns", "unique", "indexes", "eff_key", "key", "checks", "notes", "partition", "reqs"];

    private static readonly string[] KnownTraits = ["std", "app", "co", "br", "soft", "eff", "cust", "name"];

    private sealed record PendingFk(Table Table, string Column, string Target);

    public static DesignModel Load(string designFolder, string codegenFolder)
    {
        var settings = Settings.Load(Path.Combine(codegenFolder, "codegen-settings.yaml"));
        var rules = RuleBook.Load(Path.Combine(codegenFolder, "rules.yaml"));
        var tables = new List<Table>();
        var pending = new List<PendingFk>();

        foreach (var file in Directory.GetFiles(designFolder, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            foreach (var entry in YamlNode.List(YamlNode.Load(file), fileName))
            {
                tables.Add(ReadTable(YamlNode.Map(entry, fileName), fileName, settings, rules, pending));
            }
        }

        foreach (var schema in settings.TenantSchemas)
        {
            tables.AddRange(OutboxAndInbox(schema, settings));
        }

        var duplicates = tables.GroupBy(t => t.FullName).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new DesignException(string.Join(", ", duplicates), "table defined more than once");
        }

        var model = new DesignModel { Tables = tables.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList(), Settings = settings };
        ResolveForeignKeys(model, pending, rules);
        AddForeignKeyIndexes(model);
        Validator.Validate(model, rules);
        return model;
    }

    private static Table ReadTable(IReadOnlyDictionary<string, object?> map, string file, Settings settings, RuleBook rules, List<PendingFk> pending)
    {
        var fullName = YamlNode.Text(map["table"], file + " table");
        var unknownKeys = map.Keys.Except(KnownKeys).ToList();
        if (unknownKeys.Count > 0)
        {
            throw new DesignException(fullName, "unknown entry keys: " + string.Join(", ", unknownKeys));
        }

        var dot = fullName.IndexOf('.', StringComparison.Ordinal);
        var schema = fullName[..dot];
        var module = settings.ModuleOf(schema);
        var traits = YamlNode.Texts(map.GetValueOrDefault("traits"), fullName);
        var unknownTraits = traits.Except(KnownTraits).ToList();
        if (unknownTraits.Count > 0)
        {
            throw new DesignException(fullName, "unknown traits: " + string.Join(", ", unknownTraits));
        }

        var table = new Table
        {
            Schema = schema,
            Name = fullName[(dot + 1)..],
            Database = module == "controlplane" ? DatabaseKind.ControlPlane : DatabaseKind.Tenant,
            Module = module,
            SourceFile = file,
            Purpose = YamlNode.OptionalText(map, "purpose", fullName),
            Traits = traits,
            Requirements = YamlNode.Texts(map.GetValueOrDefault("reqs"), fullName),
        };
        var tableRules = rules.For(fullName);

        AddColumns(table, map, tableRules, rules, pending);
        ReadPartition(table, map, tableRules);
        ReadPrimaryKey(table, map);
        ReadUniques(table, map, tableRules, rules);
        ReadIndexes(table, map);
        ReadEffectiveDating(table, map, tableRules, rules);
        ReadChecks(table, map, tableRules, rules);
        ReadNotes(table, map, tableRules, rules);
        return table;
    }

    // ---------------------------------------------------------------- columns

    private static void AddColumns(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules, RuleBook rules, List<PendingFk> pending)
    {
        var t = table.Traits;
        void Trait(string trait, string name, ColumnKind kind, bool required, string? defaultSql = null, string? fk = null)
        {
            table.Columns.Add(new Column { Name = name, Kind = kind, SqlType = SqlTypeOf(kind, null), Required = required, Default = defaultSql, Origin = trait });
            if (fk is not null)
            {
                pending.Add(new PendingFk(table, name, fk));
            }
        }

        var keyed = t.Contains("std") || t.Contains("app");
        if (keyed)
        {
            Trait(t.Contains("std") ? "std" : "app", "id", ColumnKind.Uuid, true);
        }

        if (t.Contains("co"))
        {
            Trait("co", "company_id", ColumnKind.Uuid, true, fk: "org.companies");
        }

        if (t.Contains("br"))
        {
            Trait("br", "branch_id", ColumnKind.Uuid, false, fk: "org.branches");
        }

        if (t.Contains("name"))
        {
            Trait("name", "code", ColumnKind.Code, true);
            Trait("name", "name_i18n", ColumnKind.I18n, true);
        }

        foreach (var raw in YamlNode.List(map.GetValueOrDefault("columns"), table.FullName))
        {
            table.Columns.Add(ReadColumn(table, YamlNode.List(raw, table.FullName), tableRules, rules, pending));
        }

        if (map.TryGetValue("status", out var status))
        {
            table.Columns.Add(new Column
            {
                Name = "status",
                Kind = ColumnKind.Enum,
                SqlType = "text",
                Required = true,
                EnumValues = YamlNode.Texts(status, table.FullName + " status"),
                Origin = "status",
            });
        }

        if (t.Contains("eff"))
        {
            Trait("eff", "valid_from", ColumnKind.Date, true);
            Trait("eff", "valid_to", ColumnKind.Date, false);
        }

        if (t.Contains("name"))
        {
            Trait("name", "is_active", ColumnKind.Bool, true, "true");
        }

        if (t.Contains("cust"))
        {
            Trait("cust", "custom", ColumnKind.Json, true, "'{}'::jsonb");
        }

        if (t.Contains("soft"))
        {
            Trait("soft", "is_deleted", ColumnKind.Bool, true, "false");
            Trait("soft", "deleted_at", ColumnKind.Timestamp, false);
            Trait("soft", "deleted_by", ColumnKind.Uuid, false);
        }

        if (keyed)
        {
            var trait = t.Contains("std") ? "std" : "app";
            Trait(trait, "created_at", ColumnKind.Timestamp, true);
            Trait(trait, "created_by", ColumnKind.Uuid, true);
        }

        if (t.Contains("std"))
        {
            Trait("std", "updated_at", ColumnKind.Timestamp, true);
            Trait("std", "updated_by", ColumnKind.Uuid, true);
            Trait("std", "row_version", ColumnKind.BigInteger, true, "1");
        }

        var duplicates = table.Columns.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new DesignException(table.FullName, "column defined twice (design and trait): " + string.Join(", ", duplicates));
        }

        AddColumnChecks(table, tableRules, rules);
    }

    private static Column ReadColumn(Table table, IReadOnlyList<object?> tuple, TableRules tableRules, RuleBook rules, List<PendingFk> pending)
    {
        if (tuple.Count is < 3 or > 4)
        {
            throw new DesignException(table.FullName, "column tuple must be [name, type, req|opt, notes]");
        }

        var name = YamlNode.Text(tuple[0], table.FullName);
        var where = table.FullName + "." + name;
        var rawType = YamlNode.Text(tuple[1], where);
        var requiredText = YamlNode.Text(tuple[2], where);
        if (requiredText is not ("req" or "opt"))
        {
            throw new DesignException(where, $"third element must be req or opt, not '{requiredText}'");
        }

        var note = tuple.Count == 4 ? YamlNode.Text(tuple[3], where) : null;
        var (kind, enumValues) = ParseType(rawType, where);
        var required = requiredText == "req";
        string? defaultSql = null;
        string? generated = null;

        if (note is not null)
        {
            foreach (Match fk in FkPattern().Matches(note))
            {
                pending.Add(new PendingFk(table, name, fk.Groups[1].Value));
            }

            var generatedMatch = GeneratedPattern().Match(note);
            if (generatedMatch.Success)
            {
                generated = generatedMatch.Groups[1].Value.Trim().Replace("nil uuid", NilUuid, StringComparison.Ordinal);
            }

            defaultSql = ParseDefault(note, kind, enumValues);
            var check = CheckPattern().Match(note);
            if (check.Success)
            {
                table.Checks.Add(new CheckDef(Naming.DbName("ck", table.Name, [name, "rule"]), $"{name} {check.Groups[1].Value} {check.Groups[2].Value.Trim()}"));
            }

            foreach (var stubRule in tableRules.ColumnStubs.Where(s => s.Column == name && s.Text == note))
            {
                rules.MarkUsed(table.FullName, stubRule.Text);
                var (stubName, owner) = stubRule.Action.Stub ?? throw new DesignException(where, "column_stubs entry needs a stub");
                table.Stubs.Add(new StubDef(table.FullName, stubName, owner, $"{name}: {note}", stubRule.Action.Implementation));
            }
        }

        if (kind is ColumnKind.Bool && required)
        {
            defaultSql ??= "false";
        }

        if (kind is ColumnKind.TextArray or ColumnKind.UuidArray && required)
        {
            defaultSql ??= "'{}'";
        }

        return new Column
        {
            Name = name,
            Kind = kind,
            SqlType = SqlTypeOf(kind, rawType),
            Required = required,
            EnumValues = enumValues,
            Default = defaultSql,
            GeneratedExpression = generated,
            Comment = note,
            Origin = "design",
        };
    }

    /// <summary>
    /// A leading "default X" in a note becomes a database default only when X is a number, true/false, or one of the
    /// column's enum values. Anything else ("default by grade", "default Asia/Karachi") stays descriptive.
    /// </summary>
    private static string? ParseDefault(string note, ColumnKind kind, IReadOnlyList<string> enumValues)
    {
        var match = DefaultPattern().Match(note);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim();
        if (NumberPattern().IsMatch(value) && kind is ColumnKind.Int4 or ColumnKind.BigInteger or ColumnKind.Money or ColumnKind.Qty or ColumnKind.Rate or ColumnKind.Pct or ColumnKind.Numeric)
        {
            return value;
        }

        if (value is "true" or "false" && kind is ColumnKind.Bool)
        {
            return value;
        }

        return kind is ColumnKind.Enum && enumValues.Contains(value) ? Naming.SqlString(value) : null;
    }

    private static void AddColumnChecks(Table table, TableRules tableRules, RuleBook rules)
    {
        foreach (var column in table.Columns)
        {
            switch (column.Kind)
            {
                case ColumnKind.Code:
                    table.Checks.Add(new CheckDef(Naming.DbName("ck", table.Name, [column.Name, "code"]),
                        $"length({column.Name}) between 1 and 40 and {column.Name} = btrim({column.Name})"));
                    break;
                case ColumnKind.Enum:
                    table.Checks.Add(new CheckDef(Naming.DbName("ck", table.Name, [column.Name]),
                        $"{column.Name} in ({string.Join(", ", column.EnumValues.Select(Naming.SqlString))})"));
                    break;
                case ColumnKind.I18n:
                    table.Checks.Add(new CheckDef(Naming.DbName("ck", table.Name, [column.Name, "object"]), $"jsonb_typeof({column.Name}) = 'object'"));
                    break;
                default:
                    break;
            }
        }

        if (table.IsEffectiveDated)
        {
            table.Checks.Add(new CheckDef(Naming.DbName("ck", table.Name, ["valid_to"]), "valid_to is null or valid_to > valid_from"));
        }

        foreach (var columnCheck in tableRules.ColumnChecks)
        {
            var column = table.Column(columnCheck.Column);
            if (column.Comment != columnCheck.Text)
            {
                continue;
            }

            rules.MarkUsed(table.FullName, columnCheck.Text);
            table.Checks.Add(columnCheck.Action.Check ?? throw new DesignException(table.FullName, "column_checks entry needs a check"));
        }
    }

    private static (ColumnKind Kind, IReadOnlyList<string> EnumValues) ParseType(string raw, string where)
    {
        var enumMatch = EnumPattern().Match(raw);
        if (enumMatch.Success)
        {
            var values = enumMatch.Groups[1].Value.Split(',').Select(v => v.Trim()).ToList();
            if (values.Count == 0 || values.Any(v => !EnumValuePattern().IsMatch(v)) || values.Distinct().Count() != values.Count)
            {
                throw new DesignException(where, $"enum values in '{raw}' must be distinct lower-case words");
            }

            return (ColumnKind.Enum, values);
        }

        ColumnKind kind = raw switch
        {
            "uuid" => ColumnKind.Uuid,
            "code" => ColumnKind.Code,
            "text" => ColumnKind.Text,
            "i18n" => ColumnKind.I18n,
            "date" => ColumnKind.Date,
            "ts" => ColumnKind.Timestamp,
            "int" => ColumnKind.Int4,
            "big" => ColumnKind.BigInteger,
            "bool" => ColumnKind.Bool,
            "money" => ColumnKind.Money,
            "qty" => ColumnKind.Qty,
            "rate" => ColumnKind.Rate,
            "pct" => ColumnKind.Pct,
            "json" => ColumnKind.Json,
            "enc" => ColumnKind.Encrypted,
            "hmac" => ColumnKind.Hmac,
            "text[]" => ColumnKind.TextArray,
            "uuid[]" => ColumnKind.UuidArray,
            "bytea" => ColumnKind.Bytea,
            "time" => ColumnKind.Time,
            "inet" => ColumnKind.Inet,
            _ when CharPattern().IsMatch(raw) => ColumnKind.FixedText,
            _ when NumericPattern().IsMatch(raw) => ColumnKind.Numeric,
            _ => throw new DesignException(where, $"unknown type '{raw}'"),
        };
        return (kind, []);
    }

    public static string SqlTypeOf(ColumnKind kind, string? raw) => kind switch
    {
        ColumnKind.Uuid => "uuid",
        ColumnKind.Code or ColumnKind.Text or ColumnKind.Enum => "text",
        ColumnKind.I18n or ColumnKind.Json => "jsonb",
        ColumnKind.Date => "date",
        ColumnKind.Timestamp => "timestamptz",
        ColumnKind.Int4 => "integer",
        ColumnKind.BigInteger => "bigint",
        ColumnKind.Bool => "boolean",
        ColumnKind.Money => "numeric(19,4)",
        ColumnKind.Qty => "numeric(19,6)",
        ColumnKind.Rate => "numeric(19,10)",
        ColumnKind.Pct => "numeric(9,6)",
        ColumnKind.Encrypted or ColumnKind.Hmac or ColumnKind.Bytea => "bytea",
        ColumnKind.TextArray => "text[]",
        ColumnKind.UuidArray => "uuid[]",
        ColumnKind.Time => "time",
        ColumnKind.Inet => "inet",
        ColumnKind.FixedText or ColumnKind.Numeric => raw ?? throw new InvalidOperationException("raw type needed"),
        _ => throw new InvalidOperationException(kind.ToString()),
    };

    // ---------------------------------------------------------------- keys, uniques, indexes

    private static void ReadPartition(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules)
    {
        var text = YamlNode.OptionalText(map, "partition", table.FullName);
        if (text is null)
        {
            return;
        }

        if (tableRules.NoPartition)
        {
            table.Comments.Add("Partitioning (design): " + text + ". Not partitioned in this release (rules.yaml).");
            return;
        }

        var match = PartitionPattern().Match(text);
        if (!match.Success)
        {
            throw new DesignException(table.FullName, $"cannot read partition '{text}'; expected 'range (column) monthly'");
        }

        var column = table.Column(match.Groups[1].Value);
        if (column.Kind is not (ColumnKind.Date or ColumnKind.Timestamp) || !column.Required)
        {
            throw new DesignException(table.FullName, "partition column must be a required date or timestamp");
        }

        table.Partition = new PartitionDef(column.Name, column.Kind);
        if (match.Groups[2].Success && match.Groups[2].Value.Replace(" ", string.Empty, StringComparison.Ordinal) != "id," + column.Name)
        {
            throw new DesignException(table.FullName, $"partition key '{match.Groups[2].Value}' must be (id, {column.Name})");
        }
    }

    private static void ReadPrimaryKey(Table table, IReadOnlyDictionary<string, object?> map)
    {
        if (map.TryGetValue("key", out var key))
        {
            table.PrimaryKey.AddRange(YamlNode.Texts(key, table.FullName + " key"));
        }
        else if (table.HasColumn("id"))
        {
            table.PrimaryKey.Add("id");
        }
        else
        {
            throw new DesignException(table.FullName, "no primary key: needs trait std or app, or a key entry");
        }

        if (table.Partition is { } partition && !table.PrimaryKey.Contains(partition.Column))
        {
            table.PrimaryKey.Add(partition.Column);
        }

        foreach (var column in table.PrimaryKey)
        {
            if (!table.Column(column).Required)
            {
                throw new DesignException(table.FullName, $"primary key column '{column}' must be req");
            }
        }
    }

    private static void ReadUniques(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules, RuleBook rules)
    {
        foreach (var raw in YamlNode.List(map.GetValueOrDefault("unique"), table.FullName))
        {
            var items = YamlNode.Texts(raw, table.FullName + " unique").ToList();
            string? where = null;
            var nullsNotDistinctExplicit = false;
            if (items.Count > 0 && items[^1].StartsWith("where ", StringComparison.Ordinal))
            {
                var phrase = items[^1];
                items.RemoveAt(items.Count - 1);
                if (tableRules.UniqueWhere.TryGetValue(phrase, out var translated))
                {
                    rules.MarkUsed(table.FullName, phrase);
                    where = translated;
                }
                else
                {
                    const string nnd = "(NULLS NOT DISTINCT)";
                    if (phrase.EndsWith(nnd, StringComparison.Ordinal))
                    {
                        nullsNotDistinctExplicit = true;
                        phrase = phrase[..^nnd.Length].TrimEnd();
                    }

                    where = phrase["where ".Length..];
                }
            }

            CheckItems(table, items, "unique");
            where = AndNotDeleted(table, where);
            var nullable = items.Any(i => !table.Column(ItemColumn(i)).Required);
            table.Uniques.Add(new UniqueKey(Naming.DbName("uq", table.Name, items), items, where, nullable || nullsNotDistinctExplicit));
        }
    }

    private static void ReadIndexes(Table table, IReadOnlyDictionary<string, object?> map)
    {
        foreach (var raw in YamlNode.List(map.GetValueOrDefault("indexes"), table.FullName))
        {
            var items = YamlNode.Texts(raw, table.FullName + " index").ToList();
            string? where = null;
            if (items.Count > 0 && items[^1].StartsWith("partial where ", StringComparison.Ordinal))
            {
                where = items[^1]["partial where ".Length..];
                items.RemoveAt(items.Count - 1);
            }

            CheckItems(table, items, "index");
            table.Indexes.Add(new IndexDef(Naming.DbName("ix", table.Name, items), items, AndNotDeleted(table, where), "listed in the data design"));
        }
    }

    private static string? AndNotDeleted(Table table, string? where) =>
        !table.IsSoftDeleted ? where : where is null ? "not is_deleted" : $"({where}) and not is_deleted";

    private static void CheckItems(Table table, List<string> items, string what)
    {
        if (items.Count == 0)
        {
            throw new DesignException(table.FullName, $"empty {what}");
        }

        foreach (var item in items)
        {
            _ = table.Column(ItemColumn(item));
            if (!Naming.IsIdentifier(item) && !LowerItemPattern().IsMatch(item))
            {
                throw new DesignException(table.FullName, $"{what} item '{item}' must be a column or lower(column)");
            }
        }
    }

    public static string ItemColumn(string item)
    {
        var lower = LowerItemPattern().Match(item);
        return lower.Success ? lower.Groups[1].Value : item;
    }

    private static void ReadEffectiveDating(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules, RuleBook rules)
    {
        var hasKey = map.TryGetValue("eff_key", out var keyNode);
        if (table.IsEffectiveDated != hasKey)
        {
            throw new DesignException(table.FullName, "trait eff and eff_key must appear together");
        }

        if (!hasKey)
        {
            return;
        }

        var keys = YamlNode.Texts(keyNode, table.FullName + " eff_key");
        var elements = new List<string>();
        foreach (var key in keys)
        {
            var column = table.Column(key);
            if (column.Required)
            {
                elements.Add($"{key} with =");
            }
            else if (column.Kind is ColumnKind.Uuid)
            {
                // README rule 11: a null key must not escape the overlap check.
                elements.Add($"(coalesce({key}, {NilUuid})) with =");
            }
            else
            {
                throw new DesignException(table.FullName, $"nullable eff_key column '{key}' is not a uuid; cannot apply README rule 11");
            }
        }

        elements.Add("daterange(valid_from, valid_to, '[)') with &&");
        string? where = null;
        foreach (var note in tableRules.Notes.Where(n => n.EffWhere is not null))
        {
            if (YamlNode.OptionalText(map, "notes", table.FullName) == note.Text)
            {
                rules.MarkUsed(table.FullName, note.Text);
                where = note.EffWhere;
            }
        }

        table.Exclusions.Add(new ExclusionDef(Naming.DbName("ex", table.Name, keys), elements, AndNotDeleted(table, where)));
    }

    private static void ReadChecks(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules, RuleBook rules)
    {
        foreach (var text in YamlNode.Texts(map.GetValueOrDefault("checks"), table.FullName + " checks"))
        {
            var action = tableRules.Checks.Find(a => a.Text == text)
                ?? throw new DesignException(table.FullName, $"untranslated check \"{text}\": add it to tools/Erp.CodeGen/rules.yaml after review");
            rules.MarkUsed(table.FullName, text);
            Apply(table, action);
        }
    }

    private static void ReadNotes(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules, RuleBook rules)
    {
        var text = YamlNode.OptionalText(map, "notes", table.FullName);
        if (text is null)
        {
            return;
        }

        var action = tableRules.Notes.Find(a => a.Text == text)
            ?? throw new DesignException(table.FullName, $"untranslated note \"{text}\": add it to tools/Erp.CodeGen/rules.yaml after review");
        rules.MarkUsed(table.FullName, text);
        Apply(table, action);
    }

    private static void Apply(Table table, RuleAction action)
    {
        if (action.Check is { } check)
        {
            table.Checks.Add(check);
        }
        else if (action.Exclusion is { } exclusion)
        {
            table.Exclusions.Add(exclusion with { Where = AndNotDeleted(table, exclusion.Where) });
        }
        else if (action.Stub is { } stub)
        {
            table.Stubs.Add(new StubDef(table.FullName, stub.Name, stub.Owner, action.Text, action.Implementation));
            table.Comments.Add($"Pending rule ({stub.Owner}, placeholder {table.Schema}.todo_{stub.Name}): {action.Text}");
        }
        else if (action.IsComment)
        {
            table.Comments.Add(action.Text);
        }
        else if (action.AppendOnlyUpdatable.Count > 0)
        {
            if (!table.IsAppendOnly)
            {
                throw new DesignException(table.FullName, "append_only_updatable applies to append-only tables only");
            }

            table.AppendOnlyUpdatable.AddRange(action.AppendOnlyUpdatable.Select(c => table.Column(c).Name));
            table.Comments.Add(action.Text);
        }

        // eff_where and composite_fk are applied by ReadEffectiveDating and ResolveForeignKeys.
    }

    // ---------------------------------------------------------------- foreign keys

    private static void ResolveForeignKeys(DesignModel model, List<PendingFk> pending, RuleBook rules)
    {
        foreach (var group in pending.GroupBy(p => (p.Table.FullName, p.Column)))
        {
            if (group.Select(p => p.Target).Distinct().Count() > 1)
            {
                throw new DesignException(group.Key.FullName + "." + group.Key.Column, "note names more than one foreign key target");
            }
        }

        foreach (var fk in pending.DistinctBy(p => (p.Table.FullName, p.Column)))
        {
            var table = fk.Table;
            if (!model.TryGetTable(fk.Target, out var target))
            {
                throw new DesignException($"{table.FullName}.{fk.Column}", $"foreign key target {fk.Target} does not exist");
            }

            if (table.Database != target.Database)
            {
                throw new DesignException($"{table.FullName}.{fk.Column}", $"foreign key to {fk.Target} crosses databases");
            }

            if (table.Module != target.Module && !(table.Module == "hrm" && target.Module == "kernel"))
            {
                throw new DesignException($"{table.FullName}.{fk.Column}", $"foreign key to {fk.Target} crosses modules (README rule 2)");
            }

            var composite = rules.For(table.FullName).Notes.Select(n => n.CompositeFk).FirstOrDefault(c => c is not null && c.Column == fk.Column);
            if (composite is not null)
            {
                rules.MarkUsed(table.FullName, rules.For(table.FullName).Notes.First(n => n.CompositeFk == composite).Text);
                AddCompositeVersionFk(table, target, composite);
                continue;
            }

            table.ForeignKeys.Add(BuildForeignKey(table, fk.Column, target));
        }
    }

    private static ForeignKey BuildForeignKey(Table table, string column, Table target)
    {
        var name = Naming.DbName("fk", table.Name, [column]);
        var targetKey = TargetKey(table, column, target);
        var companyScoped = table.IsCompanyScoped && target.IsCompanyScoped && column != "company_id";
        var columns = new List<string>();
        var targetColumns = new List<string>();
        if (companyScoped)
        {
            // README rule 12: a company-scoped row can only point at a row of the same company.
            columns.Add("company_id");
            targetColumns.Add("company_id");
            target.NeedsCompanyIdKey = true;
        }

        columns.Add(column);
        targetColumns.Add(targetKey);
        if (target.Partition is { } partition)
        {
            // CR-003: a foreign key to a partitioned table includes the partition column, which the child carries.
            if (!table.HasColumn(partition.Column))
            {
                throw new DesignException($"{table.FullName}.{column}", $"references partitioned {target.FullName} but has no {partition.Column} column");
            }

            columns.Add(partition.Column);
            targetColumns.Add(partition.Column);
        }

        return new ForeignKey(name, columns, target.FullName, targetColumns);
    }

    private static string TargetKey(Table table, string column, Table target)
    {
        var key = target.PrimaryKey.Where(k => target.Partition is null || k != target.Partition.Column).ToList();
        if (key.Count != 1)
        {
            throw new DesignException($"{table.FullName}.{column}", $"cannot reference {target.FullName}: its key ({string.Join(", ", target.PrimaryKey)}) has several columns");
        }

        var targetColumn = target.Column(key[0]);
        var source = table.Column(column);
        if (targetColumn.SqlType != source.SqlType)
        {
            throw new DesignException($"{table.FullName}.{column}", $"type {source.SqlType} does not match {target.FullName}.{key[0]} ({targetColumn.SqlType})");
        }

        return key[0];
    }

    private static void AddCompositeVersionFk(Table table, Table target, CompositeFkRule rule)
    {
        foreach (var c in rule.Columns)
        {
            _ = table.Column(c);
        }

        foreach (var c in rule.TargetColumns)
        {
            _ = target.Column(c);
        }

        if (target.FullName != rule.Target)
        {
            throw new DesignException(table.FullName, $"composite_fk target {rule.Target} differs from the column note target {target.FullName}");
        }

        if (!target.Uniques.Exists(u => u.Items.SequenceEqual(rule.TargetColumns) && u.Where is null))
        {
            target.Uniques.Add(new UniqueKey(Naming.DbName("uq", target.Name, rule.TargetColumns), rule.TargetColumns, null, false));
        }

        table.ForeignKeys.Add(new ForeignKey(Naming.DbName("fk", table.Name, [rule.Column]), rule.Columns, target.FullName, rule.TargetColumns));
    }

    private static void AddForeignKeyIndexes(DesignModel model)
    {
        foreach (var table in model.Tables)
        {
            if (table.NeedsCompanyIdKey)
            {
                var key = new List<string> { "company_id", "id" };
                if (table.Partition is { } partition)
                {
                    key.Add(partition.Column);
                }

                table.Uniques.Add(new UniqueKey(Naming.DbName("uq", table.Name, key), key, null, false));
            }
        }

        foreach (var table in model.Tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!IsCovered(table, fk.Columns))
                {
                    table.Indexes.Add(new IndexDef(Naming.DbName("ix", table.Name, fk.Columns), fk.Columns, null, "foreign key " + fk.Name));
                }
            }
        }
    }

    /// <summary>True when a full (non-partial) key or index already starts with exactly these columns.</summary>
    public static bool IsCovered(Table table, IReadOnlyList<string> columns)
    {
        bool Leads(IReadOnlyList<string> items) => items.Count >= columns.Count && items.Take(columns.Count).SequenceEqual(columns);
        return Leads(table.PrimaryKey)
            || table.Uniques.Exists(u => u.Where is null && Leads(u.Items))
            || table.Indexes.Exists(i => i.Where is null && Leads(i.Items));
    }

    // ---------------------------------------------------------------- outbox and inbox (spec 03 section 7.5)

    private static IEnumerable<Table> OutboxAndInbox(string schema, Settings settings)
    {
        var module = settings.ModuleOf(schema);
        var outbox = new Table
        {
            Schema = schema,
            Name = "outbox_messages",
            Database = DatabaseKind.Tenant,
            Module = module,
            SourceFile = "spec 03 section 7.5",
            Purpose = "Events written in the business transaction; the dispatcher publishes them (spec 03 section 7.5)",
            Traits = [],
            Requirements = [],
            IsOutboxOrInbox = true,
        };
        outbox.Columns.AddRange(
        [
            new Column { Name = "id", Kind = ColumnKind.Uuid, SqlType = "uuid", Required = true, Origin = "outbox" },
            new Column { Name = "occurred_at", Kind = ColumnKind.Timestamp, SqlType = "timestamptz", Required = true, Origin = "outbox" },
            new Column { Name = "event_type", Kind = ColumnKind.Text, SqlType = "text", Required = true, Origin = "outbox" },
            new Column { Name = "aggregate_type", Kind = ColumnKind.Text, SqlType = "text", Required = true, Origin = "outbox" },
            new Column { Name = "aggregate_id", Kind = ColumnKind.Uuid, SqlType = "uuid", Required = true, Origin = "outbox" },
            new Column { Name = "payload", Kind = ColumnKind.Json, SqlType = "jsonb", Required = true, Origin = "outbox" },
            new Column { Name = "headers", Kind = ColumnKind.Json, SqlType = "jsonb", Required = true, Default = "'{}'::jsonb", Origin = "outbox" },
            new Column { Name = "published_at", Kind = ColumnKind.Timestamp, SqlType = "timestamptz", Required = false, Origin = "outbox" },
        ]);
        outbox.PrimaryKey.Add("id");
        outbox.Indexes.Add(new IndexDef(Naming.DbName("ix", "outbox_messages", ["occurred_at", "unpublished"]), ["occurred_at"], "published_at is null", "dispatcher reads unpublished messages in order"));
        yield return outbox;

        var inbox = new Table
        {
            Schema = schema,
            Name = "inbox_messages",
            Database = DatabaseKind.Tenant,
            Module = module,
            SourceFile = "spec 03 section 7.5",
            Purpose = "Messages already processed by a consumer, so consumers are idempotent (spec 03 section 7.5)",
            Traits = [],
            Requirements = [],
            IsOutboxOrInbox = true,
        };
        inbox.Columns.AddRange(
        [
            new Column { Name = "message_id", Kind = ColumnKind.Uuid, SqlType = "uuid", Required = true, Origin = "inbox" },
            new Column { Name = "consumer", Kind = ColumnKind.Text, SqlType = "text", Required = true, Origin = "inbox" },
            new Column { Name = "processed_at", Kind = ColumnKind.Timestamp, SqlType = "timestamptz", Required = true, Origin = "inbox" },
        ]);
        inbox.PrimaryKey.AddRange(["message_id", "consumer"]);
        yield return inbox;
    }

    [GeneratedRegex(@"\bfk ([a-z]+\.[a-z_]+)")]
    private static partial Regex FkPattern();

    [GeneratedRegex(@"generated always as (.+) stored")]
    private static partial Regex GeneratedPattern();

    [GeneratedRegex(@"^default ([^;,]+)")]
    private static partial Regex DefaultPattern();

    [GeneratedRegex(@"\bcheck\s+(<>|>=|<=|>|<|=)\s*([^;,]+)")]
    private static partial Regex CheckPattern();

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^enum\((.*)\)$")]
    private static partial Regex EnumPattern();

    [GeneratedRegex(@"^char\(\d+\)$")]
    private static partial Regex CharPattern();

    [GeneratedRegex(@"^numeric\(\d+,\d+\)$")]
    private static partial Regex NumericPattern();

    [GeneratedRegex(@"^range \((\w+)\) monthly(?:; key \(([\w, ]+)\))?")]
    private static partial Regex PartitionPattern();

    [GeneratedRegex(@"^lower\((\w+)\)$")]
    private static partial Regex LowerItemPattern();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex EnumValuePattern();
}
