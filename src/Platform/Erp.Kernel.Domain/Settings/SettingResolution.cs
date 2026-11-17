using System.Text.Json;

namespace Erp.Kernel.Domain.Settings;

/// <summary>What the resolver needs to know about a setting (a row of cfg.settings).</summary>
public sealed record SettingDefinition(
    string Key,
    string DataType,
    JsonElement Default,
    SettingValidation Validation,
    IReadOnlyList<string> Layers,
    string? RoleRule,
    bool TightenOnly)
{
    public bool Allows(string layer) => layer == SettingLayers.Product || Layers.Contains(layer, StringComparer.Ordinal);
}

/// <summary>One stored value: its layer, the company, role or user it belongs to (null for pack and tenant), and who set it when.</summary>
public sealed record StoredSettingValue(string Layer, Guid? ScopeId, JsonElement Value, DateTimeOffset UpdatedAt, Guid UpdatedBy);

/// <summary>A layer of the layered view (FR-ADM-SET-03): its value, and whether the resolver used or ignored it and why.</summary>
public sealed record SettingLayerView(string Layer, Guid? ScopeId, JsonElement Value, DateTimeOffset? UpdatedAt, Guid? UpdatedBy, string? Ignored);

/// <summary>The effective value and where it came from (FR-KRN-SET-02); for the role layer, <c>ScopeId</c> is null when several roles combined.</summary>
public sealed record ResolvedSetting(JsonElement Value, string Layer, Guid? ScopeId, IReadOnlyList<SettingLayerView> Layers);

/// <summary>
/// The one resolution rule of settings (slice S1.09 build item 2, rulings Q11 and CR-037). Layers, least specific first:
/// product default, pack, tenant (the customer), company, role, user; the most specific <em>valid</em> value wins.
/// <list type="bullet">
/// <item>A stored value the setting no longer accepts (wrong type, outside the rules, a layer no longer allowed) is skipped
/// and reported as ignored; the next layer is used.</item>
/// <item>Role layer: only when the definition has a role rule. The caller passes only the roles that count (held today and
/// covering the company in context); their valid values combine by the rule (any, all, lowest, highest).</item>
/// <item>Tighten-only (yes/no settings, CR-037): the most specific of product, pack, tenant and company is the base, and the
/// role and user layers can only switch it on (the S1.06 and CR-017 rule of <c>iam.mfa.required</c>).</item>
/// </list>
/// The values passed must belong to the context: the company's value only, the user's value only.
/// </summary>
public static class SettingResolution
{
    public const string IgnoredInvalid = "invalid";
    public const string IgnoredLayerNotAllowed = "layer_not_allowed";
    public const string IgnoredOverridden = "overridden";

    public static ResolvedSetting Resolve(SettingDefinition definition, IEnumerable<StoredSettingValue> values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);
        var views = new List<SettingLayerView> { new(SettingLayers.Product, null, definition.Default, null, null, null) };
        var usable = new Dictionary<string, List<StoredSettingValue>>(StringComparer.Ordinal);
        foreach (var value in values.OrderBy(v => Rank(v.Layer)).ThenBy(v => v.ScopeId))
        {
            var problem = !definition.Allows(value.Layer) || (value.Layer == SettingLayers.Role && definition.RoleRule is null) ? IgnoredLayerNotAllowed
                : definition.Validation.Problem(definition.DataType, value.Value) is not null ? IgnoredInvalid
                : null;
            views.Add(new SettingLayerView(value.Layer, value.ScopeId, value.Value, value.UpdatedAt, value.UpdatedBy, problem));
            if (problem is null)
            {
                (usable.TryGetValue(value.Layer, out var list) ? list : usable[value.Layer] = []).Add(value);
            }
        }

        // Single-valued layers: the first valid value of each (a context has one company and one user).
        (JsonElement Value, string Layer, Guid? Scope)? Single(string layer) =>
            usable.TryGetValue(layer, out var list) ? (list[0].Value, layer, list[0].ScopeId) : null;

