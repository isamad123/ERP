using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Application.Settings;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>
/// The settings centre and flag services (slice S1.09a build items 2, 3 and 5; FR-KRN-SET-01 to 06, FR-ADM-SET-01 to 04 and
/// 06, FR-ADM-USR-10; rulings Q11, Q12, Q14, Q16).
/// <list type="bullet">
/// <item>Reading needs <c>cfg.setting_values.read</c> (flags: <c>cfg.feature_flag_overrides.read</c>); a company in context
/// must be covered by the caller's assignment.</item>
/// <item>Changing needs <c>cfg.setting_values.update</c> covering the company of a company-layer value, or all companies for
/// every other layer (a Company Admin changes the company layer of its own companies only). The pack layer is written by the
/// pack loader only (S1.23).</item>
/// <item>Every change, reset and revert has a reason, a history row and an audit entry (<see cref="SettingWrites"/>); a
/// setting that needs approval is held as a maker-checker request and applies when another administrator approves it.</item>
/// <item><c>iam.*</c> keys go through the access settings service, which keeps the S1.06 rules (CR-025).</item>
/// <item>Self-service settings: the caller's own user layer only, with the permission every role holds for its own account.</item>
/// </list>
/// After a change this host drops its cached values; other hosts follow within about a second (Q13).
/// </summary>
public sealed class SettingsAdministrationService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess caller,
    SettingsService settings,
    AccessAdministrationService access,
    IEnumerable<ISettingChangeGuard> guards) : ISettingsAdministration
{
    public const string Forbidden = "errors.access.forbidden";

    private static readonly string[] FlagLayers = [FlagResolution.Tenant, FlagResolution.Company, FlagResolution.UserGroup];

    // ================================================================== reading

    public async Task<IReadOnlyList<SettingListItem>> ListAsync(string? search, string? moduleId, string? area, Guid? companyId, CancellationToken cancellationToken)
    {
        await RequireReadAsync(SettingsPermissions.Read, companyId, false, cancellationToken).ConfigureAwait(false);
        var snapshot = await settings.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var last = await LastChangesAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<SettingListItem>();
        foreach (var row in snapshot.Definitions.Values.Where(d => d.IsVisible
            && (moduleId is null || d.Module == moduleId)
            && (area is null || d.Area == area || (d.Area is not null && d.Area.StartsWith(area + ".", StringComparison.Ordinal)))
            && Matches(d, search)))
        {
            var (_, resolved) = (await settings.ResolveAsync(row.Key, companyId, null, null, cancellationToken).ConfigureAwait(false))!.Value;
            list.Add(Item(row, resolved, last.GetValueOrDefault(row.Key)));
        }

        return list;
    }

    public async Task<SettingDetail?> DetailAsync(string key, Guid? companyId, Guid? roleId, Guid? userId, CancellationToken cancellationToken)
    {
        await RequireReadAsync(SettingsPermissions.Read, companyId, false, cancellationToken).ConfigureAwait(false);
        if ((roleId is not null || userId is not null) && !await caller.CoversCompanyAsync(SettingsPermissions.Read, null, cancellationToken).ConfigureAwait(false))
        {
            // A role's or a user's values may belong to other companies: reading them needs all companies (S1.09a review finding 6).
            throw new AccessDeniedException(SettingsErrors.CompanyNotCovered);
        }

        return await LayeredAsync(key, companyId, roleId, userId, visibleOnly: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SettingHistoryItem>?> HistoryAsync(string key, string? layer, Guid? scopeId, CancellationToken cancellationToken)
    {
        await RequireReadAsync(SettingsPermissions.Read, null, allowCompanyScoped: true, cancellationToken).ConfigureAwait(false);
        var snapshot = await settings.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Definitions.TryGetValue(key, out var row) || !row.IsVisible)
        {
            return null;
        }

        // A caller holding the permission for some companies only sees those companies' rows (ruling Q16). Their rows are read
        // without the limit and filtered first, so other companies' changes never push them out (S1.09a review finding 7).
        const int Limit = 500;
        var all = await caller.CoversCompanyAsync(SettingsPermissions.Read, null, cancellationToken).ConfigureAwait(false);
        var items = new List<SettingHistoryItem>();
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using (var command = IdentityDb.Command(c, null, """
            select id, setting_key, layer, scope_id, old_value::text, new_value::text, reason, created_at, created_by from cfg.setting_value_history
            where setting_key = $1 and ($2::text is null or layer = $2) and ($3::uuid is null or scope_id = $3) and ($4 or layer = 'company')
            order by created_at desc, id desc limit (case when $4 then 500 end)
            """, key, (object?)layer ?? DBNull.Value, (object?)scopeId ?? DBNull.Value, all))
        {
            command.Parameters[1].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new SettingHistoryItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : SettingsStore.Json(reader.GetString(4)), reader.IsDBNull(5) ? null : SettingsStore.Json(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetGuid(8)));
            }
        }

        if (all)
        {
            return items;
        }

        var visible = new List<SettingHistoryItem>();
        var covered = new Dictionary<Guid, bool>();
        foreach (var item in items.Where(i => i.Layer == SettingLayers.Company && i.ScopeId is not null))
        {
            var company = item.ScopeId!.Value;
            if (!covered.TryGetValue(company, out var yes))
            {
                covered[company] = yes = await caller.CoversCompanyAsync(SettingsPermissions.Read, company, cancellationToken).ConfigureAwait(false);
            }

            if (yes)
            {
                visible.Add(item);
                if (visible.Count == Limit)
                {
                    break;
                }
            }
        }

        return visible;
    }

    public async Task<SettingDetail?> MineAsync(string key, CancellationToken cancellationToken)
    {
        await RequireAsync(SettingsPermissions.SelfService, cancellationToken).ConfigureAwait(false);
        var snapshot = await settings.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Definitions.TryGetValue(key, out var row) && row is { IsVisible: true, SelfService: true }
            ? await LayeredAsync(key, null, null, actor.UserId, visibleOnly: true, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<FlagItem>> FlagsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        await RequireReadAsync(SettingsPermissions.FlagsRead, companyId, false, cancellationToken).ConfigureAwait(false);
        var snapshot = await settings.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<FlagItem>();
        foreach (var row in snapshot.Flags.Values)
        {
            var (_, resolved, licensed) = (await settings.FlagAsync(row.Key, companyId, actor.UserId, cancellationToken).ConfigureAwait(false))!.Value;
            var overrides = snapshot.Overrides[row.Key]
                .Select(o => new FlagOverrideItem(o.Override.Layer, o.Override.ScopeId, o.Override.Enabled, new SettingChangeInfo(o.Override.UpdatedAt, o.Override.UpdatedBy)))
                .ToList();
            list.Add(new FlagItem(row.Key, row.Module, row.Name, row.Description, row.Help, row.EntitlementKey, licensed, row.DefaultEnabled,
                resolved.Enabled, resolved.Source, resolved.ScopeId, overrides));
        }

        return list;
    }

    // ================================================================== changing

    public Task<AccessResult> SetAsync(string key, SetSettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Value is not { ValueKind: not JsonValueKind.Undefined } value
            ? Task.FromResult(AccessResult.Invalid(string.IsNullOrWhiteSpace(request.Reason) ? SettingsErrors.ReasonRequired : SettingsErrors.ValueInvalid))
            : ChangeAsync(key, request.Layer ?? SettingLayers.Tenant, request.ScopeId, value, request.Reason, null, selfService: false, cancellationToken);
    }

    public Task<AccessResult> ResetAsync(string key, ResetSettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(key, request.Layer ?? SettingLayers.Tenant, request.ScopeId, null, request.Reason, null, selfService: false, cancellationToken);
    }

    public async Task<AccessResult> RevertAsync(Guid historyId, string? reason, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(SettingsPermissions.Update, cancellationToken).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(Forbidden);
        }

        string key;
        string layer;
        Guid? scope;
        JsonElement? old;
        await using (var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false))
        await using (var command = IdentityDb.Command(c, null, "select setting_key, layer, scope_id, old_value::text from cfg.setting_value_history where id = $1", historyId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.Invalid(SettingsErrors.HistoryNotFound);
            }

            (key, layer, scope, old) = (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : SettingsStore.Json(reader.GetString(3)));
        }

        // The old value of the row becomes a new change; no old value means the layer had none: the revert removes it.
        return await ChangeAsync(key, layer, scope, old, reason, historyId, selfService: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccessResult> SetMineAsync(string key, SelfSettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(SettingsPermissions.SelfService, cancellationToken).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(Forbidden);
        }

        if (request.Value is not { ValueKind: not JsonValueKind.Undefined } value)
        {
            return AccessResult.Invalid(SettingsErrors.ValueInvalid);
        }

        return await ChangeAsync(key, SettingLayers.User, actor.UserId, value, request.Reason, null, selfService: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccessResult> SetFlagAsync(string key, SetFlagRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Enabled is not { } enabled
            ? string.IsNullOrWhiteSpace(request.Reason) ? AccessResult.Invalid(SettingsErrors.ReasonRequired) : AccessResult.Invalid(SettingsErrors.FlagValueInvalid)
            : await ChangeFlagAsync(key, request.Layer ?? FlagResolution.Tenant, request.ScopeId, enabled, request.Reason, cancellationToken).ConfigureAwait(false);
    }

    public Task<AccessResult> ResetFlagAsync(string key, ResetFlagRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeFlagAsync(key, request.Layer ?? FlagResolution.Tenant, request.ScopeId, null, request.Reason, cancellationToken);
    }

    /// <summary>Set (value given) or reset (null) one layer of a setting, at once or held for approval.</summary>
    private async Task<AccessResult> ChangeAsync(string key, string layer, Guid? scopeId, JsonElement? value, string? reason, Guid? revertedFrom, bool selfService, CancellationToken ct)
    {
        if (key.StartsWith("iam.", StringComparison.Ordinal) && !selfService)
        {
            // An iam.* setting cannot be held for approval (its own S1.06 rules apply); one defined so is refused rather than
            // applied without the approval it asks for (S1.09a review finding 3; the generator refuses it too).
            if ((await settings.SnapshotAsync(ct).ConfigureAwait(false)).Definitions.TryGetValue(key, out var identity) && identity.RequiresApproval)
            {
                return AccessResult.Invalid(SettingsErrors.ApprovalNotSupported);
            }

            // The S1.06 rules (history, events, second tenant admin) live in the access settings service (CR-025).
            return value is { } v
                ? await access.SetAsync(key, layer, scopeId, v, reason, revertedFrom, ct).ConfigureAwait(false)
                : await access.ResetAsync(key, layer, scopeId, reason, revertedFrom, ct).ConfigureAwait(false);
        }

        var permission = selfService ? SettingsPermissions.SelfService : SettingsPermissions.Update;
        var result = await RunAsync(permission, async (c, tx, now, by) =>
        {
            if (!selfService && string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(SettingsErrors.ReasonRequired);
            }

            var definition = (await SettingsStore.DefinitionsAsync(c, tx, [key], ct).ConfigureAwait(false)).GetValueOrDefault(key);
            // A setting that needs approval is never self-service (S1.09a review finding 8; the generator refuses the pair).
            if (selfService && definition is not { SelfService: true, RequiresApproval: false } || selfService && key.StartsWith("iam.", StringComparison.Ordinal))
            {
                return AccessResult.Invalid(definition is null ? SettingsErrors.Unknown : SettingsErrors.NotSelfService);
            }

            if (await SettingWrites.ProblemAsync(c, tx, definition, layer, scopeId, value, ct).ConfigureAwait(false) is { } problem)
            {
                return AccessResult.Invalid(problem);
            }

            if (!selfService && !await caller.CoversCompanyAsync(permission, layer == SettingLayers.Company ? scopeId : null, ct).ConfigureAwait(false))
            {
                return AccessResult.Forbidden(SettingsErrors.CompanyNotCovered);
            }

            // A module's own rule on the change (slice S1.22a): refused before anything is written or held for approval.
            foreach (var guard in guards.Where(g => string.Equals(g.Key, key, StringComparison.Ordinal)))
            {
                if (await guard.ProblemAsync(layer == SettingLayers.Company ? scopeId : null, value, ct).ConfigureAwait(false) is { } refused)
                {
                    return AccessResult.Invalid(refused);
                }
            }

            var valueJson = value?.GetRawText();
            if (definition!.RequiresApproval)
            {
                return value is null && !await ExistsAsync(c, tx, key, layer, scopeId, ct).ConfigureAwait(false)
                    ? AccessResult.Invalid(SettingsErrors.NothingToReset)
                    : AccessResult.Pending(Guid.Empty); // held below, in the access service's own transaction
            }

            var id = await SettingWrites.ApplyAsync(c, tx, key, layer, scopeId, valueJson, string.IsNullOrWhiteSpace(reason) ? null : reason, revertedFrom, null, now, by, ct).ConfigureAwait(false);
            return id is { } written ? AccessResult.Done(written) : AccessResult.Invalid(SettingsErrors.NothingToReset);
        }, commitPending: false, ct).ConfigureAwait(false);

        if (result is { Outcome: AccessOutcome.Pending, RequestId: var held } && held == Guid.Empty)
        {
            return await access.HoldSettingChangeAsync(key, layer, scopeId, value?.GetRawText(), reason!, revertedFrom, ct).ConfigureAwait(false);
        }

        return result;
    }

    private Task<AccessResult> ChangeFlagAsync(string key, string layer, Guid? scopeId, bool? enabled, string? reason, CancellationToken ct) =>
        RunAsync(SettingsPermissions.FlagsUpdate, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(SettingsErrors.ReasonRequired);
            }

            if (!await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from cfg.feature_flags where key = $1)", ct, key).ConfigureAwait(false))
            {
                return AccessResult.Invalid(SettingsErrors.FlagUnknown);
            }

            if (!FlagLayers.Contains(layer) || (layer == FlagResolution.Tenant) != (scopeId is null))
            {
                return AccessResult.Invalid(SettingsErrors.FlagLayerInvalid);
            }

            var table = layer == FlagResolution.Company ? "org.companies" : layer == FlagResolution.UserGroup ? "iam.user_groups" : null;
            if (table is not null && !await IdentityDb.ScalarAsync<bool>(c, tx, $"select exists (select 1 from {table} where id = $1 and not is_deleted)", ct, scopeId).ConfigureAwait(false))
            {
                return AccessResult.Invalid(SettingsErrors.ScopeNotFound);
            }

            if (!await caller.CoversCompanyAsync(SettingsPermissions.FlagsUpdate, layer == FlagResolution.Company ? scopeId : null, ct).ConfigureAwait(false))
            {
                return AccessResult.Forbidden(SettingsErrors.CompanyNotCovered);
            }

            var id = await SettingWrites.ApplyFlagAsync(c, tx, key, layer, scopeId, enabled, reason, now, by, ct).ConfigureAwait(false);
            return id is { } written ? AccessResult.Done(written) : AccessResult.Invalid(SettingsErrors.NothingToReset);
        }, commitPending: true, ct);

    // ================================================================== machinery

    private delegate Task<AccessResult> Work(NpgsqlConnection c, NpgsqlTransaction tx, DateTimeOffset now, Guid by);

    /// <summary>Permission check, one transaction, commit only when applied; database refusals become answers.</summary>
    private async Task<AccessResult> RunAsync(string permission, Work work, bool commitPending, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(Forbidden);
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        AccessResult result;
        await using (var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false))
        await using (var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            try
            {
                result = await work(c, tx, now, by).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation)
            {
                return AccessResult.Conflict("errors.data.duplicate");
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
            {
                return AccessResult.Invalid(ex.SqlState == PostgresErrorCodes.CheckViolation ? "errors.data.rule_violated" : "errors.data.reference_not_found");
            }

            if (result.Outcome == AccessOutcome.Done || (commitPending && result.Outcome == AccessOutcome.Pending))
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }

        if (result.Outcome == AccessOutcome.Done)
        {
            settings.Invalidate();
        }

        return result;
    }

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new AccessDeniedException(Forbidden);
        }
    }

    /// <summary>
    /// The read permission, covering the company in context; customer-wide reading (no company) needs all companies unless
    /// <paramref name="allowCompanyScoped"/> (the service then filters what it shows).
    /// </summary>
    private async Task RequireReadAsync(string permission, Guid? companyId, bool allowCompanyScoped, CancellationToken ct)
    {
        await RequireAsync(permission, ct).ConfigureAwait(false);
        if ((companyId is not null || !allowCompanyScoped) && !await caller.CoversCompanyAsync(permission, companyId, ct).ConfigureAwait(false))
        {
            throw new AccessDeniedException(SettingsErrors.CompanyNotCovered);
        }
    }

    private async Task<SettingDetail?> LayeredAsync(string key, Guid? companyId, Guid? roleId, Guid? userId, bool visibleOnly, CancellationToken ct)
    {
        if (await settings.ResolveAsync(key, companyId, userId, roleId, ct).ConfigureAwait(false) is not { } found || (visibleOnly && !found.Row.IsVisible))
        {
            return null;
        }

        var last = (await LastChangesAsync(ct).ConfigureAwait(false)).GetValueOrDefault(key);
        var layers = found.Resolved.Layers
            .Select(l => new SettingLayerItem(l.Layer, l.ScopeId, l.Value, l.UpdatedAt is { } at ? new SettingChangeInfo(at, l.UpdatedBy!.Value) : null, l.Ignored))
            .ToList();
        return new SettingDetail(Item(found.Row, found.Resolved, last), layers);
    }

    private static SettingListItem Item(SettingRow row, ResolvedSetting resolved, SettingChangeInfo? last) =>
        new(row.Key, row.Module, row.Area, row.Name, row.Description, row.Help, row.DataType, row.Unit, row.Validation, row.Layers, row.RequiresApproval,
            row.SelfService, row.Default, resolved.Value, resolved.Layer, resolved.ScopeId, last);

    /// <summary>Search over key, names and descriptions in both languages (FR-KRN-SET-03).</summary>
    private static bool Matches(SettingRow row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var text = search.Trim();
        static IEnumerable<string> Texts(JsonElement? element) => element is { ValueKind: JsonValueKind.Object } e
            ? e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!)
            : [];
        return new[] { row.Key }.Concat(Texts(row.Name)).Concat(Texts(row.Description)).Concat(Texts(row.Help))
            .Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The latest history row of every setting: when and by whom it last changed (FR-ADM-SET-01).</summary>
    private async Task<Dictionary<string, SettingChangeInfo>> LastChangesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, SettingChangeInfo>(StringComparer.Ordinal);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var command = IdentityDb.Command(c, null, """
            select distinct on (setting_key) setting_key, created_at, created_by from cfg.setting_value_history order by setting_key, created_at desc, id desc
            """);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new SettingChangeInfo(reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2));
        }

        return result;
    }

    private static Task<bool> ExistsAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, string layer, Guid? scopeId, CancellationToken ct) =>
        IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3)", ct, key, layer, scopeId);
}
