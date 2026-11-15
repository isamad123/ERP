namespace Erp.CodeGen.Design;

/// <summary>Cross-table checks run after loading. Every problem is reported together; nothing is generated if any exist.</summary>
public static class Validator
{
    public static void Validate(DesignModel model, RuleBook rules)
    {
        var problems = new List<string>();
        problems.AddRange(rules.UnusedEntries());

        foreach (var excluded in model.Settings.ApiExcluded.Where(e => !model.TryGetTable(e, out _)))
        {
            problems.Add($"codegen-settings.yaml api_excluded names unknown table {excluded}");
        }

        foreach (var table in model.Tables)
        {
            if (table.Partition is { } partition)
            {
                foreach (var unique in table.Uniques.Where(u => !u.Items.Contains(partition.Column)))
                {
                    problems.Add($"{table}: unique key {unique.Name} on a partitioned table must include {partition.Column} (README rule 8)");
                }
            }

            if (table.Database == DatabaseKind.Tenant && table.Columns.Exists(c => c.Name == "tenant_id"))
            {
                problems.Add($"{table}: tenant_id is not allowed in a customer database (D-47)");
            }
        }

        // Index and constraint names share one namespace per schema in PostgreSQL.
        foreach (var schema in model.Tables.GroupBy(t => (t.Database, t.Schema)))
        {
            var names = schema.SelectMany(t =>
                    t.Uniques.Select(u => u.Name)
                        .Concat(t.Indexes.Select(i => i.Name))
                        .Concat(t.Exclusions.Select(e => e.Name))
                        .Concat(t.Checks.Select(c => t.Name + "/" + c.Name))
                        .Concat(t.ForeignKeys.Select(f => t.Name + "/" + f.Name))
                        .Append("pk_" + t.Name))
                .GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            problems.AddRange(names.Select(n => $"schema {schema.Key.Schema}: object name {n} is generated twice"));

            var stubs = schema.SelectMany(t => t.Stubs).GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key);
            problems.AddRange(stubs.Select(s => $"schema {schema.Key.Schema}: stub {s} defined twice"));
        }

        if (problems.Count > 0)
        {
            throw new DesignException("data design", Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }
}
