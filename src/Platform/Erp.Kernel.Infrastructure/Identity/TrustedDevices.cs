using Erp.Kernel.Application.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// "Remember this device" (slice S1.12a, FR-ADM-USR-24, ruling Q4; data design approved 2026-11-20). A browser that passed
/// the second factor may skip it for <c>iam.session.remember_device_days</c>; the password is always asked. Only the
/// SHA-256 of the device token is stored. A trust counts only while the setting is above 0 and within the current number
/// of days (a shorter setting applies at once), never for a person whose MFA is required because of a privileged role,
/// and ends on password change or reset, MFA reset, revoked sessions or removal. Adding, using and ending a trust are
/// security events.
/// </summary>
public static class TrustedDevices
{
    public const string Added = "iam.trusted_device_added";
    public const string Used = "iam.trusted_device_used";
    public const string Revoked = "iam.trusted_device_revoked";

    public const string PasswordChanged = "password_changed";
    public const string PasswordReset = "password_reset";
    public const string MfaReset = "mfa_reset";
    public const string SessionsRevoked = "sessions_revoked";
    public const string Removed = "removed";

    private const int MaxLabelLength = 100;

    /// <summary>A new trust for the user's browser; the token goes into the browser's HttpOnly cookie only.</summary>
    public static async Task<(string Token, DateTimeOffset ExpiresAt)> AddAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, int days, string? userAgent, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var token = OpaqueTokens.New();
        var id = Guid.CreateVersion7(now);
        var expires = now.AddDays(days);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.trusted_devices (id, user_id, token_hash, label, expires_at, last_used_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, null, $6, $2, $6, $2)
            """, ct, id, userId, OpaqueTokens.Hash(token), Label(userAgent), expires, now).ConfigureAwait(false);
        await IdentityDb.RaiseAsync(c, tx, Added, userId, userId, now, new { device = id, expires_at = expires }, ct).ConfigureAwait(false);
        return (token, expires);
    }

    /// <summary>
    /// True when <paramref name="token"/> is a live trust of this user under the current setting; its use is recorded and
    /// raised as a security event. Any other token (unknown, another user's, ended, expired) is simply not trusted.
    /// </summary>
    public static async Task<bool> UseAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string? token, int days, DateTimeOffset now, CancellationToken ct)
    {
        if (days <= 0 || token is not { Length: > 20 and < 200 })
        {
            return false;
        }

        var id = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
            update iam.trusted_devices set last_used_at = $3, updated_at = $3, updated_by = $1, row_version = row_version + 1
            where token_hash = $2 and user_id = $1 and revoked_at is null and expires_at > $3 and created_at + make_interval(days => $4) > $3
            returning id
            """, ct, userId, OpaqueTokens.Hash(token), now, days).ConfigureAwait(false);
        if (id is not { } device)
        {
            return false;
        }

        await IdentityDb.RaiseAsync(c, tx, Used, userId, userId, now, new { device }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Ends every live trust of the user (password change or reset, MFA reset, revoked sessions); one event when any ended.</summary>
    public static async Task<int> RevokeAllAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string reason, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var count = await IdentityDb.ExecuteAsync(c, tx, """
            update iam.trusted_devices set revoked_at = $2, revoked_reason = $3, updated_at = $2, updated_by = $4, row_version = row_version + 1
            where user_id = $1 and revoked_at is null and expires_at > $2
            """, ct, userId, now, reason, actor).ConfigureAwait(false);
        if (count > 0)
        {
            await IdentityDb.RaiseAsync(c, tx, Revoked, userId, actor, now, new { reason, count }, ct).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>The person removes one of their own trusts; false when it is not theirs or already ended.</summary>
    public static async Task<bool> RemoveAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var count = await IdentityDb.ExecuteAsync(c, tx, """
            update iam.trusted_devices set revoked_at = $3, revoked_reason = 'removed', updated_at = $3, updated_by = $2, row_version = row_version + 1
            where id = $1 and user_id = $2 and revoked_at is null and expires_at > $3
            """, ct, deviceId, userId, now).ConfigureAwait(false);
        if (count == 0)
        {
            return false;
        }

        await IdentityDb.RaiseAsync(c, tx, Revoked, userId, userId, now, new { reason = Removed, device = deviceId }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The user's live trusts, newest first (the cookie's token is never returned).</summary>
    public static async Task<IReadOnlyList<TrustedDeviceView>> ListAsync(NpgsqlConnection c, Guid userId, int days, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, null, """
            select id, label, created_at, least(expires_at, created_at + make_interval(days => $3)), last_used_at
            from iam.trusted_devices
            where user_id = $1 and revoked_at is null and expires_at > $2 and created_at + make_interval(days => $3) > $2
            order by created_at desc, id
            """, userId, now, Math.Max(0, days));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TrustedDeviceView>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TrustedDeviceView(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }

    private static string? Label(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        // A short browser name for the person's list (S1.26 screen); the full user agent is not kept here.
        var name = userAgent switch
        {
            _ when userAgent.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when userAgent.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
            _ when userAgent.Contains("Chrome/", StringComparison.Ordinal) => "Chrome",
            _ when userAgent.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => userAgent.Trim(),
        };
        return name.Length > MaxLabelLength ? name[..MaxLabelLength] : name;
    }
}
