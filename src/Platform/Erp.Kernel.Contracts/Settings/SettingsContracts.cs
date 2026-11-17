using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Settings;

/// <summary>
/// The context a setting is resolved for (slice S1.09 ruling Q11): the company of the document or screen in hand and the
/// user. Service code fills it from the server's own knowledge (the signed-in caller, the document's company), never from
/// request input. Roles come from the user's assignments on the day.
/// </summary>
public sealed record SettingContext(Guid? CompanyId, Guid? UserId)
{
    /// <summary>Customer-wide: no company and no user.</summary>
    public static SettingContext Customer { get; } = new(null, null);
}

/// <summary>An effective value and where it came from (FR-KRN-SET-02): layer product, pack, tenant, company, role or user.</summary>
public sealed record EffectiveSetting(string Key, JsonElement Value, string Layer, Guid? ScopeId);

/// <summary>
/// Settings for modules (FR-KRN-SET-02, FR-KRN-SET-06): the effective value of a setting declared in a settings manifest.
/// Values are cached per customer and refreshed within about a second of a change on any host (ruling Q13).
/// </summary>
public interface ISettings
{
    /// <summary>The effective value for the company and the signed-in caller (null company: customer-wide).</summary>
    Task<EffectiveSetting> GetAsync(string key, Guid? companyId, CancellationToken cancellationToken);

