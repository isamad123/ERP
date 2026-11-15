using Erp.BuildingBlocks.Domain.Time;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Platform;

/// <summary>One row of the customer list (FR-ADM-PLT-01, the columns the control plane holds in Release 1.0).</summary>
public sealed record TenantSummary(
    string Code, string Name, string Status, string RegionCode, string Cluster, string DatabaseStatus, string? SchemaVersion,
    string? Edition, string? EntitlementStatus, IReadOnlyList<string> Modules, bool LegalHold, DateTimeOffset? LastBackupAt, DateTimeOffset? LastRestoreTestAt);

/// <summary>
/// Customer lifecycle actions available in Release 1.0 (FR-KRN-TEN-09, FR-ADM-PLT-11): suspend and resume. Neither
/// deletes or changes customer data; a suspended customer is served nothing (locked) until resumed. Move, export,
/// offboard and delete are later releases. Every action is recorded in cp.platform_audit.
/// </summary>
public sealed class TenantLifecycle(ControlPlaneOptions options, IClock clock)
{
    public Task SuspendAsync(PlatformActor actor, string code, string reason, CancellationToken cancellationToken) =>
        ChangeStatusAsync(actor, code, "active", "suspended", "tenant.suspend", reason, cancellationToken);

    public Task ResumeAsync(PlatformActor actor, string code, string reason, CancellationToken cancellationToken) =>
        ChangeStatusAsync(actor, code, "suspended", "active", "tenant.resume", reason, cancellationToken);

    private async Task ChangeStatusAsync(PlatformActor actor, string code, string from, string to, string action, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Sql.Command(connection, transaction, """
            update cp.tenants set status = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1
            where code = $1 and status = $2 returning id, legal_hold
            """, [code, from, to, clock.UtcNow, actor.Id]);
        Guid id;
        bool legalHold;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Customer {code} is not {from}.");
            }

            id = reader.GetGuid(0);
            legalHold = reader.GetBoolean(1);
        }

        await PlatformAudit.WriteAsync(connection, transaction, clock, actor, action, id, new { code, from, to, reason, legal_hold = legalHold }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a completed backup or a verified restore drill (FR-ADM-DAT-08).</summary>
    public async Task RecordBackupAsync(PlatformActor actor, string code, bool restoreTest, object details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var column = restoreTest ? "last_restore_test_at" : "last_backup_at";
        var tenantId = await Sql.ScalarAsync<Guid>(connection, transaction, $"""
            update cp.tenant_databases d set {column} = $2, updated_at = $2, updated_by = $3, row_version = d.row_version + 1
            from cp.tenants t where t.id = d.tenant_id and t.code = $1 and d.env_type = 'production' returning t.id
            """, cancellationToken, code, now, actor.Id).ConfigureAwait(false);
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException($"Customer {code} is not registered.");
        }

        await PlatformAudit.WriteAsync(connection, transaction, clock, actor, restoreTest ? "tenant.restore_test" : "tenant.backup", tenantId, details, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Sql.Command(connection, null, """
            select t.code, t.name, t.status, t.region_code, c.name, d.status, d.schema_version, e.edition, e.status,
                   coalesce((select array_agg(m.module_id order by m.module_id) from cp.tenant_module_versions m where m.tenant_database_id = d.id), '{}'),
                   t.legal_hold, d.last_backup_at, d.last_restore_test_at
            from cp.tenants t
            join cp.tenant_databases d on d.tenant_id = t.id and d.env_type = 'production'
            join cp.db_clusters c on c.id = d.db_cluster_id
            left join cp.entitlements e on e.tenant_id = t.id and e.status <> 'superseded'
            order by t.code
            """, []);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<TenantSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TenantSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetFieldValue<string[]>(9), reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return rows;
    }
}
