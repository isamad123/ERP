namespace Erp.Kernel.Domain.Identity;

/// <summary>
/// Password policy of a customer (FR-ADM-USR-20, FR-KRN-IAM-01). Values come from settings (<c>iam.password.*</c>);
/// shipped defaults are product-owner ruling Q5 of slice S1.05.
/// </summary>
/// <param name="MinLength">Minimum number of characters.</param>
/// <param name="MinCharacterKinds">How many of the four kinds (upper, lower, digit, symbol) a password must contain.</param>
/// <param name="HistoryCount">How many previous passwords may not be reused (0 = no check).</param>
/// <param name="ExpiryDays">Days after which a password must be changed (0 = never).</param>
/// <param name="LockoutThreshold">Failed attempts in a row that lock the account (0 = never lock).</param>
/// <param name="LockoutMinutes">How long an automatic lock lasts.</param>
public sealed record PasswordPolicy(int MinLength, int MinCharacterKinds, int HistoryCount, int ExpiryDays, int LockoutThreshold, int LockoutMinutes);

/// <summary>Session policy of a customer (FR-ADM-USR-24, FR-KRN-IAM-04).</summary>
/// <param name="IdleMinutes">A session not refreshed for this long ends.</param>
/// <param name="AbsoluteMinutes">A session ends this long after sign-in, whatever its activity.</param>
/// <param name="MaxConcurrent">Most sessions a user may hold; the oldest is revoked when a new one would exceed it.</param>
/// <param name="RememberDeviceDays">Registered now, used by slice S1.12 (0 = off; product-owner ruling P9).</param>
public sealed record SessionPolicy(int IdleMinutes, int AbsoluteMinutes, int MaxConcurrent, int RememberDeviceDays);

/// <summary>MFA policy as it applies to one user (FR-ADM-USR-21, FR-KRN-IAM-02).</summary>
/// <param name="Required">True when the customer or the user's own setting requires a second factor.</param>
/// <param name="RequiredSince">When the requirement that applies was last set (start of the grace period with activation).</param>
/// <param name="GraceDays">Days a user may still sign in without a second factor after it became required.</param>
/// <param name="RecoveryCodeCount">Number of recovery codes issued at enrolment.</param>
/// <param name="AllowedMethods">Allowed methods; release 1.0 ships <c>totp</c> only (CR-009).</param>
public sealed record MfaPolicy(bool Required, DateTimeOffset? RequiredSince, int GraceDays, int RecoveryCodeCount, IReadOnlyList<string> AllowedMethods)
{
    /// <summary>
    /// End of the enrolment grace period (ruling P9a): the later of the user's activation and the time the applying
    /// requirement was set, plus <see cref="GraceDays"/>.
    /// </summary>
    public DateTimeOffset GraceEndsAt(DateTimeOffset activatedAt)
    {
        var start = RequiredSince is { } since && since > activatedAt ? since : activatedAt;
        return start.AddDays(GraceDays);
    }
}
