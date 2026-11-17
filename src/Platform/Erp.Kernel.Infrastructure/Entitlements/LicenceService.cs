using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>
/// The licence endpoints' service (slice S1.09b items 9, 11 and 12): capabilities from the request's licence and the
/// caller's flags; the licence page with each limit's current use (counted now, without taking a seat); usage statements
/// from cp.usage_meters of the bound customer.
/// </summary>
public sealed class LicenceService(
    TenantContext tenant,
    ITenantConnectionProvider connections,
    IEnumerable<ILimitCounter> counters,
    SettingsService settings,
    ISettings values,
    ControlPlaneOptions controlPlane) : ILicenceService
{
    public async Task<CapabilitiesView> CapabilitiesAsync(CancellationToken cancellationToken)
    {
        var licence = tenant.Licence;
        var snapshot = await settings.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var flags = new List<string>();
        foreach (var key in snapshot.Flags.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (await settings.IsOnAsync(key, null, cancellationToken).ConfigureAwait(false))
            {
                flags.Add(key);
            }
        }

        var (limits, warningPercent) = await LimitsAsync(licence, cancellationToken).ConfigureAwait(false);
        var warnings = limits.Where(l => l.Percent is { } p && p >= warningPercent).ToList();
        return new CapabilitiesView(
            licence.Claims is null ? [] : ["kernel", .. licence.Claims.Modules.Where(tenant.InstalledModules.Contains)],
            licence.Claims?.Features ?? [],
            flags,
            licence.State,
            licence.NextState,
            licence.NextChange,
            warnings);
    }

    public async Task<LicenceView> LicenceAsync(CancellationToken cancellationToken)
    {
        var licence = tenant.Licence;
        var claims = licence.Claims;
        var scheduled = tenant.Tenant.Entitlements.Where(e => e.IsScheduled && e.Version > (licence.Entitlement?.Version ?? 0)).OrderBy(e => e.EffectiveFrom).FirstOrDefault();
        return new LicenceView(
            licence.Entitlement?.Edition,
            licence.Entitlement?.Version,
            claims?.Deployment ?? "-",
            claims?.Modules ?? [],
            claims?.Features ?? [],
            claims?.Packs ?? [],
            claims?.Addons ?? [],
            (await LimitsAsync(licence, cancellationToken).ConfigureAwait(false)).Uses,
            licence.Entitlement?.EffectiveFrom,
            licence.Entitlement?.ExpiresAt,
            claims?.Term.MaintenanceExpiresAt,
            claims?.SupportTier,
            licence.State,
            licence.NextState,
            licence.NextChange,
            scheduled?.EffectiveFrom);
    }

    public async Task<IReadOnlyList<UsageStatement>> UsageAsync(string? fromPeriod, string? toPeriod, CancellationToken cancellationToken)
    {
        var current = UsageMeters.Period(tenant.Licence.Today);
        await using var cp = new NpgsqlConnection(controlPlane.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await UsageMeters.StatementsAsync(cp, tenant.Tenant.TenantId, fromPeriod ?? toPeriod ?? current, toPeriod ?? fromPeriod ?? current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Each limit of the licence with what is used now (no seat is taken; null when no counter exists yet).</summary>
    private async Task<(List<LimitUse> Uses, int WarningPercent)> LimitsAsync(LicenceSnapshot licence, CancellationToken cancellationToken)
    {
        var setting = await values.GetAsync(Contracts.Generated.KernelSettingKeys.CfgLimitsWarningPercent, SettingContext.Customer, cancellationToken).ConfigureAwait(false);
        var warningPercent = setting.Value.TryGetInt32(out var p) ? p : 90;
        var uses = new List<LimitUse>();
        if (licence.Claims is null)
        {
            return (uses, warningPercent);
        }

        await using var c = await IdentityDb.OpenAsync(connections.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        foreach (var (key, max) in licence.Claims.Limits.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            long? used = counters.FirstOrDefault(x => x.LimitKey == key) is { } counter ? (await counter.CountAsync(tx, null, cancellationToken).ConfigureAwait(false)).Used : null;
            uses.Add(new LimitUse(key, max, used, used is not { } u ? null : max > 0 ? (int)Math.Min(999, u * 100 / max) : 100));
        }

        return (uses, warningPercent);
    }
}
