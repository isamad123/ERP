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
    IReadOnlyList<string>? RecoveryCodes = null,
    IReadOnlyList<string>? MfaMethods = null)
{
    public static AuthResult Failed { get; } = new(AuthOutcome.Failed);

    public static AuthResult Invalid(params string[] errors) => new(AuthOutcome.Invalid, Errors: errors);
}

/// <summary>A started authenticator-app enrolment: the device stays pending until a first code confirms it.</summary>
/// <summary>
/// A started enrolment. An authenticator app (<see cref="Kind"/> <c>totp</c>) has its secret and enrolment link; an
/// e-mail factor (<c>email</c>, slice S1.11b2, CR-009) has neither: its first code was e-mailed. <see cref="EmailCodeSent"/>
/// says a code was e-mailed that the confirm step needs (the e-mail factor, or the late enrolment check of R-003).
/// </summary>
public sealed record MfaEnrolment(Guid DeviceId, string? OtpAuthUri, string? Secret, string Kind = "totp", bool EmailCodeSent = false);

/// <summary>The kinds of second factor of release 1.0 (CR-009): the authenticator app and a code sent by e-mail.</summary>
public static class MfaKinds
{
    public const string Totp = "totp";
    public const string Email = "email";
}

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
    /// <remarks>
    /// Slice S1.11b2 (R-003): an authenticator enrolled here also needs a code e-mailed to the person, so a password alone
    /// cannot add a second factor; <paramref name="kind"/> <c>email</c> enrols the e-mail factor instead (when allowed).
    /// </remarks>
    Task<MfaEnrolment?> StartRequiredEnrolmentAsync(string? challengeToken, string? kind, CancellationToken cancellationToken);

    /// <summary>
    /// Confirms it: <paramref name="code"/> is the authenticator code, or the e-mailed code for the e-mail factor;
    /// <paramref name="emailCode"/> is the e-mailed confirmation code an authenticator enrolled here needs (R-003).
    /// </summary>
    Task<AuthResult> ConfirmRequiredEnrolmentAsync(string? challengeToken, Guid deviceId, string? code, string? emailCode, ClientInfo client, CancellationToken cancellationToken);

    /// <summary>Rotates the refresh token (ruling P6). A reused, revoked or expired one gets <see cref="AuthOutcome.Failed"/>.</summary>
    Task<AuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken cancellationToken);

    Task SignOutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>The invited user sets the first password through the one-time activation token (slice step 8).</summary>
    Task<AuthResult> ActivateAsync(string? activationToken, string? newPassword, ClientInfo client, CancellationToken cancellationToken);
}

/// <summary>
/// Self-service of the signed-in user (FR-ADM-USR-10: password, sessions, MFA devices). Adding or removing an
/// authenticator and making new recovery codes need the current password or a current code (S1.05 review finding 4,
/// product owner 2026-11-16); a wrong one counts towards lockout and raises <see cref="ReauthenticationFailedException"/>.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Empty when changed; otherwise localization keys of the problems. A wrong current password counts towards lockout;
    /// a successful change ends the user's other sessions (the current one stays).
    /// </summary>
    Task<IReadOnlyList<string>> ChangePasswordAsync(Guid userId, Guid currentSessionId, string? currentPassword, string? newPassword, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionView>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken);

    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MfaDeviceView>> ListMfaDevicesAsync(Guid userId, CancellationToken cancellationToken);

    /// <remarks><paramref name="kind"/> <c>email</c> (slice S1.11b2, CR-009) enrols the e-mail factor; its first code is e-mailed.</remarks>
    Task<MfaEnrolment> StartMfaEnrolmentAsync(Guid userId, string? label, string? kind, Reauthentication proof, CancellationToken cancellationToken);

    /// <summary>Recovery codes (shown once) when the first device is confirmed, empty for a further device, null when the code is wrong.</summary>
    Task<IReadOnlyList<string>?> ConfirmMfaEnrolmentAsync(Guid userId, Guid deviceId, string? code, CancellationToken cancellationToken);

    Task<bool> RemoveMfaDeviceAsync(Guid userId, Guid deviceId, Reauthentication proof, CancellationToken cancellationToken);

    /// <summary>New recovery codes replacing the old ones; null when the user has no active device.</summary>
    Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid userId, Reauthentication proof, CancellationToken cancellationToken);
}

