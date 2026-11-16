using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// The access log (FR-KRN-AUD-02, GHR-02, HRM-PRV-05; ruling Q6): one row per answer that showed sensitive fields
/// unmasked, written on its own before the answer is returned. If it cannot be written the request fails.
/// </summary>
public sealed class SensitiveAccessLog(ITenantConnectionProvider tenant, IClock clock, IAuditContext audit) : ISensitiveAccessLog
{
    public async Task RecordAsync(string resource, Guid? entityId, IReadOnlyList<string> fields, string action, int rowCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
        {
            return;
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AuditDb.AppendAccessAsync(connection, audit.ActorUserId ?? BaselineSeed.SystemUserId, resource, entityId, fields, action, rowCount, audit.CorrelationId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The audit context of a process that is not serving a request (migrator, operator commands): the system acts.</summary>
public sealed class SystemAuditContext : IAuditContext
{
    public Guid CorrelationId { get; } = Guid.CreateVersion7();

    public string? IpAddress => null;

    public string Source => AuditSources.System;

    public Guid? ActorUserId => null;

    public string ActorKind => ActorKinds.System;
}

/// <summary>
/// Destroys a data subject's audit key (spec 03 R-21; the kernel operation that retention and erasure use later,
/// HRM-PRV-04, ADM-SEC-06). Afterwards that person's personal values in the change log read as erased; the chain still
/// verifies. Recorded as a critical security event in the same transaction.
/// </summary>
public sealed class SubjectKeyService(ITenantConnectionProvider tenant, IClock clock, IAuditContext audit)
{
    public const string EventType = "aud.subject_key_destroyed";

    public async Task<bool> DestroyAsync(Guid personId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var now = clock.UtcNow;
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select aud.destroy_subject_key($1, $2)", connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = personId });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        var destroyed = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (destroyed)
        {
            var details = new System.Text.Json.Nodes.JsonObject { ["person_id"] = personId, ["reason"] = reason, ["correlation_id"] = audit.CorrelationId };
            await AuditDb.AppendSecurityEventAsync(connection, tx, new SecurityEventRecord(now, EventType, audit.ActorUserId?.ToString(), "person:" + personId, Severities.Critical, details, audit.ActorUserId ?? BaselineSeed.SystemUserId), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return destroyed;
    }
}
