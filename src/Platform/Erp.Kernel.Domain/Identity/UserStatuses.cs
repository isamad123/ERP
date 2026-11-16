namespace Erp.Kernel.Domain.Identity;

/// <summary>
/// User statuses and their allowed transitions (FR-ADM-USR-03; product-owner ruling P2 of slice S1.05).
/// </summary>
public static class UserStatuses
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Locked = "locked";
    public const string Disabled = "disabled";
    public const string Archived = "archived";

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [Invited] = [Active, Archived],
        [Active] = [Locked, Disabled],
        [Locked] = [Active, Disabled],
        [Disabled] = [Active, Archived],
        [Archived] = [],
    };

    public static IReadOnlyCollection<string> All => Allowed.Keys;

    /// <summary>True when an administrator may move a user from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <remarks>invited → active happens only through activation (the user sets a password), never by an administrator.</remarks>
    public static bool AdminMayChange(string from, string to) =>
        !(from == Invited && to == Active) && Allowed.TryGetValue(from, out var targets) && targets.Contains(to, StringComparer.Ordinal);

    /// <summary>True when the transition is allowed at all (activation included).</summary>
    public static bool IsAllowed(string from, string to) => Allowed.TryGetValue(from, out var targets) && targets.Contains(to, StringComparer.Ordinal);

    /// <summary>Moving to this status ends every session of the user (FR-ADM-USR-03).</summary>
    public static bool RevokesSessions(string to) => to is Disabled or Archived or Locked;

    /// <summary>Users in these statuses count against the entitlement's user limit (ruling P4).</summary>
    public static bool CountsAgainstLimit(string status) => status is Active or Locked;
}

/// <summary>Automatic lockout after failed attempts (FR-KRN-IAM-01; ruling P3).</summary>
public static class Lockout
{
    /// <summary>The lock end after one more failure, or null when the account stays open.</summary>
    public static DateTimeOffset? AfterFailure(int failedAttemptsIncludingThis, DateTimeOffset now, PasswordPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.LockoutThreshold > 0 && failedAttemptsIncludingThis >= policy.LockoutThreshold ? now.AddMinutes(policy.LockoutMinutes) : null;
    }
}
