using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Tenancy;

/// <summary>The customers background work serves: active or suspended, with a database that is ready or being migrated.</summary>
public static class ServedTenants
{
    public static async Task<List<ResolvedTenant>> ListAsync(ControlPlaneOptions controlPlane, string? only, CancellationToken ct)
    {
        var list = new List<ResolvedTenant>();
        await using var cp = new NpgsqlConnection(controlPlane.ConnectionString);
        await cp.OpenAsync(ct).ConfigureAwait(false);
        var codes = new List<string>();
        await using (var command = Sql.Command(cp, null, """
            select t.code from cp.tenants t join cp.tenant_databases d on d.tenant_id = t.id and d.env_type = 'production'
            where t.status in ('active', 'suspended') and d.status in ('ready', 'migrating') and ($1::text is null or t.code = $1)
            order by t.code
            """, [only]))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                codes.Add(reader.GetString(0));
            }
        }

        foreach (var code in codes)
        {
            if (await TenantDirectory.FindByCodeAsync(cp, code, ct).ConfigureAwait(false) is { } tenant)
            {
                list.Add(tenant);
            }
        }

        return list;
    }
}