public enum AdminOutcome
{
    Done,
    NotFound,
    Invalid,
}

/// <summary>Result of an administrator action; <c>Error</c> is a localization key when the outcome is Invalid.</summary>
/// <summary>
/// The result of an administrator's action on a user. <see cref="OpenWorkflowTasks"/> is set when a status change takes the
/// user out of approving (disabled, archived): the approval tasks still assigned to them, so the screen can offer to
/// reassign them (FR-ADM-USR-03; slice S1.10b ruling P7).
/// </summary>
public sealed record AdminResult(AdminOutcome Outcome, string? Error = null, int? OpenWorkflowTasks = null)
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

/// <summary>
/// The server is already running as many password checks as it allows; the request is refused without counting a
/// failure (S1.05 review finding 5). Answered 503 with <c>errors.auth.busy</c>.
/// </summary>
public sealed class PasswordHasherBusyException : Exception
{
    public PasswordHasherBusyException()
        : base("Too many password checks are running; try again shortly.")
    {
    }

    public PasswordHasherBusyException(string message)
        : base(message)
    {
    }

    public PasswordHasherBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A sensitive self-service action was refused because the current password or authenticator code was wrong or missing
/// (S1.05 review finding 4). The failure is counted towards lockout. Answered 400 with <c>errors.auth.reauthentication_failed</c>.
/// </summary>
public sealed class ReauthenticationFailedException : Exception
{
    public ReauthenticationFailedException()
        : base("The current password or authenticator code is required and was not correct.")
    {
    }

    public ReauthenticationFailedException(string message)
        : base(message)
    {
    }

    public ReauthenticationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Proof that the signed-in person is present: the current password or a current authenticator code.</summary>
public sealed record Reauthentication(string? CurrentPassword, string? Code);

/// <summary>
/// The identity e-mails (slice S1.11b2 Build item 16; KRN-IAM-01, KRN-IAM-02, ADM-USR-02, R-003; ruling Q16). Links and
/// codes are e-mailed, never returned by the API and never written to an event.
/// </summary>
public interface IIdentityEmailService
{
    /// <summary>
    /// Forgot password: e-mails a single-use reset link to an active account found by username or e-mail. It returns the
    /// same, after about the same time, whether or not the account exists, and sends nothing beyond the hourly limit.
    /// </summary>
    Task RequestPasswordResetAsync(string? login, CancellationToken cancellationToken);

    /// <summary>Sets a new password with a reset link: null when the link is not valid; empty when done; else the password errors.</summary>
    Task<IReadOnlyList<string>?> ResetPasswordAsync(string? token, string? newPassword, CancellationToken cancellationToken);

    /// <summary>E-mails a sign-in code for a second-factor challenge; false when the challenge or the e-mail factor is not valid.</summary>
    Task<bool> SendSignInCodeAsync(string? challengeToken, CancellationToken cancellationToken);

    /// <summary>An administrator sends (or sends again) an invited person's activation link (ADM-USR-02).</summary>
    Task<AdminResult> SendInvitationAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>An administrator sends a person a password reset link.</summary>
    Task<AdminResult> SendPasswordResetLinkAsync(Guid userId, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// An administrator changes a person's e-mail address (S1.11b2 review finding 2, CR-062): open reset links, codes and
    /// activation links end, the e-mail factor is removed (the person enrols it again) and the old address gets a notice.
    /// The change of a privileged user's address waits for a second person (<see cref="EmailChangeResult.PendingRequestId"/>).
    /// </summary>
    Task<EmailChangeResult> ChangeEmailAsync(Guid userId, string? email, string? reason, CancellationToken cancellationToken);
}

/// <summary>The result of an e-mail change: done, waiting for a second person (the request id), or refused.</summary>
public sealed record EmailChangeResult(AdminOutcome Outcome, string? Error = null, Guid? PendingRequestId = null);

/// <summary>
/// A second-factor request that cannot be served (slice S1.11b2): the method is not allowed for the customer, or too many
/// codes were e-mailed in the last hour. The message is the localization key.
/// </summary>
public sealed class MfaRequestRefusedException(string errorKey) : Exception(errorKey)
{
    public const string MethodNotAllowed = "errors.mfa.method_not_allowed";
    public const string TooManyCodes = "errors.mfa.too_many_codes";
}
