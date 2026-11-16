using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Domain.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>Self-service of the signed-in user (FR-ADM-USR-10: password, sessions, MFA devices; API only in S1.05).</summary>
public sealed class AccountService(
    ITenantConnectionProvider database,
    ITenantContext tenant,
    IClock clock,
    IPasswordHasher hasher,
    IFieldEncryptorProvider encryptors) : IAccountService
{
    public const string CurrentPasswordWrong = "errors.password.current_wrong";

    public async Task<IReadOnlyList<string>> ChangePasswordAsync(Guid userId, Guid currentSessionId, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await IdentityDb.GetAccountAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false);
        var policy = await IdentitySettings.ReadAsync(connection, tx, userId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (account is not { HasCredential: true, Status: UserStatuses.Active } || string.IsNullOrEmpty(currentPassword) || !hasher.Verify(currentPassword, account.PasswordHash!, account.HashAlgorithm!, out _))
        {
            if (account is { HasCredential: true })
            {
                // A wrong current password counts like a failed sign-in (review finding 4).
                await Lockouts.CountFailureAsync(connection, tx, userId, policy, now, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return [CurrentPasswordWrong];
        }

        var errors = await Passwords.ValidateNewAsync(connection, tx, userId, newPassword, policy.Password, hasher, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return errors;
        }

        await IdentityDb.SetPasswordAsync(connection, tx, userId, hasher.Hash(newPassword!), hasher.Algorithm, userId, now, cancellationToken).ConfigureAwait(false);
        var ended = await IdentityDb.RevokeOtherSessionsAsync(connection, tx, userId, currentSessionId, "password_changed", now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.PasswordChanged, userId, userId, now, new { reason = "self", sessions_revoked = ended }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task<IReadOnlyList<SessionView>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        var policy = await IdentitySettings.ReadAsync(connection, null, userId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return await IdentityDb.LiveSessionsAsync(connection, null, userId, currentSessionId, TimeSpan.FromMinutes(policy.Session.IdleMinutes), clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var revoked = await IdentityDb.RevokeSessionAsync(connection, tx, sessionId, userId, "self_revoked", now, cancellationToken).ConfigureAwait(false) == 1;
        if (revoked)
        {
            await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.SessionsRevoked, userId, userId, now, new { reason = "self_revoked", session = sessionId }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revoked;
    }

    public async Task<IReadOnlyList<MfaDeviceView>> ListMfaDevicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var command = IdentityDb.Command(connection, null,
            "select id, kind, label, status, created_at, last_used_at from iam.mfa_devices where user_id = $1 and status <> 'revoked' order by created_at, id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<MfaDeviceView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new MfaDeviceView(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return list;
    }

    public async Task<MfaEnrolment> StartMfaEnrolmentAsync(Guid userId, string? label, Reauthentication proof, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await ReauthenticateAsync(connection, tx, userId, proof, now, cancellationToken).ConfigureAwait(false);
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var enrolment = await MfaEnrolments.StartAsync(connection, tx, account, label, tenant.TenantCode, encryptor, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return enrolment;
    }

    public async Task<IReadOnlyList<string>?> ConfirmMfaEnrolmentAsync(Guid userId, Guid deviceId, string? code, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var policy = await IdentitySettings.ReadAsync(connection, tx, userId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var codes = await MfaEnrolments.ConfirmAsync(connection, tx, userId, deviceId, code, policy.Mfa, encryptor, hasher, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return codes;
    }

    public async Task<bool> RemoveMfaDeviceAsync(Guid userId, Guid deviceId, Reauthentication proof, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ReauthenticateAsync(connection, tx, userId, proof, now, cancellationToken).ConfigureAwait(false);
        var removed = await IdentityDb.ExecuteAsync(connection, tx, """
            update iam.mfa_devices set status = 'revoked', secret = null, updated_at = $3, updated_by = $2, row_version = row_version + 1
            where id = $1 and user_id = $2 and status <> 'revoked'
            """, cancellationToken, deviceId, userId, now).ConfigureAwait(false) == 1;
        if (removed)
        {
            if (!await IdentityDb.HasActiveMfaAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false))
            {
                await IdentityDb.ExecuteAsync(connection, tx, "delete from iam.mfa_recovery_codes where user_id = $1", cancellationToken, userId).ConfigureAwait(false);
            }

            await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.MfaRemoved, userId, userId, now, new { device = deviceId }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid userId, Reauthentication proof, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await IdentityDb.HasActiveMfaAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await ReauthenticateAsync(connection, tx, userId, proof, now, cancellationToken).ConfigureAwait(false);
        var policy = await IdentitySettings.ReadAsync(connection, tx, userId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var codes = await IdentityDb.NewRecoveryCodesAsync(connection, tx, userId, policy.Mfa.RecoveryCodeCount, hasher, userId, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.RecoveryCodesRegenerated, userId, userId, now, new { count = codes.Count }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return codes;
    }

    /// <summary>
    /// Proves the person is present (review finding 4): a current authenticator code when one is given, otherwise the
    /// current password. A wrong or missing proof counts towards lockout, is committed, and raises
    /// <see cref="ReauthenticationFailedException"/>; the rest of the action does not run.
    /// </summary>
    private async Task<UserAccount> ReauthenticateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, Reauthentication proof, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var account = await IdentityDb.GetAccountAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new ReauthenticationFailedException();
        var ok = false;
        if (account is { Status: UserStatuses.Active, HasCredential: true })
        {
            if (!string.IsNullOrWhiteSpace(proof.Code))
            {
                var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
                foreach (var device in await IdentityDb.MfaDevicesAsync(connection, tx, userId, "active", cancellationToken).ConfigureAwait(false))
                {
                    var step = Totp.Verify(encryptor.Decrypt(device.Secret, IdentityDb.MfaSecretPurpose), proof.Code, now, device.LastUsedAt is { } used ? Totp.StepAt(used) : null);
                    if (step is { } accepted && await IdentityDb.MarkMfaUsedAsync(connection, tx, device.Id, accepted, userId, now, null, cancellationToken).ConfigureAwait(false))
                    {
                        ok = true;
                        break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(proof.CurrentPassword))
            {
                ok = hasher.Verify(proof.CurrentPassword, account.PasswordHash!, account.HashAlgorithm!, out _);
            }
        }

        if (!ok)
        {
            if (account.HasCredential)
            {
                var policy = await IdentitySettings.ReadAsync(connection, tx, userId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                await Lockouts.CountFailureAsync(connection, tx, userId, policy, now, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new ReauthenticationFailedException();
        }

        return account;
    }
}

/// <summary>
/// Administrator actions on users (FR-ADM-USR-03, FR-ADM-USR-05; rulings P2 to P4). Each action raises a domain event
/// with the acting administrator (ruling Q4). System users and other customers' users are never found.
/// </summary>
public sealed class UserAdministrationService(ITenantConnectionProvider database, IClock clock, ICurrentActor actor) : IUserAdministrationService
{
    public const string UserLimitReached = "errors.user.limit_reached";
    public const string NotLocked = "errors.user.not_locked";
    public const string NoPassword = "errors.user.no_password";
    public const string ReasonRequired = "errors.user.reason_required";
    public const string TransitionNotAllowed = "errors.user.transition_not_allowed";

    public Task<AdminResult> UnlockAsync(Guid userId, string? reason, CancellationToken cancellationToken) =>
        RunAsync(userId, async (c, tx, account, by, now) =>
        {
            if (account.Status != UserStatuses.Locked)
            {
                return new AdminResult(AdminOutcome.Invalid, NotLocked);
            }

            await IdentityDb.SetStatusAsync(c, tx, userId, UserStatuses.Active, by, now, cancellationToken).ConfigureAwait(false);
            await IdentityDb.SetFailuresAsync(c, tx, userId, 0, null, cancellationToken).ConfigureAwait(false);
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.UserUnlocked, userId, by, now, new { automatic = false, reason }, cancellationToken).ConfigureAwait(false);
            return AdminResult.Done;
        }, cancellationToken);

    public Task<AdminResult> ForcePasswordChangeAsync(Guid userId, string? reason, CancellationToken cancellationToken) =>
        RunAsync(userId, async (c, tx, account, by, now) =>
        {
            if (!account.HasCredential)
            {
                return new AdminResult(AdminOutcome.Invalid, NoPassword);
            }

            await IdentityDb.ExecuteAsync(c, tx, "update iam.user_credentials set must_change = true where user_id = $1", cancellationToken, userId).ConfigureAwait(false);
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.PasswordChangeForced, userId, by, now, new { reason }, cancellationToken).ConfigureAwait(false);
            return AdminResult.Done;
        }, cancellationToken);

    public Task<AdminResult> ResetMfaAsync(Guid userId, string? reason, CancellationToken cancellationToken) =>
        RunAsync(userId, async (c, tx, account, by, now) =>
        {
            var devices = await IdentityDb.ExecuteAsync(c, tx, """
                update iam.mfa_devices set status = 'revoked', secret = null, updated_at = $2, updated_by = $3, row_version = row_version + 1
                where user_id = $1 and status <> 'revoked'
                """, cancellationToken, userId, now, by).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, "delete from iam.mfa_recovery_codes where user_id = $1", cancellationToken, userId).ConfigureAwait(false);
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.MfaReset, userId, by, now, new { devices, reason }, cancellationToken).ConfigureAwait(false);
            return AdminResult.Done;
        }, cancellationToken);

    public Task<AdminResult> RevokeSessionsAsync(Guid userId, string? reason, CancellationToken cancellationToken) =>
        RunAsync(userId, async (c, tx, account, by, now) =>
        {
            var count = await IdentityDb.RevokeAllSessionsAsync(c, tx, userId, "admin", now, cancellationToken).ConfigureAwait(false);
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.SessionsRevoked, userId, by, now, new { reason = reason ?? "admin", count }, cancellationToken).ConfigureAwait(false);
            return AdminResult.Done;
        }, cancellationToken);

    public Task<AdminResult> ChangeStatusAsync(Guid userId, string? status, string? reason, CancellationToken cancellationToken) =>
        RunAsync(userId, async (c, tx, account, by, now) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return new AdminResult(AdminOutcome.Invalid, ReasonRequired);
            }

            if (status is null || !UserStatuses.AdminMayChange(account.Status, status))
            {
                return new AdminResult(AdminOutcome.Invalid, TransitionNotAllowed);
            }

            if (status == UserStatuses.Active && !UserStatuses.CountsAgainstLimit(account.Status) && !await IdentityDb.SeatAvailableAsync(c, tx, cancellationToken).ConfigureAwait(false))
            {
                return new AdminResult(AdminOutcome.Invalid, UserLimitReached);
            }

            // The last tenant admin cannot be disabled or archived (spec 02 BR; slice S1.06 item 9).
            var admins = await Access.TenantAdminGuard.CountAsync(c, tx, cancellationToken).ConfigureAwait(false);
            await IdentityDb.SetStatusAsync(c, tx, userId, status, by, now, cancellationToken).ConfigureAwait(false);
            if (await Access.TenantAdminGuard.RemovedLastAsync(c, tx, admins, cancellationToken).ConfigureAwait(false))
            {
                return new AdminResult(AdminOutcome.Invalid, Access.TenantAdminGuard.LastAdmin);
            }

            if (account.HasCredential && status is UserStatuses.Active or UserStatuses.Locked)
            {
                // Unlocking clears the failure count; an administrator's lock has no end time (ruling P3).
                await IdentityDb.SetFailuresAsync(c, tx, userId, status == UserStatuses.Active ? 0 : account.FailedAttempts, null, cancellationToken).ConfigureAwait(false);
            }

            var revoked = UserStatuses.RevokesSessions(status)
                ? await IdentityDb.RevokeAllSessionsAsync(c, tx, userId, "status_" + status, now, cancellationToken).ConfigureAwait(false)
                : 0;
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.UserStatusChanged, userId, by, now, new { from = account.Status, to = status, reason, sessions_revoked = revoked }, cancellationToken).ConfigureAwait(false);
            return AdminResult.Done;
        }, cancellationToken);

    public async Task<IReadOnlyList<SessionView>?> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await IdentityDb.GetAccountAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, userId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var sessions = await IdentityDb.LiveSessionsAsync(connection, tx, userId, null, TimeSpan.FromMinutes(policy.Session.IdleMinutes), clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sessions;
    }

    private async Task<AdminResult> RunAsync(Guid userId, Func<NpgsqlConnection, NpgsqlTransaction, UserAccount, Guid, DateTimeOffset, Task<AdminResult>> action, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var by = actor.UserId;
        await using var connection = await IdentityDb.OpenAsync(database.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await IdentityDb.GetAccountAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AdminResult.NotFound;
        }

        var result = await action(connection, tx, account, by, now).ConfigureAwait(false);
        if (result.Outcome == AdminOutcome.Done)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>
/// Activation links for invited users (slice S1.05 build item 8; ruling Q6). Used by the operator CLI for the first
/// tenant administrator; e-mail delivery comes with S1.11. A new link ends any earlier unused one.
/// </summary>
public static class ActivationTokens
{
    public static async Task<ActivationToken?> IssueAsync(NpgsqlConnection connection, Guid userId, Guid actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var account = await IdentityDb.GetAccountAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false);
        if (account is not { Status: UserStatuses.Invited })
        {
            return null;
        }

        var policy = await IdentitySettings.ReadAsync(connection, tx, userId, now, cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExpireTokensAsync(connection, tx, userId, IdentityDb.Activation, actor, now, cancellationToken).ConfigureAwait(false);
        var token = await IdentityDb.IssueTokenAsync(connection, tx, userId, IdentityDb.Activation, policy.ActivationTokenLifetime, actor, now, cancellationToken).ConfigureAwait(false);
        var expires = now + policy.ActivationTokenLifetime;
        await IdentityDb.RaiseAsync(connection, tx, IdentityEvents.ActivationTokenIssued, userId, actor, now, new { expires_at = expires }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationToken(token, expires);
    }

    /// <summary>The invited first tenant administrator (role <c>tenant_admin</c>), oldest first; null when none is waiting.</summary>
    public static Task<Guid?> FirstInvitedAdminAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
        IdentityDb.ScalarAsync<Guid?>(connection, null, """
            select u.id from iam.users u
            join iam.user_roles ur on ur.user_id = u.id
            join iam.roles r on r.id = ur.role_id and r.code = 'tenant_admin'
            where u.status = 'invited' and not u.is_deleted and not u.is_system
            order by u.created_at, u.id
            limit 1
            """, cancellationToken);
}
