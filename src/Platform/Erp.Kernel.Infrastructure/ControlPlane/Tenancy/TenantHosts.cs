using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Tenancy;

/// <summary>Host names of a customer in the control plane (cp.tenant_domains).</summary>
public static class TenantHosts
{
    /// <summary>The customer's primary host (or its first one); null when the customer code is unknown.</summary>
    public static async Task<string?> PrimaryAsync(ControlPlaneOptions options, string tenantCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            select d.host from cp.tenant_domains d join cp.tenants t on t.id = d.tenant_id
            where t.code = $1 order by d.is_primary desc, d.created_at, d.id limit 1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantCode });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
