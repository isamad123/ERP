using System.Reflection;
using System.Security.Cryptography;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Infrastructure.Generated;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Audit;

/// <summary>
/// Slice S1.07 Q4 (CR-026): every audited table writes its change-log entries for create, update and delete through the
/// EF change tracker, and every table the data design opts out writes none. One theory row per table of the audit
/// catalogue, run in a transaction that is rolled back, against a migrated database.
/// </summary>
[Collection(MigratedDatabaseGroup.Name)]
public sealed class AuditCoverageTests(MigratedDatabases db)
{
    private static readonly AuditCatalog Catalog = new([.. KernelAuditCatalog.All, .. HrmAuditCatalog.All]);

    public static TheoryData<string> AuditedTables()
    {
        var data = new TheoryData<string>();
        foreach (var t in Catalog.All.Where(t => t.Audited && typeof(IEntity).IsAssignableFrom(t.EntityType)).OrderBy(t => t.Table, StringComparer.Ordinal))
        {
            data.Add(t.Table);
        }

        return data;
    }

    [Fact]
    public void FR_KRN_AUD_01_S107_Q4_every_table_is_audited_or_says_why_not()
    {
        var all = Catalog.All.ToList();
        Assert.True(all.Count > 200, $"only {all.Count} tables in the audit catalogue");
        Assert.All(all.Where(t => !t.Audited), t => Assert.False(string.IsNullOrWhiteSpace(t.Reason), t.Table));
        Assert.All(all.Where(t => t.Audited), t => Assert.NotNull(t.KeyColumn));
        Assert.All(all.Where(t => t.Table.StartsWith("aud.", StringComparison.Ordinal) || t.Table.EndsWith("outbox_messages", StringComparison.Ordinal)), t => Assert.False(t.Audited, t.Table));
        TestContext.Current.SendDiagnosticMessage($"{all.Count(t => t.Audited)} audited tables, {all.Count(t => !t.Audited)} opted out with a reason");
    }

