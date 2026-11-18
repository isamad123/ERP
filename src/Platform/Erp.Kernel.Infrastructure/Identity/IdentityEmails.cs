using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Domain.Identity;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Notifications;
using Erp.Kernel.Infrastructure.Notifications.Email;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// E-mailed six-digit codes (slice S1.11b2; KRN-IAM-02 e-mail factor, CR-009; R-003 late enrolment; ruling Q16: 10 minutes,
/// 5 tries, one use). A code is a row of <c>iam.one_time_tokens</c> whose hash covers the code and what it is bound to (the
/// sign-in challenge, or the device being enrolled), so a code works only for the step it was sent for. A new code ends the
/// earlier one of the same purpose; each wrong entry counts, and the code stops working at the try limit.
/// </summary>
internal static class EmailCodes
{
    public const string SignIn = "email_code";
    public const string EnrolmentConfirm = "enrolment_confirm";

    private static string Hash(Guid binding, string code) => OpaqueTokens.Hash(binding.ToString("N") + ":" + code);

    public static async Task<string> IssueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string purpose, Guid binding, TimeSpan lifetime, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        await IdentityDb.ExpireTokensAsync(c, tx, userId, purpose, actor, now, ct).ConfigureAwait(false);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        // The same binding and code may have been issued (and ended) before: that row is taken again, fresh.
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.one_time_tokens as t (id, user_id, purpose, token_hash, expires_at, used_at, attempts, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, null, 0, $6, $7, $6, $7)
            on conflict (token_hash) do update set expires_at = excluded.expires_at, used_at = null, attempts = 0, created_at = excluded.created_at,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = t.row_version + 1
            """, ct, Guid.CreateVersion7(now), userId, purpose, Hash(binding, code), now + lifetime, now, actor).ConfigureAwait(false);
        return code;
    }

    /// <summary>True when the code is the person's live code of the purpose for this binding; a wrong code counts one try.</summary>
    public static async Task<bool> VerifyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string purpose, Guid binding, string? code, int maxAttempts, Guid actor,
        DateTimeOffset now, CancellationToken ct)
    {
        var live = (await NotificationConsumer.ReadAsync(c, tx, """
            select id, token_hash, attempts from iam.one_time_tokens
            where user_id = $1 and purpose = $2 and used_at is null and expires_at > $3
            order by created_at desc, id desc limit 1 for update
            """, r => (Id: r.GetGuid(0), Hash: r.GetString(1), Attempts: r.GetInt32(2)), ct, userId, purpose, now).ConfigureAwait(false)).FirstOrDefault();
        if (live.Hash is null)
        {
            return false;
        }

        var candidate = code?.Trim();
        if (candidate is { Length: 6 } && candidate.All(char.IsAsciiDigit)
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(live.Hash), Encoding.ASCII.GetBytes(Hash(binding, candidate))))
        {
            await IdentityDb.UseTokenAsync(c, tx, live.Id, actor, now, ct).ConfigureAwait(false);
            return true;
        }

        // A wrong entry counts; at the limit the code stops working (used_at set), so a new one must be asked for.
        await IdentityDb.ExecuteAsync(c, tx, """
            update iam.one_time_tokens set attempts = attempts + 1, used_at = case when attempts + 1 >= $2 then $3 else used_at end,
                updated_at = $3, updated_by = $4, row_version = row_version + 1
            where id = $1
            """, ct, live.Id, maxAttempts, now, actor).ConfigureAwait(false);
        return false;
    }

    /// <summary>Codes e-mailed to one person in the last hour (each kind); more are refused, so a mailbox cannot be flooded.</summary>
    public const int PerHour = 10;

    public static Task<long> SentLastHourAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ScalarAsync<long>(c, tx, "select count(*) from iam.one_time_tokens where user_id = $1 and purpose in ('email_code', 'enrolment_confirm') and created_at > $2",
            ct, userId, now.AddHours(-1));
}

/// <summary>Queues an identity e-mail (catalogue entries <c>identity.*</c>): its link or code travels only in the encrypted secret.</summary>
internal static class IdentityMail
{
    public const string Invitation = "identity.invitation";
    public const string PasswordReset = "identity.password_reset";
    public const string SignInCode = "identity.email_code";
    public const string EnrolmentCode = "identity.enrolment_code";

    public static async Task QueueAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationCatalog catalog, IFieldEncryptor encryptor, Guid userId, string eventCode,
        object values, IReadOnlyDictionary<string, string> secret, DateTimeOffset now, CancellationToken ct)
    {
        var spec = catalog.Find(eventCode) ?? throw new InvalidOperationException($"The notification catalogue has no {eventCode}.");
        var person = (await NotificationConsumer.ReadAsync(c, tx, "select email, language from iam.users where id = $1", r => (Email: r.GetString(0), Language: r.GetString(1)), ct, userId)
            .ConfigureAwait(false)).Single();
        var map = JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        await EmailOutbox.QueueAsync(c, tx, spec, new QueuedEmail(userId, person.Email, spec.Code, null, person.Language, map, null, null, now, null, secret), encryptor,
            BaselineSeed.SystemUserId, now, ct).ConfigureAwait(false);
        await EmailOutbox.RequestSendSoonAsync(c, tx, now, ct).ConfigureAwait(false);
    }

    /// <summary>A label for an e-mail factor that names the address without showing it whole: <c>a***@example.com</c>.</summary>
    public static string Masked(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }
}

/// <summary>
/// The identity e-mails (slice S1.11b2 Build item 16; KRN-IAM-01, KRN-IAM-02, ADM-USR-02; ruling Q16; plan points P9 to
/// P11). Forgot password answers the same whether or not the account exists and takes at least
/// <see cref="ForgotMinimumTime"/>, so its time tells nothing either. A reset link works once, within
/// <c>iam.password_reset.minutes</c>; setting the password ends every session and an automatic lock, never an
/// administrator's lock or a disabled account. Every link and code goes out by e-mail only.
/// </summary>
public sealed class IdentityEmailService(
    ITenantConnectionProvider database,
    IClock clock,
    ICurrentActor actor,
    IPasswordHasher hasher,
    IFieldEncryptorProvider encryptors,
    NotificationCatalog catalog,
    TimeProvider time) : IIdentityEmailService
{
    public const string ResetLinkPath = "/reset-password?token=";
    public const string ActivationLinkPath = "/activate?token=";
    public const string NotInvited = "errors.user.not_invited";
    public const string NoResetPossible = "errors.user.reset_not_possible";

    /// <summary>The least time a forgot-password request takes, whatever happens inside.</summary>
    public static readonly TimeSpan ForgotMinimumTime = TimeSpan.FromMilliseconds(400);

    private const int MaxLoginLength = 320;

    public async Task RequestPasswordResetAsync(string? login, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        try
        {
            if (string.IsNullOrWhiteSpace(login) || login.Length > MaxLoginLength)
            {
                return;
            }

            var now = clock.UtcNow;
            await using var c = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var account = await IdentityDb.FindForSignInAsync(c, tx, login.Trim(), cancellationToken).ConfigureAwait(false);
            if (account is null || !MayReset(account))
            {
                return;
            }

            var policy = await IdentitySettings.ReadAsync(c, tx, account.Id, now, cancellationToken).ConfigureAwait(false);
            var recent = await IdentityDb.ScalarAsync<long>(c, tx, "select count(*) from iam.one_time_tokens where user_id = $1 and purpose = 'password_reset' and created_at > $2",
                cancellationToken, account.Id, now.AddHours(-1)).ConfigureAwait(false);
            if (recent >= policy.Email.ResetLinksPerHour)
            {
                return; // the hourly limit: the same answer, no e-mail
            }

            await SendResetAsync(c, tx, account, policy, account.Id, "self", null, now, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var left = ForgotMinimumTime - time.GetElapsedTime(started);
            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left, time, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<string>?> ResetPasswordAsync(string? token, string? newPassword, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var link = await IdentityDb.FindTokenAsync(c, tx, token, IdentityDb.PasswordReset, now, cancellationToken).ConfigureAwait(false);
        var account = link is null ? null : await IdentityDb.GetAccountAsync(c, tx, link.UserId, cancellationToken).ConfigureAwait(false);
        if (link is null || account is null || !MayReset(account))
        {
            return null;
        }

        var policy = await IdentitySettings.ReadAsync(c, tx, account.Id, now, cancellationToken).ConfigureAwait(false);
        var errors = await Passwords.ValidateNewAsync(c, tx, account.Id, newPassword, policy.Password, hasher, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return errors; // the link stays usable until it expires
        }

        await IdentityDb.SetPasswordAsync(c, tx, account.Id, hasher.Hash(newPassword!), hasher.Algorithm, account.Id, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.UseTokenAsync(c, tx, link.Id, account.Id, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExpireTokensAsync(c, tx, account.Id, IdentityDb.PasswordReset, account.Id, now, cancellationToken).ConfigureAwait(false);
        if (account.Status == UserStatuses.Locked)
        {
            // Only an automatic lock gets here (MayReset): proving the mailbox ends it, as the lock time would.
            await IdentityDb.SetStatusAsync(c, tx, account.Id, UserStatuses.Active, account.Id, now, cancellationToken).ConfigureAwait(false);
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.UserUnlocked, account.Id, account.Id, now, new { automatic = true, reason = "password_reset" }, cancellationToken).ConfigureAwait(false);
        }

        var ended = await IdentityDb.RevokeAllSessionsAsync(c, tx, account.Id, "password_reset", now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, IdentityEvents.PasswordChanged, account.Id, account.Id, now, new { reason = "reset", sessions_revoked = ended }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task<bool> SendSignInCodeAsync(string? challengeToken, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var challenge = await IdentityDb.FindTokenAsync(c, tx, challengeToken, IdentityDb.MfaChallenge, now, cancellationToken).ConfigureAwait(false);
        var account = challenge is null ? null : await IdentityDb.GetAccountAsync(c, tx, challenge.UserId, cancellationToken).ConfigureAwait(false);
        if (challenge is null || account is not { Status: UserStatuses.Active })
        {
            return false;
        }

        var policy = await IdentitySettings.ReadAsync(c, tx, account.Id, now, cancellationToken).ConfigureAwait(false);
        if (!policy.Mfa.AllowedMethods.Contains(MfaKinds.Email, StringComparer.Ordinal) || !await IdentityDb.HasActiveEmailFactorAsync(c, tx, account.Id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!await EmailCodeForAsync(c, tx, catalog, encryptor, account.Id, EmailCodes.SignIn, challenge.Id, IdentityMail.SignInCode, policy, now, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<AdminResult> SendInvitationAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await IdentityDb.GetAccountAsync(c, tx, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AdminResult.NotFound;
        }

        if (account.Status != UserStatuses.Invited)
        {
            return new AdminResult(AdminOutcome.Invalid, NotInvited);
        }

        var policy = await IdentitySettings.ReadAsync(c, tx, userId, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExpireTokensAsync(c, tx, userId, IdentityDb.Activation, actor.UserId, now, cancellationToken).ConfigureAwait(false);
        var token = await IdentityDb.IssueTokenAsync(c, tx, userId, IdentityDb.Activation, policy.ActivationTokenLifetime, actor.UserId, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, IdentityEvents.ActivationTokenIssued, userId, actor.UserId, now, new { expires_at = now + policy.ActivationTokenLifetime, emailed = true }, cancellationToken).ConfigureAwait(false);
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        await IdentityMail.QueueAsync(c, tx, catalog, encryptor, userId, IdentityMail.Invitation, new { hours = (int)policy.ActivationTokenLifetime.TotalHours },
            new Dictionary<string, string> { ["link"] = ActivationLinkPath + Uri.EscapeDataString(token) }, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AdminResult.Done;
    }

    public async Task<AdminResult> SendPasswordResetLinkAsync(Guid userId, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await IdentityDb.GetAccountAsync(c, tx, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AdminResult.NotFound;
        }

        if (!MayReset(account))
        {
            return new AdminResult(AdminOutcome.Invalid, NoResetPossible);
        }

        var policy = await IdentitySettings.ReadAsync(c, tx, userId, now, cancellationToken).ConfigureAwait(false);
        await SendResetAsync(c, tx, account, policy, actor.UserId, "admin", reason, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AdminResult.Done;
    }

    /// <summary>
    /// Who may reset a password by e-mail (plan point P9): an active account with a password, or one under an automatic
    /// lock (it has an end time). An administrator's lock, a disabled, archived or invited account: never.
    /// </summary>
    private static bool MayReset(UserAccount account) =>
        account.HasCredential && (account.Status == UserStatuses.Active || (account.Status == UserStatuses.Locked && account.LockedUntil is not null));

    private async Task SendResetAsync(NpgsqlConnection c, NpgsqlTransaction tx, UserAccount account, IdentityPolicy policy, Guid by, string source, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        await IdentityDb.ExpireTokensAsync(c, tx, account.Id, IdentityDb.PasswordReset, by, now, ct).ConfigureAwait(false);
        var token = await IdentityDb.IssueTokenAsync(c, tx, account.Id, IdentityDb.PasswordReset, policy.Email.ResetLinkLifetime, by, now, ct).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, IdentityEvents.PasswordResetLinkSent, account.Id, by, now, new { by = source, reason, expires_at = now + policy.Email.ResetLinkLifetime }, ct).ConfigureAwait(false);
        var encryptor = await encryptors.GetAsync(ct).ConfigureAwait(false);
        await IdentityMail.QueueAsync(c, tx, catalog, encryptor, account.Id, IdentityMail.PasswordReset, new { minutes = (int)policy.Email.ResetLinkLifetime.TotalMinutes },
            new Dictionary<string, string> { ["link"] = ResetLinkPath + Uri.EscapeDataString(token) }, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues and e-mails a code for a step of the sign-in or enrolment services (the e-mail factor's first code, the R-003
    /// confirmation), in their transaction. False when the hourly limit of codes is reached.
    /// </summary>
    internal static async Task<bool> EmailCodeForAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationCatalog catalog, IFieldEncryptor encryptor, Guid userId, string purpose,
        Guid binding, string eventCode, IdentityPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        if (await EmailCodes.SentLastHourAsync(c, tx, userId, now, ct).ConfigureAwait(false) >= EmailCodes.PerHour)
        {
            return false;
        }

        var code = await EmailCodes.IssueAsync(c, tx, userId, purpose, binding, policy.Email.CodeLifetime, userId, now, ct).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, IdentityEvents.EmailCodeSent, userId, userId, now, new { purpose, expires_at = now + policy.Email.CodeLifetime }, ct).ConfigureAwait(false);
        await IdentityMail.QueueAsync(c, tx, catalog, encryptor, userId, eventCode, new { minutes = (int)policy.Email.CodeLifetime.TotalMinutes },
            new Dictionary<string, string> { ["code"] = code }, now, ct).ConfigureAwait(false);
        return true;
    }
}