        var role = CombineRoles(definition, usable.GetValueOrDefault(SettingLayers.Role));
        var baseValue = Single(SettingLayers.Company) ?? Single(SettingLayers.Tenant) ?? Single(SettingLayers.Pack) ?? (definition.Default, SettingLayers.Product, null);
        (JsonElement Value, string Layer, Guid? Scope) effective;
        if (definition.TightenOnly && definition.DataType == SettingTypes.Bool)
        {
            effective = baseValue.Value.ValueKind == JsonValueKind.True ? baseValue
                : role is { Value.ValueKind: JsonValueKind.True } r ? r
                : Single(SettingLayers.User) is { Value.ValueKind: JsonValueKind.True } u ? u
                : baseValue;
        }
        else
        {
            effective = Single(SettingLayers.User) ?? role ?? baseValue;
        }

        // Valid values that did not decide are reported as overridden, so the layered view explains itself.
        var shown = views.Select(v => v.Ignored is null && !(v.Layer == effective.Layer && (effective.Scope is null || v.ScopeId == effective.Scope))
                ? v with { Ignored = IgnoredOverridden }
                : v)
            .ToList();
        return new ResolvedSetting(effective.Value, effective.Layer, effective.Scope, shown);
    }

    private static (JsonElement Value, string Layer, Guid? Scope)? CombineRoles(SettingDefinition definition, List<StoredSettingValue>? roles)
    {
        if (roles is not { Count: > 0 } || definition.RoleRule is null)
        {
            return null;
        }

        if (roles.Count == 1)
        {
            return (roles[0].Value, SettingLayers.Role, roles[0].ScopeId);
        }

        StoredSettingValue? pick = definition.RoleRule switch
        {
            SettingRoleRules.Any => roles.FirstOrDefault(r => r.Value.ValueKind == JsonValueKind.True) ?? roles[0],
            SettingRoleRules.All => roles.FirstOrDefault(r => r.Value.ValueKind == JsonValueKind.False) ?? roles[0],
            SettingRoleRules.Lowest => roles.MinBy(r => r.Value.GetDecimal()),
            SettingRoleRules.Highest => roles.MaxBy(r => r.Value.GetDecimal()),
            _ => null,
        };
        return pick is null ? null : (pick.Value, SettingLayers.Role, pick.ScopeId);
    }

    private static int Rank(string layer)
    {
        var index = SettingLayers.Order.ToList().IndexOf(layer);
        return index < 0 ? int.MaxValue : index;
    }
}

/// <summary>A flag override (cfg.feature_flag_overrides): customer, company or user group.</summary>
public sealed record FlagOverride(string Layer, Guid? ScopeId, bool Enabled, DateTimeOffset UpdatedAt, Guid UpdatedBy);

/// <summary>A flag's state and why (layer <c>default</c>, <c>tenant</c>, <c>company</c>, <c>user_group</c> or <c>entitlement</c>).</summary>
public sealed record ResolvedFlag(bool Enabled, string Source, Guid? ScopeId);

/// <summary>
/// Feature flags (FR-KRN-SET-05, slice S1.09 ruling Q14): a flag whose entitlement key is not licensed is off whatever its
/// overrides say; otherwise the most specific override wins (user group, then company, then customer, then the default);
/// when the user's groups disagree, on wins (flags open a capability to a pilot group). The overrides passed must belong to
/// the context: the company in context and the user's groups.
/// </summary>
public static class FlagResolution
{
    public const string Default = "default";
    public const string Tenant = "tenant";
    public const string Company = "company";
    public const string UserGroup = "user_group";
    public const string Entitlement = "entitlement";

    public static IReadOnlyList<string> OverrideLayers { get; } = [Tenant, Company, UserGroup];

    public static ResolvedFlag Resolve(bool defaultEnabled, bool licensed, IEnumerable<FlagOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (!licensed)
        {
            return new ResolvedFlag(false, Entitlement, null);
        }

        var list = overrides.ToList();
        var groups = list.Where(o => o.Layer == UserGroup).OrderBy(o => o.ScopeId).ToList();
        if (groups.Count > 0)
        {
            var on = groups.FirstOrDefault(g => g.Enabled);
            return on is not null ? new ResolvedFlag(true, UserGroup, on.ScopeId) : new ResolvedFlag(false, UserGroup, groups[0].ScopeId);
        }

        return list.FirstOrDefault(o => o.Layer == Company) is { } company ? new ResolvedFlag(company.Enabled, Company, company.ScopeId)
            : list.FirstOrDefault(o => o.Layer == Tenant) is { } tenant ? new ResolvedFlag(tenant.Enabled, Tenant, null)
            : new ResolvedFlag(defaultEnabled, Default, null);
    }
}
