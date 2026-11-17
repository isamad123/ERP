using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Settings;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>
/// Works out a licence from stored entitlements, the customer's time zone and the clock (slice S1.09 rulings Q4, Q6 and
/// B1). The API host, the licence loop and the operator commands all use this one rule.
/// </summary>
public static class LicenceSnapshots
{
    public static LicenceSnapshot Compute(IEnumerable<EntitlementRecord> records, string timeZone, DateTimeOffset now, Func<EntitlementRecord, EntitlementClaims?> claimsOf)
    {
        ArgumentNullException.ThrowIfNull(claimsOf);
        var zone = LicenceStates.Zone(timeZone);
        var today = LicenceStates.Today(now, zone);
        var inForce = EntitlementSelection.InForce(records, today);
        var claims = inForce is null ? null : claimsOf(inForce);
        if (inForce is null || claims is null)
        {
            return LicenceSnapshot.None(today, zone.Id);
        }

        var (state, next, nextState) = LicenceStates.At(inForce.ExpiresAt, claims.Term, today);
        return new LicenceSnapshot(inForce, claims, state, today, zone.Id, next, nextState);
    }

    /// <summary>Parses stored claims; claims that no longer pass the key catalogue give no licence (fail closed: Locked).</summary>
    public static EntitlementClaims? TryParse(EntitlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return EntitlementCatalogs.Parse(record.ClaimsJson);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>
/// The API host's licence resolver (design note D1): the customer-layer <c>org.time_zone</c> comes from the host's settings
/// cache (no extra read while nothing changes), the claims of each stored entitlement are parsed once per host.
/// </summary>
public sealed class LicenceResolver(SettingsCache cache, IClock clock) : ILicenceResolver
{
    private readonly ConcurrentDictionary<Guid, EntitlementClaims?> _claims = new();

    public async ValueTask<LicenceSnapshot> ResolveAsync(ResolvedTenant tenant, DbDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var snapshot = await cache.GetAsync(tenant.Code, dataSource, cancellationToken).ConfigureAwait(false);
        var timeZone = "Asia/Karachi";
        if (snapshot.Definitions.TryGetValue(KernelSettingKeys.OrgTimeZone, out var row))
        {
            var customerValues = snapshot.Values[KernelSettingKeys.OrgTimeZone]
                .Where(v => v.Value.Layer is SettingLayers.Pack or SettingLayers.Tenant).Select(v => v.Value);
            var resolved = SettingResolution.Resolve(row.Definition, customerValues);
            if (resolved.Value.ValueKind == JsonValueKind.String)
            {
                timeZone = resolved.Value.GetString()!;
            }
        }

        return LicenceSnapshots.Compute(tenant.Entitlements, timeZone, clock.UtcNow, r => _claims.GetOrAdd(r.Id, _ => LicenceSnapshots.TryParse(r)));
    }
}
