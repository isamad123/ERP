using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Application.Jobs;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.ControlPlane;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// The system health page's extra figures (FR-ADM-OPS-05, FR-ADM-DAT-08; slice S1.16 Build items 11 and 12): the job
/// numbers of <see cref="IJobCentre"/>, files not scanned, storage used, database size and the bound customer's last backup
/// and restore drill from the control plane. Only the caller's own customer is read (the tenant id comes from the resolved
/// tenant, never from input).
/// </summary>
public sealed class SystemHealthService(
    TenantContext tenant,
    ITenantConnectionProvider connections,
    ControlPlaneOptions controlPlane,
    IJobCentre jobs,
    IPermissionChecker permissions,
    IClock clock) : ISystemHealth
{
    public async Task<SystemHealthView> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(JobPermissions.RunsRead, cancellationToken).ConfigureAwait(false))
        {
            throw new JobCentreException("errors.access.forbidden", 403);
        }

        var health = await jobs.HealthAsync(cancellationToken).ConfigureAwait(false);
        long storage;
        long database;
        int unscanned;
        DateTimeOffset? oldest;
        await using (var c = (NpgsqlConnection)await connections.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand("""
                select count(*) filter (where scanned_at is null and status in ('uploading', 'quarantined')),
                       min(created_at) filter (where scanned_at is null and status in ('uploading', 'quarantined')),
                       coalesce(sum(size_bytes) filter (where status <> 'deleted'), 0),
                       pg_database_size(current_database())
                from doc.files
                """, c);
            await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await r.ReadAsync(cancellationToken).ConfigureAwait(false);
            unscanned = (int)r.GetInt64(0);
            oldest = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
            storage = r.GetInt64(2);
            database = r.GetInt64(3);
        }

        DateTimeOffset? backup = null;
        DateTimeOffset? restore = null;
        await using (var cp = new NpgsqlConnection(controlPlane.ConnectionString))
        {
            await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("select last_backup_at, last_restore_test_at from cp.tenant_databases where id = $1", cp)
            {
                Parameters = { new NpgsqlParameter { Value = tenant.Tenant.TenantDatabaseId } },
            };
            await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                backup = r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0);
                restore = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
            }
        }

        return new SystemHealthView(health, unscanned, oldest, storage, database, backup, restore, clock.UtcNow);
    }
}
