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
        ["table", "purpose", "traits", "status", "columns", "unique", "indexes", "eff_key", "key", "checks", "notes", "partition", "reqs",
         "added_in", "updatable_added_in", "api_read_only", "scope", "field_class", "api_actions", "audit", "audit_reason", "indexes_added_in",
         "numbering", "optional_added_in", "enum_added_in", "checks_added_in", "uniques_added_in", "uniques_replaced_in", "entitlement"];

    /// <summary>Keys of a design file's <c>defaults</c> entry (slice S1.09b): values every table of the file takes unless it overrides them.</summary>
    private static readonly string[] DefaultKeys = ["defaults", "entitlement"];

    /// <summary>Data-scope dimensions a table may declare in <c>scope:</c> (CR-018).</summary>
    public static readonly string[] ScopeDimensions = ["company", "branch", "org_unit", "employment", "person", "user"];

    public static readonly string[] FieldClasses = ["salary", "personal", "restricted"];

    public static readonly string[] ApiActionNames = ["read", "create", "update", "delete"];

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
            string? fileEntitlement = null;
            foreach (var entry in YamlNode.List(YamlNode.Load(file), fileName))
            {
                var map = YamlNode.Map(entry, fileName);
                if (map.ContainsKey("defaults"))
                {
                    fileEntitlement = ReadDefaults(map, fileName, tables.Exists(t => t.SourceFile == fileName));
                    continue;
                }

                tables.Add(ReadTable(map, fileName, settings, rules, pending, fileEntitlement));
            }
        }

        foreach (var schema in settings.TenantSchemas)
        {
            foreach (var machinery in OutboxAndInbox(schema, settings))
            {
                machinery.Audited = false;
                machinery.AuditReason = "machinery: transactional outbox and inbox (S1.07 Q4)";
                tables.Add(machinery);
            }
        }

        var duplicates = tables.GroupBy(t => t.FullName).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new DesignException(string.Join(", ", duplicates), "table defined more than once");
        }

        var model = new DesignModel { Tables = tables.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList(), Settings = settings, Functions = rules.Functions };
        ResolveForeignKeys(model, pending, rules);
        ResolveScopeParents(model);
        AddForeignKeyIndexes(model);
        Validator.Validate(model, rules);
        return model;
    }

    /// <summary>
    /// The file's <c>- defaults: {}</c> entry, before its first table (slice S1.09b): <c>entitlement: hrm.core</c> names the
    /// feature key of every table of the file that does not give its own (ruling Q9, CR-036).
    /// </summary>
    private static string? ReadDefaults(IReadOnlyDictionary<string, object?> map, string file, bool afterTables)
    {
        var unknown = map.Keys.Except(DefaultKeys).ToList();
        if (unknown.Count > 0 || afterTables || map["defaults"] is not null)
        {
            throw new DesignException(file, "a `- defaults:` entry comes before the first table and holds only `entitlement:` (unknown: " + string.Join(", ", unknown) + ")");
        }

        return map.GetValueOrDefault("entitlement") is string key ? EntitlementKey(key, file) : null;
    }

    /// <summary>A D-24 feature key: lower-case dot-separated words, for example <c>platform.base</c> or <c>hrm.payroll</c>.</summary>
    private static string EntitlementKey(string key, string where) =>
        EntitlementKeyPattern().IsMatch(key) ? key : throw new DesignException(where, $"entitlement '{key}' is not a feature key such as hrm.core");

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex EntitlementKeyPattern();

    private static Table ReadTable(IReadOnlyDictionary<string, object?> map, string file, Settings settings, RuleBook rules, List<PendingFk> pending, string? fileEntitlement)
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
            AddedIn = map.GetValueOrDefault("added_in") switch
            {
                string slice => SliceId(slice, fullName),
                // added_in: {table: S1.05, S1.11b-r1: [column]} (S1.11b2): a table of a later slice that later gains columns.
                Dictionary<object, object?> later when later.TryGetValue("table", out var first) && first is string slice => SliceId(slice, fullName),
                _ => null,
            },
            Entitlement = map.GetValueOrDefault("entitlement") is string key ? EntitlementKey(key, fullName) : fileEntitlement,
        };
        if (table.Database == DatabaseKind.ControlPlane ? table.Entitlement is not null : table.Entitlement is null)
        {
            throw new DesignException(fullName, table.Database == DatabaseKind.ControlPlane
                ? "control-plane tables have no entitlement key"
                : "every customer-database table needs an entitlement key (file `- defaults: {entitlement: ...}` or the table's `entitlement:`)");
        }
        var tableRules = rules.For(fullName);

        AddColumns(table, map, tableRules, rules, pending);
        ReadPartition(table, map, tableRules);
        ReadPrimaryKey(table, map);
        ReadUniques(table, map, tableRules, rules);
        ReadIndexes(table, map);
        ReadEffectiveDating(table, map, tableRules, rules);
        ReadChecks(table, map, tableRules, rules);
        ReadNotes(table, map, tableRules, rules);
        ReadLaterChanges(table, map);
        ReadChecksAddedIn(table, map, tableRules, rules);
        ReadAccessKeys(table, map);
        ReadAuditKeys(table, map);
        ReadNumbering(table, map);
        return table;
    }

    // ---------------------------------------------------------------- changes after the first migrations (S1.05)

    /// <summary>
    /// Changes made after the release's first migrations were merged. Those files are frozen, so the generator leaves
    /// these items out of them and writes them to the owning slice's design migration instead:
    /// <c>added_in: S1.05</c> (whole table) or <c>added_in: {S1.05: [column, ...]}</c> (new nullable columns);
    /// <c>updatable_added_in: {S1.05: {columns: [...], note: "..."}}</c> (more columns an append-only table may update);
    /// <c>api_read_only: [column, ...]</c> (generated API returns the column but never accepts it, CR-012).
    /// </summary>
    private static void ReadLaterChanges(Table table, IReadOnlyDictionary<string, object?> map)
    {
        if (map.GetValueOrDefault("added_in") is Dictionary<object, object?> columns)
        {
            foreach (var (slice, names) in YamlNode.Map(columns, table.FullName + ".added_in"))
            {
                if (slice == "table")
                {
                    continue; // the slice that created the table (read with the table)
                }

                foreach (var name in YamlNode.Texts(names, table.FullName + ".added_in"))
                {
                    var column = table.Column(name);
                    column.AddedIn = SliceId(slice, table.FullName);
                    // The column's own check (enum values S1.07; code shape and i18n object S1.09a) is added with the column
                    // by the slice's design migration.
                    string[]? checkParts = column.Kind switch
                    {
                        ColumnKind.Enum => [column.Name],
                        ColumnKind.Code => [column.Name, "code"],
                        ColumnKind.I18n => [column.Name, "object"],
                        _ => null,
                    };
                    if (checkParts is not null)
                    {
                        var check = Naming.DbName("ck", table.Name, checkParts);
                        var index = table.Checks.FindIndex(c => c.Name == check);
                        table.Checks[index] = table.Checks[index] with { AddedIn = column.AddedIn };
                    }
                }
            }
        }
        else if (map.GetValueOrDefault("added_in") is not (null or string))
        {
            throw new DesignException(table.FullName, "added_in must be a slice id or a map of slice id to column names");
        }

        foreach (var (slice, raw) in YamlNode.Map(map.GetValueOrDefault("updatable_added_in"), table.FullName + ".updatable_added_in"))
        {
            if (!table.IsAppendOnly)
            {
                throw new DesignException(table.FullName, "updatable_added_in applies to append-only tables only");
            }

            var entry = YamlNode.Map(raw, table.FullName + ".updatable_added_in");
            var note = YamlNode.OptionalText(entry, "note", table.FullName) ?? throw new DesignException(table.FullName, "updatable_added_in needs a note");
            foreach (var name in YamlNode.Texts(entry.GetValueOrDefault("columns"), table.FullName + ".updatable_added_in"))
            {
                var column = table.Column(name).Name;
                table.AppendOnlyUpdatable.Add(column);
                table.UpdatableAddedIn.Add((SliceId(slice, table.FullName), column));
            }

            table.CommentsAddedIn.Add((SliceId(slice, table.FullName), note));
        }

        foreach (var name in YamlNode.Texts(map.GetValueOrDefault("api_read_only"), table.FullName + ".api_read_only"))
        {
            table.Column(name).ApiReadOnly = true;
        }

        // optional_added_in: {S1.08: [column, ...]} (S1.08, CR-033): required columns made optional; the frozen file keeps NOT NULL.
        foreach (var (slice, names) in YamlNode.Map(map.GetValueOrDefault("optional_added_in"), table.FullName + ".optional_added_in"))
        {
            foreach (var name in YamlNode.Texts(names, table.FullName + ".optional_added_in"))
            {
                var column = table.Column(name);
                if (!column.Required || column.AddedIn is not null || column.Default is not null)
                {
                    throw new DesignException(table.FullName, $"optional_added_in: {name} must be a required column of the first migrations without a default");
                }

                column.Required = false;
                column.OptionalIn = SliceId(slice, table.FullName);
            }
        }

        // enum_added_in: {S1.08: {column: [value, ...]}} (S1.08, CR-033): values added to an existing enum; the slice's
        // design migration replaces the column's check, the frozen file keeps the first list.
        foreach (var (slice, raw) in YamlNode.Map(map.GetValueOrDefault("enum_added_in"), table.FullName + ".enum_added_in"))
        {
            foreach (var (name, values) in YamlNode.Map(raw, table.FullName + ".enum_added_in"))
            {
                var column = table.Column(name);
                var added = YamlNode.Texts(values, table.FullName + ".enum_added_in." + name).ToList();
                if (column.Kind != ColumnKind.Enum || column.AddedIn is not null || added.Count == 0 || added.Except(column.EnumValues).Any() || added.Count >= column.EnumValues.Count)
                {
                    throw new DesignException(table.FullName, $"enum_added_in: {name} must be an enum column of the first migrations listing some of its values");
                }

                var check = Naming.DbName("ck", table.Name, [column.Name]);
                var index = table.Checks.FindIndex(c => c.Name == check);
                var baseValues = column.EnumValues.Except(added).Select(Naming.SqlString);
                table.Checks[index] = table.Checks[index] with { BaseSql = $"{column.Name} in ({string.Join(", ", baseValues)})", ReplacedIn = SliceId(slice, table.FullName) };
            }
        }

        // uniques_added_in: {S1.08: [[column, ...], ...]} (S1.08, CR-033): plain unique keys the slice's design migration adds.
        foreach (var (slice, raw) in YamlNode.Map(map.GetValueOrDefault("uniques_added_in"), table.FullName + ".uniques_added_in"))
        {
            foreach (var key in YamlNode.List(raw, table.FullName + ".uniques_added_in"))
            {
                var items = YamlNode.Texts(key, table.FullName + ".uniques_added_in").ToList();
                var where = TrailingWhere(items);
                if (items.Count == 0 || !items.All(Naming.IsIdentifier))
                {
                    throw new DesignException(table.FullName, "uniques_added_in lists plain column names only (optionally followed by \"where ...\")");
                }

                CheckItems(table, items, "unique");
                var nullable = items.Any(i => !table.Column(i).Required);
                table.Uniques.Add(new UniqueKey(Naming.DbName("uq", table.Name, items), items, AndNotDeleted(table, where), nullable, SliceId(slice, table.FullName)));
            }
        }

        // uniques_replaced_in: {S1.09b: [[column, ..., "where ..."], ...]} (S1.09b, CR-036 item 6): the partial unique key on
        // these columns gets a new condition; the frozen file keeps the first one, the slice's design migration recreates it.
        foreach (var (slice, raw) in YamlNode.Map(map.GetValueOrDefault("uniques_replaced_in"), table.FullName + ".uniques_replaced_in"))
        {
            foreach (var key in YamlNode.List(raw, table.FullName + ".uniques_replaced_in"))
            {
                var items = YamlNode.Texts(key, table.FullName + ".uniques_replaced_in").ToList();
                var where = TrailingWhere(items);
                var index = table.Uniques.FindIndex(u => u.Items.SequenceEqual(items) && u.Where is not null && u.AddedIn is null && u.ReplacedIn is null);
                if (where is null || index < 0)
                {
                    throw new DesignException(table.FullName, "uniques_replaced_in names a partial unique key of the first migrations by its columns and gives its new \"where ...\"");
                }

                var old = table.Uniques[index];
                table.Uniques[index] = old with { Where = AndNotDeleted(table, where), BaseWhere = old.Where, ReplacedIn = SliceId(slice, table.FullName) };
            }
        }

        // indexes_added_in: {S1.07: [[column, ...], ...]} (S1.07, CR-029): indexes the slice's design migration creates.
        foreach (var (slice, raw) in YamlNode.Map(map.GetValueOrDefault("indexes_added_in"), table.FullName + ".indexes_added_in"))
        {
            foreach (var index in YamlNode.List(raw, table.FullName + ".indexes_added_in"))
            {
                var items = YamlNode.Texts(index, table.FullName + ".indexes_added_in").ToList();
                CheckItems(table, items, "index");
                table.Indexes.Add(new IndexDef(Naming.DbName("ix", table.Name, items), items, AndNotDeleted(table, null), "added by " + SliceId(slice, table.FullName), SliceId(slice, table.FullName)));
            }
        }
    }

    /// <summary>
    /// <c>checks_added_in: {S1.08: ["text", ...]}</c> (S1.08, CR-033): checks the slice's design migration adds, each
    /// translated by a reviewed entry of rules.yaml like the <c>checks</c> key.
    /// </summary>
    private static void ReadChecksAddedIn(Table table, IReadOnlyDictionary<string, object?> map, TableRules tableRules, RuleBook rules)
    {
        foreach (var (slice, raw) in YamlNode.Map(map.GetValueOrDefault("checks_added_in"), table.FullName + ".checks_added_in"))
        {
            foreach (var text in YamlNode.Texts(raw, table.FullName + ".checks_added_in"))
            {
                var action = tableRules.Checks.Find(a => a.Text == text)
                    ?? throw new DesignException(table.FullName, $"untranslated check \"{text}\": add it to tools/Erp.CodeGen/rules.yaml after review");
                var check = action.Check ?? throw new DesignException(table.FullName, $"checks_added_in \"{text}\" must translate to a check");
                rules.MarkUsed(table.FullName, text);
                table.Checks.Add(check with { AddedIn = SliceId(slice, table.FullName) });
            }
        }
    }

    /// <summary>
    /// Numbering key (slice S1.08 Q1, CR-033): <c>numbering: {column, type, assign: create|post, gap_free, override,
    /// tenant_wide, default, reset, date}</c>, one map or a list of maps. The column becomes read-only in the generated API
    /// (FR-KRN-NUM-03). The default pattern must pass the same rules as a customer's pattern (Q4).
    /// </summary>
    private static void ReadNumbering(Table table, IReadOnlyDictionary<string, object?> map)
    {
        if (!map.TryGetValue("numbering", out var raw) || raw is null)
        {
            return;
        }

        List<object?> entries = raw is List<object?> list ? list : [raw];
        foreach (var entry in entries)
        {
            var where = table.FullName + ".numbering";
            var m = YamlNode.Map(entry, where);
            var unknown = m.Keys.Except(NumberingKeys).ToList();
            if (unknown.Count > 0)
            {
                throw new DesignException(table.FullName, "unknown numbering keys: " + string.Join(", ", unknown));
            }

            var column = table.Column(YamlNode.Text(m.GetValueOrDefault("column"), where + ".column"));
            if (column.Kind is not ColumnKind.Text || !column.Required)
            {
                throw new DesignException(table.FullName, $"numbering column {column.Name} must be a required text column");
            }

            var type = YamlNode.Text(m.GetValueOrDefault("type"), where + ".type");
            if (!DocumentTypePattern().IsMatch(type) || !type.StartsWith(table.Schema + ".", StringComparison.Ordinal))
            {
                throw new DesignException(table.FullName, $"numbering type '{type}' must look like {table.Schema}.employee_no");
            }

            var assign = YamlNode.Text(m.GetValueOrDefault("assign"), where + ".assign");
            if (assign is not ("create" or "post"))
            {
                throw new DesignException(table.FullName, "numbering assign must be create or post");
            }

            bool Flag(string key)
            {
                var text = YamlNode.OptionalText(m, key, where) ?? "no";
                return text switch
                {
                    "yes" or "true" or "True" => true,
                    "no" or "false" or "False" => false,
                    _ => throw new DesignException(table.FullName, $"numbering {key} must be yes or no"),
                };
            }

            var gapFree = Flag("gap_free");
            var allowOverride = Flag("override");
            var tenantWide = Flag("tenant_wide");
            if (assign == "create" && gapFree)
            {
                throw new DesignException(table.FullName, "a number given on create cannot be gap-free: gap-free numbers are given only at posting (S1.08 Q3)");
            }

            if (allowOverride && gapFree)
            {
                throw new DesignException(table.FullName, "a manual override is allowed only for gap-allowed series (S1.08 Q9)");
            }

            var company = table.ScopeLocators.GetValueOrDefault("company") ?? (table.HasColumn("company_id") ? "company_id" : null);
            if (tenantWide == (company is not null))
            {
                throw new DesignException(table.FullName, tenantWide ? "a tenant-wide document type cannot have a company column" : "a company document type needs a company column (or tenant_wide: yes)");
            }

            string? branch = table.HasColumn("branch_id") ? "branch_id" : null;
            var date = YamlNode.OptionalText(m, "date", where);
            if (date is not null && table.Column(date).Kind is not ColumnKind.Date)
            {
                throw new DesignException(table.FullName, $"numbering date {date} must be a date column");
            }

            var pattern = YamlNode.Text(m.GetValueOrDefault("default"), where + ".default");
            var reset = YamlNode.OptionalText(m, "reset", where) ?? "never";
            try
            {
                Erp.Kernel.Domain.Numbering.NumberPattern.Validate(pattern, reset, new Erp.Kernel.Domain.Numbering.PatternContext(!tenantWide, branch is not null));
            }
            catch (Erp.Kernel.Domain.Numbering.NumberPatternException ex)
            {
                throw new DesignException(table.FullName, $"numbering default pattern '{pattern}' ({reset}) is refused: {ex.ErrorKey}");
            }

            column.ApiReadOnly = true;
            table.Numbering.Add(new NumberingDef(column.Name, type, assign, gapFree, allowOverride, tenantWide, pattern, reset, date, company, branch));
        }
    }

    private static readonly string[] NumberingKeys = ["column", "type", "assign", "gap_free", "override", "tenant_wide", "default", "reset", "date"];

    [GeneratedRegex(@"^[a-z]+\.[a-z][a-z0-9_]{1,60}$")]
    private static partial Regex DocumentTypePattern();

    /// <summary>
    /// Audit keys (slice S1.07 Q4, CR-026): <c>audit: false</c> with <c>audit_reason: "..."</c> takes the table out of the
    /// change log. Every table is audited unless it says so.
    /// </summary>
    private static void ReadAuditKeys(Table table, IReadOnlyDictionary<string, object?> map)
    {
        var reason = YamlNode.OptionalText(map, "audit_reason", table.FullName);
        switch (map.GetValueOrDefault("audit"))
        {
            case null:
                break;
            case bool audited:
                table.Audited = audited;
                break;
            case string text when bool.TryParse(text, out var audited):
                table.Audited = audited;
                break;
            default:
                throw new DesignException(table.FullName, "audit must be true or false");
        }

        if (!table.Audited && string.IsNullOrWhiteSpace(reason))
        {
            throw new DesignException(table.FullName, "audit: false needs an audit_reason");
        }

        if (table.Audited && reason is not null)
        {
            throw new DesignException(table.FullName, "audit_reason is only allowed with audit: false");
        }

        table.AuditReason = reason;
    }

    /// <summary>
    /// Access-control keys (slice S1.06): <c>scope: {company: company_id, employment: employment_id, attributes: [status]}</c>
    /// (CR-018), <c>field_class: {gross_amount: salary}</c> (CR-018) and <c>api_actions: [read]</c> (CR-020, CR-021).
    /// </summary>
    private static void ReadAccessKeys(Table table, IReadOnlyDictionary<string, object?> map)
    {
        foreach (var (dimension, raw) in YamlNode.Map(map.GetValueOrDefault("scope"), table.FullName + ".scope"))
        {
            if (dimension == "attributes")
            {
                foreach (var name in YamlNode.Texts(raw, table.FullName + ".scope.attributes"))
                {
                    var column = table.Column(name);
                    if (column.Kind is not (ColumnKind.Text or ColumnKind.Code or ColumnKind.Enum))
                    {
                        throw new DesignException(table.FullName, $"scope attribute {name} must be a text, code or enum column");
                    }

                    table.ScopeAttributes.Add(name);
                }

                continue;
            }

            if (dimension == "via")
            {
                var parentKey = table.Column(YamlNode.Text(raw, table.FullName + ".scope.via"));
                if (parentKey.Kind is not ColumnKind.Uuid)
                {
                    throw new DesignException(table.FullName, $"scope via must name a uuid foreign-key column, not {parentKey.Name}");
                }

                table.ScopeVia = parentKey.Name;
                continue;
            }

            if (!ScopeDimensions.Contains(dimension))
            {
                throw new DesignException(table.FullName, $"unknown scope dimension '{dimension}' (allowed: {string.Join(", ", ScopeDimensions)}, attributes)");
            }

            var locator = table.Column(YamlNode.Text(raw, table.FullName + ".scope." + dimension));
            if (locator.Kind is not ColumnKind.Uuid)
            {
                throw new DesignException(table.FullName, $"scope {dimension} must name a uuid column, not {locator.Name}");
            }

            table.ScopeLocators[dimension] = locator.Name;
        }

        foreach (var (name, raw) in YamlNode.Map(map.GetValueOrDefault("field_class"), table.FullName + ".field_class"))
        {
            var fieldClass = YamlNode.Text(raw, table.FullName + ".field_class." + name);
            if (!FieldClasses.Contains(fieldClass))
            {
                throw new DesignException(table.FullName, $"unknown field class '{fieldClass}' (allowed: {string.Join(", ", FieldClasses)})");
            }

            table.Column(name).FieldClass = fieldClass;
        }

        if (map.ContainsKey("api_actions"))
        {
            var actions = YamlNode.Texts(map["api_actions"], table.FullName + ".api_actions");
            var unknown = actions.Except(ApiActionNames).ToList();
            if (unknown.Count > 0 || !actions.Contains("read"))
            {
                throw new DesignException(table.FullName, $"api_actions must list read and only {string.Join(", ", ApiActionNames)}");
            }

            table.ApiActions = actions;
        }
    }

    private static string SliceId(string slice, string where) =>
        SlicePattern().IsMatch(slice) ? slice : throw new DesignException(where, $"'{slice}' is not a slice id such as S1.05 (S1.09a for a part of a slice built in parts, S1.05-r1 for a follow-up after that slice's design file was merged)");

    [GeneratedRegex(@"^S\d\.\d{2}[a-d]?(-r[1-3])?$")]
    private static partial Regex SlicePattern();

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

    /// <summary>Removes and returns a trailing <c>"where ..."</c> item (the condition of a partial unique key), or null.</summary>
    private static string? TrailingWhere(List<string> items)
    {
        if (items.Count == 0 || !items[^1].StartsWith("where ", StringComparison.Ordinal))
        {
            return null;
        }

        var where = items[^1]["where ".Length..];
        items.RemoveAt(items.Count - 1);
        return where;
    }

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

    /// <summary>
    /// <c>scope: {via: column}</c> (CR-025): the column is a foreign key to a table of the same schema with an id key and
    /// its own locators; the child declares nothing else in <c>scope</c> (its rows are placed by the parent's).
    /// </summary>
    private static void ResolveScopeParents(DesignModel model)
    {
        foreach (var table in model.Tables.Where(t => t.ScopeVia is not null))
        {
            if (table.ScopeLocators.Count > 0 || table.ScopeAttributes.Count > 0)
            {
                throw new DesignException(table.FullName, "scope via cannot be combined with other scope keys");
            }

            var fk = table.ForeignKeys.Find(f => f.Columns.Count == 1 && f.Columns[0] == table.ScopeVia)
                ?? throw new DesignException(table.FullName, $"scope via column {table.ScopeVia} must be a foreign key");
            if (!model.TryGetTable(fk.TargetTable, out var parent) || fk.CrossesSchema(table.Schema))
            {
                throw new DesignException(table.FullName, $"scope via must point at a table of schema {table.Schema}");
            }

            if (!parent.HasIdKey || parent.ScopeLocators.Count == 0 || parent.ScopeVia is not null)
            {
                throw new DesignException(table.FullName, $"scope via parent {parent.FullName} must have an id key and its own scope locators");
            }

            table.ScopeViaTable = parent;
        }
    }

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
                    // A foreign key on a column added later (S1.11a) gets its index in that slice's design file.
                    var addedIn = table.ForeignKeyAddedIn(fk);
                    table.Indexes.Add(new IndexDef(Naming.DbName("ix", table.Name, fk.Columns), fk.Columns, null, "foreign key " + fk.Name, addedIn));
                }
            }
        }
    }

    /// <summary>
    /// True when a full (non-partial) key or index of the first migrations already starts with exactly these columns. Keys
    /// added later (S1.08) do not count: the frozen file created the foreign-key index and it stays.
    /// </summary>
    public static bool IsCovered(Table table, IReadOnlyList<string> columns)
    {
        bool Leads(IReadOnlyList<string> items) => items.Count >= columns.Count && items.Take(columns.Count).SequenceEqual(columns);
        return Leads(table.PrimaryKey)
            || table.Uniques.Exists(u => u.Where is null && u.AddedIn is null && Leads(u.Items))
            || table.Indexes.Exists(i => i.Where is null && Leads(i.Items));
    }

    // ---------------------------------------------------------------- outbox and inbox (spec 03 section 7.5)

    /// <summary>Slice whose design migrations add the retry columns to every outbox (CR-045 item 2).</summary>
    public const string OutboxRetrySlice = "S1.11a";

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

            // Slice S1.11a (CR-045 item 2, ruling Q7): the dispatcher retries a failed delivery with growing waits.
            new Column { Name = "attempts", Kind = ColumnKind.Int4, SqlType = "integer", Required = true, Default = "0", Origin = "outbox", AddedIn = OutboxRetrySlice, Comment = "failed deliveries so far (CR-045, Q7)" },
            new Column { Name = "next_attempt_at", Kind = ColumnKind.Timestamp, SqlType = "timestamptz", Required = false, Origin = "outbox", AddedIn = OutboxRetrySlice, Comment = "a failed message waits until this time (CR-045, Q7)" },
            new Column { Name = "last_error", Kind = ColumnKind.Text, SqlType = "text", Required = false, Origin = "outbox", AddedIn = OutboxRetrySlice, Comment = "why the last delivery failed (CR-045, Q7)" },
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
