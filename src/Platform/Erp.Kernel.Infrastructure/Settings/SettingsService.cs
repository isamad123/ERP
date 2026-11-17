using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>
/// Effective settings and flags for the customer of the request, from the host's cache (slice S1.09a build items 2, 4, 5;
/// FR-KRN-SET-02, FR-KRN-SET-05, FR-KRN-SET-06). The context is the server's: the signed-in caller (or an explicit user
/// given by service code) and the company in hand. Role values count for the roles the user holds today whose assignment
/// covers that company; flag overrides by group count for the user's active groups. Reading roles and groups costs one
/// query, only for settings that allow the role layer or flags that have group overrides.
/// </summary>
public sealed class SettingsService(
    ITenantContext tenant,
    ITenantConnectionProvider connections,
    ICurrentActor actor,
    IClock clock,
    SettingsCache cache,
    ILicensedFeatures licensed) : ISettings, IFeatureFlags
{
    public Task<EffectiveSetting> GetAsync(string key, Guid? companyId, CancellationToken cancellationToken) =>
        GetAsync(key, new SettingContext(companyId, actor.UserId), cancellationToken);

    public async Task<EffectiveSetting> GetAsync(string key, SettingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (_, resolved) = await ResolveAsync(key, context.CompanyId, context.UserId, null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Setting {key} is not registered.");
        return new EffectiveSetting(key, resolved.Value, resolved.Layer, resolved.ScopeId);
    }

    public async Task<bool> IsOnAsync(string key, Guid? companyId, CancellationToken cancellationToken) =>
        (await FlagAsync(key, companyId, actor.UserId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Flag {key} is not registered.")).Resolved.Enabled;

    /// <summary>The current snapshot of the customer's settings and flags.</summary>
    public Task<SettingsSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        cache.GetAsync(tenant.TenantCode, connections.DataSource, cancellationToken);

    /// <summary>Drops this host's snapshot after a change, so the change is in force here at once.</summary>
    public void Invalidate() => cache.Invalidate(tenant.TenantCode);

    /// <summary>
    /// Resolves a setting for a context; <paramref name="roleId"/> names one role for the layered view of that role (instead of
    /// the user's roles). Null when the key is unknown.
    /// </summary>
    public async Task<(SettingRow Row, ResolvedSetting Resolved)?> ResolveAsync(string key, Guid? companyId, Guid? userId, Guid? roleId, CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Definitions.TryGetValue(key, out var row))
        {
            return null;
        }

        IReadOnlyCollection<Guid> roles = roleId is { } one ? [one]
            : userId is { } u && row.RoleRule is not null
                ? await RolesAsync(u, companyId, cancellationToken).ConfigureAwait(false)
                : [];
        var values = snapshot.Values[key].Where(v => v.Value.Layer switch
        {
            SettingLayers.Company => companyId is not null && v.Value.ScopeId == companyId,
            SettingLayers.Role => v.Value.ScopeId is { } r && roles.Contains(r),
            _ => true,
        }).ToList();
        // The user's value (one query per user per snapshot); one stored at a layer the setting no longer allows is shown as ignored.
        if (userId is { } user)
        {
            values.AddRange((await SettingsCache.UserValuesAsync(snapshot, connections.DataSource, user, cancellationToken).ConfigureAwait(false))[key]);
        }

        return (row, SettingResolution.Resolve(row.Definition, values.Select(v => v.Value)));
    }

    /// <summary>A flag's state for a context (ruling Q14); null when the key is unknown.</summary>
    public async Task<(FlagRow Row, ResolvedFlag Resolved, bool Licensed)?> FlagAsync(string key, Guid? companyId, Guid? userId, CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Flags.TryGetValue(key, out var row))
        {
            return null;
        }

        var isLicensed = row.EntitlementKey is not { } feature || await licensed.IsLicensedAsync(feature, cancellationToken).ConfigureAwait(false);
        var overrides = snapshot.Overrides[key].ToList();
        Guid[] groups = userId is { } user && overrides.Exists(o => o.Override.Layer == FlagResolution.UserGroup)
            ? await GroupsAsync(user, cancellationToken).ConfigureAwait(false)
            : [];
        var applying = overrides.Select(o => o.Override).Where(o => o.Layer switch
        {
            FlagResolution.Company => companyId is not null && o.ScopeId == companyId,
            FlagResolution.UserGroup => o.ScopeId is { } g && groups.Contains(g),
            _ => true,
        });
        return (row, FlagResolution.Resolve(row.DefaultEnabled, isLicensed, applying), isLicensed);
    }

    private async Task<Guid[]> RolesAsync(Guid userId, Guid? companyId, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(connections.DataSource, ct).ConfigureAwait(false);
        return await SettingsStore.RoleIdsAsync(c, null, userId, companyId, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct).ConfigureAwait(false);
    }

    private async Task<Guid[]> GroupsAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(connections.DataSource, ct).ConfigureAwait(false);
        return await SettingsStore.GroupIdsAsync(c, null, userId, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Licensed features from the customer's copy of the signed entitlement (<c>claims.features</c> of meta.local_entitlement;
/// slice S1.09a, until S1.09b's entitlement service replaces it behind <see cref="ILicensedFeatures"/>). A missing list
/// means no feature is licensed.
/// </summary>
public sealed class LocalEntitlementFeatures(ITenantConnectionProvider connections) : ILicensedFeatures
{
    public async Task<bool> IsLicensedAsync(string featureKey, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(connections.DataSource, cancellationToken).ConfigureAwait(false);
        return await IdentityDb.ScalarAsync<bool>(c, null, """
            select coalesce((select jsonb_typeof(f) = 'array' and f ? $1
                             from (select payload -> 'payload' -> 'claims' -> 'features' as f from meta.local_entitlement order by created_at desc limit 1) e), false)
            """, cancellationToken, featureKey).ConfigureAwait(false);
    }
}
