using System.Text.Json;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// Data scopes and the identity and access settings (review S1.06 findings 2 and 5, CR-025). Both used to be written
/// through generated endpoints that applied none of the access rules and recorded nothing.
/// </summary>
public sealed partial class AccessAdministrationService : IAccessSettingsService
{
    public const string ScopeKindInvalid = "errors.scope.kind_invalid";
    public const string ScopeDefinitionInvalid = "errors.scope.definition_invalid";
    public const string SettingUnknown = "errors.settings.unknown_key";
    public const string SettingLayerInvalid = "errors.settings.layer_invalid";
    public const string SettingValueInvalid = "errors.settings.value_invalid";

    /// <summary>Permission to change settings (held by Tenant Admin through <c>cfg.*</c>), now also for the <c>iam.*</c> keys here.</summary>
    public const string ManageSettings = "cfg.setting_values.update";

    /// <summary>Settings whose switching off needs a second administrator when the customer has two or more (finding 5).</summary>
    private static readonly string[] TwoPersonSwitches = [IdentitySettings.MakerCheckerSensitiveRoles, IdentitySettings.MfaRequiredForPrivileged];

    // ================================================================== data scopes (FR-KRN-IAM-12, FR-ADM-ROL-06)

    public Task<AccessResult> CreateDataScopeAsync(string? code, IReadOnlyDictionary<string, string>? name, string? kind, JsonElement? definition, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (code is null || !CodePattern().IsMatch(code))
            {
                return AccessResult.Invalid(CodeInvalid);
            }

            if (!HasText(name))
            {
                return AccessResult.Invalid(NameRequired);
            }

            if (kind is null || !ScopeRule.Kinds.Contains(kind))
            {
                return AccessResult.Invalid(ScopeKindInvalid);
            }

            var text = definition is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } d ? d.GetRawText() : "{}";
            if (await DefinitionProblemAsync(c, tx, kind, text, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return AccessResult.Invalid(problem);
            }

            var id = Guid.CreateVersion7(now);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.data_scopes (id, code, name_i18n, kind, definition, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                """, cancellationToken, id, code, Json(name!), kind, new JsonPayload(text), now, by).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.DataScopeCreated, "iam.data_scope", id, by, now, new { code, kind, definition = JsonDocument.Parse(text).RootElement }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(id);
        }, cancellationToken);

    /// <summary>
    /// A definition must be exactly what the engine reads (anything else would silently match nothing): the ids of its
    /// kind, all existing; <c>include_children</c> for org units only; conditions "equals" or "is one of".
    /// </summary>
    private static async Task<string?> DefinitionProblemAsync(NpgsqlConnection c, NpgsqlTransaction tx, string kind, string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ScopeDefinitionInvalid;
        }

        var (idsName, table) = kind switch
        {
            ScopeRule.Companies => ("company_ids", "org.companies"),
            ScopeRule.Branches => ("branch_ids", "org.branches"),
            ScopeRule.OrgUnits => ("org_unit_ids", "org.org_units"),
            _ => ((string?)null, (string?)null),
        };
        foreach (var property in root.EnumerateObject())
        {
            var allowed = property.Name == "conditions" || property.Name == idsName || (property.Name == "include_children" && kind == ScopeRule.OrgUnits);
            if (!allowed)
            {
                return ScopeDefinitionInvalid;
            }
        }

        if (idsName is not null)
        {
            if (!root.TryGetProperty(idsName, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0
                || array.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || !Guid.TryParse(e.GetString(), out _)))
            {
                return ScopeDefinitionInvalid;
            }

            var ids = array.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).Distinct().ToArray();
            var found = await IdentityDb.ScalarAsync<long>(c, tx, $"select count(*) from {table} where id = any($1) and not is_deleted", ct, (object)ids).ConfigureAwait(false);
            if (found != ids.Length)
            {
                return ScopeDefinitionInvalid;
            }
        }

        if (root.TryGetProperty("include_children", out var children) && children.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ScopeDefinitionInvalid;
        }

        if (root.TryGetProperty("conditions", out var conditions))
        {
            if (conditions.ValueKind != JsonValueKind.Array)
            {
                return ScopeDefinitionInvalid;
            }

            foreach (var condition in conditions.EnumerateArray())
            {
                if (condition.ValueKind != JsonValueKind.Object
                    || !condition.TryGetProperty("attribute", out var attribute) || attribute.ValueKind != JsonValueKind.String
                    || condition.EnumerateObject().Count() != 2)
                {
                    return ScopeDefinitionInvalid;
                }

                var ok = condition.TryGetProperty("equals", out var equals) ? equals.ValueKind == JsonValueKind.String
                    : condition.TryGetProperty("in", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0
                      && values.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String);
                if (!ok)
                {
                    return ScopeDefinitionInvalid;
                }
            }
        }

        return null;
    }

    // ================================================================== iam.* settings

    public Task<AccessResult> SetAsync(string key, string? layer, Guid? scopeId, JsonElement? value, string? reason, CancellationToken cancellationToken) =>
        RunAsync(ManageSettings, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            if (value is not { ValueKind: not JsonValueKind.Undefined } v)
            {
                return AccessResult.Invalid(SettingValueInvalid);
            }

            var change = new AccessChange
            {
                Kind = AccessChange.SetSetting,
                SettingKey = key,
                SettingLayer = layer ?? "tenant",
                SettingScopeId = scopeId,
                SettingValue = v.GetRawText(),
                Reason = reason,
                SettingRowId = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
                    select id from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3
                    """, cancellationToken, key, layer ?? "tenant", scopeId).ConfigureAwait(false) ?? Guid.CreateVersion7(now),
            };
            if (await ValidateSettingAsync(c, tx, change, cancellationToken).ConfigureAwait(false) is { } invalid)
            {
                return invalid;
            }

