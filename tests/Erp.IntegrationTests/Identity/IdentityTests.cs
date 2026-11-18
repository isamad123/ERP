using System.Net;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Erp.IntegrationTests.Identity;

/// <summary>
/// Slice S1.05 acceptance, end to end through the real API pipeline and real customer databases (two customers from
/// <see cref="TenancyFixture"/>). Time is moved with a test clock; each test starts it at the real time.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class IdentityTests(TenancyFixture fx) : IAsyncLifetime
{
    /// <summary>
    /// A successful change to "disabled" or "archived" (FR-ADM-USR-03): since S1.10b (ruling P7) it answers 200 with the user's open
    /// approval tasks, so the screen can offer to reassign them; the users of these tests hold none.
    /// </summary>
    internal static void AssertDisabled(Answer answer)
    {
        Assert.True(answer.Status == HttpStatusCode.OK, $"{answer.Status}: {answer.Body}");
        Assert.Equal(0, answer.Json.GetProperty("openWorkflowTasks").GetInt32());
    }

    internal static readonly SemaphoreSlim ProvisionGate = new(1, 1);
    internal static string? provisionedFor;

    private IdentityHarness h = null!;

    // Two customers of their own, so identity data (users with passwords, sessions, setting changes) never meets the
    // S1.03/S1.04 tests that inspect the shared customers A and B.
    private string A => "i" + fx.Suffix;

    private string B => "j" + fx.Suffix;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await ProvisionGate.WaitAsync(ct);
        try
        {
            if (provisionedFor != fx.Suffix)
            {
                await fx.ProvisionAsync(A, ct);
                await fx.ProvisionAsync(B, ct);
                provisionedFor = fx.Suffix;
            }
        }
        finally
        {
            ProvisionGate.Release();
        }

        h = await IdentityHarness.StartAsync(fx, ct);
    }

    public async ValueTask DisposeAsync() => await h.App.DisposeAsync();

    [Fact]
    public async Task FR_KRN_IAM_01_sign_in_returns_a_token_the_api_accepts_and_the_same_credentials_fail_on_another_customers_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, _) = await h.ActiveUserAsync(A, ct);

        var signIn = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal(HttpStatusCode.OK, signIn.Status);
        Assert.Equal("tokens", signIn.Text("outcome"));
        var access = signIn.Text("accessToken")!;

        var token = new JsonWebToken(access);
        Assert.Equal(userId.ToString(), token.Subject);
        Assert.Equal(A, token.GetClaim("tenant").Value);
        Assert.Equal(signIn.Text("sessionId"), token.GetClaim("sid").Value);
        Assert.False(string.IsNullOrEmpty(token.Kid));
        Assert.Equal(TimeSpan.FromMinutes(5), token.ValidTo - token.IssuedAt); // ruling Q3

        var sessions = await h.GetAsync(A, "/api/iam/me/sessions", access, ct);
        Assert.Equal(HttpStatusCode.OK, sessions.Status);
        Assert.Contains(sessions.Json.EnumerateArray(), s => s.GetProperty("isCurrent").GetBoolean());

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync(B, "/api/iam/me/sessions", access, ct)).Status); // token of A on B's host
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SignInAsync(B, username, IdentityHarness.Password, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(B, signIn.Text("refreshToken")!, ct)).Status);
        Assert.Equal(["success", "success"], await h.OutcomesAsync(A, username, ct)); // activation and sign-in (FR-KRN-AUD-02)
    }

    [Fact]
    public async Task FR_KRN_IAM_01_unknown_user_wrong_password_disabled_and_locked_accounts_get_identical_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, wrong, _) = await h.ActiveUserAsync(A, ct);
        var (disabledId, disabled, _) = await h.ActiveUserAsync(A, ct);
        var (_, locked, _) = await h.ActiveUserAsync(A, ct);
        AssertDisabled(await h.PostAsync(A, $"/api/iam/users/{disabledId}/status", new { status = "disabled", reason = "left" }, h.AdminToken(A), ct));
        for (var i = 0; i < 5; i++)
        {
            await h.SignInAsync(A, locked, "Wrong-Password-1", ct);
        }

        var answers = new[]
        {
            await h.SignInAsync(A, "nobody-" + Guid.NewGuid().ToString("N"), IdentityHarness.Password, ct),
            await h.SignInAsync(A, wrong, "Wrong-Password-1", ct),
            await h.SignInAsync(A, disabled, IdentityHarness.Password, ct),
            await h.SignInAsync(A, locked, IdentityHarness.Password, ct),
        };
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a.Status));
        Assert.Single(answers.Select(a => a.Body).Distinct(StringComparer.Ordinal)); // byte-for-byte the same body
        Assert.Equal("errors.auth.sign_in_failed", answers[0].Text("title"));
        Assert.Contains("disabled", await h.OutcomesAsync(A, disabled, ct));
        Assert.Contains("locked", await h.OutcomesAsync(A, locked, ct));
    }

    [Fact]
    public async Task FR_KRN_IAM_01_five_wrong_passwords_lock_the_account_until_the_lock_ends_or_an_admin_unlocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, _) = await h.ActiveUserAsync(A, ct);

        // One second between attempts, so iam.login_attempts has a clear order.
        async Task<Answer> AttemptAsync(string password)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            return await h.SignInAsync(A, username, password, ct);
        }

        for (var i = 0; i < 4; i++)
        {
            await AttemptAsync("Wrong-Password-1");
        }

        Assert.Equal("active", await h.StatusAsync(A, userId, ct));
        await AttemptAsync("Wrong-Password-1");
        var lockedAt = h.Clock.UtcNow;
        Assert.Equal("locked", await h.StatusAsync(A, userId, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptAsync(IdentityHarness.Password)).Status);

        h.Clock.Advance(lockedAt.AddMinutes(15) - h.Clock.UtcNow - TimeSpan.FromSeconds(2)); // 1 second before the lock ends
        Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptAsync(IdentityHarness.Password)).Status);
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(IdentityHarness.Password)).Status); // the lock has ended
        Assert.Equal("active", await h.StatusAsync(A, userId, ct));

        for (var i = 0; i < 5; i++)
        {
            await AttemptAsync("Wrong-Password-1");
        }

        Assert.Equal("locked", await h.StatusAsync(A, userId, ct));
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{userId}/unlock", new { reason = "called the help desk" }, h.AdminToken(A), ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(IdentityHarness.Password)).Status);

        var outcomes = await h.OutcomesAsync(A, username, ct);
        Assert.Equal(["success", "bad_password", "bad_password", "bad_password", "bad_password", "bad_password", "locked", "locked", "success",
            "bad_password", "bad_password", "bad_password", "bad_password", "bad_password", "success"], outcomes);
        var events = await h.EventsAsync(A, userId, ct);
        Assert.Equal(2, events.Count(e => e == IdentityEvents.UserLocked));
        Assert.Equal(2, events.Count(e => e == IdentityEvents.UserUnlocked)); // automatic, then by the administrator
    }

    [Fact]
    public async Task FR_ADM_USR_20_a_password_reused_within_the_history_is_refused_and_an_expired_password_forces_a_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, tokens) = await h.ActiveUserAsync(A, ct);
        var access = tokens.Text("accessToken")!;

        var tooSimple = await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = IdentityHarness.Password, newPassword = "alllowercaseletters" }, access, ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooSimple.Status);
        Assert.Contains("errors.password.too_simple", tooSimple.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = IdentityHarness.Password, newPassword = "Second-Pass-22" }, access, ct)).Status);
        var reused = await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = "Second-Pass-22", newPassword = IdentityHarness.Password }, access, ct);
        Assert.Equal(HttpStatusCode.BadRequest, reused.Status);
        Assert.Contains("errors.password.reused", reused.Body, StringComparison.Ordinal);
        Assert.Contains("errors.password.current_wrong", (await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = "nope", newPassword = "Third-Pass-333" }, access, ct)).Body, StringComparison.Ordinal);

        var undo = await h.SetSettingAsync(A, IdentitySettings.PasswordExpiryDays, "30", null, ct);
        try
        {
            h.Clock.Advance(TimeSpan.FromDays(31));
            var signIn = await h.SignInAsync(A, username, "Second-Pass-22", ct);
            Assert.Equal("password_change_required", signIn.Text("outcome"));
            Assert.Null(signIn.Text("accessToken"));
            var challenge = signIn.Text("challengeToken")!;
            Assert.Contains("errors.password.reused", (await h.PostAsync(A, "/api/iam/auth/password-change", new { challengeToken = challenge, newPassword = "Second-Pass-22" }, ct: ct)).Body, StringComparison.Ordinal);
            var changed = await h.PostAsync(A, "/api/iam/auth/password-change", new { challengeToken = challenge, newPassword = "Fourth-Pass-4444" }, ct: ct);
            Assert.Equal("tokens", changed.Text("outcome"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/password-change", new { challengeToken = challenge, newPassword = "Fifth-Pass-55555" }, ct: ct)).Status); // single use
        }
        finally
        {
            await undo();
        }

        Assert.Contains(IdentityEvents.PasswordChanged, await h.EventsAsync(A, userId, ct));
    }

    [Fact]
    public async Task FR_ADM_USR_05_a_forced_password_change_ends_refresh_and_requires_a_new_password_at_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, tokens) = await h.ActiveUserAsync(A, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{userId}/force-password-change", new { reason = "suspected leak" }, h.AdminToken(A), ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct)).Status);
        var signIn = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal("password_change_required", signIn.Text("outcome"));
        Assert.Contains(IdentityEvents.PasswordChangeForced, await h.EventsAsync(A, userId, ct));
    }

    [Fact]
    public async Task FR_KRN_IAM_02_with_mfa_sign_in_gives_no_access_token_until_a_valid_code_and_a_replayed_code_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, tokens) = await h.ActiveUserAsync(A, ct);
        var (secret, recovery) = await h.EnrolAsync(A, tokens.Text("accessToken")!, ct);
        Assert.Equal(10, recovery.Count); // ruling Q5

        // The stored secret is ciphertext (P7): the plain secret bytes do not appear in the column.
        await using (var owner = await h.OwnerAsync(A, ct))
        await using (var command = new Npgsql.NpgsqlCommand("select secret from iam.mfa_devices where user_id = $1 and status = 'active'", owner))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = userId });
            var stored = (byte[])(await command.ExecuteScalarAsync(ct))!;
            var plain = Erp.Kernel.Domain.Identity.Base32.Decode(secret);
            Assert.True(stored.Length > plain.Length);
            Assert.False(stored.AsSpan().IndexOf(plain) >= 0);
        }

        h.Clock.Advance(TimeSpan.FromSeconds(30)); // the enrolment used the current step
        var first = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal("mfa_required", first.Text("outcome"));
        Assert.Null(first.Text("accessToken"));
        var challenge = first.Text("challengeToken")!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code = "000000" }, ct: ct)).Status);
        var code = h.Code(secret);
        var done = await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code }, ct: ct);
        Assert.Equal("tokens", done.Text("outcome"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code }, ct: ct)).Status); // challenge used

        var second = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = second.Text("challengeToken"), code }, ct: ct)).Status); // replayed code
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("tokens", (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = second.Text("challengeToken"), code = h.Code(secret) }, ct: ct)).Text("outcome"));

        var third = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal("tokens", (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = third.Text("challengeToken"), recoveryCode = recovery[0] }, ct: ct)).Text("outcome"));
        var fourth = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = fourth.Text("challengeToken"), recoveryCode = recovery[0] }, ct: ct)).Status);

        Assert.Equal(3, (await h.OutcomesAsync(A, username, ct)).Count(o => o == "mfa_failed"));
        Assert.Contains(IdentityEvents.MfaEnrolled, await h.EventsAsync(A, userId, ct));
    }

    [Fact]
    public async Task FR_ADM_USR_21_required_mfa_allows_sign_in_during_the_grace_period_and_then_only_enrolment()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, _) = await h.ActiveUserAsync(A, ct);
        var undo = await h.SetSettingAsync(A, IdentitySettings.MfaRequired, "true", userId, ct);
        try
        {
            var during = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
            Assert.Equal("tokens", during.Text("outcome"));
            Assert.Equal(h.Clock.UtcNow.AddDays(7).ToUnixTimeSeconds(), during.Json.GetProperty("mfaEnrolBy").GetDateTimeOffset().ToUnixTimeSeconds());

            h.Clock.Advance(TimeSpan.FromDays(7));
            var after = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
            Assert.Equal("mfa_enrolment_required", after.Text("outcome"));
            Assert.Null(after.Text("accessToken"));
            var challenge = after.Text("challengeToken")!;
            var start = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment", new { challengeToken = challenge }, ct: ct);
            var secret = start.Text("secret")!;
            // S1.11b2 (R-003): an enrolment after the grace period also needs the code e-mailed to the person.
            var emailCode = await h.EmailedCodeAsync(A, userId, "identity.enrolment_code", ct);
            Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync(A, "/api/iam/auth/mfa-enrolment/confirm", new { challengeToken = challenge, deviceId = start.Text("deviceId"), code = "000000", emailCode }, ct: ct)).Status);
            var done = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment/confirm", new { challengeToken = challenge, deviceId = start.Text("deviceId"), code = h.Code(secret), emailCode }, ct: ct);
            Assert.Equal("tokens", done.Text("outcome"));
            Assert.Equal(10, done.Json.GetProperty("recoveryCodes").GetArrayLength());
            Assert.Equal("mfa_required", (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Text("outcome"));
        }
        finally
        {
            await undo();
        }
    }

    [Fact]
    public async Task FR_KRN_IAM_04_idle_and_absolute_timeouts_end_the_session_and_a_fourth_session_revokes_the_oldest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, username, activation) = await h.ActiveUserAsync(A, ct);
        var oldest = activation.Text("refreshToken")!;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Text("refreshToken")!;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var third = (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Text("refreshToken")!;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var fourth = (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Text("refreshToken")!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, oldest, ct)).Status); // limit 3 (ruling Q5)

        // Idle: 30 minutes without a refresh ends the session.
        h.Clock.Advance(TimeSpan.FromMinutes(29));
        var kept = await h.RefreshAsync(A, second, ct);
        Assert.Equal(HttpStatusCode.OK, kept.Status);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, kept.Text("refreshToken")!, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, third, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, fourth, ct)).Status);

        // Absolute: refreshed every 25 minutes, the session still ends 12 hours after sign-in.
        var session = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        var refresh = session.Text("refreshToken")!;
        var signedInAt = h.Clock.UtcNow;
        while (h.Clock.UtcNow + TimeSpan.FromMinutes(25) < signedInAt.AddHours(12))
        {
            h.Clock.Advance(TimeSpan.FromMinutes(25));
            var next = await h.RefreshAsync(A, refresh, ct);
            Assert.Equal(HttpStatusCode.OK, next.Status);
            refresh = next.Text("refreshToken")!;
        }

        h.Clock.Advance(signedInAt.AddHours(12) - h.Clock.UtcNow);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, refresh, ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_IAM_04_a_revoked_session_and_a_reused_refresh_token_stop_working_at_the_next_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, tokens) = await h.ActiveUserAsync(A, ct);
        var rotated = await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.NotEqual(tokens.Text("refreshToken"), rotated.Text("refreshToken"));

        // The old refresh token again: possible theft, so the whole session ends (ruling P6).
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, rotated.Text("refreshToken")!, ct)).Status);

        // Self-service revocation; the access token itself lives at most 5 minutes (ruling Q3).
        var other = await h.ActiveUserAsync(A, ct);
        var otherAccess = other.Tokens.Text("accessToken")!;
        var sessionId = other.Tokens.Text("sessionId")!;
        Assert.Equal(HttpStatusCode.NoContent, (await h.DeleteAsync(A, "/api/iam/me/sessions/" + sessionId, otherAccess, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, other.Tokens.Text("refreshToken")!, ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.DeleteAsync(A, "/api/iam/me/sessions/" + sessionId, otherAccess, ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.DeleteAsync(A, "/api/iam/me/sessions/" + tokens.Text("sessionId"), otherAccess, ct)).Status); // not the caller's
    }

    [Fact]
    public async Task FR_ADM_USR_03_disabling_a_user_revokes_all_sessions_and_status_changes_follow_the_allowed_transitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = h.AdminToken(A);
        var (userId, username, first) = await h.ActiveUserAsync(A, ct);
        var second = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal(2, (await h.GetAsync(A, $"/api/iam/users/{userId}/sessions", admin, ct)).Json.GetArrayLength());

        Assert.Contains("errors.user.reason_required", (await h.PostAsync(A, $"/api/iam/users/{userId}/status", new { status = "disabled" }, admin, ct)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.user.transition_not_allowed", (await h.PostAsync(A, $"/api/iam/users/{userId}/status", new { status = "archived", reason = "x" }, admin, ct)).Body, StringComparison.Ordinal);
        AssertDisabled(await h.PostAsync(A, $"/api/iam/users/{userId}/status", new { status = "disabled", reason = "left the company" }, admin, ct));

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, first.Text("refreshToken")!, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, second.Text("refreshToken")!, ct)).Status);
        Assert.Equal(0, await h.CountAsync(A, "select count(*) from iam.sessions where user_id = $1 and revoked_at is null", ct, userId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Status);

        AssertDisabled(await h.PostAsync(A, $"/api/iam/users/{userId}/status", new { status = "archived", reason = "retention" }, admin, ct)); // archived answers like disabled (P7)
        Assert.Contains("errors.user.transition_not_allowed", (await h.PostAsync(A, $"/api/iam/users/{userId}/status", new { status = "active", reason = "x" }, admin, ct)).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync(A, $"/api/iam/users/{BaselineSeed.SystemUserId}/status", new { status = "disabled", reason = "x" }, admin, ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync(B, $"/api/iam/users/{userId}/unlock", new { reason = "x" }, h.AdminToken(B), ct)).Status); // A's user on B's host
        Assert.Equal(2, (await h.EventsAsync(A, userId, ct)).Count(e => e == IdentityEvents.UserStatusChanged));
    }

    [Fact]
    public async Task FR_ADM_USR_05_admin_reset_mfa_and_revoke_sessions_take_effect_and_raise_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = h.AdminToken(A);
        var (userId, username, tokens) = await h.ActiveUserAsync(A, ct);
        await h.EnrolAsync(A, tokens.Text("accessToken")!, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{userId}/reset-mfa", new { reason = "lost phone" }, admin, ct)).Status);
        Assert.Equal("tokens", (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Text("outcome")); // no second factor left
        Assert.Equal(0, await h.CountAsync(A, "select count(*) from iam.mfa_recovery_codes where user_id = $1", ct, userId));

        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{userId}/revoke-sessions", new { reason = "incident" }, admin, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct)).Status);
        var events = await h.EventsAsync(A, userId, ct);
        Assert.Contains(IdentityEvents.MfaReset, events);
        Assert.Contains(IdentityEvents.SessionsRevoked, events);
        Assert.Contains("errors.user.not_locked", (await h.PostAsync(A, $"/api/iam/users/{userId}/unlock", new { reason = "x" }, admin, ct)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S105_activation_token_works_once_expires_and_cannot_be_used_on_another_customers_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, token) = await h.InviteAsync(A, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(B, "/api/iam/auth/activate", new { token, newPassword = IdentityHarness.Password }, ct: ct)).Status);
        Assert.Contains("errors.password.too_short", (await h.PostAsync(A, "/api/iam/auth/activate", new { token, newPassword = "Sh0rt!" }, ct: ct)).Body, StringComparison.Ordinal);
        Assert.Equal("tokens", (await h.PostAsync(A, "/api/iam/auth/activate", new { token, newPassword = IdentityHarness.Password }, ct: ct)).Text("outcome"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/activate", new { token, newPassword = "Another-Pass-9" }, ct: ct)).Status);
        Assert.Equal("active", await h.StatusAsync(A, userId, ct));
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Status);
        Assert.Contains(IdentityEvents.UserActivated, await h.EventsAsync(A, userId, ct));

        var (lateId, _, late) = await h.InviteAsync(A, ct);
        h.Clock.Advance(TimeSpan.FromHours(72));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/activate", new { token = late, newPassword = IdentityHarness.Password }, ct: ct)).Status);
        Assert.Equal("invited", await h.StatusAsync(A, lateId, ct));
    }

    [Fact]
    public async Task S105_the_operator_link_for_the_first_admin_replaces_the_previous_link_and_activates_the_provisioned_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "c" + fx.Suffix;
        await fx.ProvisionAsync(code, ct);
        var activation = new FirstAdminActivation(fx.Options, fx.Secrets, h.Clock, fx.Migrations);
        var first = await activation.IssueAsync(code, ct);
        var second = await activation.IssueAsync(code, ct);
        Assert.StartsWith($"https://{TenancyFixture.HostOf(code)}/activate?token=", second!.Url, StringComparison.Ordinal);
        string TokenOf(FirstAdminActivation.Link link) => Uri.UnescapeDataString(link.Url[(link.Url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(code, "/api/iam/auth/activate", new { token = TokenOf(first!), newPassword = IdentityHarness.Password }, ct: ct)).Status);
        Assert.Equal("tokens", (await h.PostAsync(code, "/api/iam/auth/activate", new { token = TokenOf(second), newPassword = IdentityHarness.Password }, ct: ct)).Text("outcome"));
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(code, $"admin@{code}.example.test", IdentityHarness.Password, ct)).Status);
        Assert.Null(await activation.IssueAsync(code, ct)); // nobody is waiting any more
    }

    [Fact]
    public async Task FR_KRN_IAM_18_activating_a_user_beyond_the_entitlement_user_limit_is_refused_with_a_clear_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var (pendingId, _, token) = await h.InviteAsync(A, ct);
        var (disabledId, _, _) = await h.ActiveUserAsync(A, ct);
        AssertDisabled(await h.PostAsync(A, $"/api/iam/users/{disabledId}/status", new { status = "disabled", reason = "seat test" }, h.AdminToken(A), ct));
        var used = await h.CountAsync(A, "select count(*) from iam.users where not is_system and not is_deleted and status in ('active', 'locked')", ct);
        // Slice S1.09b: the limit is claims.limits["users.named"] of the entitlement in force (ruling Q1), enforced by the limit guard.
        var claims = await h.Fixture.SetClaimAsync(A, "limits,users.named", (int)used, ct);
        try
        {
            var refused = await h.PostAsync(A, "/api/iam/auth/activate", new { token, newPassword = IdentityHarness.Password }, ct: ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Contains("errors.licence.limit_reached", refused.Body, StringComparison.Ordinal);
            Assert.Contains("users.named", refused.Body, StringComparison.Ordinal);
            Assert.Equal("invited", await h.StatusAsync(A, pendingId, ct));
            var reEnable = await h.PostAsync(A, $"/api/iam/users/{disabledId}/status", new { status = "active", reason = "back" }, h.AdminToken(A), ct);
            Assert.Equal(HttpStatusCode.Conflict, reEnable.Status);
            Assert.Contains("errors.licence.limit_reached", reEnable.Body, StringComparison.Ordinal);
        }
        finally
        {
            await h.Fixture.RestoreClaimsAsync(A, claims, CancellationToken.None);
        }

        Assert.Equal("tokens", (await h.PostAsync(A, "/api/iam/auth/activate", new { token, newPassword = IdentityHarness.Password }, ct: ct)).Text("outcome"));
    }

    [Fact]
    public async Task CR012_the_generated_user_api_never_sets_status_so_new_users_start_invited()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await h.PostAsync(A, "/api/iam/users", new { username = "gen" + fx.Suffix, email = $"gen{fx.Suffix}@example.test", displayName = "Generated", language = "en", timeZone = "Asia/Karachi", status = "active", isSystem = true }, h.AdminToken(A), ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("invited", created.Text("status"));
        Assert.False(created.Json.GetProperty("isSystem").GetBoolean());
    }

    [Fact]
    public async Task S105_sign_in_still_works_while_the_customer_is_read_only_but_changes_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, username, tokens) = await h.ActiveUserAsync(A, ct);
        await fx.SetDaysAfterEndAsync(A, 20, ct); // S1.09b: read-only follows from the end date (20 days after it)

        try
        {
            Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(A, username, IdentityHarness.Password, ct)).Status);
            Assert.Equal(HttpStatusCode.OK, (await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = IdentityHarness.Password, newPassword = "Read-Only-Pass-1" }, tokens.Text("accessToken"), ct)).Status);
        }
        finally
        {
            await fx.SetDaysAfterEndAsync(A, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task S105_identity_settings_are_seeded_once_with_the_ruling_Q5_defaults_and_customer_values_survive_a_reseed()
    {
        var ct = TestContext.Current.CancellationToken;
        var undo = await h.SetSettingAsync(A, IdentitySettings.LockoutThreshold, "7", null, ct);
        try
        {
            await using (var owner = await h.OwnerAsync(A, ct))
            {
                await IdentitySetup.EnsureAsync(owner, fx.Secrets, A, ct);
                await IdentitySetup.EnsureAsync(owner, fx.Secrets, A, ct);
                var policy = await IdentitySettings.ReadAsync(owner, null, null, DateTimeOffset.UtcNow, ct);
                Assert.Equal(new Erp.Kernel.Domain.Identity.PasswordPolicy(10, 3, 5, 0, 7, 15), policy.Password);
                Assert.Equal(new Erp.Kernel.Domain.Identity.SessionPolicy(30, 720, 3, 0), policy.Session);
                Assert.Equal(7, policy.Mfa.GraceDays);
                Assert.Equal(10, policy.Mfa.RecoveryCodeCount);
                Assert.False(policy.Mfa.Required);
            }

            Assert.Equal(IdentitySettings.All.Count, await h.CountAsync(A, "select count(*) from cfg.settings where module_id = 'iam'", ct));
        }
        finally
        {
            await undo();
        }
    }
}
