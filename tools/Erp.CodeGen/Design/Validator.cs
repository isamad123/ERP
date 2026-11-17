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
            problems.AddRange(AuditProblems(table));
        }

        // Document types of the numbering key (S1.08) are one registry for the whole customer database.
        foreach (var duplicate in model.Tables.SelectMany(t => t.Numbering).GroupBy(n => n.DocumentType, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"numbering document type {duplicate.Key} is declared more than once");
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
        if (table.ChangedIn.Any() && table.Module is not ("kernel" or "controlplane"))
        {
            yield return $"{table}: added_in is supported for kernel and control-plane tables only (module design migrations are not built yet)";
        }

        if (table.Database == DatabaseKind.ControlPlane && table.ChangedIn.Any() && table.AddedIn is null)
        {
            yield return $"{table}: on the control plane only whole tables can be added later (added_in: <slice>)";
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

            // An optional enum column may come later with its own value check (NULL passes it, so existing rows stay valid).
            if (column.Required || column.Default is not null || column.IsGenerated || column.Kind is ColumnKind.Code or ColumnKind.I18n)
            {
                yield return $"{table}.{column.Name}: a column added later must be optional, without default, generated value or check (expand only)";
            }

            if (table.ForeignKeys.Exists(f => f.Columns.Contains(column.Name)) || table.Uniques.Exists(u => u.Items.Contains(column.Name))
                || table.Indexes.Exists(i => i.Items.Contains(column.Name)) || table.Checks.Exists(c => c.AddedIn != column.AddedIn && c.Sql.Contains(column.Name, StringComparison.Ordinal)))
            {
                yield return $"{table}.{column.Name}: a column added later cannot have keys, indexes or checks yet";
            }
        }

        foreach (var index in table.Indexes.Where(i => i.AddedIn is not null && (table.AddedIn is not null || i.Where is not null)))
        {
            yield return $"{table}: index {index.Name} added later must be a plain index on an existing table";
        }

        // A numbered column is filled by the numbering service on create (S1.08), so it needs no default.
        foreach (var column in table.Columns.Where(c => c.ApiReadOnly && c.Required && c.Default is null && c.Name != "status" && !table.Numbering.Exists(n => n.Column == c.Name)))
        {
            yield return $"{table}.{column.Name}: api_read_only on a required column needs a database default";
        }
    }

    /// <summary>
    /// Audit keys (slice S1.07 Q4, CR-026): append-only tables and tables without a uuid key must say <c>audit: false</c>
    /// with a reason, so the list of tables the change log skips is visible and reviewed; personal and restricted fields
    /// need a person (directly or through an employment) whose key encrypts them (R-21).
    /// </summary>
    private static IEnumerable<string> AuditProblems(Table table)
    {
        if (table.Database != DatabaseKind.Tenant)
        {
            if (!table.Audited || table.AuditReason is not null)
            {
                yield return $"{table}: audit keys apply to customer-database tables only";
            }

            yield break;
        }

        if (!table.Audited)
        {
            yield break;
        }

        if (table.IsAppendOnly)
        {
            yield return $"{table}: append-only table must declare audit: false with audit_reason (the row itself is the record, S1.07 Q4)";
        }

        if (table.AuditKeyColumn is null)
        {
            yield return $"{table}: no uuid key column for the change log's entity_id; declare audit: false with audit_reason";
        }

        foreach (var column in table.Columns.Where(c => c.FieldClass is "personal" or "restricted"))
        {
            if (!table.ScopeLocators.ContainsKey("person") && !table.ScopeLocators.ContainsKey("employment"))
            {
                yield return $"{table}.{column.Name}: field class {column.FieldClass} needs a person or employment scope locator (the data subject whose key encrypts it, R-21)";
            }
        }
    }
}
