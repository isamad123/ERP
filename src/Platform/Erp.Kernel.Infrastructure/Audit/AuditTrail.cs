using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// The audit writer of the hand-written services (identity, access, settings, platform recovery; slice S1.07 build items
/// 1 and 5). Their <c>iam.*</c> events stay in <c>iam.outbox_messages</c> as published; every event is also written, in the
/// same transaction, as a security event (ruling Q7) and, when it changes a record, as a change-log entry with the actor
/// from <c>headers.actor</c> (GHR-09, ADM-USR-04, ADM-USR-05, KRN-SET-04).
/// </summary>
public static class AuditTrail
{
    /// <summary>Events that change no record of their own (sessions, credentials, factors): security event only.</summary>
    private static readonly HashSet<string> SecurityOnly = new(StringComparer.Ordinal)
    {
        IdentityEvents.PasswordChanged, IdentityEvents.PasswordChangeForced, IdentityEvents.MfaEnrolled, IdentityEvents.MfaRemoved,
        IdentityEvents.MfaReset, IdentityEvents.SessionsRevoked, IdentityEvents.ActivationTokenIssued, IdentityEvents.RecoveryCodesRegenerated,
        AccessEvents.PermissionsRegistered,
    };

    /// <summary>Change-log action per event (default update).</summary>
    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        [AccessEvents.RoleCreated] = "create",
        [AccessEvents.RoleArchived] = "delete",
        [AccessEvents.GroupCreated] = "create",
        [AccessEvents.GroupArchived] = "delete",
        [AccessEvents.DataScopeCreated] = "create",
        [AccessEvents.ChangeRequested] = "create",
        [AccessEvents.ChangeApproved] = "status",
        [AccessEvents.ChangeRejected] = "status",
        [AccessEvents.ChangeExpired] = "status",
        [AccessEvents.RoleAssignmentEnded] = "update",
        [AccessEvents.GroupRoleEnded] = "update",
        [IdentityEvents.UserActivated] = "status",
        [IdentityEvents.UserLocked] = "status",
        [IdentityEvents.UserUnlocked] = "status",
        [IdentityEvents.UserStatusChanged] = "status",
        [AccessEvents.SettingReset] = "delete",
        [Contracts.Settings.SettingsEvents.Reset] = "delete",
        [Contracts.Settings.SettingsEvents.FlagReset] = "delete",
    };

    /// <summary>
    /// Writes the audit rows of one <c>iam.*</c> event. <paramref name="aggregateType"/> is the event's singular aggregate
    /// (<c>iam.role</c>); the change log names the table (<c>iam.roles</c>), so record history finds it.
    /// </summary>
    public static async Task RecordEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, string aggregateType, Guid aggregateId, Guid actor, DateTimeOffset now, object payload, CancellationToken cancellationToken)
    {
        var details = JsonSerializer.SerializeToNode(payload, AccessJson) as JsonObject ?? [];
        var request = AuditAmbient.Current;
        var system = actor == BaselineSeed.SystemUserId;
        var table = aggregateType.EndsWith('s') ? aggregateType : aggregateType + "s";
        var withRecord = new JsonObject { ["entity_type"] = table, ["entity_id"] = aggregateId, ["payload"] = details.DeepClone() };
        if (request is not null)
        {
            withRecord["correlation_id"] = request.CorrelationId;
            withRecord["ip_address"] = request.IpAddress;
        }

        await AuditDb.AppendSecurityEventAsync(connection, transaction,
            new SecurityEventRecord(now, eventType, actor.ToString(), aggregateType + ":" + aggregateId, SeverityOf(eventType, details), withRecord, actor), cancellationToken).ConfigureAwait(false);

        if (SecurityOnly.Contains(eventType))
        {
            return;
        }

        var changes = new JsonArray();
        if (details.ContainsKey("old_value") || details.ContainsKey("new_value"))
        {
            changes.Add(new JsonObject { ["field"] = "value", ["before"] = details["old_value"]?.DeepClone(), ["after"] = details["new_value"]?.DeepClone() });
        }

        if (details.ContainsKey("from") && details.ContainsKey("to"))
        {
            changes.Add(new JsonObject { ["field"] = "status", ["before"] = details["from"]?.DeepClone(), ["after"] = details["to"]?.DeepClone() });
        }

        foreach (var (name, value) in details.Where(p => p.Key is not ("old_value" or "new_value" or "from" or "to")))
        {
            changes.Add(new JsonObject { ["field"] = name, ["after"] = value?.DeepClone() });
        }

        changes.Add(new JsonObject { ["field"] = "event", ["after"] = eventType });
        var entry = new ChangeEntry(Guid.CreateVersion7(now), now, system ? null : actor, system ? ActorKinds.System : ActorKinds.User, table, aggregateId, null,
            Actions.GetValueOrDefault(eventType, "update"), changes, null, request?.CorrelationId, request?.IpAddress,
            system && request is null ? AuditSources.System : AuditAmbient.Source, actor);
        await AuditDb.AppendChangesAsync(connection, transaction, [entry], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One security event per sign-in attempt (ruling Q7: one place to search). <c>iam.login_attempts</c> keeps its row too.
    /// </summary>
    public static Task RecordSignInAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string login, string outcome, string? ipAddress, Guid actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var success = outcome == "success";
        var request = AuditAmbient.Current;
        var details = new JsonObject
        {
            ["outcome"] = outcome,
            ["ip_address"] = ipAddress,
            ["correlation_id"] = request?.CorrelationId,
        };
        var severity = success ? Severities.Info : outcome == "locked" ? Severities.Warning : Severities.Info;
        var who = actor == BaselineSeed.SystemUserId ? null : actor.ToString();
        return AuditDb.AppendSecurityEventAsync(connection, transaction,
            new SecurityEventRecord(now, success ? "iam.sign_in_succeeded" : "iam.sign_in_failed", who, Truncate(login.Trim().ToLowerInvariant(), 320), severity, details, actor), cancellationToken);
    }

    /// <summary>
    /// Ruling Q7: critical for Super Admin recovery and for switching off maker-checker or privileged MFA; warning for
    /// lockouts; info for the rest.
    /// </summary>
    public static string SeverityOf(string eventType, JsonObject details)
    {
        ArgumentNullException.ThrowIfNull(details);
        switch (eventType)
        {
            case AccessEvents.PlatformAdminRoleRepaired or AccessEvents.PlatformAdminGranted or AccessEvents.PlatformAdminRevoked:
                return Severities.Critical;
            case IdentityEvents.UserLocked:
                return Severities.Warning;
            case AccessEvents.SettingChanged:
                var key = (string?)details["key"] ?? string.Empty;
                var off = details["new_value"] is JsonValue v && (v.TryGetValue<bool>(out var b) ? !b : string.Equals(v.ToJsonString().Trim('"'), "false", StringComparison.OrdinalIgnoreCase));
                return off && (key.StartsWith("iam.maker_checker", StringComparison.Ordinal) || key.StartsWith("iam.mfa", StringComparison.Ordinal)) ? Severities.Critical : Severities.Info;
            default:
                return Severities.Info;
        }
    }

    private static readonly JsonSerializerOptions AccessJson = new(JsonSerializerDefaults.Web);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
