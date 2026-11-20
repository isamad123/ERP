using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Infrastructure.Migrations;
using Erp.CodeGen;
using Erp.CodeGen.Dictionary;
using Erp.CodeGen.Emit;
using Erp.IntegrationTests.Infrastructure;
using Npgsql;

namespace Erp.IntegrationTests.DataDesign;

/// <summary>
/// Slice S1.02 acceptance: all generated migrations apply to empty databases on PostgreSQL 16, every table holds
/// exactly the keys, constraints, indexes and triggers of the approved data design (D-50), and the guards work.
/// </summary>
[Collection(MigratedDatabaseGroup.Name)]
public sealed partial class DataDesignTests(MigratedDatabases db)
{
    public static TheoryData<string> AllTables => [.. MigratedDatabases.Catalog.Tables.Select(t => t.Database + ":" + t.FullName)];

    public static TheoryData<string> AppendOnlyTables => [.. MigratedDatabases.Catalog.Tables.Where(t => t.Traits.Contains("app")).Select(t => t.Database + ":" + t.FullName)];

    public static TheoryData<string> EffectiveDatedTables => [.. MigratedDatabases.Catalog.Tables.Where(t => t.Traits.Contains("eff")).Select(t => t.Database + ":" + t.FullName)];

    private static ExpectedTable Find(string key) => MigratedDatabases.Catalog.Tables.Single(t => t.Database + ":" + t.FullName == key);

    [Fact]
    public void D50_catalogue_covers_all_204_design_tables_plus_outbox_and_inbox_per_schema()
    {
        var design = MigratedDatabases.Catalog.Tables.Where(t => t.Traits.Count > 0 || t.Name is not ("outbox_messages" or "inbox_messages")).ToList();
        // 204 tables approved by D-50 plus the two of CR-010 (slice S1.05), the one of CR-019 (slice S1.06), the
        // control-plane table of CR-027 (slice S1.07), the change counters of CR-036 (slice S1.09a) and the control-plane
        // migration history of CR-054 (slice S1.11f2, CR-006).
        Assert.Equal(210, design.Count);
        Assert.Contains(design, t => t.Schema == "cp" && t.Name == "schema_history");
        Assert.Contains(design, t => t.Schema == "cfg" && t.Name == "config_versions");
        Assert.Contains(design, t => t.Schema == "cp" && t.Name == "audit_anchors");
        Assert.Contains(design, t => t.Schema == "iam" && t.Name == "one_time_tokens");
        Assert.Contains(design, t => t.Schema == "iam" && t.Name == "mfa_recovery_codes");
        Assert.Contains(design, t => t.Schema == "iam" && t.Name == "user_group_roles");
        Assert.Equal(15, design.Count(t => t.Database == "control_plane"));
        Assert.Equal(24, MigratedDatabases.Catalog.Tables.Count(t => t.Name is "outbox_messages" or "inbox_messages"));
    }