            // Switching off maker-checker or privileged MFA needs a second administrator when there is one (finding 5).
            if (TwoPersonSwitches.Contains(key) && change.SettingLayer == "tenant" && change.SettingValue == "false"
                && await AccessDb.SettingOnAsync(c, tx, key, cancellationToken).ConfigureAwait(false)
                && (await TenantAdminGuard.CountAsync(c, tx, Today(now), cancellationToken).ConfigureAwait(false)).Active >= 2)
            {
                return await SubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
            }

            return await ApplySettingAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>The key is an identity or access setting, the layer is one it allows, the scope exists and the value has the setting's type.</summary>
    private static async Task<AccessResult?> ValidateSettingAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        if (IdentitySettings.All.FirstOrDefault(d => d.Key == change.SettingKey) is not { } definition)
        {
            return AccessResult.Invalid(SettingUnknown);
        }

        var layer = change.SettingLayer;
        if (layer is null || !definition.Layers.Contains(layer) || (layer == "tenant") != (change.SettingScopeId is null))
        {
            return AccessResult.Invalid(SettingLayerInvalid);
        }

        if (layer is "role" or "user")
        {
            var table = layer == "role" ? "iam.roles" : "iam.users";
            if (!await IdentityDb.ScalarAsync<bool>(c, tx, $"select exists (select 1 from {table} where id = $1 and not is_deleted)", ct, change.SettingScopeId).ConfigureAwait(false))
            {
                return AccessResult.Invalid(SettingLayerInvalid);
            }
        }

        using var doc = JsonDocument.Parse(change.SettingValue ?? "null");
        var value = doc.RootElement;
        var ok = definition.DataType switch
        {
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && (definition.Min is not { } min || number >= min),
            "list" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String),
            _ => false,
        };
        return ok ? null : AccessResult.Invalid(SettingValueInvalid);
    }

    /// <summary>Writes the value, its history row (FR-KRN-SET-04) and an <c>iam.setting_changed</c> event.</summary>
    private static async Task<AccessResult> ApplySettingAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var old = await IdentityDb.ScalarAsync<string>(c, tx, """
            select value::text from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3 for update
            """, ct, change.SettingKey, change.SettingLayer, change.SettingScopeId).ConfigureAwait(false);
        var id = await IdentityDb.ScalarAsync<Guid>(c, tx, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
            on conflict (setting_key, layer, scope_id) do update set value = excluded.value, updated_at = excluded.updated_at,
                updated_by = excluded.updated_by, row_version = cfg.setting_values.row_version + 1
            returning id
            """, ct, change.SettingRowId, change.SettingKey, change.SettingLayer, change.SettingScopeId, new JsonPayload(change.SettingValue!), now, by).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into cfg.setting_value_history (id, setting_value_id, setting_key, layer, scope_id, old_value, new_value, reason, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """, ct, Guid.CreateVersion7(now), id, change.SettingKey, change.SettingLayer, change.SettingScopeId,
            old is null ? null : new JsonPayload(old), new JsonPayload(change.SettingValue!), change.Reason, now, by).ConfigureAwait(false);
        await AccessDb.RaiseAsync(c, tx, AccessEvents.SettingChanged, "cfg.setting_value", id, by, now, new
        {
            key = change.SettingKey,
            layer = change.SettingLayer,
            scope_id = change.SettingScopeId,
            old_value = old is null ? (JsonElement?)null : JsonDocument.Parse(old).RootElement,
            new_value = JsonDocument.Parse(change.SettingValue!).RootElement,
            reason = change.Reason,
        }, ct).ConfigureAwait(false);
        return AccessResult.Done(id);
    }
}
