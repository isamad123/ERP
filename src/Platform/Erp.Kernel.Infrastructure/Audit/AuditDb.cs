using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// One change-log entry before it is written (FR-KRN-AUD-01). <see cref="Changes"/> is the JSON array stored in
/// <c>aud.change_log.changes</c> (protected values already encrypted). The database function decides the final time,
/// the hashes and the anchors (rulings Q1, Q2, P2).
/// </summary>
public sealed record ChangeEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorKind,
    string EntityType,
    Guid EntityId,
    Guid? CompanyId,
    string Action,
    JsonArray Changes,
    Guid? SubjectPersonId,
    Guid? CorrelationId,
    string? IpAddress,
    string Source,
    Guid CreatedBy);

/// <summary>Parameterized calls of the audit functions (slice S1.07, migration V…0334__kernel__functions_s1_07.sql).</summary>
public static class AuditDb
{
    /// <summary>Appends entries to the hash chain in the caller's transaction; returns the number written.</summary>
    public static async Task<int> AppendChangesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<ChangeEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return 0;
        }

        var array = new JsonArray();
        foreach (var e in entries)
        {
            array.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["occurred_at"] = e.OccurredAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["actor_user_id"] = e.ActorUserId,
                ["actor_kind"] = e.ActorKind,
                ["entity_type"] = e.EntityType,
                ["entity_id"] = e.EntityId,
                ["company_id"] = e.CompanyId,
                ["action"] = e.Action,
                ["changes"] = e.Changes.DeepClone(),
                ["subject_person_id"] = e.SubjectPersonId,
                ["correlation_id"] = e.CorrelationId,
                ["ip_address"] = e.IpAddress,
                ["source"] = e.Source,
                ["created_by"] = e.CreatedBy,
            });
        }

        await using var command = new NpgsqlCommand("select aud.append_changes($1)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = array.ToJsonString() });
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Appends one security event (FR-KRN-AUD-02) in the caller's transaction (or on its own when none).</summary>
    public static Task AppendSecurityEventAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, SecurityEventRecord e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);
        return IdentityDb.ExecuteAsync(connection, transaction, "select aud.append_security_event($1, $2, $3, $4, $5, $6, $7, $8)", cancellationToken,
            Guid.CreateVersion7(e.OccurredAt), e.OccurredAt, e.EventType, e.Actor, e.Subject, e.Severity, new JsonPayload(JsonSerializer.Serialize(e.Details ?? new JsonObject())), e.CreatedBy);
    }

    /// <summary>Appends one access-log row (FR-KRN-AUD-02, HRM-PRV-05).</summary>
    public static async Task AppendAccessAsync(NpgsqlConnection connection, Guid actorUserId, string entityType, Guid? entityId, IReadOnlyList<string> fields, string action, int rowCount, Guid? correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        await using var command = new NpgsqlCommand("select aud.append_access($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7(now) });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = actorUserId });
        command.Parameters.Add(new NpgsqlParameter { Value = entityType });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)entityId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = fields.ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = action });
        command.Parameters.Add(new NpgsqlParameter { Value = rowCount });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)correlationId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = actorUserId });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person's wrapped audit key (stores <paramref name="offered"/> when they have none) and whether it was destroyed.</summary>
    public static async Task<(byte[] WrappedKey, bool Destroyed)> EnsureSubjectKeyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid personId, byte[] offered, CancellationToken cancellationToken)
    {
        await using var command = IdentityDb.Command(connection, transaction, "select wrapped_key, destroyed from aud.ensure_subject_key($1, $2)", personId, offered);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("aud.ensure_subject_key returned no key.");
        }

        return ((byte[])reader.GetValue(0), reader.GetBoolean(1));
    }

    /// <summary>The person's wrapped key when one exists (null when none; empty with destroyed = true after erasure).</summary>
    public static async Task<(byte[] WrappedKey, bool Destroyed)?> FindSubjectKeyAsync(NpgsqlConnection connection, Guid personId, CancellationToken cancellationToken)
    {
        await using var command = IdentityDb.Command(connection, null, "select wrapped_key, destroyed_at is not null from aud.subject_keys where person_id = $1", personId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ((byte[])reader.GetValue(0), reader.GetBoolean(1)) : null;
    }
}

/// <summary>One aud.security_events row (FR-KRN-AUD-02, ruling Q7).</summary>
public sealed record SecurityEventRecord(DateTimeOffset OccurredAt, string EventType, string? Actor, string? Subject, string Severity, JsonObject? Details, Guid CreatedBy);

/// <summary>Severities of aud.security_events (ruling Q7).</summary>
public static class Severities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}
