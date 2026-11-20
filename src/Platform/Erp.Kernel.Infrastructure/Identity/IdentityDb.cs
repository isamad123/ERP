using System.Data.Common;
using System.Net;
using System.Text.Json;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Domain.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>A user row joined with its credential (null credential = invited user without a password).</summary>
internal sealed record UserAccount(
    Guid Id,
    string Username,
    string Email,
    string Status,
    bool HasCredential,
    string? PasswordHash,
    string? HashAlgorithm,
    DateTimeOffset? PasswordChangedAt,
    bool MustChange,
    int FailedAttempts,
    DateTimeOffset? LockedUntil);

internal sealed record SessionRow(Guid Id, Guid UserId, DateTimeOffset IssuedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, string? RefreshTokenHash, string? PreviousRefreshHash);

internal sealed record OneTimeToken(Guid Id, Guid UserId, string Purpose);

/// <summary>Parameterized SQL of the identity services over the bound customer's database. No string-built values.</summary>
internal static class IdentityDb
{
    public const string Activation = "activation";
    public const string MfaChallenge = "mfa_challenge";
    public const string PasswordChange = "password_change";
    public const string MfaEnrolment = "mfa_enrolment";
    public const string PasswordReset = "password_reset";
    public const string MfaSecretPurpose = "iam.mfa_devices.secret";