    /// <summary>The effective value for an explicit context (background work, or a document's company and owner).</summary>
    Task<EffectiveSetting> GetAsync(string key, SettingContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Feature flags (FR-KRN-SET-05, ruling Q14): whether a capability is switched on for the company and the signed-in caller.
/// A flag whose entitlement key is not licensed is always off.
/// </summary>
public interface IFeatureFlags
{
    Task<bool> IsOnAsync(string key, Guid? companyId, CancellationToken cancellationToken);
}

/// <summary>
/// The feature keys the customer is licensed for (D-24 catalogue, for example <c>hrm.payroll</c>). Slice S1.09a reads them
/// from the customer's copy of the signed entitlement; slice S1.09b replaces the source with the entitlement service.
/// </summary>
public interface ILicensedFeatures
{
    Task<bool> IsLicensedAsync(string featureKey, CancellationToken cancellationToken);
}

/// <summary>Permissions of the settings and flag endpoints (ruling Q16). The generated writes are closed (CR-036); the codes stay.</summary>
public static class SettingsPermissions
{
    public const string Read = "cfg.setting_values.read";
    public const string Update = "cfg.setting_values.update";
    public const string FlagsRead = "cfg.feature_flag_overrides.read";
    public const string FlagsUpdate = "cfg.feature_flag_overrides.update";
    public const string HistoryRead = "cfg.setting_value_history.read";

    /// <summary>Self-service settings use the permission every baseline role holds for its own account (S1.05, S1.06).</summary>
    public const string SelfService = "iam.self.manage";

    /// <summary>Update keeps its code although the generated endpoints are closed (CR-036); read stays generated.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Update, "cfg", "permissions.cfg.setting_values.update", false),
        new(FlagsUpdate, "cfg", "permissions.cfg.feature_flag_overrides.update", false),
    ];
}

/// <summary>Events of settings and flags (in cfg.outbox_messages, actor in <c>headers.actor</c>). <c>iam.*</c> keys keep <c>iam.setting_changed</c>.</summary>
public static class SettingsEvents
{
    public const string Changed = "cfg.setting_changed";
    public const string Reset = "cfg.setting_reset";
    public const string FlagChanged = "cfg.feature_flag_changed";
    public const string FlagReset = "cfg.feature_flag_reset";
}

/// <summary>Error keys of the settings services (texts in <c>Settings/Localization/settings.*.json</c>).</summary>
public static class SettingsErrors
{
    public const string Unknown = "errors.settings.unknown_key";
    public const string LayerInvalid = "errors.settings.layer_invalid";
    public const string ValueInvalid = "errors.settings.value_invalid";
    public const string ReasonRequired = "errors.settings.reason_required";
    public const string ScopeNotFound = "errors.settings.scope_not_found";
    public const string PackLayer = "errors.settings.pack_layer";
    public const string NotSelfService = "errors.settings.not_self_service";
    public const string NothingToReset = "errors.settings.nothing_to_reset";
    public const string HistoryNotFound = "errors.settings.history_not_found";
    public const string CompanyNotCovered = "errors.settings.company_not_covered";
    public const string FlagUnknown = "errors.flags.unknown_key";
    public const string FlagLayerInvalid = "errors.flags.layer_invalid";
    public const string FlagValueInvalid = "errors.flags.value_invalid";
}

/// <summary>Body of <c>PUT /api/cfg/settings/{key}</c>: the layer (default tenant), its company, role or user, the value and a reason.</summary>
public sealed record SetSettingRequest(string? Layer, Guid? ScopeId, JsonElement? Value, string? Reason);

/// <summary>Body of <c>POST /api/cfg/settings/{key}/reset</c>.</summary>
public sealed record ResetSettingRequest(string? Layer, Guid? ScopeId, string? Reason);

/// <summary>Body of a revert, a self-service change and a reason-only action.</summary>
public sealed record ReasonRequest(string? Reason);

/// <summary>Body of <c>PUT /api/cfg/me/settings/{key}</c>.</summary>
public sealed record SelfSettingRequest(JsonElement? Value, string? Reason);

/// <summary>Body of <c>PUT /api/cfg/feature-flags/{key}</c>: layer tenant, company or user_group, its scope, on or off, and a reason.</summary>
public sealed record SetFlagRequest(string? Layer, Guid? ScopeId, bool? Enabled, string? Reason);

/// <summary>Body of <c>POST /api/cfg/feature-flags/{key}/reset</c>.</summary>
public sealed record ResetFlagRequest(string? Layer, Guid? ScopeId, string? Reason);

/// <summary>Last change of a value: when and by whom.</summary>
public sealed record SettingChangeInfo(DateTimeOffset At, Guid By);

/// <summary>A line of the settings centre (FR-ADM-SET-01, FR-ADM-SET-02, FR-KRN-SET-03).</summary>
public sealed record SettingListItem(
    string Key,
    string Module,
    string? Area,
    JsonElement? Name,
    JsonElement Description,
    JsonElement? Help,
    string Type,
    string? Unit,
    JsonElement Validation,
    IReadOnlyList<string> Layers,
    bool RequiresApproval,
    bool SelfService,
    JsonElement Default,
    JsonElement Value,
    string Source,
    Guid? SourceScopeId,
    SettingChangeInfo? LastChange);

/// <summary>One layer of the layered view (FR-ADM-SET-03); <c>Ignored</c>: invalid, layer_not_allowed or overridden.</summary>
public sealed record SettingLayerItem(string Layer, Guid? ScopeId, JsonElement Value, SettingChangeInfo? Changed, string? Ignored);

/// <summary>The layered view of one setting for a context.</summary>
public sealed record SettingDetail(SettingListItem Setting, IReadOnlyList<SettingLayerItem> Layers);

/// <summary>A history row (FR-ADM-SET-06); a null value means "no value at this layer".</summary>
public sealed record SettingHistoryItem(Guid Id, string Key, string Layer, Guid? ScopeId, JsonElement? OldValue, JsonElement? NewValue, string? Reason, DateTimeOffset At, Guid By);

/// <summary>An override of a flag.</summary>
public sealed record FlagOverrideItem(string Layer, Guid? ScopeId, bool Enabled, SettingChangeInfo Changed);

/// <summary>A flag with its state for the caller and company in context, its source and its overrides (FR-KRN-SET-05).</summary>
public sealed record FlagItem(
    string Key,
    string? Module,
    JsonElement? Name,
    JsonElement Description,
    JsonElement? Help,
    string? EntitlementKey,
    bool Licensed,
    bool Default,
    bool Enabled,
    string Source,
    Guid? SourceScopeId,
    IReadOnlyList<FlagOverrideItem> Overrides);
