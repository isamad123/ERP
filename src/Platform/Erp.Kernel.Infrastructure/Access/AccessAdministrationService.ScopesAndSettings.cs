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
        SetAsync(key, layer, scopeId, value, reason, null, cancellationToken);

    /// <remarks><paramref name="revertedFrom"/>: the history row a revert restores (slice S1.09a).</remarks>
    public async Task<AccessResult> SetAsync(string key, string? layer, Guid? scopeId, JsonElement? value, string? reason, Guid? revertedFrom, CancellationToken cancellationToken)
    {
        if (value is not { ValueKind: not JsonValueKind.Undefined } v)
        {
            return string.IsNullOrWhiteSpace(reason) ? AccessResult.Invalid(ReasonRequired) : AccessResult.Invalid(SettingValueInvalid);
        }

        return await ChangeIdentitySettingAsync(key, layer, scopeId, v.GetRawText(), reason, revertedFrom, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a layer's value of an <c>iam.*</c> setting (reset to inherited, slice S1.09a), with the same rules as a change.</summary>
    public Task<AccessResult> ResetAsync(string key, string? layer, Guid? scopeId, string? reason, Guid? revertedFrom, CancellationToken cancellationToken) =>
        ChangeIdentitySettingAsync(key, layer, scopeId, null, reason, revertedFrom, cancellationToken);

    private async Task<AccessResult> ChangeIdentitySettingAsync(string key, string? layer, Guid? scopeId, string? valueJson, string? reason, Guid? revertedFrom, CancellationToken cancellationToken)
    {
        var result = await RunAsync(ManageSettings, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            // Identity settings belong to the whole customer: a Company Admin's company-scoped settings permission (S1.09a,
            // ruling Q16) does not reach them.
            if (!await caller.CoversCompanyAsync(ManageSettings, null, cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.Forbidden(CompanyNotCovered);
            }

            var change = new AccessChange
            {
                Kind = AccessChange.SetSetting,
                SettingKey = key,
                SettingLayer = layer ?? "tenant",
                SettingScopeId = scopeId,
                SettingValue = valueJson,
                SettingReset = valueJson is null,
                SettingRevertedFrom = revertedFrom,
                Reason = reason,
                SettingRowId = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
                    select id from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3
                    """, cancellationToken, key, layer ?? "tenant", scopeId).ConfigureAwait(false) ?? Guid.CreateVersion7(now),
            };
            if (await ValidateSettingAsync(c, tx, change, cancellationToken).ConfigureAwait(false) is { } invalid)
            {
                return invalid;
            }

            // Switching off maker-checker or privileged MFA needs a second administrator when there is one (finding 5); a
            // reset switches to the shipped default.
            var after = valueJson ?? await IdentityDb.ScalarAsync<string>(c, tx, "select default_value::text from cfg.settings where key = $1", cancellationToken, key).ConfigureAwait(false);
            if (TwoPersonSwitches.Contains(key) && change.SettingLayer == "tenant" && after == "false"
                && await AccessDb.SettingOnAsync(c, tx, key, cancellationToken).ConfigureAwait(false)
                && (await TenantAdminGuard.CountAsync(c, tx, Today(now), cancellationToken).ConfigureAwait(false)).Active >= 2)
            {
                return await SubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
            }

            return await ApplySettingAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        SettingsChanged(result);
        return result;
    }

    /// <summary>
    /// Holds a change of a setting that needs approval (slice S1.09a, FR-ADM-SET-04, ruling Q12) as a maker-checker request.
    /// The settings service has validated it and checked the caller's right; the approval checks both again.
    /// </summary>
    public Task<AccessResult> HoldSettingChangeAsync(string key, string layer, Guid? scopeId, string? valueJson, string reason, Guid? revertedFrom, CancellationToken cancellationToken) =>
        RunAsync(Contracts.Settings.SettingsPermissions.Update, async (c, tx, now, by) =>
        {
            var change = new AccessChange
            {
                Kind = AccessChange.ChangeSetting,
                SettingKey = key,
                SettingLayer = layer,
                SettingScopeId = scopeId,
                SettingValue = valueJson,
                SettingReset = valueJson is null,
                SettingRevertedFrom = revertedFrom,
                Reason = reason,
                SettingRowId = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
                    select id from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3
                    """, cancellationToken, key, layer, scopeId).ConfigureAwait(false) ?? Guid.CreateVersion7(now),
            };
            return await ValidateHeldSettingAsync(c, tx, change, cancellationToken).ConfigureAwait(false)
                ?? await SubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>A held setting change is still valid when it is decided: same checks as when it was made (definitions may have changed).</summary>
    private static async Task<AccessResult?> ValidateHeldSettingAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        if (change.SettingKey is not { } key || key.StartsWith("iam.", StringComparison.Ordinal) || change.SettingLayer is not { } layer)
        {
            return AccessResult.Invalid(SettingUnknown);
        }

        var definition = (await Settings.SettingsStore.DefinitionsAsync(c, tx, [key], ct).ConfigureAwait(false)).GetValueOrDefault(key);
        JsonElement? value = change.SettingReset ? null : Settings.SettingsStore.Json(change.SettingValue ?? "null");
        return await Settings.SettingWrites.ProblemAsync(c, tx, definition, layer, change.SettingScopeId, value, ct).ConfigureAwait(false) is { } problem
            ? AccessResult.Invalid(problem)
            : null;
    }

    /// <summary>After a settings change this host drops its cached values, so the change is in force here at once (S1.09a).</summary>
    private void SettingsChanged(AccessResult result)
    {
        if (result.Outcome == AccessOutcome.Done)
        {
            (services.GetService(typeof(Settings.SettingsService)) as Settings.SettingsService)?.Invalidate();
        }
    }

    /// <summary>
    /// The key is an identity or access setting, the layer is one it allows, the scope exists and the value has the setting's
    /// type and rules. Since slice S1.09a the definition comes from cfg.settings (the kernel manifest), not from code.
    /// </summary>
    private static async Task<AccessResult?> ValidateSettingAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        if (change.SettingKey is not { } key || !key.StartsWith("iam.", StringComparison.Ordinal)
            || (await Settings.SettingsStore.DefinitionsAsync(c, tx, [key], ct).ConfigureAwait(false)).GetValueOrDefault(key) is not { IsVisible: true } definition)
        {
            return AccessResult.Invalid(SettingUnknown);
        }

        var layer = change.SettingLayer;
        if (layer is null || layer == "pack" || !definition.Layers.Contains(layer) || (layer == "tenant") != (change.SettingScopeId is null))
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

        if (layer == "company"
            && !await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from org.companies where id = $1 and not is_deleted)", ct, change.SettingScopeId).ConfigureAwait(false))
        {
            return AccessResult.Invalid(SettingLayerInvalid);
        }

        // A reset carries no value (S1.09a): nothing to check.
        if (change.SettingReset)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(change.SettingValue ?? "null");
        return definition.Definition.Validation.Problem(definition.DataType, doc.RootElement) is null ? null : AccessResult.Invalid(SettingValueInvalid);
    }

    /// <summary>Writes (or removes) the value with its history row and event (FR-KRN-SET-04), through the one settings writer.</summary>
    private static async Task<AccessResult> ApplySettingAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var id = await Settings.SettingWrites.ApplyAsync(c, tx, change.SettingKey!, change.SettingLayer!, change.SettingScopeId,
            change.SettingReset ? null : change.SettingValue, change.Reason, change.SettingRevertedFrom, change.SettingRowId, now, by, ct).ConfigureAwait(false);
        return id is { } written ? AccessResult.Done(written) : AccessResult.Invalid(Contracts.Settings.SettingsErrors.NothingToReset);
    }
}
