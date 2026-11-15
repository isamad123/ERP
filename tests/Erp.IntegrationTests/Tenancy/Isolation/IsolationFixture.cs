using System.Globalization;
using System.Text;
using Erp.CodeGen.Emit;
using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Infrastructure.ControlPlane;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// Two fresh customers (A and B) of their own, served by the real API pipeline (S1.04), so the suite cannot disturb or
/// be disturbed by the S1.03 tests. Rows are written straight into each customer database with
/// <see cref="RowFactory"/>, one minimal valid row (and the rows it references) per call.
/// </summary>
public sealed class IsolationFixture : IAsyncLifetime
{
    public TenancyFixture Tenancy { get; } = new();

    public string CodeA => Tenancy.CodeA;

    public string CodeB => Tenancy.CodeB;

    public string HostA => TenancyFixture.HostOf(CodeA);

    public string HostB => TenancyFixture.HostOf(CodeB);

    private readonly Dictionary<string, (int Next, object? Company)> _seedState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _seeded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public ValueTask InitializeAsync() => Tenancy.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        _seedLock.Dispose();
        await Tenancy.DisposeAsync();
    }

    public static ExpectedTable Table(string fullName) =>
        MigratedDatabases.Catalog.Tables.SingleOrDefault(t => t.Database == "tenant" && t.FullName == fullName)
        ?? throw new InvalidOperationException($"{fullName} is not a customer-database table in the generated catalogue.");

    /// <summary>A superuser connection to one customer database (test set-up and inspection only, never the API's path).</summary>
    public async Task<NpgsqlConnection> OpenAdminAsync(string code, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(Tenancy.AdminConnectionString) { Database = TenantLogins.DatabaseName(code), Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// The row of <paramref name="table"/> seeded for this customer, written on first use and then shared by the
    /// endpoints of that table (they only read or update it). <see cref="Forget"/> after the row was deleted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> RowAsync(string code, ExpectedTable table, CancellationToken cancellationToken)
    {
        var key = code + ":" + table.FullName;
        if (_seeded.TryGetValue(key, out var row))
        {
            return row;
        }

        row = await SeedAsync(code, table, cancellationToken);
        _seeded[key] = row;
        return row;
    }

    public void Forget(string code, ExpectedTable table) => _seeded.Remove(code + ":" + table.FullName);

    /// <summary>
    /// Writes one committed row of <paramref name="table"/> into the customer's database and returns its values. All rows
    /// of one customer share one company and one running value counter, so codes never repeat. Fixed-length codes
    /// (two or three letters) can still wrap around and meet an earlier one, and unique keys over optional columns
    /// (nulls not distinct) meet the earlier row; such a set-up clash is retried with fresh values and the next allowed
    /// enum values (never an isolation result).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> SeedAsync(string code, ExpectedTable table, CancellationToken cancellationToken)
    {
        await _seedLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAdminAsync(code, cancellationToken);
            for (var attempt = 1; ; attempt++)
            {
                var (next, company) = _seedState.GetValueOrDefault(code, (0, null));
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var factory = new RowFactory(connection, transaction, MigratedDatabases.Catalog, next, company, enumChoice: attempt - 1);
                try
                {
                    var row = await factory.InsertAsync(table, null, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    _seedState[code] = (factory.LastValue + 1, factory.CompanyId);
                    return row;
                }
                catch (PostgresException e) when (attempt < 6 && (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation
                    || (attempt > 1 && e.SqlState == PostgresErrorCodes.CheckViolation)))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _seedState[code] = (factory.LastValue + 1 + 997, company);
                }
            }
        }
        finally
        {
            _seedLock.Release();
        }
    }

    /// <summary>
    /// Row count and an MD5 of every row of every table in the customer's database. Two equal fingerprints mean nothing
    /// in that database was inserted, changed or deleted in between.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> FingerprintAsync(string code, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAdminAsync(code, cancellationToken);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("""
            select format('%I.%I', n.nspname, c.relname)
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p') and not c.relispartition and n.nspname not in ('pg_catalog', 'information_schema') and n.nspname not like 'pg_toast%'
            order by 1
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var sql = new StringBuilder();
        foreach (var table in tables)
        {
            if (sql.Length > 0)
            {
                sql.Append(" union all ");
            }

            // Names come from pg_class, quoted by format('%I'); there are no values in this text.
            sql.Append(CultureInfo.InvariantCulture, $"select '{table.Replace("'", "''", StringComparison.Ordinal)}', count(*)::text || ':' || coalesce(md5(string_agg(x::text, '|' order by x::text)), '') from {table} x");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
#pragma warning disable CA2100 // identifiers from the database catalogue, see above
        await using var command = new NpgsqlCommand(sql.ToString(), connection);
#pragma warning restore CA2100
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            result[rows.GetString(0)] = rows.GetString(1);
        }

        return result;
    }

    /// <summary>The tables whose fingerprint differs.</summary>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        [.. before.Keys.Union(after.Keys).Where(t => !before.TryGetValue(t, out var b) || !after.TryGetValue(t, out var a) || a != b).Order(StringComparer.Ordinal)];
}

[CollectionDefinition(Name)]
public sealed class IsolationGroup : ICollectionFixture<IsolationFixture>
{
    public const string Name = "Tenant isolation suite (S1.04)";
}
