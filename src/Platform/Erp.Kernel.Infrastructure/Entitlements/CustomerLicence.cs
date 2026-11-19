using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>
/// The customer database's side of the licence (slice S1.09b, design note D2): the copy of the entitlement in force
/// (meta.local_entitlement, read inside customer transactions), the licence security events (KRN-AUD-02) with their outbox
/// events (<c>meta.outbox_messages</c>, read by the e-mails of S1.11) and the customer's time zone for the day boundary.
/// </summary>
public static class CustomerLicence
{
    /// <summary>The customer-layer time zone (<c>org.time_zone</c>; default Asia/Karachi), as an IANA id.</summary>
    public static async Task<string> TimeZoneAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly today, CancellationToken cancellationToken)
    {
        var resolved = await SettingsStore.ResolveAsync(connection, transaction, [KernelSettingKeys.OrgTimeZone], null, null, today, cancellationToken).ConfigureAwait(false);
        return resolved.TryGetValue(KernelSettingKeys.OrgTimeZone, out var zone) && zone.Value.ValueKind == JsonValueKind.String ? zone.Value.GetString()! : "Asia/Karachi";
    }

    /// <summary>
    /// Serializes the writers of the customer's licence copy and its announcements (licence job, <c>licence refresh</c>,
    /// <c>entitlement apply</c>, provisioning) for the rest of the transaction; read the copy after it (S1.09b review finding 3).
    /// </summary>
    public static Task LockCopyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        IdentityDb.ExecuteAsync(connection, transaction, "select pg_advisory_xact_lock(hashtext('licence.copy'))", cancellationToken);

    /// <summary>The version held by the customer's copy (0 when there is none).</summary>
    public static async Task<int> CopyVersionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken) =>
        await IdentityDb.ScalarAsync<int?>(connection, transaction,
            "select (payload -> 'payload' ->> 'version')::int from meta.local_entitlement order by created_at desc limit 1", cancellationToken).ConfigureAwait(false) ?? 0;

    /// <summary>Replaces the customer's copy with the entitlement now in force (one row, kept since provisioning).</summary>
    public static async Task ReplaceCopyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EntitlementPayload payload, string keyId, byte[] signature, string state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = JsonSerializer.Serialize(new { payload = payload.ToJson(), key_id = keyId });
        await using var update = new NpgsqlCommand("""
            update meta.local_entitlement set payload = $1, signature = $2, state = $3, state_since = $4,
                highest_time_seen = greatest(highest_time_seen, $4), updated_at = $4, updated_by = $5, row_version = row_version + 1
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        update.Parameters.Add(new NpgsqlParameter { Value = signature });
        update.Parameters.Add(new NpgsqlParameter { Value = state });
        update.Parameters.Add(new NpgsqlParameter { Value = now });
        update.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await using var insert = new NpgsqlCommand("""
                insert into meta.local_entitlement (id, payload, signature, state, state_since, highest_time_seen, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $5, $5, $6, $5, $6)
                """, connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7(now) });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
            insert.Parameters.Add(new NpgsqlParameter { Value = signature });
            insert.Parameters.Add(new NpgsqlParameter { Value = state });
            insert.Parameters.Add(new NpgsqlParameter { Value = now });
            insert.Parameters.Add(new NpgsqlParameter { Value = BaselineSeed.SystemUserId });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records the copy's state (the licence loop, after a state change).</summary>
    public static Task SetCopyStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string state, DateTimeOffset now, CancellationToken cancellationToken) =>
        IdentityDb.ExecuteAsync(connection, transaction, """
            update meta.local_entitlement set state = $1, state_since = $2, highest_time_seen = greatest(highest_time_seen, $2), updated_at = $2, updated_by = $3,
                row_version = row_version + 1
            where state is distinct from $1
            """, cancellationToken, state, now, BaselineSeed.SystemUserId);

    /// <summary>
    /// One licence event in the customer database: a security event (FR-KRN-AUD-02; warning for read-only, locked and refused
    /// seats, info otherwise) and an outbox event of the same type, in the caller's transaction. Returns the security event id.
    /// </summary>
    public static async Task<Guid> RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, string severity, JsonObject details, Guid actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(details);
        var request = AuditAmbient.Current;
        if (request is not null)
        {
            details["correlation_id"] = request.CorrelationId;
        }

        var id = Guid.CreateVersion7(now);
        var who = actor == BaselineSeed.SystemUserId ? null : actor.ToString();
        await IdentityDb.ExecuteAsync(connection, transaction, "select aud.append_security_event($1, $2, $3, $4, $5, $6, $7, $8)", cancellationToken,
            id, now, eventType, who, "licence", severity, new JsonPayload(details.ToJsonString()), actor).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(connection, transaction, """
            insert into meta.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, 'meta.local_entitlement', $4, $5, $6, null)
            """, cancellationToken, Guid.CreateVersion7(now), now, eventType, id, new JsonPayload(details.ToJsonString()),
            new JsonPayload(JsonSerializer.Serialize(new { actor, security_event_id = id }))).ConfigureAwait(false);
        return id;
    }

    /// <summary>Severity of a licence event: warning when the customer loses full use or a seat is refused.</summary>
    public static string SeverityOf(string eventType, string? toState) =>
        eventType == LicenceEvents.LimitReached || (eventType == LicenceEvents.StateChanged && toState is "read_only" or "locked")
            ? Severities.Warning
            : Severities.Info;
}
