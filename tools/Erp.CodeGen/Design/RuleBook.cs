namespace Erp.CodeGen.Design;

public sealed record CompositeFkRule(string Column, IReadOnlyList<string> Columns, string Target, IReadOnlyList<string> TargetColumns);

/// <summary>One reviewed translation of a design sentence (tools/Erp.CodeGen/rules.yaml).</summary>
public sealed record RuleAction(
    string Text,
    CheckDef? Check,
    ExclusionDef? Exclusion,
    (string Name, string Owner)? Stub,
    bool IsComment,
    string? EffWhere,
    CompositeFkRule? CompositeFk,
    IReadOnlyList<string> AppendOnlyUpdatable);

public sealed record ColumnRule(string Column, string Text, RuleAction Action);

public sealed class TableRules
{
    public List<RuleAction> Checks { get; } = [];

    public List<RuleAction> Notes { get; } = [];

    public Dictionary<string, string> UniqueWhere { get; } = new(StringComparer.Ordinal);

    public bool NoPartition { get; set; }

    public List<ColumnRule> ColumnStubs { get; } = [];

    public List<ColumnRule> ColumnChecks { get; } = [];
}

/// <summary>tools/Erp.CodeGen/rules.yaml: how every free-text rule of the data design is generated.</summary>
public sealed class RuleBook
{
    private readonly Dictionary<string, TableRules> _tables;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private RuleBook(Dictionary<string, TableRules> tables) => _tables = tables;

    public TableRules For(string table) => _tables.TryGetValue(table, out var rules) ? rules : new TableRules();

    /// <summary>Records that a rule entry matched the design, so unmatched (stale) entries can be reported.</summary>
    public void MarkUsed(string table, string text) => _used.Add(table + "\u0001" + text);

    public IEnumerable<string> UnusedEntries()
    {
        foreach (var (table, rules) in _tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var texts = rules.Checks.Concat(rules.Notes).Select(a => a.Text)
                .Concat(rules.UniqueWhere.Keys)
                .Concat(rules.ColumnStubs.Select(c => c.Text))
                .Concat(rules.ColumnChecks.Select(c => c.Text));
            foreach (var text in texts.Where(t => !_used.Contains(table + "\u0001" + t)))
            {
                yield return $"{table}: rules.yaml entry \"{text}\" matches nothing in the data design";
            }
        }
    }

    public static RuleBook Load(string path)
    {
        var root = YamlNode.Map(YamlNode.Load(path), path);
        var tables = new Dictionary<string, TableRules>(StringComparer.Ordinal);
        foreach (var (table, node) in YamlNode.Map(root["tables"], "tables"))
        {
            var where = "rules.yaml " + table;
            var map = YamlNode.Map(node, where);
            var rules = new TableRules();
            foreach (var entry in YamlNode.List(map.GetValueOrDefault("checks"), where))
            {
                rules.Checks.Add(ReadAction(YamlNode.Map(entry, where), where));
            }

            foreach (var entry in YamlNode.List(map.GetValueOrDefault("notes"), where))
            {
                rules.Notes.Add(ReadAction(YamlNode.Map(entry, where), where));
            }

            foreach (var (text, sql) in YamlNode.Map(map.GetValueOrDefault("unique_where"), where))
            {
                rules.UniqueWhere[text] = YamlNode.Text(sql, where);
            }

            rules.NoPartition = YamlNode.OptionalText(map, "partition", where) == "none";
            rules.ColumnStubs.AddRange(ReadColumnRules(map.GetValueOrDefault("column_stubs"), where));
            rules.ColumnChecks.AddRange(ReadColumnRules(map.GetValueOrDefault("column_checks"), where));
            tables[table] = rules;
        }

        return new RuleBook(tables);
    }

    private static IEnumerable<ColumnRule> ReadColumnRules(object? node, string where)
    {
        foreach (var entry in YamlNode.List(node, where))
        {
            var map = YamlNode.Map(entry, where);
            yield return new ColumnRule(YamlNode.Text(map["column"], where), YamlNode.Text(map["text"], where), ReadAction(map, where));
        }
    }

    private static RuleAction ReadAction(IReadOnlyDictionary<string, object?> map, string where)
    {
        var text = YamlNode.Text(map["text"], where + " text");
        CheckDef? check = null;
        ExclusionDef? exclusion = null;
        (string, string)? stub = null;
        CompositeFkRule? compositeFk = null;
        IReadOnlyList<string> updatable = [];

        if (map.TryGetValue("check", out var c))
        {
            var cm = YamlNode.Map(c, where);
            check = new CheckDef(YamlNode.Text(cm["name"], where), YamlNode.Text(cm["sql"], where));
        }

        if (map.TryGetValue("exclusion", out var e))
        {
            var em = YamlNode.Map(e, where);
            exclusion = new ExclusionDef(YamlNode.Text(em["name"], where), YamlNode.Texts(em["elements"], where), YamlNode.OptionalText(em, "where", where));
        }

        if (map.TryGetValue("stub", out var s))
        {
            var sm = YamlNode.Map(s, where);
            stub = (YamlNode.Text(sm["name"], where), YamlNode.Text(sm["owner"], where));
        }

        if (map.TryGetValue("composite_fk", out var f))
        {
            var fm = YamlNode.Map(f, where);
            compositeFk = new CompositeFkRule(YamlNode.Text(fm["column"], where), YamlNode.Texts(fm["columns"], where),
                YamlNode.Text(fm["target"], where), YamlNode.Texts(fm["target_columns"], where));
        }

        if (map.TryGetValue("append_only_updatable", out var u))
        {
            updatable = YamlNode.Texts(u, where);
        }

        var action = new RuleAction(text, check, exclusion, stub, map.ContainsKey("comment"), YamlNode.OptionalText(map, "eff_where", where), compositeFk, updatable);
        var kinds = new object?[] { action.Check, action.Exclusion, action.Stub, action.IsComment ? true : null, action.EffWhere, action.CompositeFk, updatable.Count > 0 ? true : null };
        if (kinds.Count(k => k is not null) != 1)
        {
            throw new DesignException($"{where}: \"{text}\"", "each rules.yaml entry needs exactly one of check, exclusion, stub, comment, eff_where, composite_fk, append_only_updatable");
        }

        return action;
    }
}
