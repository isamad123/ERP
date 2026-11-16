namespace Erp.Kernel.Application.Identity;

/// <summary>Where a request came from (stored on sessions and sign-in attempts). Never used to choose the customer.</summary>
public sealed record ClientInfo(string? IpAddress, string? UserAgent);

/// <summary>What a sign-in step produced.</summary>
public enum AuthOutcome
{
    /// <summary>Signed in: access and refresh tokens issued.</summary>
    Tokens,

    /// <summary>The password was right; a second factor is needed (<see cref="AuthResult.ChallengeToken"/>).</summary>
    MfaRequired,

    /// <summary>The password must be changed before anything else (must_change or expired).</summary>
    PasswordChangeRequired,

    /// <summary>MFA is required and the grace period is over: the user may only enrol an authenticator app.</summary>
    MfaEnrolmentRequired,

    /// <summary>Generic failure; identical for unknown user, wrong password, disabled and locked accounts (ruling P5).</summary>
    Failed,

    /// <summary>The request was understood but refused for a stated reason (<see cref="AuthResult.Errors"/>, localization keys).</summary>
    Invalid,
}

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, Guid SessionId);

/// <summary>
/// Result of a sign-in step. <c>MfaEnrolBy</c> is set while MFA is required but not yet enrolled and the grace period
/// runs; <c>RecoveryCodes</c> are shown once, after an enrolment completed during sign-in.
/// </summary>
public sealed record AuthResult(
    AuthOutcome Outcome,
    TokenPair? Tokens = null,
    string? ChallengeToken = null,
    IReadOnlyList<string>? Errors = null,
    DateTimeOffset? MfaEnrolBy = null,
    IReadOnlyList<string>? RecoveryCodes = null)
{
    public static AuthResult Failed { get; } = new(AuthOutcome.Failed);

    public static AuthResult Invalid(params string[] errors) => new(AuthOutcome.Invalid, Errors: errors);
}

/// <summary>A started authenticator-app enrolment: the device stays pending until a first code confirms it.</summary>
public sealed record MfaEnrolment(Guid DeviceId, string OtpAuthUri, string Secret);

public sealed record SessionView(Guid Id, DateTimeOffset IssuedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, string? IpAddress, string? UserAgent, bool IsCurrent);

public sealed record MfaDeviceView(Guid Id, string Kind, string? Label, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// Sign-in, second factor, forced password change, refresh, sign-out and activation (FR-KRN-IAM-01, 02, 04). Anonymous
/// steps run for the customer bound by host name only; every attempt is written to iam.login_attempts (FR-KRN-AUD-02).
/// </summary>
public interface IAuthenticationService
{
    Task<AuthResult> SignInAsync(string? login, string? password, ClientInfo client, CancellationToken cancellationToken);

    /// <summary>Second step with an authenticator code or a recovery code.</summary>
    Task<AuthResult> CompleteMfaAsync(string? challengeToken, string? code, string? recoveryCode, ClientInfo client, CancellationToken cancellationToken);

    Task<AuthResult> ChangeRequiredPasswordAsync(string? challengeToken, string? newPassword, ClientInfo client, CancellationToken cancellationToken);

    /// <summary>Starts the enrolment a user must complete after the grace period; null when the challenge is not valid.</summary>
    Task<MfaEnrolment?> StartRequiredEnrolmentAsync(string? challengeToken, CancellationToken cancellationToken);

    Task<AuthResult> ConfirmRequiredEnrolmentAsync(string? challengeToken, Guid deviceId, string? code, ClientInfo client, CancellationToken cancellationToken);

    /// <summary>Rotates the refresh token (ruling P6). A reused, revoked or expired one gets <see cref="AuthOutcome.Failed"/>.</summary>
    Task<AuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken cancellationToken);

    Task SignOutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>The invited user sets the first password through the one-time activation token (slice step 8).</summary>
    Task<AuthResult> ActivateAsync(string? activationToken, string? newPassword, ClientInfo client, CancellationToken cancellationToken);
}

/// <summary>Self-service of the signed-in user (FR-ADM-USR-10: password, sessions, MFA devices).</summary>
public interface IAccountService
{
    /// <summary>Empty when changed; otherwise localization keys of the problems.</summary>
    Task<IReadOnlyList<string>> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionView>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken);

    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MfaDeviceView>> ListMfaDevicesAsync(Guid userId, CancellationToken cancellationToken);

    Task<MfaEnrolment> StartMfaEnrolmentAsync(Guid userId, string? label, CancellationToken cancellationToken);

    /// <summary>Recovery codes (shown once) when the first device is confirmed, empty for a further device, null when the code is wrong.</summary>
    Task<IReadOnlyList<string>?> ConfirmMfaEnrolmentAsync(Guid userId, Guid deviceId, string? code, CancellationToken cancellationToken);

    Task<bool> RemoveMfaDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    /// <summary>New recovery codes replacing the old ones; null when the user has no active device.</summary>
    Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken);
}

public enum AdminOutcome
{
    Done,
    NotFound,
    Invalid,
}

/// <summary>Result of an administrator action; <c>Error</c> is a localization key when the outcome is Invalid.</summary>
public sealed record AdminResult(AdminOutcome Outcome, string? Error = null)
{
    public static AdminResult Done { get; } = new(AdminOutcome.Done);

    public static AdminResult NotFound { get; } = new(AdminOutcome.NotFound);
}

/// <summary>An activation link token, shown once (the database keeps only its hash).</summary>
public sealed record ActivationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Administrator actions on a user (FR-ADM-USR-03, FR-ADM-USR-05): each raises a domain event in iam.outbox_messages
/// (ruling Q4; S1.07 records them in the audit log).
/// </summary>
public interface IUserAdministrationService
{
    Task<AdminResult> UnlockAsync(Guid userId, string? reason, CancellationToken cancellationToken);

    Task<AdminResult> ForcePasswordChangeAsync(Guid userId, string? reason, CancellationToken cancellationToken);

    Task<AdminResult> ResetMfaAsync(Guid userId, string? reason, CancellationToken cancellationToken);

    Task<AdminResult> RevokeSessionsAsync(Guid userId, string? reason, CancellationToken cancellationToken);

    Task<AdminResult> ChangeStatusAsync(Guid userId, string? status, string? reason, CancellationToken cancellationToken);

    /// <summary>Null when the user does not exist.</summary>
    Task<IReadOnlyList<SessionView>?> ListSessionsAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Issues access tokens (ES256, key id in the header) for a session (S1.03 token contract: sub, tenant).</summary>
public interface IAccessTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, string tenantCode, Guid sessionId, DateTimeOffset now);
}

/// <summary>Password hashing with an algorithm label so hashes can be upgraded at the next sign-in.</summary>
public interface IPasswordHasher
{
    /// <summary>Label stored in <c>user_credentials.hash_algorithm</c> for new hashes.</summary>
    string Algorithm { get; }

    string Hash(string password);

    /// <summary>True when the password matches; <paramref name="needsRehash"/> is true when the stored hash uses older settings.</summary>
    bool Verify(string password, string storedHash, string algorithm, out bool needsRehash);
}
