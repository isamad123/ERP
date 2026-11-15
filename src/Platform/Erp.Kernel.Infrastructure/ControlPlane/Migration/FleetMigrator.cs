using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Migration;

/// <summary>Outcome for one customer database.</summary>
public sealed record TenantMigrationOutcome(string Code, bool Succeeded, int Applied, string? SchemaVersion, string? Error);

/// <summary>
/// The migrator command (S1.03 build item 5): applies pending migrations to every customer database one at a time.
/// A failure puts that customer's database in <c>maintenance</c> (no requests are served for it), is recorded in
/// cp.platform_audit, and the migrator continues with the next customer. A database in maintenance is retried on
/// the next run. Canary batches are Release 1.1 (FR-KRN-TEN-11).
/// </summary>
public sealed class FleetMigrator(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    public async Task<IReadOnlyList<TenantMigrationOutcome>> MigrateAsync(PlatformActor actor, string? onlyTenantCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var provisioner = new TenantProvisioner(options, secrets, clock, migrations);
        var outcomes = new List<TenantMigrationOutcome>();
        foreach (var tenant in await TenantsAsync(onlyTenantCode, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SetStatusAsync(tenant, "migrating", actor, cancellationToken).ConfigureAwait(false);
            try
            {
                IEnumerable<string> modules = tenant.InstalledModules.Count == 0 ? [MigrationSet.Kernel] : tenant.InstalledModules;
                SchemaMigrationResult result;
                await using (var owner = await provisioner.OpenTenantAsync(tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
                {
                    result = await TenantSchemaMigrator.ApplyAsync(owner, migrations.TenantScripts(modules), clock, BaselineSeed.SystemUserId, cancellationToken).ConfigureAwait(false);
                }

                await provisioner.RecordVersionsAsync(actor, tenant, result, cancellationToken).ConfigureAwait(false);
                await SetStatusAsync(tenant, "ready", actor, cancellationToken).ConfigureAwait(false);
                await AuditAsync(actor, "tenant.migrate", tenant, new { code = tenant.Code, applied = result.Applied, schema_version = result.SchemaVersion }, cancellationToken).ConfigureAwait(false);
                outcomes.Add(new TenantMigrationOutcome(tenant.Code, true, result.Applied, result.SchemaVersion, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SetStatusAsync(tenant, "maintenance", actor, CancellationToken.None).ConfigureAwait(false);
                await AuditAsync(actor, "tenant.migrate.failed", tenant, new { code = tenant.Code, error = ex.Message }, CancellationToken.None).ConfigureAwait(false);
                outcomes.Add(new TenantMigrationOutcome(tenant.Code, false, 0, null, ex.Message));
            }
        }

        return outcomes;
    }

    private async Task<List<ResolvedTenant>> TenantsAsync(string? onlyTenantCode, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var codes = new List<string>();
        await using (var command = Sql.Command(connection, null, """
            select t.code from cp.tenants t join cp.tenant_databases d on d.tenant_id = t.id and d.env_type = 'production'
            where t.status in ('active', 'suspended') and d.status in ('ready', 'migrating', 'maintenance') and ($1::text is null or t.code = $1)
            order by t.code
            """, [onlyTenantCode]))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                codes.Add(reader.GetString(0));
            }
        }

        var tenants = new List<ResolvedTenant>();
        foreach (var code in codes)
        {
            tenants.Add((await TenantDirectory.FindByCodeAsync(connection, code, cancellationToken).ConfigureAwait(false))!);
        }

        return tenants;
    }

    private async Task SetStatusAsync(ResolvedTenant tenant, string status, PlatformActor actor, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, null,
            "update cp.tenant_databases set status = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1 and status <> $2",
            cancellationToken, tenant.TenantDatabaseId, status, clock.UtcNow, actor.Id).ConfigureAwait(false);
    }

    private async Task AuditAsync(PlatformActor actor, string action, ResolvedTenant tenant, object details, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await PlatformAudit.WriteAsync(connection, null, clock, actor, action, tenant.TenantId, details, cancellationToken).ConfigureAwait(false);
    }
}
