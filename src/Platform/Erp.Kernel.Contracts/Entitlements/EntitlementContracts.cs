using System.Data.Common;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Entitlements;

/// <summary>Limit keys of the D-24 catalogue (spec 14 section 8.3) that the kernel counts or offers to other slices.</summary>
public static class LimitKeys
{
    public const string NamedUsers = "users.named";
    public const string ConcurrentUsers = "users.concurrent";
    public const string Companies = "companies";
    public const string ActiveEmployees = "employees.active";
    public const string StorageGb = "storage.gb";
    public const string ApiCalls = "api.calls";
}

/// <summary>
/// One counter of a limit (slice S1.09 ruling Q7): how many seats are taken now, read in the caller's transaction, and
/// whether the holder already holds one (a user signing in again on another device, ruling B2). Kernel
/// counters: named users, concurrent users, companies. Later slices register theirs (employees S1.20, storage S1.11,
/// API calls KRN-INT-07).
/// </summary>
public interface ILimitCounter
{
    string LimitKey { get; }

    Task<LimitCount> CountAsync(DbTransaction transaction, Guid? holder, CancellationToken cancellationToken);
}

/// <summary>Seats taken now and whether the holder already has one of them.</summary>
public sealed record LimitCount(long Used, bool HolderCounted);

/// <summary>
/// The answer of the limit guard. <see cref="Max"/> null: the entitlement sets no limit. <see cref="Used"/>: seats taken
/// once this one is (unchanged when refused or already held). <see cref="Warning"/>: at or above the warning threshold
/// (<c>cfg.limits.warning_percent</c>, default 90); <see cref="Crossed"/>: this seat crossed it (announced once).
/// </summary>
public sealed record LimitDecision(string LimitKey, long? Max, long Used, bool Allowed, bool Warning, bool Crossed, int WarningPercent)
{
    /// <summary>Whole percent of the limit in use (0 when there is no limit).</summary>
    public int Percent => Max is > 0 ? (int)Math.Min(999, Used * 100 / Max.Value) : 0;
}

/// <summary>
/// The kernel limit guard (FR-KRN-ENT-05, FR-KRN-IAM-18, FR-ADM-USR-08; ruling Q7): checks and takes a seat in the caller's
/// transaction, under a lock per customer and limit, so two people can never take the last seat. A crossing of the warning
/// threshold is announced once (security event and outbox event, same transaction); a refusal is recorded on its own and
/// raised as <see cref="LimitReachedException"/>. A limit lowered below current use blocks only new seats.
/// </summary>
public interface ILimitGuard
{
    Task<LimitDecision> TakeAsync(string limitKey, DbTransaction transaction, Guid? holder, CancellationToken cancellationToken);
}

/// <summary>A hard stop at a limit; <see cref="Exception.Message"/> is the localization key <see cref="ErrorKey"/>.</summary>
public sealed class LimitReachedException(LimitDecision decision) : Exception(ErrorKey)
{
    public const string ErrorKey = "errors.licence.limit_reached";

    public LimitDecision Decision { get; } = decision;
}

/// <summary>Active employees for the monthly meter (ruling Q8); the HR module provides it (S1.20). Counted only when HR is licensed.</summary>
public interface IActiveEmployeeCounter
{
    Task<long> CountAsync(DbConnection customerDatabase, CancellationToken cancellationToken);
}

/// <summary>Licence events (security events in the customer database and outbox events; ruling Q6, KRN-AUD-02).</summary>
public static class LicenceEvents
{
    public const string Applied = "kernel.licence.applied";
    public const string Scheduled = "kernel.licence.scheduled";
    public const string StateChanged = "kernel.licence.state_changed";
    public const string LimitWarning = "kernel.licence.limit_warning";
    public const string LimitReached = "kernel.licence.limit_reached";
}

/// <summary>Error titles of the licence (localization keys, <c>Entitlements/Localization/licence.*.json</c>).</summary>
public static class LicenceErrors
{
    public const string Locked = "errors.licence.locked";
    public const string LimitReached = LimitReachedException.ErrorKey;
}

/// <summary>Response header that carries a limit warning (<c>key;percent</c>) on the request that took the seat.</summary>
public static class LimitHeaders
{
    public const string Warning = "Erp-Limit-Warning";
}

/// <summary>Permissions of the licence endpoints (ruling Q16). Capabilities need only a signed-in user.</summary>
public static class LicencePermissions
{
    public const string Read = "cfg.licence.read";

    public static IReadOnlyList<PermissionDefinition> All { get; } = [new(Read, "cfg", "permissions.cfg.licence.read", false)];
}

/// <summary>A limit on the licence page: the limit, what is used now and the percentage (null max: no limit).</summary>
public sealed record LimitUse(string Key, long? Max, long? Used, int? Percent);

/// <summary>
/// The licence page (FR-ADM-OPS-07, server side): edition, modules, features, packs, add-ons, limits with use, dates, the
/// maintenance date, the state and the next state with its date. A new entitlement is applied by the provider (ruling Q15).
/// </summary>
public sealed record LicenceView(
    string? Edition,
    int? Version,
    string Deployment,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Packs,
    IReadOnlyList<string> Addons,
    IReadOnlyList<LimitUse> Limits,
    DateOnly? EffectiveFrom,
    DateOnly? ExpiresAt,
    DateOnly? MaintenanceExpiresAt,
    string? SupportTier,
    string State,
    string? NextState,
    DateOnly? NextStateOn,
    DateOnly? ScheduledFrom);

/// <summary>
/// What the web shell may show (FR-KRN-ENT-03; ruling Q9): licensed modules and features, the flags on for the caller, the
/// licence state and next change, and the limits at or above the warning threshold.
/// </summary>
public sealed record CapabilitiesView(
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Flags,
    string State,
    string? NextState,
    DateOnly? NextStateOn,
    IReadOnlyList<LimitUse> Warnings);

/// <summary>One meter of a month's usage statement (FR-KRN-ENT-06; ruling Q8).</summary>
public sealed record UsageLine(string Meter, long Value, string? LimitKey, long? Limit, int? Percent);

/// <summary>A month's usage statement: each meter, its limit and percentage, and the highest percentage.</summary>
public sealed record UsageStatement(string Period, IReadOnlyList<UsageLine> Lines, int? HighestPercent);
