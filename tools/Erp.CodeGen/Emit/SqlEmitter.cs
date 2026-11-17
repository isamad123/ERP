using System.Globalization;
using System.Text;
using Erp.CodeGen.Design;

namespace Erp.CodeGen.Emit;

/// <summary>
/// SQL-first migrations (spec 03 section 9). One file per schema for the release, named
/// V{yyyymmddHHmm}__{module}__{description}.sql. Every statement is idempotent, so applying a file twice changes nothing.
/// </summary>
public static class SqlEmitter
{
    /// <summary>SQLSTATE raised by append-only guards; class "ER" is not used by PostgreSQL.</summary>
    public const string AppendOnlySqlState = "ER001";

    /// <summary>SQLSTATE raised by a placeholder for a rule a later slice implements.</summary>
    public const string StubSqlState = "ER900";

    public const string KernelMigrations = "src/Platform/Erp.Kernel.Infrastructure/Migrations";
    public const string HrmMigrations = "src/Modules/Hrm/Erp.Hrm.Infrastructure/Migrations";
    public const string ControlPlaneMigrations = "src/Platform/Erp.Kernel.Infrastructure/ControlPlane/Migrations";

    public static void Emit(DesignModel model, OutputSet output)
    {
        var s = model.Settings;
        var kernelTables = model.Tables.Where(t => t.Module == "kernel").ToList();
        var minute = 0;
        output.Add($"{KernelMigrations}/{FileName(s, minute++, "kernel", "extensions")}", Extensions(s.KernelSchemas, model, "kernel"));
        foreach (var schema in s.KernelSchemas)
        {
            output.Add($"{KernelMigrations}/{FileName(s, minute++, "kernel", schema)}", SchemaFile(model, schema, crossSchemaForeignKeys: false));
        }

        output.Add($"{KernelMigrations}/{FileName(s, 59, "kernel", "foreign_keys")}", CrossSchemaForeignKeys(kernelTables));

        var hrmMinute = 60;
        foreach (var schema in s.HrmSchemas)
        {
            output.Add($"{HrmMigrations}/{FileName(s, hrmMinute++, "hrm", schema)}",
                Extensions(s.HrmSchemas, model, "hrm") + SchemaFile(model, schema, crossSchemaForeignKeys: true));
        }

        // Slice S1.03 onwards. Files above are frozen once merged (CLAUDE.md "Migrations"); later changes are new files.
        output.Add($"{HrmMigrations}/{FileName(s, hrmMinute, "hrm", "grants")}", Grants(model, s.HrmSchemas, "hrm"));
        output.Add($"{KernelMigrations}/{FileName(s, 120, "kernel", "grants")}", Grants(model, s.KernelSchemas, "kernel"));
        foreach (var slice in model.ImplementedStubs.Where(st => model.Table(st.Table).Module == "kernel").GroupBy(st => st.Implementation!.Slice).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sequence = 121 + RuleSliceOrder(slice.Key);
            output.Add($"{KernelMigrations}/{FileName(s, sequence, "kernel", "rules_" + slice.Key.Replace(".", "_", StringComparison.Ordinal).ToLowerInvariant())}", RuleImplementations(model, slice.Key, slice));
        }

        // Design changes after the first migrations (data design key added_in): one new file per slice, never an edit.
        foreach (var slice in kernelTables.SelectMany(t => t.ChangedIn).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            // A follow-up revision (S1.05-r1) gets its own file one hour later in the sequence, because the slice's
            // own design file is frozen once merged: S1.05 → 0302, S1.05-r1 → 0402.
            var revision = slice.Contains("-r", StringComparison.Ordinal) ? int.Parse(slice[^1..], CultureInfo.InvariantCulture) : 0;
            var sequence = 180 + (60 * revision) + RuleSliceOrder(slice.Split('-')[0]);
            output.Add($"{KernelMigrations}/{FileName(s, sequence, "kernel", "design_" + slice.Replace(".", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant())}", DesignChanges(model, slice, "kernel"));
        }

        // Database functions written by a slice (rules.yaml `functions`, S1.07): after that slice's design file.
        foreach (var group in model.Functions.GroupBy(f => f.Slice).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // A revision (S1.07-r1) gets its own file one hour later, like design revisions: S1.07 → 0334, S1.07-r1 → 0434.
            var revision = group.Key.Contains("-r", StringComparison.Ordinal) ? int.Parse(group.Key[^1..], CultureInfo.InvariantCulture) : 0;
            var sequence = 180 + (60 * revision) + 30 + RuleSliceOrder(group.Key.Split('-')[0]);
            output.Add($"{KernelMigrations}/{FileName(s, sequence, "kernel", "functions_" + group.Key.Replace(".", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant())}", Functions(group.Key, group));
        }

        if (model.ImplementedStubs.Any(st => model.Table(st.Table).Module != "kernel"))
        {
            throw new DesignException("rules.yaml", "implemented rules outside the kernel need their module's rules migration (not built yet)");
        }

        foreach (var schema in s.ControlPlaneSchemas)
        {
            output.Add($"{ControlPlaneMigrations}/{FileName(s, 0, "controlplane", schema)}",
                Extensions([schema], model, "controlplane") + SchemaFile(model, schema, crossSchemaForeignKeys: true));
        }

        // Control-plane tables added after the first migration (S1.07, CR-027): one new file per slice.
        foreach (var slice in model.Tables.Where(t => t.Module == "controlplane").SelectMany(t => t.ChangedIn).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var sequence = 180 + RuleSliceOrder(slice.Split('-')[0]);
            output.Add($"{ControlPlaneMigrations}/{FileName(s, sequence, "controlplane", "design_" + slice.Replace(".", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant())}", DesignChanges(model, slice, "controlplane"));
        }
    }

