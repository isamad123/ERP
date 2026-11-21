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

    /// <summary>Key purpose of the code hash: a keyed hash (HMAC with the customer's field key), so a copy of the database alone cannot find a live code (S1.11b2 review finding 10).</summary>
    public const string HashPurpose = "iam.one_time_tokens.email_code";

    private static string Hash(IFieldEncryptor key, Guid binding, string code) =>
        Convert.ToHexStringLower(key.BlindIndex(Encoding.UTF8.GetBytes(binding.ToString("N") + ":" + code), HashPurpose));

    public static async Task<string> IssueAsync(NpgsqlConnection c, NpgsqlTransaction tx, IFieldEncryptor key, Guid userId, string purpose, Guid binding, TimeSpan lifetime, Guid actor, DateTimeOffset now,
        CancellationToken ct)
    {
        await IdentityDb.ExpireTokensAsync(c, tx, userId, purpose, actor, now, ct).ConfigureAwait(false);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        // The same binding and code may have been issued (and ended) before: that row is taken again, fresh.
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.one_time_tokens as t (id, user_id, purpose, token_hash, expires_at, used_at, attempts, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, null, 0, $6, $7, $6, $7)
            on conflict (token_hash) do update set expires_at = excluded.expires_at, used_at = null, attempts = 0, created_at = excluded.created_at,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = t.row_version + 1
            """, ct, Guid.CreateVersion7(now), userId, purpose, Hash(key, binding, code), now + lifetime, now, actor).ConfigureAwait(false);
        return code;
    }

    /// <summary>True when the code is the person's live code of the purpose for this binding; a wrong code counts one try.</summary>
    public static async Task<bool> VerifyAsync(NpgsqlConnection c, NpgsqlTransaction tx, IFieldEncryptor key, Guid userId, string purpose, Guid binding, string? code, int maxAttempts, Guid actor,
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
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(live.Hash), Encoding.ASCII.GetBytes(Hash(key, binding, candidate))))
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
    public const string EmailChanged = "identity.email_changed";

    /// <summary>Queues the e-mail to the person's address, or to <paramref name="recipient"/> (the old address of an e-mail change).</summary>
    public static async Task QueueAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationCatalog catalog, IFieldEncryptor? encryptor, Guid userId, string eventCode,
        object values, IReadOnlyDictionary<string, string> secret, DateTimeOffset now, CancellationToken ct, string? recipient = null)
    {
        var spec = catalog.Find(eventCode) ?? throw new InvalidOperationException($"The notification catalogue has no {eventCode}.");
        var person = (await NotificationConsumer.ReadAsync(c, tx, "select email, language from iam.users where id = $1", r => (Email: r.GetString(0), Language: r.GetString(1)), ct, userId)
            .ConfigureAwait(false)).Single();
        var map = JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        await EmailOutbox.QueueAsync(c, tx, spec, new QueuedEmail(userId, recipient ?? person.Email, spec.Code, null, person.Language, map, null, null, now, null, secret), encryptor,
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

        // Lock order user, then token, like forgot password and the administrator's link (S1.11b2 review finding 8): the
        // person is found without a lock, locked, and only then is the link locked and checked again.
        var owner = await IdentityDb.TokenOwnerAsync(c, tx, token, IdentityDb.PasswordReset, now, cancellationToken).ConfigureAwait(false);
        var account = owner is { } id ? await IdentityDb.GetAccountAsync(c, tx, id, cancellationToken).ConfigureAwait(false) : null;
        var link = account is null ? null : await IdentityDb.FindTokenAsync(c, tx, token, IdentityDb.PasswordReset, now, cancellationToken).ConfigureAwait(false);
        if (link is null || account is null || link.UserId != account.Id || !MayReset(account))
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
        await TrustedDevices.RevokeAllAsync(c, tx, account.Id, TrustedDevices.PasswordReset, account.Id, now, cancellationToken).ConfigureAwait(false);
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

    public const string EmailInvalid = "errors.user.email_invalid";
    public const string EmailTaken = "errors.user.email_taken";
    public const string EmailUnchanged = "errors.user.email_unchanged";
    public const string EmailChangedMeanwhile = "errors.user.email_changed_meanwhile";
    public const string ReasonRequired = "errors.user.reason_required";
    private const int MaxEmailLength = 254;

    public async Task<EmailChangeResult> ChangeEmailAsync(Guid userId, string? email, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await IdentityDb.GetAccountAsync(c, tx, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new EmailChangeResult(AdminOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return new EmailChangeResult(AdminOutcome.Invalid, ReasonRequired);
        }

        var wanted = email?.Trim() ?? string.Empty;
        if (await CheckNewEmailAsync(c, tx, account.Id, account.Email, wanted, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return new EmailChangeResult(AdminOutcome.Invalid, problem);
        }

        // A privileged user's address waits for a second person while the customer keeps maker-checker on (S1.06 Q10).
        if (await Access.AccessDb.SettingOnAsync(c, tx, IdentitySettings.MakerCheckerSensitiveRoles, cancellationToken).ConfigureAwait(false)
            && await IdentitySettings.IsPrivilegedAsync(c, tx, account.Id, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false))
        {
            var change = new Access.AccessChange
            {
                Kind = Access.AccessChange.ChangeUserEmail,
                UserId = account.Id,
                Email = wanted,
                PreviousEmail = account.Email,
                Reason = reason.Trim(),
            };
            var pending = await Access.AccessAdministrationService.SubmitChangeAsync(c, tx, change, now, actor.UserId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EmailChangeResult(AdminOutcome.Done, null, pending.RequestId);
        }

        await ApplyEmailChangeAsync(c, tx, catalog, account, wanted, reason.Trim(), now, actor.UserId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EmailChangeResult(AdminOutcome.Done);
    }

    /// <summary>The checks of a held e-mail change when it is approved: null when it can still be applied, else the error key.</summary>
    internal static async Task<string?> CheckHeldEmailChangeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid? userId, string? email, string? previous, CancellationToken ct)
    {
        if (userId is null || email is null || previous is null)
        {
            return Access.AccessAdministrationService.InvalidRequest;
        }

        var current = await IdentityDb.ScalarAsync<string?>(c, tx, "select email from iam.users where id = $1 and not is_deleted and not is_system", ct, userId).ConfigureAwait(false);
        if (current is null)
        {
            return Access.AccessAdministrationService.UserNotFound;
        }

        return !string.Equals(current, previous, StringComparison.OrdinalIgnoreCase)
            ? EmailChangedMeanwhile
            : await CheckNewEmailAsync(c, tx, userId.Value, current, email, ct).ConfigureAwait(false);
    }

    /// <summary>Applies an approved e-mail change in the approval's transaction (maker-checker, CR-062).</summary>
    internal static async Task<Application.Access.AccessResult> ApplyHeldEmailChangeAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationCatalog catalog, Guid userId, string email,
        string previous, string? reason, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var account = await IdentityDb.GetAccountAsync(c, tx, userId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return Application.Access.AccessResult.Invalid(Access.AccessAdministrationService.UserNotFound);
        }

        if (!string.Equals(account.Email, previous, StringComparison.OrdinalIgnoreCase))
        {
            return Application.Access.AccessResult.Invalid(EmailChangedMeanwhile);
        }

        await ApplyEmailChangeAsync(c, tx, catalog, account, email, reason, now, by, ct).ConfigureAwait(false);
        return Application.Access.AccessResult.Done(userId);
    }

    /// <summary>
    /// A new address must be a single valid mailbox, not the current one, and used by nobody else as address or username
    /// (so sign-in by e-mail cannot be shadowed either).
    /// </summary>
    private static async Task<string?> CheckNewEmailAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string current, string email, CancellationToken ct)
    {
        if (email.Length is 0 or > MaxEmailLength || !MimeKit.MailboxAddress.TryParse(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.Ordinal)
            || parsed.Address.IndexOf('@', StringComparison.Ordinal) <= 0)
        {
            return EmailInvalid;
        }

        if (string.Equals(email, current, StringComparison.OrdinalIgnoreCase))
        {
            return EmailUnchanged;
        }

        return await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.users where id <> $1 and (lower(email) = lower($2) or lower(username) = lower($2)))", ct, userId, email)
            .ConfigureAwait(false)
            ? EmailTaken
            : null;
    }

    /// <summary>
    /// The change itself: the new address; every open reset link, e-mail code and activation link of the person ends (they
    /// went to the old address); the e-mail factor is removed, so codes never follow an address the person has not proved;
    /// the old address is told.
    /// </summary>
    private static async Task ApplyEmailChangeAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationCatalog catalog, UserAccount account, string email, string? reason,
        DateTimeOffset now, Guid by, CancellationToken ct)
    {
        await IdentityDb.ExecuteAsync(c, tx, "update iam.users set email = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", ct, account.Id, email, now, by)
            .ConfigureAwait(false);
        foreach (var purpose in new[] { IdentityDb.PasswordReset, EmailCodes.SignIn, EmailCodes.EnrolmentConfirm, IdentityDb.Activation })
        {
            await IdentityDb.ExpireTokensAsync(c, tx, account.Id, purpose, by, now, ct).ConfigureAwait(false);
        }

        var removed = await IdentityDb.ExecuteAsync(c, tx, """
            update iam.mfa_devices set status = 'revoked', updated_at = $2, updated_by = $3, row_version = row_version + 1
            where user_id = $1 and kind = 'email' and status <> 'revoked'
            """, ct, account.Id, now, by).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, IdentityEvents.EmailChanged, account.Id, by, now,
            new { from = IdentityMail.Masked(account.Email), to = IdentityMail.Masked(email), reason, email_factor_removed = removed > 0 }, ct).ConfigureAwait(false);
        await IdentityMail.QueueAsync(c, tx, catalog, null, account.Id, IdentityMail.EmailChanged, new { masked = IdentityMail.Masked(email) }, new Dictionary<string, string>(), now, ct,
            recipient: account.Email).ConfigureAwait(false);
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

        var code = await EmailCodes.IssueAsync(c, tx, encryptor, userId, purpose, binding, policy.Email.CodeLifetime, userId, now, ct).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, IdentityEvents.EmailCodeSent, userId, userId, now, new { purpose, expires_at = now + policy.Email.CodeLifetime }, ct).ConfigureAwait(false);
        await IdentityMail.QueueAsync(c, tx, catalog, encryptor, userId, eventCode, new { minutes = (int)policy.Email.CodeLifetime.TotalMinutes },
            new Dictionary<string, string> { ["code"] = code }, now, ct).ConfigureAwait(false);
        return true;
    }
}
