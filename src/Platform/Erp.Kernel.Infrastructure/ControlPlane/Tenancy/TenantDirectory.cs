using System.Collections.Concurrent;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Tenancy;

/// <summary>
/// Host name → customer, read from the control plane (cp.tenant_domains, FR-KRN-TEN-04) with a short cache. Unknown
/// hosts are cached as well, so repeated probes do not reach the control-plane database.
/// </summary>
public sealed class TenantDirectory(ControlPlaneOptions options, IClock clock) : ITenantDirectory
{
    internal const string SelectTenant = """
        select t.id, t.code, t.status, d.id, d.database_name, d.status, c.host, c.port, d.app_secret_ref,
               (select e.status from cp.entitlements e where e.tenant_id = t.id and e.status <> 'superseded' order by e.version desc limit 1),
               coalesce((select array_agg(m.module_id order by m.module_id) from cp.tenant_module_versions m where m.tenant_database_id = d.id), '{}')
        from cp.tenants t
        join cp.tenant_databases d on d.tenant_id = t.id and d.env_type = 'production'
        join cp.db_clusters c on c.id = d.db_cluster_id
        """;

    private readonly ConcurrentDictionary<string, (ResolvedTenant? Tenant, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    public async ValueTask<ResolvedTenant?> FindByHostAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHost);
        var now = clock.UtcNow;
        if (_cache.TryGetValue(normalizedHost, out var hit) && hit.Expires > now)
        {
            return hit.Tenant;
        }

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await ReadAsync(connection, SelectTenant + " join cp.tenant_domains h on h.tenant_id = t.id where h.host = $1", normalizedHost, cancellationToken).ConfigureAwait(false);
        _cache[normalizedHost] = (tenant, now.AddSeconds(options.HostCacheSeconds));
        return tenant;
    }

    /// <summary>The customer with this code (control-plane operations: migrator, provisioning, backups).</summary>
    public static Task<ResolvedTenant?> FindByCodeAsync(NpgsqlConnection connection, string code, CancellationToken cancellationToken) =>
        ReadAsync(connection, SelectTenant + " where t.code = $1", code, cancellationToken);

    /// <summary>Forgets cached lookups (after a lifecycle change made by this process).</summary>
    public void Invalidate() => _cache.Clear();

    internal static async Task<ResolvedTenant?> ReadAsync(NpgsqlConnection connection, string sql, string key, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // constant SQL text; the key is a parameter
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ResolvedTenant(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            new HashSet<string>(reader.GetFieldValue<string[]>(10), StringComparer.Ordinal));
    }
}