    [Fact]
    public async Task D50_migrations_create_every_table_on_an_empty_database()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var database in new[] { "tenant", "control_plane" })
        {
            var live = (await db.SnapshotAsync(database, ct)).Tables.Select(t => t.FullName).Order(StringComparer.Ordinal);
            var expected = MigratedDatabases.Catalog.Tables.Where(t => t.Database == database).Select(t => t.FullName).Order(StringComparer.Ordinal);
            Assert.Equal(expected, live);
        }
    }

    [Fact]
    public async Task D50_applying_the_migrations_again_changes_nothing()
    {
        // No earlier release exists yet, so the "previous release" case is a database already at release 1.0.
        var ct = TestContext.Current.CancellationToken;
        var before = await db.SnapshotAsync("tenant", ct);
        await SqlScriptRunner.ApplyAsync(db.TenantConnectionString, MigratedDatabases.TenantScripts, ct);
        var after = await db.SnapshotAsync("tenant", ct);
        Assert.Equal(DictionaryWriterText(before), DictionaryWriterText(after));

        var cpBefore = await db.SnapshotAsync("control_plane", ct);
        await SqlScriptRunner.ApplyAsync(db.ControlPlaneConnectionString, MigratedDatabases.ControlPlaneScripts, ct);
        Assert.Equal(DictionaryWriterText(cpBefore), DictionaryWriterText(await db.SnapshotAsync("control_plane", ct)));
    }

    private static string DictionaryWriterText(CatalogSnapshot snapshot) =>
        DictionaryWriter.Render(Generator.LoadModel(MigratedDatabases.Root), snapshot, snapshot);

    [Theory]
    [MemberData(nameof(AllTables))]
    public async Task D50_table_matches_the_data_design(string key)
    {
        var expected = Find(key);
        var snapshot = await db.SnapshotAsync(expected.Database, TestContext.Current.CancellationToken);
        var live = snapshot.Tables.SingleOrDefault(t => t.FullName == expected.FullName);
        Assert.NotNull(live);

        Assert.Equal(expected.Columns.Select(c => (c.Name, c.Type, c.NotNull)), live.Columns.Select(c => (c.Name, c.Type, c.NotNull)));
        Assert.Equal(expected.PartitionColumn is not null, live.IsPartitioned);

        var expectedNames = new[] { "pk_" + expected.Name }
            .Concat(expected.Uniques).Concat(expected.Checks).Concat(expected.Exclusions)
            .Concat(expected.ForeignKeys.Select(f => f.Name)).Concat(expected.Indexes)
            .Order(StringComparer.Ordinal);
        var liveNames = live.Constraints.Select(c => c.Name).Concat(live.Indexes.Select(i => i.Name)).Order(StringComparer.Ordinal);
        Assert.Equal(expectedNames, liveNames);

        var pk = Assert.Single(live.Constraints, c => c.Kind == 'p');
        Assert.Equal($"PRIMARY KEY ({string.Join(", ", expected.PrimaryKey)})", pk.Definition);
        foreach (var fk in expected.ForeignKeys)
        {
            var definition = Assert.Single(live.Constraints, c => c.Name == fk.Name).Definition;
            Assert.Equal($"FOREIGN KEY ({string.Join(", ", fk.Columns)}) REFERENCES {fk.Target}({string.Join(", ", fk.TargetColumns)})", definition);
        }

        Assert.Equal(expected.Triggers.Order(StringComparer.Ordinal), live.Triggers.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllTables))]
    public async Task D50_table_accepts_a_minimal_valid_row(string key)
    {
        var table = Find(key);
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.OpenAsync(table.Database, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rows = new RowFactory(connection, transaction, MigratedDatabases.Catalog);

        var values = await rows.InsertAsync(table, null, ct);

        Assert.NotEmpty(values);
        await transaction.RollbackAsync(ct);
    }

    [Theory]
    [MemberData(nameof(AppendOnlyTables))]
    [Trait("Invariant", "3")]
    public async Task README_rule7_append_only_table_refuses_update_and_delete(string key)
    {
        var table = Find(key);
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.OpenAsync(table.Database, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var values = await new RowFactory(connection, transaction, MigratedDatabases.Catalog).InsertAsync(table, null, ct);
        var id = values["id"];

        await AssertRefusedAsync(connection, transaction, $"update {table.FullName} set created_by = gen_random_uuid() where id = @id", id, ct);
        await AssertRefusedAsync(connection, transaction, $"delete from {table.FullName} where id = @id", id, ct);
        await AssertRefusedAsync(connection, transaction, $"truncate {table.FullName} cascade", null, ct);

        foreach (var column in table.AppendOnlyUpdatable)
        {
            // The documented exception (iam.sessions): the session service may still update these columns.
            await ExecuteAsync(connection, transaction, $"update {table.FullName} set {column} = {column} where id = @id", id, ct);
        }

        await transaction.RollbackAsync(ct);
    }

    [Theory]
    [MemberData(nameof(EffectiveDatedTables))]
    [Trait("Invariant", "5")]
    public async Task R06_effective_dated_table_refuses_overlapping_periods(string key)
    {
        var table = Find(key);
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.OpenAsync(table.Database, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var factory = new RowFactory(connection, transaction, MigratedDatabases.Catalog);
        var overrides = ExclusionStatus.TryGetValue(table.FullName, out var status) ? new Dictionary<string, object?> { ["status"] = status } : null;
        var first = await factory.InsertAsync(table, overrides, ct);

        // Same business key and same open-ended period; everything else (id, codes, other references) fresh.
        var exclusionName = table.Exclusions.Single(e => e.StartsWith("ex_" + table.Name, StringComparison.Ordinal) && !ListedExclusions.Contains(e));
        var keyColumns = await ExclusionColumnsAsync(connection, table, exclusionName, ct);
        var copy = keyColumns.Append("valid_from").Append("valid_to").Append("company_id")
            .Distinct().Where(first.ContainsKey).ToDictionary(c => c, c => first[c], StringComparer.Ordinal);
        if (status is not null)
        {
            copy["status"] = status;
        }

        await using (var savepoint = connection.CreateCommand())
        {
            savepoint.Transaction = transaction;
            savepoint.CommandText = "savepoint overlap";
            await savepoint.ExecuteNonQueryAsync(ct);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => factory.InsertAsync(table, copy, ct));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, error.SqlState);
        Assert.Equal(exclusionName, error.ConstraintName);
        await transaction.RollbackAsync(ct);
    }

    /// <summary>Exclusions listed in table checks (not from effective dating), excluded from the overlap test above.</summary>
    private static readonly HashSet<string> ListedExclusions = ["ex_pay_periods_pay_group_id_dates", "ex_statutory_table_rows_table_id_key_bounds", "ex_pay_run_employees_employment_id_period", "ex_attendance_periods_pay_group_id_period"];

    /// <summary>hrm.policy_versions: the exclusion applies only where status &lt;&gt; 'draft' (rules.yaml), so use an active version.</summary>
    private static readonly Dictionary<string, string> ExclusionStatus = new(StringComparer.Ordinal) { ["hrm.policy_versions"] = "active" };

    /// <summary>Plain columns named in the exclusion constraint, plus the columns a generated key column is computed from.</summary>
    private static async Task<IReadOnlyList<string>> ExclusionColumnsAsync(NpgsqlConnection connection, ExpectedTable table, string exclusion, CancellationToken ct)
    {
        var snapshot = await CatalogReader.ReadAsync(connection, ct);
        var live = snapshot.Tables.Single(t => t.FullName == table.FullName);
        var definition = live.Constraints.Single(c => c.Name == exclusion).Definition;
        var names = new List<string>();
        foreach (var column in live.Columns)
        {
            if (!Regex.IsMatch(definition, $@"\b{column.Name}\b") || column.Name is "valid_from" or "valid_to")
            {
                continue;
            }

            if (column.Generated is { } expression)
            {
                names.AddRange(live.Columns.Where(c => c.Generated is null && Regex.IsMatch(expression, $@"\b{c.Name}\b")).Select(c => c.Name));
            }
            else
            {
                names.Add(column.Name);
            }
        }

        return names;
    }

    [Fact]
    [Trait("Invariant", "9")]
    public async Task Invariant9_every_table_has_a_primary_key_and_every_foreign_key_an_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var problems = new List<string>();
        foreach (var database in new[] { "tenant", "control_plane" })
        {
            await using var connection = await db.OpenAsync(database, ct);
            problems.AddRange(await QueryAsync(connection, """
                select n.nspname || '.' || c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace
                where c.relkind in ('r','p') and not c.relispartition and n.nspname not in ('pg_catalog','information_schema','public')
                  and not exists (select 1 from pg_constraint k where k.conrelid = c.oid and k.contype = 'p')
                """, ct));
            problems.AddRange(await QueryAsync(connection, """
                select k.conrelid::regclass::text || ' ' || k.conname from pg_constraint k join pg_class c on c.oid = k.conrelid
                where k.contype = 'f' and not c.relispartition
                  and not exists (
                    select 1 from pg_index i
                    where i.indrelid = k.conrelid and i.indpred is null
                      and (i.indkey::int2[])[0:array_length(k.conkey, 1) - 1] = k.conkey)
                """, ct));
        }

        Assert.True(problems.Count == 0, "Missing primary key or foreign-key index: " + string.Join(", ", problems));
    }

    [Fact]
    [Trait("Invariant", "8")]
    public async Task Invariant8_no_foreign_key_from_the_kernel_into_a_business_module()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.OpenAsync("tenant", ct);
        var crossing = await QueryAsync(connection, """
            select k.conrelid::regclass::text || ' -> ' || k.confrelid::regclass::text
            from pg_constraint k join pg_class c on c.oid = k.conrelid join pg_namespace n on n.oid = c.relnamespace
            join pg_class t on t.oid = k.confrelid join pg_namespace tn on tn.oid = t.relnamespace
            where k.contype = 'f' and n.nspname <> 'hrm' and tn.nspname = 'hrm'
            """, ct);
        Assert.Empty(crossing);
    }

    [Fact]
    public async Task D47_no_customer_database_table_has_a_tenant_id_column()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.OpenAsync("tenant", ct);
        Assert.Empty(await QueryAsync(connection, "select table_schema || '.' || table_name from information_schema.columns where column_name = 'tenant_id'", ct));
    }

    [Fact]
    public async Task S102_placeholders_in_the_database_match_the_register()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.OpenAsync("tenant", ct);
        var live = await QueryAsync(connection, "select n.nspname || '.' || p.proname from pg_proc p join pg_namespace n on n.oid = p.pronamespace where p.proname like 'todo\\_%' order by 1", ct);
        var expected = MigratedDatabases.Catalog.Stubs.Select(s => s.Schema + "." + s.Function).Order(StringComparer.Ordinal);
        Assert.Equal(expected, live);

        // A placeholder enforces nothing: calling it must fail loudly rather than pretend.
        var stub = MigratedDatabases.Catalog.Stubs[0];
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, null, $"select {stub.Schema}.{stub.Function}()", null, ct));
        Assert.Equal(SqlEmitter.StubSqlState, error.SqlState);
    }

    [Fact]
    public async Task S102_committed_data_dictionary_matches_the_live_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = DictionaryWriter.Render(Generator.LoadModel(MigratedDatabases.Root), await db.SnapshotAsync("tenant", ct), await db.SnapshotAsync("control_plane", ct));
        var committed = await File.ReadAllTextAsync(Path.Combine(MigratedDatabases.Root, DictionaryWriter.RelativePath), ct);
        Assert.True(text == committed, "docs/data-design/generated/data-dictionary.md is out of date: run `dotnet run --project tools/Erp.CodeGen -- dictionary --connection \"$ERP_TEST_PG\"`.");
    }

    private static async Task AssertRefusedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object? id, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "savepoint guard", null, ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, sql, id, ct));
        Assert.Equal(SqlEmitter.AppendOnlySqlState, error.SqlState);
        await ExecuteAsync(connection, transaction, "rollback to savepoint guard", null, ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, object? id, CancellationToken ct)
    {
#pragma warning disable CA2100 // Statements built from generated catalogue names only.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        if (id is not null)
        {
            command.Parameters.AddWithValue("id", id);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> QueryAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // Fixed catalogue queries.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }
}
