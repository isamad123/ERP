using System.Net;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Erp.IntegrationTests.Identity;

/// <summary>
/// Fixes for the S1.05 fresh-session review (docs/reviews/S1.05-review.md, product owner "go ahead" 2026-11-16). Each
/// test reproduces the reviewer's scenario, so it fails on the code as merged in PR #6.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class IdentityReviewFixTests(TenancyFixture fx) : IAsyncLifetime
{
    private IdentityHarness h = null!;

    private string A => "i" + fx.Suffix;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await IdentityTests.ProvisionGate.WaitAsync(ct);
        try
        {
            if (IdentityTests.provisionedFor != fx.Suffix)
            {
                await fx.ProvisionAsync("i" + fx.Suffix, ct);
                await fx.ProvisionAsync("j" + fx.Suffix, ct);
                IdentityTests.provisionedFor = fx.Suffix;
            }
        }
        finally
        {
            IdentityTests.ProvisionGate.Release();
        }

        h = await IdentityHarness.StartAsync(fx, ct);
    }

    public async ValueTask DisposeAsync() => await h.App.DisposeAsync();

    private Task<long> FailuresAsync(Guid userId, CancellationToken ct) =>
        h.CountAsync(A, "select failed_attempts::bigint from iam.user_credentials where user_id = $1", ct, userId);

    [Fact]
    public async Task FR_KRN_IAM_01_FR_KRN_IAM_02_review1_signing_in_again_does_not_wipe_failed_second_factor_guesses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, tokens) = await h.ActiveUserAsync(A, ct);
        await h.EnrolAsync(A, tokens.Text("accessToken")!, ct);

        var first = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        for (var i = 0; i < 4; i++)
        {
            await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = first.Text("challengeToken"), code = "000000" }, ct: ct);
        }

        var again = await h.SignInAsync(A, username, IdentityHarness.Password, ct); // used to reset the count to 0
        Assert.Equal("mfa_required", again.Text("outcome"));
        Assert.Equal(4, await FailuresAsync(userId, ct));
        await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = again.Text("challengeToken"), code = "000000" }, ct: ct);
        Assert.Equal("locked", await h.StatusAsync(A, userId, ct));
    }

    [Fact]
    public async Task FR_KRN_IAM_01_review2_parallel_wrong_passwords_are_all_counted_and_lock_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, _) = await h.ActiveUserAsync(A, ct);
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => h.SignInAsync(A, username, "Wrong-Password-1", ct)));
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a.Status));
        Assert.Equal("locked", await h.StatusAsync(A, userId, ct));
        Assert.Equal(5, await FailuresAsync(userId, ct)); // after the 5th, the rest find the account locked and are not counted
        Assert.Single(await h.EventsAsync(A, userId, ct), e => e == IdentityEvents.UserLocked);
    }

    [Fact]
    public async Task FR_KRN_IAM_04_review3_refresh_racing_sign_in_or_an_admin_disable_never_fails_with_a_server_error()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 5; round++)
        {
            var (userId, username, first) = await h.ActiveUserAsync(A, ct);
            await h.SignInAsync(A, username, IdentityHarness.Password, ct);
            await h.SignInAsync(A, username, IdentityHarness.Password, ct);

            // The 4th sign-in revokes the oldest session while that session refreshes.
            var race = await Task.WhenAll(h.RefreshAsync(A, first.Text("refreshToken")!, ct), h.SignInAsync(A, username, IdentityHarness.Password, ct));
            Assert.All(race, a => Assert.True((int)a.Status < 500, $"server error {a.Status}: {a.Body}"));

            var latest = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
            var disable = await Task.WhenAll(
                h.RefreshAsync(A, latest.Text("refreshToken")!, ct),
                h.PostAsync(A, $"/api/iam/users/{userId}/status", new { status = "disabled", reason = "race" }, h.AdminToken(A), ct));
            Assert.All(disable, a => Assert.True((int)a.Status < 500, $"server error {a.Status}: {a.Body}"));
            Assert.Equal(HttpStatusCode.NoContent, disable[1].Status);
        }
    }

    [Fact]
    public async Task FR_ADM_USR_10_review4_sensitive_self_service_needs_the_current_password_or_a_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, _, tokens) = await h.ActiveUserAsync(A, ct);
        var access = tokens.Text("accessToken")!;

        var noProof = await h.PostAsync(A, "/api/iam/me/mfa-devices", new { label = "phone" }, access, ct);
        Assert.Equal(HttpStatusCode.BadRequest, noProof.Status);
        Assert.Contains("errors.auth.reauthentication_failed", noProof.Body, StringComparison.Ordinal);
        Assert.Equal(1, await FailuresAsync(userId, ct)); // counted towards lockout

        var (secret, _) = await h.EnrolAsync(A, access, ct); // with the current password
        Assert.Contains("errors.auth.reauthentication_failed", (await h.PostAsync(A, "/api/iam/me/recovery-codes", new { currentPassword = "Wrong-Password-1" }, access, ct)).Body, StringComparison.Ordinal);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        var regenerated = await h.PostAsync(A, "/api/iam/me/recovery-codes", new { code = h.Code(secret) }, access, ct);
        Assert.Equal(HttpStatusCode.OK, regenerated.Status);
        Assert.Equal(10, regenerated.Json.GetProperty("recoveryCodes").GetArrayLength());

        var devices = await h.GetAsync(A, "/api/iam/me/mfa-devices", access, ct);
        var deviceId = devices.Json.EnumerateArray().First(d => d.GetProperty("status").GetString() == "active").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync(A, $"/api/iam/me/mfa-devices/{deviceId}/remove", new { currentPassword = "Wrong-Password-1" }, access, ct)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/me/mfa-devices/{deviceId}/remove", new { currentPassword = IdentityHarness.Password }, access, ct)).Status);
        Assert.Contains(IdentityEvents.RecoveryCodesRegenerated, await h.EventsAsync(A, userId, ct)); // review finding 13
    }

    [Fact]
    public async Task FR_ADM_USR_10_review4_wrong_current_passwords_lock_the_account_and_a_password_change_ends_the_other_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, current) = await h.ActiveUserAsync(A, ct);
        var other = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        var access = current.Text("accessToken")!;

        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = IdentityHarness.Password, newPassword = "Changed-Pass-55" }, access, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, other.Text("refreshToken")!, ct)).Status); // the other session ended
        Assert.Equal(HttpStatusCode.OK, (await h.RefreshAsync(A, current.Text("refreshToken")!, ct)).Status); // this one stays

        for (var i = 0; i < 5; i++)
        {
            await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = "Wrong-Password-1", newPassword = "Another-Pass-66" }, access, ct);
        }

        Assert.Equal("locked", await h.StatusAsync(A, userId, ct));
    }

    [Fact]
    public async Task FR_ADM_USR_20_review4_a_required_password_change_ends_the_sessions_opened_before_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, before) = await h.ActiveUserAsync(A, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{userId}/force-password-change", new { reason = "rotation" }, h.AdminToken(A), ct)).Status);
        var signIn = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal("tokens", (await h.PostAsync(A, "/api/iam/auth/password-change", new { challengeToken = signIn.Text("challengeToken"), newPassword = "Rotated-Pass-77" }, ct: ct)).Text("outcome"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, before.Text("refreshToken")!, ct)).Status); // must_change is cleared, but the old session stays ended
    }

    [Fact]
    public async Task FR_KRN_IAM_04_review6_a_session_id_with_a_wrong_secret_does_not_end_the_session_but_reusing_the_last_token_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, tokens) = await h.ActiveUserAsync(A, ct);
        var sessionId = Guid.Parse(tokens.Text("sessionId")!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, sessionId.ToString("N") + ".not-the-secret-at-all-xxxxxxxxxxxx", ct)).Status);
        var rotated = await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct);
        Assert.Equal(HttpStatusCode.OK, rotated.Status); // the stranger's attempt did not end it

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, tokens.Text("refreshToken")!, ct)).Status); // reuse of the replaced token
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.RefreshAsync(A, rotated.Text("refreshToken")!, ct)).Status); // so the session ended
    }

    [Fact]
    public void FR_KRN_IAM_04_review7_access_tokens_get_no_clock_skew_so_revocation_holds_within_five_minutes()
    {
        var options = h.App.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal(TimeSpan.Zero, options.TokenValidationParameters.ClockSkew);
        Assert.Equal(TimeSpan.FromMinutes(5), JwtAccessTokenIssuer.Lifetime);
    }

    [Fact]
    public async Task FR_KRN_AUD_02_review9_malformed_sign_in_attempts_are_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var login = "malformed" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SignInAsync(A, login, string.Empty, ct)).Status);
        Assert.Equal(["bad_password"], await h.OutcomesAsync(A, login, ct));
    }

    [Fact]
    public async Task FR_ADM_USR_24_review10_out_of_range_or_malformed_setting_values_never_break_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, username, _) = await h.ActiveUserAsync(A, ct);
        foreach (var bad in new[] { "0", "-3", "2.5", "\"three\"" })
        {
            var undo = await h.SetSettingAsync(A, IdentitySettings.SessionMaxConcurrent, bad, null, ct);
            try
            {
                var signIn = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
                Assert.True(signIn.Text("outcome") == "tokens", $"value {bad}: {signIn.Body}");
                var refreshed = await h.RefreshAsync(A, signIn.Text("refreshToken")!, ct);
                Assert.True(refreshed.Status == HttpStatusCode.OK, $"value {bad}: refresh {refreshed.Status}"); // not revoked by a limit of 0
            }
            finally
            {
                await undo();
            }
        }
    }

    [Fact]
    public async Task FR_ADM_USR_21_review11_a_user_setting_cannot_relax_the_customers_mfa_requirement()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, _) = await h.ActiveUserAsync(A, ct);
        var undoTenant = await h.SetSettingAsync(A, IdentitySettings.MfaRequired, "true", null, ct);
        var undoUser = await h.SetSettingAsync(A, IdentitySettings.MfaRequired, "false", userId, ct);
        try
        {
            var signIn = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
            Assert.Equal("tokens", signIn.Text("outcome"));
            Assert.True(signIn.Json.TryGetProperty("mfaEnrolBy", out var by) && by.ValueKind == System.Text.Json.JsonValueKind.String, "MFA is still required (grace deadline shown)");
        }
        finally
        {
            await undoUser();
            await undoTenant();
        }
    }

    [Fact]
    public async Task FR_KRN_AUD_02_review13_sign_out_and_self_revocation_raise_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, username, first) = await h.ActiveUserAsync(A, ct);
        var second = await h.SignInAsync(A, username, IdentityHarness.Password, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await h.DeleteAsync(A, "/api/iam/me/sessions/" + second.Text("sessionId"), first.Text("accessToken"), ct)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, "/api/iam/auth/sign-out", null, first.Text("accessToken"), ct)).Status);
        Assert.Equal(2, (await h.EventsAsync(A, userId, ct)).Count(e => e == IdentityEvents.SessionsRevoked));
    }
}
