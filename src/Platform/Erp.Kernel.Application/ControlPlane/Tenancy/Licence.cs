using System.Data.Common;
using Erp.Kernel.Domain.Entitlements;

namespace Erp.Kernel.Application.ControlPlane.Tenancy;

/// <summary>
/// One stored entitlement of a customer as the control plane holds it (cp.entitlements): the current one and any
/// scheduled ones; superseded rows are history only. <see cref="ClaimsJson"/> is the signed claims as stored.
/// </summary>
public sealed record EntitlementRecord(Guid Id, int Version, string Status, string Edition, DateOnly EffectiveFrom, DateOnly? ExpiresAt, string ClaimsJson)
{
    public const string Scheduled = "scheduled";

    public const string Superseded = "superseded";

    public bool IsScheduled => Status == Scheduled;
}

/// <summary>
/// Which stored entitlement is in force on a day (slice S1.09 rulings Q3 and Q6): the highest version among the current
/// row and the scheduled rows whose start date has come. A scheduled entitlement is in force from its start day even
/// before the licence loop records the change, so the answer never depends on something having run.
/// </summary>
public static class EntitlementSelection
{
    public static EntitlementRecord? InForce(IEnumerable<EntitlementRecord> records, DateOnly today) =>
        records.Where(r => r.Status != EntitlementRecord.Superseded && (!r.IsScheduled || r.EffectiveFrom <= today))
            .OrderByDescending(r => r.Version).FirstOrDefault();
}

/// <summary>
/// The licence a request is served under (FR-KRN-ENT-03, FR-KRN-ENT-04): the entitlement in force, its parsed claims,
/// the state on the customer's day and the next change. <see cref="Entitlement"/> is null when the customer has no
/// entitlement at all; the state is then Locked (fail closed).
/// </summary>
public sealed record LicenceSnapshot(
    EntitlementRecord? Entitlement,
    EntitlementClaims? Claims,
    string State,
    DateOnly Today,
    string TimeZone,
    DateOnly? NextChange,
    string? NextState)
{
    public bool IsLicensed(string featureKey) => Claims?.HasFeature(featureKey) == true;

    public bool HasModule(string module) => Claims?.HasModule(module) == true;

    /// <summary>The licence of a customer with no entitlement (or one that cannot be read): Locked.</summary>
    public static LicenceSnapshot None(DateOnly today, string timeZone) => new(null, null, LicenceStates.Locked, today, timeZone, null, null);
}

/// <summary>The request's licence, bound by tenant resolution together with the customer (read by services in the request).</summary>
public interface ICurrentLicence
{
    LicenceSnapshot Licence { get; }
}

/// <summary>
/// Works out a customer's licence for now (slice S1.09 ruling Q6, design note D1): the entitlement in force, its claims
/// and its state, with the day taken in the customer's time zone (<c>org.time_zone</c>, customer layer).
/// </summary>
public interface ILicenceResolver
{
    ValueTask<LicenceSnapshot> ResolveAsync(ResolvedTenant tenant, DbDataSource dataSource, CancellationToken cancellationToken);
}