    /// <summary>
    /// Stable position of a slice's rules file after the grants file: S1.03 → 0, S1.10 → 7. A slice built in parts
    /// (S1.09a, S1.09b; slice S1.09 ruling Q0) shares its slice's position: the part letter in the file description keeps
    /// the names distinct and in order (the migrator sorts and records files by name).
    /// </summary>
    private static int RuleSliceOrder(string slice) =>
        int.Parse(slice[(slice.IndexOf('.', StringComparison.Ordinal) + 1)..].TrimEnd('a', 'b', 'c', 'd'), CultureInfo.InvariantCulture) - 3;

    /// <summary>The part letter of a slice built in parts (S1.09a → 1, S1.09b → 2); 0 for a slice built in one go.</summary>
    private static int SlicePart(string slice) =>
        slice.Split('-')[0][^1] is var last && char.IsAsciiLetterLower(last) ? last - 'a' + 1 : 0;

    /// <summary>Database roles of spec 03 section 3.7 used by the customer database.</summary>
    public static readonly IReadOnlyList<string> GroupRoles = ["app_rw", "app_ro", "app_audit", "backup"];

    /// <summary>
    /// Privileges of the group roles (spec 03 section 3.7 and 3.6). The migrator's login owns the objects; app_rw reads and
    /// writes business tables but cannot update or delete append-only rows (the trigger is the second barrier) and only
    /// reads audit tables; app_audit only inserts into audit tables; app_ro and backup only read. Per-customer logins
    /// are members of these roles and can connect only to their own database (CR-004).
    /// </summary>
    private static string Grants(DesignModel model, IEnumerable<string> schemas, string module)
    {
        var b = new StringBuilder(Header($"Privileges of the database roles on the {module} tables (spec 03 section 3.7; CR-004)"));
        b.Append("-- Group roles are created here only where the migrating login may create roles (a fresh server or a test);\n");
        b.Append("-- provisioning creates them before the first migration, so a customer's owner login never needs that right.\n");
        b.Append("do $do$ begin\n");
        foreach (var role in GroupRoles)
        {
            b.Append(CultureInfo.InvariantCulture, $"    if not exists (select 1 from pg_roles where rolname = '{role}') then create role {role} nologin; end if;\n");
        }

        b.Append("end $do$;\n\n");
        foreach (var schema in schemas)
        {
            var tables = model.Tables.Where(t => t.Schema == schema && t.Database == DatabaseKind.Tenant && t.AddedIn is null).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            if (tables.Count == 0)
            {
                continue;
            }

            b.Append(CultureInfo.InvariantCulture, $"grant usage on schema {schema} to {string.Join(", ", GroupRoles)};\n");
            foreach (var table in tables)
            {
                b.Append(TableGrants(table, table.BaseAppendOnlyUpdatable.ToList(), auditInsert: true));
            }

            b.Append(CultureInfo.InvariantCulture, $"grant select on all tables in schema {schema} to app_ro, backup;\n");
            b.Append(CultureInfo.InvariantCulture, $"grant usage, select on all sequences in schema {schema} to app_rw, app_ro, backup;\n\n");
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    /// <remarks>
    /// <paramref name="auditInsert"/>: the frozen S1.03 grants file gave app_audit INSERT on audit tables; CR-031 (S1.07-r1)
    /// took it away, so audit tables added later are written only through the audit functions.
    /// </remarks>
    private static string TableGrants(Table table, List<string> updatable, bool auditInsert = false)
    {
        if (table.Schema == "aud")
        {
            return auditInsert ? $"grant select on {table.FullName} to app_rw;\ngrant insert on {table.FullName} to app_audit;\n" : $"grant select on {table.FullName} to app_rw;\n";
        }

        if (!table.IsAppendOnly)
        {
            return $"grant select, insert, update, delete on {table.FullName} to app_rw;\n";
        }

        var b = new StringBuilder($"grant select, insert on {table.FullName} to app_rw;\n");
        if (updatable.Count > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $"grant update ({string.Join(", ", updatable)}) on {table.FullName} to app_rw;\n");
        }

        return b.ToString();
    }

    private static string UniqueIndex(Table table, UniqueKey unique, string? condition)
    {
        var nulls = unique.NullsNotDistinct ? " nulls not distinct" : string.Empty;
        var where = condition is null ? string.Empty : $" where ({condition})";
        return $"create unique index if not exists {unique.Name} on {table.FullName} ({string.Join(", ", unique.Items)}){nulls}{where};\n";
    }

    /// <summary>
    /// Design changes of one slice after the release's first migrations were merged (data design key <c>added_in</c>,
    /// CR-010): new tables with their keys, foreign keys, grants and comments; new nullable columns; more columns an
    /// append-only table may update. Expand only, idempotent, and the frozen files stay byte-for-byte unchanged.
    /// </summary>
    private static string DesignChanges(DesignModel model, string slice, string module)
    {
        var tables = model.Tables.Where(t => t.Module == module && t.ChangedIn.Contains(slice)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        var added = tables.Where(t => t.AddedIn == slice).ToList();
        var b = new StringBuilder(Header($"Data design changes of slice {slice} (expand only; earlier migrations are frozen)"));
        foreach (var schema in added.Where(t => t.IsAppendOnly).Select(t => t.Schema).Distinct(StringComparer.Ordinal))
        {
            b.Append(AppendOnlyFunction(schema));
        }

        foreach (var table in added)
        {
            b.Append(CreateTable(table, model.Settings, baseOnly: false));
        }

        foreach (var table in tables)
        {
            foreach (var column in table.Columns.Where(c => c.AddedIn == slice))
            {
                var required = column.Required ? $" not null default {column.Default}" : string.Empty;
                b.Append(CultureInfo.InvariantCulture, $"alter table {table.FullName} add column if not exists {column.Name} {column.SqlType}{required};\n");
            }

            if (table.AddedIn == slice)
            {
                continue;
            }

            foreach (var check in table.Checks.Where(c => c.AddedIn == slice))
            {
                b.Append(CultureInfo.InvariantCulture, $"do $do$ begin\n    if not exists (select 1 from pg_constraint where conname = {Naming.SqlString(check.Name)} and conrelid = {Naming.SqlString(table.FullName)}::regclass) then\n");
                b.Append(CultureInfo.InvariantCulture, $"        alter table {table.FullName} add constraint {check.Name} check ({check.Sql});\n    end if;\nend $do$;\n");
            }

            // Required columns made optional (optional_added_in, S1.08): expand only.
            foreach (var column in table.Columns.Where(c => c.OptionalIn == slice))
            {
                b.Append(CultureInfo.InvariantCulture, $"alter table {table.FullName} alter column {column.Name} drop not null;\n");
            }

            // Enum values added (enum_added_in, S1.08): the check is replaced by the wider one; existing rows stay valid.
            foreach (var check in table.Checks.Where(c => c.ReplacedIn == slice))
            {
                b.Append(CultureInfo.InvariantCulture, $"alter table {table.FullName} drop constraint if exists {check.Name};\n");
                b.Append(CultureInfo.InvariantCulture, $"alter table {table.FullName} add constraint {check.Name} check ({check.Sql});\n");
            }

            // Unique keys added (uniques_added_in, S1.08); a partial one is a unique index (S1.09b).
            foreach (var unique in table.Uniques.Where(u => u.AddedIn == slice && !u.IsPlain))
            {
                b.Append(UniqueIndex(table, unique, unique.Where));
            }

            // Partial unique keys with a new condition (uniques_replaced_in, S1.09b): dropped and recreated in the same file.
            foreach (var unique in table.Uniques.Where(u => u.ReplacedIn == slice))
            {
                b.Append(CultureInfo.InvariantCulture, $"drop index if exists {table.Schema}.{unique.Name};\n");
                b.Append(UniqueIndex(table, unique, unique.Where));
            }

            foreach (var unique in table.Uniques.Where(u => u.AddedIn == slice && u.IsPlain))
            {
                var nulls = unique.NullsNotDistinct ? " nulls not distinct" : string.Empty;
                b.Append(CultureInfo.InvariantCulture, $"do $do$ begin\n    if not exists (select 1 from pg_constraint where conname = {Naming.SqlString(unique.Name)} and conrelid = {Naming.SqlString(table.FullName)}::regclass) then\n");
                b.Append(CultureInfo.InvariantCulture, $"        alter table {table.FullName} add constraint {unique.Name} unique{nulls} ({string.Join(", ", unique.Items)});\n    end if;\nend $do$;\n");
            }

            foreach (var index in table.Indexes.Where(i => i.AddedIn == slice))
            {
                b.Append(CultureInfo.InvariantCulture, $"create index if not exists {index.Name} on {table.FullName} ({string.Join(", ", index.Items)}); -- {index.Purpose}\n");
            }
        }

        foreach (var table in added)
        {
            foreach (var fk in table.ForeignKeys)
            {
                b.Append(AddForeignKey(table, fk));
            }
        }

        foreach (var table in tables.Where(t => t.AddedIn != slice && t.UpdatableAddedIn.Exists(u => u.Slice == slice)))
        {
            var asOf = table.AppendOnlyUpdatable.Where(c => table.UpdatableAddedIn.Find(u => u.Column == c) is not { Slice: { } later } || UpTo(later, slice));
            var args = string.Join(", ", asOf.Select(Naming.SqlString));
            b.Append(CultureInfo.InvariantCulture, $"create or replace trigger tg_{table.Name}_append_only before update or delete on {table.FullName} for each row execute function {table.Schema}.tg_refuse_change({args});\n");
            b.Append(CultureInfo.InvariantCulture, $"grant update ({string.Join(", ", table.UpdatableAddedIn.Where(u => u.Slice == slice).Select(u => u.Column))}) on {table.FullName} to app_rw;\n");
        }

        foreach (var schema in added.Where(_ => module != "controlplane").Select(t => t.Schema).Distinct(StringComparer.Ordinal))
        {
            foreach (var table in added.Where(t => t.Schema == schema))
            {
                b.Append(TableGrants(table, table.AppendOnlyUpdatable));
            }

            b.Append(CultureInfo.InvariantCulture, $"grant select on all tables in schema {schema} to app_ro, backup;\n");
        }

        b.Append('\n');
        foreach (var table in tables)
        {
            b.Append(Comments(table, baseOnly: false, asOfSlice: slice));
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// The real rules that replace placeholders (rules.yaml <c>implemented</c>), in a new migration of the owning slice:
    /// the placeholder function is dropped, the rule's SQL is applied and the table comment no longer says "pending".
    /// </summary>
    private static string RuleImplementations(DesignModel model, string slice, IEnumerable<StubDef> stubs)
    {
        var b = new StringBuilder(Header($"Rules implemented by slice {slice} (replacing placeholders of docs/registers/todo-stubs.md)"));
        foreach (var stub in stubs.OrderBy(st => st.Table, StringComparer.Ordinal).ThenBy(st => st.Name, StringComparer.Ordinal))
        {
            var table = model.Table(stub.Table);
            b.Append(CultureInfo.InvariantCulture, $"-- {stub.Table}: {stub.Rule}\n");
            b.Append(CultureInfo.InvariantCulture, $"drop function if exists {table.Schema}.{stub.FunctionName}();\n");
            b.Append(stub.Implementation!.Sql.Replace("\r", string.Empty, StringComparison.Ordinal).TrimEnd('\n'));
            b.Append('\n');
            b.Append(Comments(table, c => c.StartsWith($"Pending rule ({stub.Owner}, placeholder {table.Schema}.{stub.FunctionName}): ", StringComparison.Ordinal)
                ? $"Rule (implemented in {slice}): {stub.Rule}"
                : c));
            b.Append('\n');
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    private static string FileName(Settings s, int sequence, string module, string description) =>
        Naming.Invariant($"V{s.MigrationDate}{sequence / 60:00}{sequence % 60:00}__{module}__{description}.sql");

    private static string Header(string title) =>
        "-- <auto-generated>\n" +
        "-- Generated by tools/Erp.CodeGen from docs/data-design/*.yaml (slice S1.02). Do not edit by hand:\n" +
        "-- change the data design through a change request (D-39) and run the generator.\n" +
        "-- </auto-generated>\n" +
        $"-- {title}\n\n";

    /// <summary>
    /// Database functions a slice writes (rules.yaml <c>functions</c>, slice S1.07): reviewed SQL kept with the generator
    /// so every migration file stays generated. One file per slice, after that slice's design file.
    /// </summary>
    private static string Functions(string slice, IEnumerable<FunctionDef> functions)
    {
        var b = new StringBuilder(Header($"Database functions of slice {slice} (tools/Erp.CodeGen/rules.yaml, functions)"));
        foreach (var function in functions)
        {
            b.Append(CultureInfo.InvariantCulture, $"-- {function.Name}: {function.Purpose}\n");
            b.Append(function.Sql.Replace("\r", string.Empty, StringComparison.Ordinal).TrimEnd('\n'));
            b.Append("\n\n");
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    private static string Extensions(IEnumerable<string> schemas, DesignModel model, string module)
    {
        var b = new StringBuilder();
        if (module == "kernel")
        {
            b.Append(Header("Extensions used by the kernel (customer database)"));
        }

        var needsGist = model.Tables.Where(t => t.Module == module).Any(t => t.Exclusions.Count > 0);
        if (needsGist)
        {
            b.Append("create extension if not exists btree_gist;\n");
        }

        if (module != "kernel")
        {
            foreach (var schema in schemas)
            {
                b.Append(CultureInfo.InvariantCulture, $"create schema if not exists {schema};\n");
            }
        }

        b.Append('\n');
        return module == "kernel" ? b.ToString() : Header($"Schema {string.Join(", ", schemas)} (release {model.Settings.Release})") + b;
    }

    private static string SchemaFile(DesignModel model, string schema, bool crossSchemaForeignKeys)
    {
        var tables = model.Tables.Where(t => t.Schema == schema && t.AddedIn is null).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var b = new StringBuilder();
        if (model.Settings.KernelSchemas.Contains(schema))
        {
            b.Append(Header($"Schema {schema} (release {model.Settings.Release}): {tables.Count} tables"));
            b.Append(CultureInfo.InvariantCulture, $"create schema if not exists {schema};\n\n");
        }

        if (tables.Exists(t => t.IsAppendOnly))
        {
            b.Append(AppendOnlyFunction(schema));
        }

        foreach (var table in tables)
        {
            b.Append(CreateTable(table, model.Settings, baseOnly: true));
        }

        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeys.Where(f => crossSchemaForeignKeys || !f.CrossesSchema(schema)))
            {
                b.Append(AddForeignKey(table, fk));
            }
        }

        foreach (var stub in tables.SelectMany(t => t.Stubs))
        {
            b.Append(Stub(schema, stub));
        }

        foreach (var table in tables)
        {
            b.Append(Comments(table));
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    private static string CrossSchemaForeignKeys(IReadOnlyList<Table> tables)
    {
        var b = new StringBuilder(Header("Foreign keys between kernel schemas, added after every kernel table exists"));
        foreach (var table in tables.Where(t => t.AddedIn is null).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var fk in table.ForeignKeys.Where(f => f.CrossesSchema(table.Schema)))
            {
                b.Append(AddForeignKey(table, fk));
            }
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    private static string AppendOnlyFunction(string schema) =>
        $"""
        -- Append-only guard (README rule 7, spec 03 section 3.6): refuses UPDATE, DELETE and TRUNCATE. Trigger arguments
        -- name columns a documented service may still update (for example iam.sessions.last_seen_at).
        create or replace function {schema}.tg_refuse_change() returns trigger
        language plpgsql as $fn$
        begin
            if tg_op = 'UPDATE' and tg_nargs > 0 and (to_jsonb(new) - tg_argv) = (to_jsonb(old) - tg_argv) then
                return new;
            end if;
            raise exception 'append-only table %.%: % is not allowed', tg_table_schema, tg_table_name, tg_op
                using errcode = '{AppendOnlySqlState}';
        end
        $fn$;


        """.Replace("\r", string.Empty, StringComparison.Ordinal);

    // baseOnly: true for the frozen first migration; columns and exceptions added later are left out.
    private static string CreateTable(Table table, Settings settings, bool baseOnly)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"-- {table.FullName}: {table.Purpose ?? Naming.Pascal(table.Name)}");
        if (table.Requirements.Count > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $" [{string.Join(", ", table.Requirements)}]");
        }

        b.Append(CultureInfo.InvariantCulture, $"\ncreate table if not exists {table.FullName} (\n");
        var lines = new List<string>();
        foreach (var column in table.Columns.Where(c => !baseOnly || c.AddedIn is null))
        {
            var line = new StringBuilder($"    {column.Name} {column.SqlType}");
            if (column.GeneratedExpression is not null)
            {
                line.Append(CultureInfo.InvariantCulture, $" generated always as ({column.GeneratedExpression}) stored");
            }

            if (column.Required || (baseOnly && column.OptionalIn is not null))
            {
                line.Append(" not null");
            }

            if (column.Default is not null)
            {
                line.Append(CultureInfo.InvariantCulture, $" default {column.Default}");
            }

            lines.Add(line.ToString());
        }

        lines.Add($"    constraint pk_{table.Name} primary key ({string.Join(", ", table.PrimaryKey)})");
        foreach (var unique in table.Uniques.Where(u => u.IsPlain && (!baseOnly || u.AddedIn is null)))
        {
            var nulls = unique.NullsNotDistinct ? " nulls not distinct" : string.Empty;
            lines.Add($"    constraint {unique.Name} unique{nulls} ({string.Join(", ", unique.Items)})");
        }

        foreach (var check in table.Checks.Where(c => !baseOnly || c.AddedIn is null))
        {
            lines.Add($"    constraint {check.Name} check ({(baseOnly && check.BaseSql is not null ? check.BaseSql : check.Sql)})");
        }

        foreach (var exclusion in table.Exclusions)
        {
            var where = exclusion.Where is null ? string.Empty : $" where ({exclusion.Where})";
            lines.Add($"    constraint {exclusion.Name} exclude using gist ({string.Join(", ", exclusion.Elements)}){where}");
        }

        b.Append(string.Join(",\n", lines));
        b.Append(table.Partition is { } p ? $"\n) partition by range ({p.Column});\n" : "\n);\n");

        if (table.Partition is { } partition)
        {
            b.Append(Partitions(table, partition, settings));
        }

        foreach (var unique in table.Uniques.Where(u => !u.IsPlain && (!baseOnly || u.AddedIn is null)))
        {
            b.Append(UniqueIndex(table, unique, baseOnly && unique.ReplacedIn is not null ? unique.BaseWhere : unique.Where));
        }

        foreach (var index in table.Indexes.Where(i => !baseOnly || i.AddedIn is null))
        {
            var where = index.Where is null ? string.Empty : $" where ({index.Where})";
            b.Append(CultureInfo.InvariantCulture, $"create index if not exists {index.Name} on {table.FullName} ({string.Join(", ", index.Items)}){where}; -- {index.Purpose}\n");
        }

        if (table.IsAppendOnly)
        {
            var args = string.Join(", ", (baseOnly ? table.BaseAppendOnlyUpdatable : table.AppendOnlyUpdatable).Select(Naming.SqlString));
            b.Append(CultureInfo.InvariantCulture, $"create or replace trigger tg_{table.Name}_append_only before update or delete on {table.FullName} for each row execute function {table.Schema}.tg_refuse_change({args});\n");
            b.Append(CultureInfo.InvariantCulture, $"create or replace trigger tg_{table.Name}_no_truncate before truncate on {table.FullName} for each statement execute function {table.Schema}.tg_refuse_change();\n");
        }

        b.Append('\n');
        return b.ToString();
    }

    private static string Partitions(Table table, PartitionDef partition, Settings settings)
    {
        var b = new StringBuilder();
        for (var month = settings.PartitionsFrom; month <= settings.PartitionsTo; month = month.AddMonths(1))
        {
            var next = month.AddMonths(1);
            b.Append(Naming.Invariant($"create table if not exists {table.FullName}_p{month:yyyy_MM} partition of {table.FullName} for values from ({Bound(month, partition)}) to ({Bound(next, partition)});\n"));
        }

        b.Append(CultureInfo.InvariantCulture, $"create table if not exists {table.FullName}_default partition of {table.FullName} default;\n");
        return b.ToString();
    }

    private static string Bound(DateOnly month, PartitionDef partition) =>
        partition.ColumnKind is ColumnKind.Timestamp
            ? Naming.SqlString(month.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " 00:00:00+00")
            : Naming.SqlString(month.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private static string AddForeignKey(Table table, ForeignKey fk) =>
        $"""
        do $do$ begin
            if not exists (select 1 from pg_constraint where conname = '{fk.Name}' and conrelid = '{table.FullName}'::regclass) then
                alter table {table.FullName} add constraint {fk.Name} foreign key ({string.Join(", ", fk.Columns)}) references {fk.TargetTable} ({string.Join(", ", fk.TargetColumns)});
            end if;
        end $do$;

        """.Replace("\r", string.Empty, StringComparison.Ordinal);

    private static string Stub(string schema, StubDef stub) =>
        $"""
        -- Placeholder for a rule owned by {stub.Owner} (docs/registers/todo-stubs.md). It enforces nothing yet.
        create or replace function {schema}.{stub.FunctionName}() returns void
        language plpgsql as $fn$
        begin
            raise exception 'not implemented: rule {stub.Name} on {stub.Table} (owner {stub.Owner})' using errcode = '{StubSqlState}';
        end
        $fn$;
        comment on function {schema}.{stub.FunctionName}() is {Naming.SqlString($"Pending rule for {stub.Table}, owned by {stub.Owner}: {stub.Rule}")};


        """.Replace("\r", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="item"/> (a slice id, optionally with a revision such as S1.05-r1) comes no later than
    /// <paramref name="slice"/>, so a design file describes the database exactly as of its own slice.
    /// </summary>
    private static bool UpTo(string item, string slice)
    {
        static (int Order, int Revision) Key(string id) =>
            (RuleSliceOrder(id.Split('-')[0]) * 10 + SlicePart(id), id.Contains("-r", StringComparison.Ordinal) ? int.Parse(id[^1..], CultureInfo.InvariantCulture) : 0);
        return Key(item).CompareTo(Key(slice)) <= 0;
    }

    private static string Comments(Table table, Func<string, string>? rewrite = null, bool baseOnly = true, string? asOfSlice = null)
    {
        var b = new StringBuilder();
        var text = new List<string>();
        if (table.Purpose is not null)
        {
            text.Add(table.Purpose.TrimEnd('.') + ".");
        }

        if (table.Requirements.Count > 0)
        {
            text.Add("Requirements: " + string.Join(", ", table.Requirements) + ".");
        }

        text.AddRange(rewrite is null ? table.Comments : table.Comments.Select(rewrite));
        if (!baseOnly)
        {
            text.AddRange(table.CommentsAddedIn.Where(c => asOfSlice is null || UpTo(c.Slice, asOfSlice)).Select(c => c.Text));
        }

        text.Add("Source: " + table.SourceFile + ".");
        b.Append(CultureInfo.InvariantCulture, $"comment on table {table.FullName} is {Naming.SqlString(string.Join(" ", text))};\n");
        foreach (var column in table.Columns.Where(c => c.Comment is not null && (baseOnly ? c.AddedIn is null : c.AddedIn is null || asOfSlice is null || UpTo(c.AddedIn, asOfSlice))))
        {
            b.Append(CultureInfo.InvariantCulture, $"comment on column {table.FullName}.{column.Name} is {Naming.SqlString(column.Comment!)};\n");
        }

        return b.ToString();
    }
}