    public static async Task<NpgsqlConnection> OpenAsync(DbDataSource dataSource, CancellationToken cancellationToken) =>
        (NpgsqlConnection)await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
#pragma warning disable CA2100 // constant SQL text; every value is a parameter
        var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(value switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value },
                JsonPayload json => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json.Json },
                IPAddress ip => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Inet, Value = ip },
                _ => new NpgsqlParameter { Value = value },
            });
        }

        return command;
    }

    /// <remarks>A refused write to an audit table is recorded as a critical security event (S1.07-r1, risk R-012).</remarks>
    public static Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values) =>
        Audit.TamperAttempt.GuardAsync(connection, sql, async () =>
        {
            await using var command = Command(connection, transaction, sql, values);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });

    /// <remarks>A refused write to an audit table is recorded as a critical security event (S1.07-r1, risk R-012).</remarks>
    public static Task<T?> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values) =>
        Audit.TamperAttempt.GuardAsync(connection, sql, async () =>
        {
            await using var command = Command(connection, transaction, sql, values);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null or DBNull ? default : (T)result;
        });

    private const string AccountColumns = """
        u.id, u.username, u.email, u.status, c.user_id is not null, c.password_hash, c.hash_algorithm, c.password_changed_at,
        coalesce(c.must_change, false), coalesce(c.failed_attempts, 0), c.locked_until
        """;

    /// <summary>
    /// The person signing in, by username first and then by e-mail (case-insensitive). System users never sign in.
    /// The user row is locked, so concurrent sign-ins of one user run one after another and each sees the status the
    /// previous one left. The credential columns come from the statement's snapshot and may be stale under
    /// concurrency: failures are therefore counted with <see cref="IncrementFailuresAsync"/>, never from this value.
    /// </summary>
    public static Task<UserAccount?> FindForSignInAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string login, CancellationToken cancellationToken) =>
        ReadAccountAsync(connection, transaction, $"""
            select {AccountColumns}
            from iam.users u left join iam.user_credentials c on c.user_id = u.id
            where not u.is_deleted and not u.is_system and (lower(u.username) = lower($1) or lower(u.email) = lower($1))
            order by (lower(u.username) = lower($1)) desc
            limit 1
            for update of u
            """, login, cancellationToken);

    /// <summary>A non-system user by id, locked for the transaction; null when absent.</summary>
    public static Task<UserAccount?> GetAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken) =>
        ReadAccountAsync(connection, transaction, $"""
            select {AccountColumns}
            from iam.users u left join iam.user_credentials c on c.user_id = u.id
            where u.id = $1 and not u.is_deleted and not u.is_system
            for update of u
            """, userId, cancellationToken);

    private static async Task<UserAccount?> ReadAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object value, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new UserAccount(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8), reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
    }

    public static Task SetStatusAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string status, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.users set status = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", ct, userId, status, now, actor);

    public static Task SetFailuresAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, int failed, DateTimeOffset? lockedUntil, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.user_credentials set failed_attempts = $2, locked_until = $3 where user_id = $1", ct, userId, failed, lockedUntil);

    /// <summary>
    /// Adds one failure in the database itself and returns the new count, so failures of concurrent requests are all
    /// counted (S1.05 review finding 2). 0 when the user has no credential.
    /// </summary>
    public static async Task<int> IncrementFailuresAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, CancellationToken ct) =>
        await ScalarAsync<int?>(c, tx, "update iam.user_credentials set failed_attempts = failed_attempts + 1 where user_id = $1 returning failed_attempts", ct, userId).ConfigureAwait(false) ?? 0;

    /// <summary>Clears the failure count and any automatic lock end; only called when a session is actually issued (finding 1).</summary>
    public static Task ResetFailuresAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.user_credentials set failed_attempts = 0, locked_until = null where user_id = $1 and (failed_attempts <> 0 or locked_until is not null)", ct, userId);

    public static Task SetLockedUntilAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, DateTimeOffset? lockedUntil, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.user_credentials set locked_until = $2 where user_id = $1", ct, userId, lockedUntil);

    /// <summary>Sets a new password: credential row (created on activation), history row, must_change cleared.</summary>
    public static async Task SetPasswordAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string hash, string algorithm, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            insert into iam.user_credentials (user_id, password_hash, hash_algorithm, password_changed_at, must_change, failed_attempts, locked_until)
            values ($1, $2, $3, $4, false, 0, null)
            on conflict (user_id) do update set password_hash = excluded.password_hash, hash_algorithm = excluded.hash_algorithm,
                password_changed_at = excluded.password_changed_at, must_change = false, failed_attempts = 0, locked_until = null
            """, ct, userId, hash, algorithm, now).ConfigureAwait(false);
        await ExecuteAsync(c, tx, "insert into iam.password_history (id, user_id, password_hash, created_at, created_by) values ($1, $2, $3, $4, $5)",
            ct, Guid.CreateVersion7(now), userId, hash, now, actor).ConfigureAwait(false);
    }

    /// <summary>Replaces the hash after an algorithm upgrade (same password, so no history row and no change date).</summary>
    public static Task RehashAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string hash, string algorithm, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.user_credentials set password_hash = $2, hash_algorithm = $3 where user_id = $1", ct, userId, hash, algorithm);

    public static async Task<List<string>> RecentPasswordHashesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, int count, CancellationToken ct)
    {
        var hashes = new List<string>();
        if (count <= 0)
        {
            return hashes;
        }

        await using var command = Command(c, tx, "select password_hash from iam.password_history where user_id = $1 order by created_at desc, id desc limit $2", userId, count);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    /// <summary>When the user first set a password (activation): the oldest history row.</summary>
    public static async Task<DateTimeOffset?> ActivatedAtAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var command = Command(c, tx, "select min(created_at) from iam.password_history where user_id = $1", userId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    // ------------------------------------------------------------------ one-time tokens

    public static async Task<string> IssueTokenAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string purpose, TimeSpan lifetime, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var token = OpaqueTokens.New();
        await ExecuteAsync(c, tx, """
            insert into iam.one_time_tokens (id, user_id, purpose, token_hash, expires_at, used_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, null, $6, $7, $6, $7)
            """, ct, Guid.CreateVersion7(now), userId, purpose, OpaqueTokens.Hash(token), now + lifetime, now, actor).ConfigureAwait(false);
        return token;
    }

    /// <summary>The user of a valid token of the purpose, read without a lock (to lock the user first); null otherwise.</summary>
    public static Task<Guid?> TokenOwnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, string? token, string purpose, DateTimeOffset now, CancellationToken ct) =>
        string.IsNullOrEmpty(token) || token.Length > 200
            ? Task.FromResult<Guid?>(null)
            : ScalarAsync<Guid?>(c, tx, "select user_id from iam.one_time_tokens where token_hash = $1 and purpose = $2 and used_at is null and expires_at > $3", ct, OpaqueTokens.Hash(token), purpose, now);

    /// <summary>A valid (unused, unexpired) token of the purpose, locked; null otherwise.</summary>
    public static async Task<OneTimeToken?> FindTokenAsync(NpgsqlConnection c, NpgsqlTransaction tx, string? token, string purpose, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return null;
        }

        await using var command = Command(c, tx, """
            select id, user_id, purpose from iam.one_time_tokens
            where token_hash = $1 and purpose = $2 and used_at is null and expires_at > $3
            for update
            """, OpaqueTokens.Hash(token), purpose, now);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new OneTimeToken(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)) : null;
    }

    public static Task UseTokenAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tokenId, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.one_time_tokens set used_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", ct, tokenId, now, actor);

    /// <summary>Ends every still-valid token of the purpose for the user (a new activation link replaces the old one).</summary>
    public static Task ExpireTokensAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string purpose, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, """
            update iam.one_time_tokens set expires_at = $3, updated_at = $3, updated_by = $4, row_version = row_version + 1
            where user_id = $1 and purpose = $2 and used_at is null and expires_at > $3
            """, ct, userId, purpose, now, actor);

    // ------------------------------------------------------------------ sessions

    public static async Task<SessionRow?> GetSessionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, CancellationToken ct)
    {
        await using var command = Command(c, tx, "select id, user_id, issued_at, last_seen_at, expires_at, revoked_at, refresh_token_hash, previous_refresh_hash from iam.sessions where id = $1 for update", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new SessionRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;
    }

    /// <summary>
    /// The owner of a session, read without a lock. Refresh locks the user first and the session second, the same order
    /// as sign-in and the administrator actions, so they cannot deadlock (S1.05 review finding 3).
    /// </summary>
    public static Task<Guid?> SessionOwnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, CancellationToken ct) =>
        ScalarAsync<Guid?>(c, tx, "select user_id from iam.sessions where id = $1", ct, sessionId);

    public static Task InsertSessionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, Guid userId, DateTimeOffset now, DateTimeOffset expiresAt, ClientInfo client, string refreshHash, CancellationToken ct) =>
        ExecuteAsync(c, tx, """
            insert into iam.sessions (id, user_id, issued_at, last_seen_at, expires_at, ip_address, user_agent, revoked_at, revoked_reason, refresh_token_hash, created_at, created_by)
            values ($1, $2, $3, $3, $4, $5, $6, null, null, $7, $3, $2)
            """, ct, sessionId, userId, now, expiresAt, ParseIp(client.IpAddress), Truncate(client.UserAgent, 500), refreshHash);

    /// <summary>New refresh token; the replaced one is kept so only its reuse ends the session (CR-016).</summary>
    public static Task RotateRefreshAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, string refreshHash, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.sessions set previous_refresh_hash = refresh_token_hash, refresh_token_hash = $2, last_seen_at = $3 where id = $1", ct, sessionId, refreshHash, now);

    public static Task<int> RevokeSessionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sessionId, Guid? userId, string reason, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.sessions set revoked_at = $3, revoked_reason = $4 where id = $1 and ($2::uuid is null or user_id = $2) and revoked_at is null",
            ct, sessionId, userId, now, reason);

    /// <summary>Revokes every open session of the user; returns how many.</summary>
    public static Task<int> RevokeAllSessionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string reason, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.sessions set revoked_at = $2, revoked_reason = $3 where user_id = $1 and revoked_at is null and expires_at > $2", ct, userId, now, reason);

    /// <summary>Revokes every open session of the user except <paramref name="keep"/> (after a self-service password change).</summary>
    public static Task<int> RevokeOtherSessionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, Guid keep, string reason, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, "update iam.sessions set revoked_at = $3, revoked_reason = $4 where user_id = $1 and id <> $2 and revoked_at is null and expires_at > $3", ct, userId, keep, now, reason);

    /// <summary>
    /// Revokes the oldest live sessions so that at most <paramref name="keep"/> remain (FR-KRN-IAM-04 concurrent limit).
    /// The session just issued is always kept, even when another one was issued in the same instant.
    /// </summary>
    public static Task<int> EnforceConcurrentLimitAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, Guid newSessionId, int keep, TimeSpan idle, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, """
            update iam.sessions set revoked_at = $4, revoked_reason = 'concurrent_limit'
            where id in (
                select id from iam.sessions
                where user_id = $1 and id <> $2 and revoked_at is null and expires_at > $4 and last_seen_at > $5
                order by issued_at desc, id desc
                offset greatest($3 - 1, 0))
            """, ct, userId, newSessionId, keep, now, now - idle);

    public static async Task<List<SessionView>> LiveSessionsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid userId, Guid? current, TimeSpan idle, DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<SessionView>();
        await using var command = Command(c, tx, """
            select id, issued_at, last_seen_at, expires_at, host(ip_address), user_agent from iam.sessions
            where user_id = $1 and revoked_at is null and expires_at > $2 and last_seen_at > $3
            order by issued_at desc, id desc
            """, userId, now, now - idle);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            list.Add(new SessionView(id, reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), id == current));
        }

        return list;
    }

    // ------------------------------------------------------------------ MFA

    public sealed record MfaDevice(Guid Id, string Status, byte[]? Secret, DateTimeOffset? LastUsedAt);

    public static async Task<List<MfaDevice>> MfaDevicesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string? status, CancellationToken ct)
    {
        var list = new List<MfaDevice>();
        await using var command = Command(c, tx, "select id, status, secret, last_used_at from iam.mfa_devices where user_id = $1 and kind = 'totp' and ($2::text is null or status = $2) order by created_at, id", userId, status);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MfaDevice(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader[2], reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return list;
    }

    /// <summary>Whether the user has an active e-mail factor (slice S1.11b2, CR-009).</summary>
    public static Task<bool> HasActiveEmailFactorAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, CancellationToken ct) =>
        ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.mfa_devices where user_id = $1 and kind = 'email' and status = 'active')", ct, userId);

    public static Task<bool> HasActiveMfaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, CancellationToken ct) =>
        ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.mfa_devices where user_id = $1 and status = 'active')", ct, userId);

    /// <summary>
    /// Records the step a code was accepted for; succeeds only if no later or equal step was used before, so the same
    /// code cannot be accepted twice even by two concurrent requests (replay protection).
    /// </summary>
    public static async Task<bool> MarkMfaUsedAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid deviceId, long step, Guid actor, DateTimeOffset now, string? newStatus, CancellationToken ct) =>
        await ExecuteAsync(c, tx, """
            update iam.mfa_devices set last_used_at = $2, status = coalesce($5, status), updated_at = $3, updated_by = $4, row_version = row_version + 1
            where id = $1 and (last_used_at is null or last_used_at < $2)
            """, ct, deviceId, Totp.StartOfStep(step), now, actor, newStatus).ConfigureAwait(false) == 1;

    /// <summary>Replaces the user's recovery codes with new ones; returns the codes (shown once).</summary>
    public static async Task<List<string>> NewRecoveryCodesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, int count, IPasswordHasher hasher, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, "delete from iam.mfa_recovery_codes where user_id = $1", ct, userId).ConfigureAwait(false);
        var codes = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var code = OpaqueTokens.NewRecoveryCode();
            codes.Add(code);
            await ExecuteAsync(c, tx, """
                insert into iam.mfa_recovery_codes (id, user_id, code_hash, used_at, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, null, $4, $5, $4, $5)
                """, ct, Guid.CreateVersion7(now), userId, hasher.Hash(code), now, actor).ConfigureAwait(false);
        }

        return codes;
    }

    /// <summary>Uses a recovery code if it matches an unused one; true when accepted.</summary>
    public static async Task<bool> UseRecoveryCodeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string code, IPasswordHasher hasher, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = OpaqueTokens.NormalizeRecoveryCode(code);
        var candidates = new List<(Guid Id, string Hash)>();
        await using (var command = Command(c, tx, "select id, code_hash from iam.mfa_recovery_codes where user_id = $1 and used_at is null for update", userId))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var (id, hash) in candidates)
        {
            if (hasher.Verify(normalized, hash, hasher.Algorithm, out _))
            {
                await ExecuteAsync(c, tx, "update iam.mfa_recovery_codes set used_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", ct, id, now, actor).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ attempts, events, limits

    /// <summary>One row per sign-in attempt (FR-KRN-AUD-02). Outcomes: success, bad_password, locked, mfa_failed, disabled.</summary>
    /// <remarks>Slice S1.07 (ruling Q7): each attempt is also a security event, in the same transaction.</remarks>
    public static async Task LogAttemptAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string login, string outcome, ClientInfo client, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, "insert into iam.login_attempts (id, occurred_at, username, outcome, ip_address, created_at, created_by) values ($1, $2, $3, $4, $5, $2, $6)",
            ct, Guid.CreateVersion7(now), now, Truncate(login.Trim().ToLowerInvariant(), 320) ?? string.Empty, outcome, ParseIp(client.IpAddress), actor).ConfigureAwait(false);
        await Audit.AuditTrail.RecordSignInAsync(c, tx, login, outcome, ParseIp(client.IpAddress)?.ToString(), actor, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A domain event in the transactional outbox (architecture reference section 3; ruling Q4). The dispatcher and the
    /// audit log that records these events come with later slices (S1.07).
    /// </summary>
    /// <remarks>Slice S1.07: the audit rows of the event are written in the same transaction (<see cref="Audit.AuditTrail"/>).</remarks>
    public static async Task RaiseAsync(NpgsqlConnection c, NpgsqlTransaction tx, string eventType, Guid userId, Guid actor, DateTimeOffset now, object payload, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            insert into iam.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, 'iam.user', $4, $5, $6, null)
            """, ct, Guid.CreateVersion7(now), now, eventType, userId,
            new JsonPayload(JsonSerializer.Serialize(payload)), new JsonPayload(JsonSerializer.Serialize(new { actor }))).ConfigureAwait(false);
        await Audit.AuditTrail.RecordEventAsync(c, tx, eventType, "iam.user", userId, actor, now, payload, ct).ConfigureAwait(false);
    }

    private static IPAddress? ParseIp(string? ip) => IPAddress.TryParse(ip, out var parsed) ? parsed : null;

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>A JSON text passed as a jsonb parameter.</summary>
internal sealed record JsonPayload(string Json);

