using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Audit;

/// <summary>
/// Permissions of the audit endpoints (slice S1.07 build item 7, ruling Q10). The access log and export are sensitive:
/// they reveal who looked at salaries and copy the log out of the system.
/// </summary>
public static class AuditPermissions
{
    /// <summary>Search the whole change log (FR-KRN-AUD-04).</summary>
    public const string ViewChanges = "aud.changes.view";

    /// <summary>See the history of a record the user can read, through their own data scope and field security (FR-KRN-AUD-07).</summary>
    public const string ViewHistory = "aud.history.view";

    /// <summary>See who viewed or exported sensitive fields (FR-KRN-AUD-02, HRM-PRV-05).</summary>
    public const string ViewAccessLog = "aud.access_log.view";

    /// <summary>See sign-ins, lockouts, permission changes and other security events (FR-KRN-AUD-02).</summary>
    public const string ViewSecurityEvents = "aud.security_events.view";

    /// <summary>Export change-log search results as CSV; every export is itself logged.</summary>
    public const string Export = "aud.export";

    /// <summary>Check the hash chain for tampering (FR-KRN-AUD-03).</summary>
    public const string Verify = "aud.verify";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(ViewChanges, "aud", "permissions.aud.changes.view", false),
        new(ViewHistory, "aud", "permissions.aud.history.view", false),
        new(ViewAccessLog, "aud", "permissions.aud.access_log.view", true),
        new(ViewSecurityEvents, "aud", "permissions.aud.security_events.view", false),
        new(Export, "aud", "permissions.aud.export", true),
        new(Verify, "aud", "permissions.aud.verify", false),
    ];
}
