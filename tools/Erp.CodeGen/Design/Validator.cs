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

            problems.AddRange(LaterChangeProblems(model, table));
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

    /// <summary>
    /// Limits of the design migrations written for changes after the first migrations (added_in): expand only, and only
    /// shapes the generator can add to an existing database without guessing.
    /// </summary>
    private static IEnumerable<string> LaterChangeProblems(DesignModel model, Table table)
    {
        if (table.ChangedIn.Any() && table.Module != "kernel")
        {
            yield return $"{table}: added_in is supported for kernel tables only (module design migrations are not built yet)";
        }

        if (table.AddedIn is not null && table.Stubs.Count > 0)
        {
            yield return $"{table}: a table added later cannot carry placeholder rules";
        }

        if (table.AddedIn is null)
        {
            foreach (var fk in table.ForeignKeys.Where(f => model.TryGetTable(f.TargetTable, out var target) && target.AddedIn is not null))
            {
                yield return $"{table}: frozen table references {fk.TargetTable}, which is added later";
            }
        }

        foreach (var column in table.Columns.Where(c => c.AddedIn is not null))
        {
            if (table.AddedIn is not null)
            {
                yield return $"{table}.{column.Name}: column added_in on a table that is itself added later";
            }

            if (column.Required || column.Default is not null || column.IsGenerated || column.Kind is ColumnKind.Enum or ColumnKind.Code or ColumnKind.I18n)
            {
                yield return $"{table}.{column.Name}: a column added later must be optional, without default, generated value or check (expand only)";
            }

            if (table.ForeignKeys.Exists(f => f.Columns.Contains(column.Name)) || table.Uniques.Exists(u => u.Items.Contains(column.Name))
                || table.Indexes.Exists(i => i.Items.Contains(column.Name)) || table.Checks.Exists(c => c.Sql.Contains(column.Name, StringComparison.Ordinal)))
            {
                yield return $"{table}.{column.Name}: a column added later cannot have keys, indexes or checks yet";
            }
        }

        foreach (var column in table.Columns.Where(c => c.ApiReadOnly && c.Required && c.Default is null && c.Name != "status"))
        {
            yield return $"{table}.{column.Name}: api_read_only on a required column needs a database default";
        }
    }
}