/// <summary>Event types raised by the identity services (ruling Q4; recorded in the audit log by S1.07).</summary>
public static class IdentityEvents
{
    public const string UserActivated = "iam.user_activated";
    public const string UserLocked = "iam.user_locked";
    public const string UserUnlocked = "iam.user_unlocked";
    public const string UserStatusChanged = "iam.user_status_changed";
    public const string PasswordChanged = "iam.password_changed";
    public const string PasswordChangeForced = "iam.password_change_forced";
    public const string MfaEnrolled = "iam.mfa_enrolled";
    public const string MfaRemoved = "iam.mfa_removed";
    public const string MfaReset = "iam.mfa_reset";
    public const string SessionsRevoked = "iam.sessions_revoked";
    public const string ActivationTokenIssued = "iam.activation_token_issued";
    public const string RecoveryCodesRegenerated = "iam.recovery_codes_regenerated";

    /// <summary>A password reset link was e-mailed (slice S1.11b2): by the person (forgot password) or an administrator.</summary>
    public const string PasswordResetLinkSent = "iam.password_reset_link_sent";

    /// <summary>A sign-in or enrolment code was e-mailed (slice S1.11b2, CR-009, R-003). The code itself is never in the event.</summary>
    public const string EmailCodeSent = "iam.email_code_sent";

    /// <summary>An administrator changed a person's e-mail address (S1.11b2 review finding 2, CR-062); addresses are masked.</summary>
    public const string EmailChanged = "iam.email_changed";
}
