using System.Text.Json;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>
/// The one way a setting value or a flag override is written (slice S1.09a build item 3; FR-KRN-SET-04, FR-ADM-SET-06):
/// the row is locked, set or removed, a history row records the old and new value with the reason, and an event is raised
/// with its audit rows (security event and change log, S1.07) in the same transaction. <c>iam.*</c> keys raise
/// <c>iam.setting_changed</c> / <c>iam.setting_reset</c> in iam.outbox_messages as since S1.06; every other key raises
/// <c>cfg.setting_changed</c> / <c>cfg.setting_reset</c> in cfg.outbox_messages. Callers validate first.
/// </summary>
public static class SettingWrites
{
    /// <summary>Sets (<paramref name="valueJson"/> given) or removes (null) the value of one layer; returns the row id, or null when there was nothing to remove.</summary>
    public static async Task<Guid?> ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, string layer, Guid? scopeId, string? valueJson, string? reason,
        Guid? revertedFrom, Guid? rowId, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        string? old = null;
        Guid? existing = null;
        await using (var command = IdentityDb.Command(c, tx, """
            select id, value::text from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3 for update
            """, key, layer, (object?)scopeId ?? DBNull.Value))
        {
            command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existing = reader.GetGuid(0);
                old = reader.GetString(1);
            }
        }

        Guid id;
        if (valueJson is null)
        {
            if (existing is not { } current)
            {
                return null;
            }

            id = current;
            await IdentityDb.ExecuteAsync(c, tx, "delete from cfg.setting_values where id = $1", ct, id).ConfigureAwait(false);
        }
        else
        {
            id = await IdentityDb.ScalarAsync<Guid>(c, tx, """
                insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                on conflict (setting_key, layer, scope_id) do update set value = excluded.value, updated_at = excluded.updated_at,
                    updated_by = excluded.updated_by, row_version = cfg.setting_values.row_version + 1
                returning id
                """, ct, existing ?? rowId ?? Guid.CreateVersion7(now), key, layer, scopeId, new JsonPayload(valueJson), now, by).ConfigureAwait(false);
        }

        await IdentityDb.ExecuteAsync(c, tx, """
            insert into cfg.setting_value_history (id, setting_value_id, setting_key, layer, scope_id, old_value, new_value, reason, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """, ct, Guid.CreateVersion7(now), id, key, layer, scopeId, old is null ? null : new JsonPayload(old),
            valueJson is null ? null : new JsonPayload(valueJson), reason, now, by).ConfigureAwait(false);

        var iam = key.StartsWith("iam.", StringComparison.Ordinal);
        var eventType = valueJson is null
            ? iam ? AccessEvents.SettingReset : SettingsEvents.Reset
            : iam ? AccessEvents.SettingChanged : SettingsEvents.Changed;
        // The S1.06 payload of iam.setting_changed (docs/registers/contracts.md), plus the reverted history row when there is one.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["layer"] = layer,
            ["scope_id"] = scopeId,
            ["old_value"] = old is null ? null : SettingsStore.Json(old),
            ["new_value"] = valueJson is null ? null : SettingsStore.Json(valueJson),
            ["reason"] = reason,
        };
        if (revertedFrom is { } history)
        {
            payload["reverted_from"] = history;
        }

        await RaiseAsync(c, tx, iam ? "iam" : "cfg", eventType, "cfg.setting_value", id, by, now, payload, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Sets (<paramref name="enabled"/> given) or removes (null) a flag override; returns the row id, or null when there was nothing to remove.</summary>
    public static async Task<Guid?> ApplyFlagAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, string layer, Guid? scopeId, bool? enabled, string reason, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        bool? old = null;
        Guid? existing = null;
        await using (var command = IdentityDb.Command(c, tx, """
            select id, enabled from cfg.feature_flag_overrides where flag_key = $1 and layer = $2 and scope_id is not distinct from $3 for update
            """, key, layer, (object?)scopeId ?? DBNull.Value))
        {
            command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existing = reader.GetGuid(0);
                old = reader.GetBoolean(1);
            }
        }

        Guid id;
        if (enabled is not { } on)
        {
            if (existing is not { } current)
            {
                return null;
            }

            id = current;
            await IdentityDb.ExecuteAsync(c, tx, "delete from cfg.feature_flag_overrides where id = $1", ct, id).ConfigureAwait(false);
        }
        else
        {
            id = await IdentityDb.ScalarAsync<Guid>(c, tx, """
                insert into cfg.feature_flag_overrides (id, flag_key, layer, scope_id, enabled, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                on conflict (flag_key, layer, scope_id) do update set enabled = excluded.enabled, updated_at = excluded.updated_at,
                    updated_by = excluded.updated_by, row_version = cfg.feature_flag_overrides.row_version + 1
                returning id
                """, ct, existing ?? Guid.CreateVersion7(now), key, layer, scopeId, on, now, by).ConfigureAwait(false);
        }

        await RaiseAsync(c, tx, "cfg", enabled is null ? SettingsEvents.FlagReset : SettingsEvents.FlagChanged, "cfg.feature_flag_override", id, by, now,
            new { key, layer, scope_id = scopeId, old_value = old, new_value = enabled, reason }, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Checks a value for a layer of a setting (rulings Q10 to Q12): a visible setting, a layer it allows (the pack layer is
    /// written only by the pack loader, S1.23), the scope of the layer (no scope for tenant; an existing company, role or user
    /// otherwise) and, for a value, its type and rules. Null when everything is in order, else the error key.
    /// </summary>
    public static async Task<string?> ProblemAsync(NpgsqlConnection c, NpgsqlTransaction tx, SettingRow? definition, string layer, Guid? scopeId, JsonElement? value, CancellationToken ct)
    {
        if (definition is not { IsVisible: true })
        {
            return SettingsErrors.Unknown;
        }

        if (layer == SettingLayers.Pack)
        {
            return SettingsErrors.PackLayer;
        }

        if (!SettingLayers.Stored.Contains(layer) || !definition.Layers.Contains(layer) || (layer == SettingLayers.Tenant) != (scopeId is null))
        {
            return SettingsErrors.LayerInvalid;
        }

        var table = layer switch
        {
            SettingLayers.Company => "org.companies",
            SettingLayers.Role => "iam.roles",
            SettingLayers.User => "iam.users",
            _ => null,
        };
        if (table is not null && !await IdentityDb.ScalarAsync<bool>(c, tx, $"select exists (select 1 from {table} where id = $1 and not is_deleted)", ct, scopeId).ConfigureAwait(false))
        {
            return SettingsErrors.ScopeNotFound;
        }

        return value is { } v && definition.Definition.Validation.Problem(definition.DataType, v) is { } problem
            ? problem == "time_zone" ? SettingsErrors.TimeZoneUnknown : SettingsErrors.ValueInvalid
            : null;
    }

    private static async Task RaiseAsync(NpgsqlConnection c, NpgsqlTransaction tx, string schema, string eventType, string aggregateType, Guid aggregateId, Guid actor, DateTimeOffset now, object payload, CancellationToken ct)
    {
        var outbox = schema == "iam" ? "iam.outbox_messages" : "cfg.outbox_messages";
        await IdentityDb.ExecuteAsync(c, tx, $"""
            insert into {outbox} (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, $4, $5, $6, $7, null)
            """, ct, Guid.CreateVersion7(now), now, eventType, aggregateType, aggregateId,
            new JsonPayload(JsonSerializer.Serialize(payload, Access.AccessDb.JsonOptions)), new JsonPayload(JsonSerializer.Serialize(new { actor }))).ConfigureAwait(false);
        await Audit.AuditTrail.RecordEventAsync(c, tx, eventType, aggregateType, aggregateId, actor, now, payload, ct).ConfigureAwait(false);
    }
}