    [Theory]
    [MemberData(nameof(AuditedTables))]
    public async Task FR_KRN_AUD_01_S107_Q4_create_update_and_delete_write_their_entries(string table)
    {
        var ct = TestContext.Current.CancellationToken;
        var audited = Catalog.Find(table)!;
        await using var connection = await db.OpenAsync("tenant", ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var expected = MigratedDatabases.Catalog.Tables.Single(t => t.Database == "tenant" && t.FullName == table);
        var row = await new RowFactory(connection, tx, MigratedDatabases.Catalog, 500_000).InsertAsync(expected, null, ct);
        var id = (Guid)row[audited.KeyColumn!]!;

        using var services = Services();
        await using var context = Context(audited, connection, services);
        await context.Database.UseTransactionAsync(tx, ct);

        // Read the row as EF sees it, remove it with SQL, then create it again through EF. A row that others already point
        // at (a company and its default branch) is copied instead: new id, text values made unique.
        var entity = await Find(context, audited, id, ct);
        context.ChangeTracker.Clear();
        await tx.SaveAsync("audit_create", ct);
        try
        {
            await Exec(connection, tx, $"delete from {table} where {audited.KeyColumn} = $1", ct, id);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await tx.RollbackAsync("audit_create", ct);
            id = Copy(context, entity, audited, expected);
        }

        context.Add(entity);
        await context.SaveChangesAsync(ct);
        Assert.Equal(1, await Count(connection, tx, table, id, "create", ct));

        // Update: the first column change the table's rules accept (savepoint per attempt).
        var updated = await TryUpdateAsync(context, connection, tx, audited, id, ct);
        Assert.True(updated is not null, $"{table}: no column could be changed to exercise an update");
        Assert.Equal(1, await Count(connection, tx, table, id, null, ct, actions: ["update", "status"]));

        // Delete: soft delete for master data, otherwise a real delete.
        context.ChangeTracker.Clear();
        var current = await Find(context, audited, id, ct);
        if (current is ISoftDeletable soft)
        {
            soft.IsDeleted = true;
            soft.DeletedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            context.Remove(current);
        }

        await context.SaveChangesAsync(ct);
        Assert.Equal(1, await Count(connection, tx, table, id, "delete", ct));
        await tx.RollbackAsync(ct);
    }

    [Fact]
    public async Task FR_KRN_AUD_01_S107_Q4_a_table_that_opts_out_writes_no_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var audited = Catalog.Find("job.runs")!;
        Assert.False(audited.Audited);
        await using var connection = await db.OpenAsync("tenant", ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var expected = MigratedDatabases.Catalog.Tables.Single(t => t.Database == "tenant" && t.FullName == "job.runs");
        var row = await new RowFactory(connection, tx, MigratedDatabases.Catalog, 600_000).InsertAsync(expected, null, ct);
        using var services = Services();
        await using var context = Context(audited, connection, services);
        await context.Database.UseTransactionAsync(tx, ct);
        var entity = await Find(context, audited, (Guid)row["id"]!, ct);
        context.ChangeTracker.Clear();
        await Exec(connection, tx, "delete from job.runs where id = $1", ct, row["id"]);
        context.Add(entity);
        await context.SaveChangesAsync(ct);
        Assert.Equal(0, await Count(connection, tx, "job.runs", (Guid)row["id"]!, null, ct));
        await tx.RollbackAsync(ct);
    }

    private static async Task<string?> TryUpdateAsync(DbContext context, NpgsqlConnection connection, NpgsqlTransaction tx, AuditedTable audited, Guid id, CancellationToken ct)
    {
        string[] skipped = ["id", "created_at", "created_by", "updated_at", "updated_by", "row_version", "deleted_at", "deleted_by", "is_deleted", "company_id"];
        context.ChangeTracker.Clear();
        var probe = await Find(context, audited, id, ct);
        var candidates = context.Entry(probe).Properties
            .Where(p => !skipped.Contains(p.Metadata.GetColumnName()) && !p.Metadata.IsPrimaryKey() && !p.Metadata.IsForeignKey() && p.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never)
            .Select(p => p.Metadata.Name).ToList();
        foreach (var name in candidates)
        {
            context.ChangeTracker.Clear();
            var entity = await Find(context, audited, id, ct);
            var property = context.Entry(entity).Property(name);
            var column = ExpectedColumns(audited).FirstOrDefault(c => c.Name == property.Metadata.GetColumnName());
            var next = column is { EnumValues.Count: > 0 } ? column.EnumValues.FirstOrDefault(v => !Equals(v, property.CurrentValue)) : Changed(property.Metadata.ClrType, property.CurrentValue);
            if (next is null)
            {
                continue;
            }

            await tx.SaveAsync("audit_update", ct);
            property.CurrentValue = next;
            try
            {
                await context.SaveChangesAsync(ct);
                return name;
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync("audit_update", ct);
            }
        }

        return null;
    }

    private static IReadOnlyList<Erp.CodeGen.Emit.ExpectedColumn> ExpectedColumns(AuditedTable table) =>
        MigratedDatabases.Catalog.Tables.Single(t => t.Database == "tenant" && t.FullName == table.Table).Columns;

    /// <summary>Turns the loaded entity into a new row: a new key and its non-enum text values made unique.</summary>
    private static Guid Copy(DbContext context, object entity, AuditedTable table, Erp.CodeGen.Emit.ExpectedTable expected)
    {
        var entry = context.Entry(entity);
        var id = Guid.CreateVersion7();
        foreach (var property in entry.Properties)
        {
            var column = expected.Columns.FirstOrDefault(c => c.Name == property.Metadata.GetColumnName());
            if (property.Metadata.GetColumnName() == table.KeyColumn)
            {
                property.CurrentValue = id;
            }
            else if (property.CurrentValue is string text && column is { EnumValues.Count: 0, Kind: "Text" or "Code" } && !property.Metadata.IsForeignKey())
            {
                property.CurrentValue = text.Length >= 30 ? text[..30] + "c" : text + "c";
            }
        }

        entry.State = EntityState.Detached;
        return id;
    }

    private static object? Changed(Type type, object? current)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t switch
        {
            _ when t == typeof(System.Text.Json.JsonDocument) => current is System.Text.Json.JsonDocument { RootElement.ValueKind: System.Text.Json.JsonValueKind.Object } doc
                ? System.Text.Json.JsonDocument.Parse(System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject().Also(o => o["audit_test"] = true).ToJsonString())
                : current is null ? System.Text.Json.JsonDocument.Parse("{}") : null,
            _ when t == typeof(bool) => current is bool b ? !b : true,
            _ when t == typeof(string) => current is string s ? (s.Length > 1 ? s[..^1] + (s[^1] == 'x' ? "y" : "x") : "ax") : "audit test",
            _ when t == typeof(int) => current is int i ? i + 1 : 1,
            _ when t == typeof(long) => current is long l ? l + 1 : 1L,
            _ when t == typeof(decimal) => current is decimal d ? d + 1 : 1m,
            _ when t == typeof(DateOnly) => current is DateOnly day ? day.AddDays(1) : new DateOnly(2026, 2, 1),
            _ when t == typeof(DateTimeOffset) => current is DateTimeOffset at ? at.AddMinutes(1) : new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            _ => null,
        };
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new TestClock());
        services.AddSingleton<IAuditContext>(new SystemAuditContext());
        services.AddSingleton<IFieldEncryptorProvider>(new FixedKey());
        services.AddKernelAudit([.. KernelAuditCatalog.All, .. HrmAuditCatalog.All]);
        return services.BuildServiceProvider();
    }

    private static DbContext Context(AuditedTable table, NpgsqlConnection connection, ServiceProvider services)
    {
        var hrm = table.Table.StartsWith("hrm.", StringComparison.Ordinal);
        DbContextOptionsBuilder Options(DbContextOptionsBuilder b) => b.UseNpgsql(connection).UseApplicationServiceProvider(services).UseErpAudit(services);
        return hrm
            ? new HrmDbContext((DbContextOptions<HrmDbContext>)Options(new DbContextOptionsBuilder<HrmDbContext>()).Options)
            : new KernelDbContext((DbContextOptions<KernelDbContext>)Options(new DbContextOptionsBuilder<KernelDbContext>()).Options);
    }

    private static async Task<object> Find(DbContext context, AuditedTable table, Guid id, CancellationToken ct)
    {
        var entityType = context.Model.FindEntityType(table.EntityType)!;
        var key = entityType.GetProperties().First(p => p.GetColumnName() == table.KeyColumn);
        var method = typeof(AuditCoverageTests).GetMethod(nameof(FindTyped), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(table.EntityType);
        return await (Task<object>)method.Invoke(null, [context, key.Name, id, ct])!;
    }

    private static async Task<object> FindTyped<T>(DbContext context, string keyProperty, Guid id, CancellationToken ct)
        where T : class =>
        await context.Set<T>().FirstAsync(e => EF.Property<Guid>(e, keyProperty) == id, ct);

    private static async Task<long> Count(NpgsqlConnection c, NpgsqlTransaction tx, string table, Guid id, string? action, CancellationToken ct, string[]? actions = null)
    {
        await using var command = new NpgsqlCommand("select count(*) from aud.change_log where entity_type = $1 and entity_id = $2 and action = any($3)", c, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = actions ?? (action is null ? ["create", "update", "status", "delete"] : [action]) });
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object?[] values)
    {
#pragma warning disable CA2100 // test SQL text built from catalogue table names
        await using var command = new NpgsqlCommand(sql, c, tx);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class FixedKey : IFieldEncryptorProvider
    {
        private readonly AesGcmFieldEncryptor _key = new(RandomNumberGenerator.GetBytes(32), 1);

        public ValueTask<IFieldEncryptor> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IFieldEncryptor>(_key);
    }
}

internal static class JsonObjectExtensions
{
    public static System.Text.Json.Nodes.JsonObject Also(this System.Text.Json.Nodes.JsonObject value, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        change(value);
        return value;
    }
}
