using System.Security.Cryptography;
using System.Text;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Domain.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// Sign-in for the customer bound by host name (FR-KRN-IAM-01, 02, 04; slice S1.05 build items 3 to 5 and 8).
/// Every failure before the password is proven answers the same <see cref="AuthResult.Failed"/> after the same
/// Argon2id work, whether the user is unknown, the password wrong, or the account disabled or locked (ruling P5).
/// Order of steps after the password: second factor (if a device is enrolled) → required password change →
/// required MFA enrolment (after the grace period) → session.
/// </summary>
public sealed class AuthenticationService(
    ITenantConnectionProvider database,
    ITenantContext tenant,
    IClock clock,
    IPasswordHasher hasher,
    IAccessTokenIssuer issuer,
    IFieldEncryptorProvider encryptors) : IAuthenticationService
{
    private const int MaxLoginLength = 320;
    private const int MaxPasswordLength = 1024;

    private static readonly Lock DummyGate = new();
    private static string? dummyHash;

    public async Task<AuthResult> SignInAsync(string? login, string? password, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password) || login.Length > MaxLoginLength || password.Length > MaxPasswordLength)
        {
            // Malformed attempts are recorded too (every attempt is written, S1.05 review finding 9).
            return await FailAsync(connection, tx, login ?? string.Empty, "bad_password", client, now, cancellationToken).ConfigureAwait(false);
        }

        var account = await IdentityDb.FindForSignInAsync(connection, tx, login.Trim(), cancellationToken).ConfigureAwait(false);

        // The same work on every path (policy read, one Argon2id check, one credential statement), so an unknown user
        // takes about as long as a known one (slice build item 3, review finding 9).
        var policy = await IdentitySettings.ReadAsync(connection, tx, account?.Id, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.HasCredential)
        {
            hasher.Verify(password, DummyHash(), hasher.Algorithm, out _);
            await IdentityDb.ExecuteAsync(connection, tx, "update iam.user_credentials set failed_attempts = failed_attempts where false", cancellationToken).ConfigureAwait(false);
            return await FailAsync(connection, tx, login, "bad_password", client, now, cancellationToken).ConfigureAwait(false);
        }

        var ok = hasher.Verify(password, account.PasswordHash!, account.HashAlgorithm!, out var needsRehash);
        if (account.Status is not (UserStatuses.Active or UserStatuses.Locked))
        {
            return await FailAsync(connection, tx, login, "disabled", client, now, cancellationToken).ConfigureAwait(false);
        }

        if (account.Status == UserStatuses.Locked)
        {
            if (account.LockedUntil is not { } until || until > now)
            {
                return await FailAsync(connection, tx, login, "locked", client, now, cancellationToken).ConfigureAwait(false);
            }

            // The automatic lock has ended (ruling P3): the account is active again, with a fresh failure count.
            await IdentityDb.SetStatusAsync(connection, tx, account.Id, UserStatuses.Active, account.Id, now, cancellationToken).ConfigureAwait(false);
            await IdentityDb.SetFailuresAsync(connection, tx, account.Id, 0, null, cancellationToken).ConfigureAwait(false);
            await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.UserUnlocked, account.Id, account.Id, now, new { automatic = true }, cancellationToken).ConfigureAwait(false);
            account = account with { Status = UserStatuses.Active, FailedAttempts = 0, LockedUntil = null };
        }

        if (!ok)
        {
            await CountFailureAsync(connection, tx, account, policy, now, cancellationToken).ConfigureAwait(false);
            return await FailAsync(connection, tx, login, "bad_password", client, now, cancellationToken).ConfigureAwait(false);
        }

        if (needsRehash)
        {
            await IdentityDb.RehashAsync(connection, tx, account.Id, hasher.Hash(password), hasher.Algorithm, cancellationToken).ConfigureAwait(false);
        }

        // The failure count is NOT reset here: only issuing a session resets it (ContinueAsync), so a known password
        // cannot be used to wipe failed second-factor guesses (S1.05 review finding 1).
        var result = await ContinueAsync(connection, tx, account, policy, mfaDone: false, client, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<AuthResult> CompleteMfaAsync(string? challengeToken, string? code, string? recoveryCode, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (token, account) = await ChallengeAsync(connection, tx, challengeToken, IdentityDb.MfaChallenge, now, cancellationToken).ConfigureAwait(false);
        if (token is null || account is null)
        {
            return AuthResult.Failed;
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        var ok = false;
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            ok = recoveryCode.Length <= 40 && await IdentityDb.UseRecoveryCodeAsync(connection, tx, account.Id, recoveryCode, hasher, account.Id, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
            foreach (var device in await IdentityDb.MfaDevicesAsync(connection, tx, account.Id, "active", cancellationToken).ConfigureAwait(false))
            {
                var secret = encryptor.Decrypt(device.Secret, IdentityDb.MfaSecretPurpose);
                var step = Totp.Verify(secret, code, now, device.LastUsedAt is { } used ? Totp.StepAt(used) : null);
                if (step is { } accepted && await IdentityDb.MarkMfaUsedAsync(connection, tx, device.Id, accepted, account.Id, now, null, cancellationToken).ConfigureAwait(false))
                {
                    ok = true;
                    break;
                }
            }
        }

        if (!ok)
        {
            await CountFailureAsync(connection, tx, account, policy, now, cancellationToken).ConfigureAwait(false);
            return await FailAsync(connection, tx, account.Username, "mfa_failed", client, now, cancellationToken).ConfigureAwait(false);
        }

        await IdentityDb.UseTokenAsync(connection, tx, token.Id, account.Id, now, cancellationToken).ConfigureAwait(false);
        var result = await ContinueAsync(connection, tx, account, policy, mfaDone: true, client, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<AuthResult> ChangeRequiredPasswordAsync(string? challengeToken, string? newPassword, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (token, account) = await ChallengeAsync(connection, tx, challengeToken, IdentityDb.PasswordChange, now, cancellationToken).ConfigureAwait(false);
        if (token is null || account is null)
        {
            return AuthResult.Failed;
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        var errors = await Passwords.ValidateNewAsync(connection, tx, account.Id, newPassword, policy.Password, hasher, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return new AuthResult(AuthOutcome.Invalid, Errors: errors);
        }

        await IdentityDb.SetPasswordAsync(connection, tx, account.Id, hasher.Hash(newPassword!), hasher.Algorithm, account.Id, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.UseTokenAsync(connection, tx, token.Id, account.Id, now, cancellationToken).ConfigureAwait(false);
        var ended = await IdentityDb.RevokeAllSessionsAsync(connection, tx, account.Id, "password_changed", now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.PasswordChanged, account.Id, account.Id, now, new { reason = "required", sessions_revoked = ended }, cancellationToken).ConfigureAwait(false);

        // The change token is only issued after the second factor (or when the user has none), so MFA is done here.
        var result = await ContinueAsync(connection, tx, account with { MustChange = false, PasswordChangedAt = now }, policy, mfaDone: true, client, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<MfaEnrolment?> StartRequiredEnrolmentAsync(string? challengeToken, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (token, account) = await ChallengeAsync(connection, tx, challengeToken, IdentityDb.MfaEnrolment, now, cancellationToken).ConfigureAwait(false);
        if (token is null || account is null)
        {
            return null;
        }

        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var enrolment = await MfaEnrolments.StartAsync(connection, tx, account, null, tenant.TenantCode, encryptor, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return enrolment;
    }

    public async Task<AuthResult> ConfirmRequiredEnrolmentAsync(string? challengeToken, Guid deviceId, string? code, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (token, account) = await ChallengeAsync(connection, tx, challengeToken, IdentityDb.MfaEnrolment, now, cancellationToken).ConfigureAwait(false);
        if (token is null || account is null)
        {
            return AuthResult.Failed;
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var codes = await MfaEnrolments.ConfirmAsync(connection, tx, account.Id, deviceId, code, policy.Mfa, encryptor, hasher, now, cancellationToken).ConfigureAwait(false);
        if (codes is null)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuthResult.Invalid(MfaEnrolments.CodeInvalid);
        }

        await IdentityDb.UseTokenAsync(connection, tx, token.Id, account.Id, now, cancellationToken).ConfigureAwait(false);
        var result = await ContinueAsync(connection, tx, account, policy, mfaDone: true, client, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result with { RecoveryCodes = codes };
    }

    public async Task<AuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!SessionTokens.TryParse(refreshToken, out var sessionId))
        {
            return AuthResult.Failed;
        }

        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Lock order user → session, as in sign-in and the administrator actions (S1.05 review finding 3).
        if (await IdentityDb.SessionOwnerAsync(connection, tx, sessionId, cancellationToken).ConfigureAwait(false) is not { } owner)
        {
            return AuthResult.Failed;
        }

        var account = await IdentityDb.GetAccountAsync(connection, tx, owner, cancellationToken).ConfigureAwait(false);
        var session = await IdentityDb.GetSessionAsync(connection, tx, sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null)
        {
            return AuthResult.Failed;
        }

        async Task<AuthResult> EndAsync(string reason)
        {
            await IdentityDb.RevokeSessionAsync(connection, tx, session.Id, null, reason, now, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuthResult.Failed;
        }

        var presented = OpaqueTokens.Hash(refreshToken!);
        if (!Matches(session.RefreshTokenHash, presented))
        {
            if (!Matches(session.PreviousRefreshHash, presented))
            {
                return AuthResult.Failed; // a wrong secret is a plain failure; knowing a session id cannot end it (finding 6)
            }

            // The token replaced by the last rotation came back: it may have been stolen, so the session ends (ruling P6, CR-016).
            await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.SessionsRevoked, session.UserId, ControlPlane.Provisioning.BaselineSeed.SystemUserId, now, new { reason = "refresh_reuse", session = session.Id }, cancellationToken).ConfigureAwait(false);
            return await EndAsync("refresh_reuse").ConfigureAwait(false);
        }

        if (now >= session.ExpiresAt)
        {
            return await EndAsync("absolute_timeout").ConfigureAwait(false);
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, session.UserId, cancellationToken).ConfigureAwait(false);
        if (now - session.LastSeenAt >= TimeSpan.FromMinutes(policy.Session.IdleMinutes))
        {
            return await EndAsync("idle_timeout").ConfigureAwait(false);
        }

        // An automatic lock (it has an end time) stops new sign-ins but does not end open sessions, so a stranger
        // guessing passwords cannot sign the real user out. Every other status but active ends the session.
        if (account is null || !(account.Status == UserStatuses.Active || (account.Status == UserStatuses.Locked && account.LockedUntil is not null)))
        {
            return await EndAsync("user_" + (account?.Status ?? "missing")).ConfigureAwait(false);
        }

        if (account.MustChange || (account.PasswordChangedAt is { } changed && PasswordRules.IsExpired(changed, now, policy.Password)))
        {
            return await EndAsync("password_change_required").ConfigureAwait(false);
        }

        var refresh = SessionTokens.New(session.Id);
        await IdentityDb.RotateRefreshAsync(connection, tx, session.Id, OpaqueTokens.Hash(refresh), now, cancellationToken).ConfigureAwait(false);
        var (access, expires) = issuer.Issue(session.UserId, tenant.TenantCode, session.Id, now);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AuthResult(AuthOutcome.Tokens, new TokenPair(access, expires, refresh, session.Id));
    }

    public async Task SignOutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await IdentityDb.RevokeSessionAsync(connection, tx, sessionId, userId, "sign_out", now, cancellationToken).ConfigureAwait(false) == 1)
        {
            await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.SessionsRevoked, userId, userId, now, new { reason = "sign_out", session = sessionId }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResult> ActivateAsync(string? activationToken, string? newPassword, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var token = await IdentityDb.FindTokenAsync(connection, tx, activationToken, IdentityDb.Activation, now, cancellationToken).ConfigureAwait(false);
        var account = token is null ? null : await IdentityDb.GetAccountAsync(connection, tx, token.UserId, cancellationToken).ConfigureAwait(false);
        if (token is null || account is null || account.Status != UserStatuses.Invited)
        {
            return AuthResult.Failed;
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        var errors = PasswordRules.Check(newPassword, policy.Password);
        if (errors.Count > 0)
        {
            return new AuthResult(AuthOutcome.Invalid, Errors: errors);
        }

        if (!await IdentityDb.SeatAvailableAsync(connection, tx, cancellationToken).ConfigureAwait(false))
        {
            return AuthResult.Invalid(UserAdministrationService.UserLimitReached);
        }

        await IdentityDb.SetPasswordAsync(connection, tx, account.Id, hasher.Hash(newPassword!), hasher.Algorithm, account.Id, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.UseTokenAsync(connection, tx, token.Id, account.Id, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.SetStatusAsync(connection, tx, account.Id, UserStatuses.Active, account.Id, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.UserActivated, account.Id, account.Id, now, new { }, cancellationToken).ConfigureAwait(false);
        var activated = account with { Status = UserStatuses.Active, HasCredential = true, MustChange = false, PasswordChangedAt = now, FailedAttempts = 0 };
        var result = await ContinueAsync(connection, tx, activated, policy, mfaDone: false, client, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The next step after a proven password or factor: another challenge, or a new session.</summary>
    private async Task<AuthResult> ContinueAsync(NpgsqlConnection connection, NpgsqlTransaction tx, UserAccount account, IdentityPolicy policy, bool mfaDone, ClientInfo client, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hasDevice = await IdentityDb.HasActiveMfaAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        if (hasDevice && !mfaDone)
        {
            var challenge = await IdentityDb.IssueTokenAsync(connection, tx, account.Id, IdentityDb.MfaChallenge, policy.ChallengeLifetime, account.Id, now, cancellationToken).ConfigureAwait(false);
            return new AuthResult(AuthOutcome.MfaRequired, ChallengeToken: challenge);
        }

        if (account.MustChange || (account.PasswordChangedAt is { } changed && PasswordRules.IsExpired(changed, now, policy.Password)))
        {
            var change = await IdentityDb.IssueTokenAsync(connection, tx, account.Id, IdentityDb.PasswordChange, policy.ChallengeLifetime, account.Id, now, cancellationToken).ConfigureAwait(false);
            return new AuthResult(AuthOutcome.PasswordChangeRequired, ChallengeToken: change);
        }

        DateTimeOffset? enrolBy = null;
        if (policy.Mfa.Required && !hasDevice)
        {
            var activatedAt = await IdentityDb.ActivatedAtAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false) ?? now;
            var graceEnd = policy.Mfa.GraceEndsAt(activatedAt);
            if (now >= graceEnd)
            {
                var enrol = await IdentityDb.IssueTokenAsync(connection, tx, account.Id, IdentityDb.MfaEnrolment, policy.ChallengeLifetime, account.Id, now, cancellationToken).ConfigureAwait(false);
                return new AuthResult(AuthOutcome.MfaEnrolmentRequired, ChallengeToken: enrol);
            }

            enrolBy = graceEnd;
        }

        await IdentityDb.ResetFailuresAsync(connection, tx, account.Id, cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.CreateVersion7(now);
        var refresh = SessionTokens.New(sessionId);
        await IdentityDb.InsertSessionAsync(connection, tx, sessionId, account.Id, now, now.AddMinutes(policy.Session.AbsoluteMinutes), client, OpaqueTokens.Hash(refresh), cancellationToken).ConfigureAwait(false);
        await IdentityDb.EnforceConcurrentLimitAsync(connection, tx, account.Id, sessionId, policy.Session.MaxConcurrent, TimeSpan.FromMinutes(policy.Session.IdleMinutes), now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(connection, tx, "update iam.users set last_login_at = $2 where id = $1", cancellationToken, account.Id, now).ConfigureAwait(false);
        await IdentityDb.LogAttemptAsync(connection, tx, account.Username, "success", client, account.Id, now, cancellationToken).ConfigureAwait(false);
        var (access, expires) = issuer.Issue(account.Id, tenant.TenantCode, sessionId, now);
        return new AuthResult(AuthOutcome.Tokens, new TokenPair(access, expires, refresh, sessionId), MfaEnrolBy: enrolBy);
    }

    /// <summary>A valid challenge token of the purpose and its user, who must still be active.</summary>
    private static async Task<(OneTimeToken? Token, UserAccount? Account)> ChallengeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string? challengeToken, string purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = await IdentityDb.FindTokenAsync(connection, tx, challengeToken, purpose, now, cancellationToken).ConfigureAwait(false);
        var account = token is null ? null : await IdentityDb.GetAccountAsync(connection, tx, token.UserId, cancellationToken).ConfigureAwait(false);
        return account is { Status: UserStatuses.Active, HasCredential: true } ? (token, account) : (null, null);
    }

    private static Task CountFailureAsync(NpgsqlConnection connection, NpgsqlTransaction tx, UserAccount account, IdentityPolicy policy, DateTimeOffset now, CancellationToken cancellationToken) =>
        Lockouts.CountFailureAsync(connection, tx, account.Id, policy, now, cancellationToken);

    private static bool Matches(string? storedHash, string presentedHash) =>
        storedHash is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(presentedHash));

    /// <summary>A fixed Argon2id hash for unknown users, made once with the injected hasher (same parameters and gate).</summary>
    private string DummyHash()
    {
        lock (DummyGate)
        {
            return dummyHash ??= hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
        }
    }

    private static async Task<AuthResult> FailAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string login, string outcome, ClientInfo client, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await IdentityDb.LogAttemptAsync(connection, tx, login, outcome, client, ControlPlane.Provisioning.BaselineSeed.SystemUserId, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AuthResult.Failed;
    }
}

/// <summary>Failed passwords and codes, counted towards the automatic lock (FR-KRN-IAM-01; ruling P3).</summary>
internal static class Lockouts
{
    /// <summary>
    /// Adds one failure atomically (review finding 2); at the threshold the account locks until the lock time. The lock
    /// event is raised once, by the request that reaches the threshold.
    /// </summary>
    public static async Task CountFailureAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, IdentityPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failed = await IdentityDb.IncrementFailuresAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false);
        if (Lockout.AfterFailure(failed, now, policy.Password) is { } until)
        {
            await IdentityDb.SetLockedUntilAsync(connection, tx, userId, until, cancellationToken).ConfigureAwait(false);
            await IdentityDb.SetStatusAsync(connection, tx, userId, UserStatuses.Locked, userId, now, cancellationToken).ConfigureAwait(false);
            if (failed == policy.Password.LockoutThreshold)
            {
                await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.UserLocked, userId, userId, now, new { automatic = true, locked_until = until }, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Refresh tokens: <c>{session id}.{256 random bits}</c>, so a reused old token can be traced to its session.</summary>
internal static class SessionTokens
{
    public static string New(Guid sessionId) => sessionId.ToString("N") + "." + OpaqueTokens.New();

    public static bool TryParse(string? token, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        return token is { Length: > 33 and < 200 } && token[32] == '.' && Guid.TryParseExact(token[..32], "N", out sessionId);
    }
}

/// <summary>New passwords: policy rules plus the history check against stored hashes (FR-ADM-USR-20).</summary>
internal static class Passwords
{
    public static async Task<IReadOnlyList<string>> ValidateNewAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, string? password, PasswordPolicy policy, IPasswordHasher hasher, CancellationToken cancellationToken)
    {
        var errors = PasswordRules.Check(password, policy);
        if (errors.Count > 0)
        {
            return errors;
        }

        foreach (var old in await IdentityDb.RecentPasswordHashesAsync(connection, tx, userId, policy.HistoryCount, cancellationToken).ConfigureAwait(false))
        {
            if (hasher.Verify(password!, old, hasher.Algorithm, out _))
            {
                return [PasswordRules.Reused];
            }
        }

        return [];
    }
}

/// <summary>Authenticator-app enrolment shared by self-service and the required enrolment at sign-in (FR-KRN-IAM-02).</summary>
internal static class MfaEnrolments
{
    public const string CodeInvalid = "errors.mfa.code_invalid";

    public static async Task<MfaEnrolment> StartAsync(NpgsqlConnection connection, NpgsqlTransaction tx, UserAccount account, string? label, string tenantCode, IFieldEncryptor encryptor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // An unfinished earlier enrolment is replaced, so pending secrets do not pile up.
        await IdentityDb.ExecuteAsync(connection, tx, "delete from iam.mfa_devices where user_id = $1 and status = 'pending'", cancellationToken, account.Id).ConfigureAwait(false);
        var secret = RandomNumberGenerator.GetBytes(Totp.SecretBytes);
        var id = Guid.CreateVersion7(now);
        await IdentityDb.ExecuteAsync(connection, tx, """
            insert into iam.mfa_devices (id, user_id, kind, secret, label, last_used_at, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'totp', $3, $4, null, 'pending', $5, $2, $5, $2)
            """, cancellationToken, id, account.Id, encryptor.Encrypt(secret, IdentityDb.MfaSecretPurpose), label is { Length: > 100 } ? label[..100] : label, now).ConfigureAwait(false);
        return new MfaEnrolment(id, Totp.EnrolmentUri("ERP " + tenantCode, account.Username, secret), Base32.Encode(secret));
    }

    /// <summary>Activates a pending device with its first code; recovery codes for the first device, empty for another, null if the code is wrong.</summary>
    public static async Task<IReadOnlyList<string>?> ConfirmAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, Guid deviceId, string? code, MfaPolicy policy, IFieldEncryptor encryptor, IPasswordHasher hasher, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var device = (await IdentityDb.MfaDevicesAsync(connection, tx, userId, "pending", cancellationToken).ConfigureAwait(false)).Find(d => d.Id == deviceId);
        if (device?.Secret is null)
        {
            return null;
        }

        var step = Totp.Verify(encryptor.Decrypt(device.Secret, IdentityDb.MfaSecretPurpose), code, now, null);
        var first = !await IdentityDb.HasActiveMfaAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false);
        if (step is not { } accepted || !await IdentityDb.MarkMfaUsedAsync(connection, tx, device.Id, accepted, userId, now, "active", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.MfaEnrolled, userId, userId, now, new { device = device.Id, kind = "totp" }, cancellationToken).ConfigureAwait(false);
        return first ? await IdentityDb.NewRecoveryCodesAsync(connection, tx, userId, policy.RecoveryCodeCount, hasher, userId, now, cancellationToken).ConfigureAwait(false) : [];
    }
}
