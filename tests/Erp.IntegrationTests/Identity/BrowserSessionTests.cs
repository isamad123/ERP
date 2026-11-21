using System.Net;
using System.Net.Http.Json;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Identity;

/// <summary>
/// A browser as the web application drives the API (slice S1.12a): it sends <c>X-Erp-Client: web</c> and keeps the cookies
/// the API sets, as a browser would for the customer's host (path, expiry and deletion; HttpOnly and Secure are checked
/// on the Set-Cookie text, since the browser enforces them).
/// </summary>
public sealed class TestBrowser(IdentityHarness h, string code)
{
    private readonly Dictionary<string, string> jar = new(StringComparer.Ordinal);

    public List<string> LastSetCookies { get; } = [];

    public bool Has(string name) => jar.ContainsKey(name);

    public string? Cookie(string name) => jar.GetValueOrDefault(name);

    public void Put(string name, string value) => jar[name] = value;

    public void Forget(string name) => jar.Remove(name);

    public async Task<Answer> SendAsync(HttpMethod method, string path, object? body = null, string? token = null, bool header = true, CancellationToken ct = default)
    {
        using var client = h.Client(code, token);
        using var request = new HttpRequestMessage(method, path);
        if (header)
        {
            request.Headers.Add("X-Erp-Client", "web");
        }

        if (path.StartsWith("/api/iam/auth", StringComparison.Ordinal) && jar.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value)));
        }

        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            request.Content = JsonContent.Create(body ?? new { });
        }

        using var response = await client.SendAsync(request, ct);
        LastSetCookies.Clear();
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var raw in cookies)
            {
                LastSetCookies.Add(raw);
                var first = raw.Split(';')[0];
                var name = first[..first.IndexOf('=', StringComparison.Ordinal)];
                var value = first[(name.Length + 1)..];
                var expired = raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) || value.Length == 0;
                if (expired)
                {
                    jar.Remove(name);
                }
                else
                {
                    jar[name] = value;
                }
            }
        }

        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public Task<Answer> PostAsync(string path, object? body = null, string? token = null, bool header = true, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, body, token, header, ct);

    public Task<Answer> SignInAsync(string login, CancellationToken ct) =>
        PostAsync("/api/iam/auth/sign-in", new { login, password = IdentityHarness.Password }, ct: ct);

    public Task<Answer> RefreshAsync(CancellationToken ct) => PostAsync("/api/iam/auth/refresh", new { }, ct: ct);

    public string? SetCookie(string name) => LastSetCookies.FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal));
}

/// <summary>
/// Identity API version 3 (slice S1.12a, CR-063 item 1, ruling Q3; FR-KRN-IAM-01, FR-KRN-IAM-04): a browser's refresh token
/// travels only in an HttpOnly, Secure, SameSite=Strict cookie for <c>/api/iam/auth</c>; the cookie is accepted only with the
/// <c>X-Erp-Client: web</c> header; clients without the header keep the JSON body form.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class BrowserSessionTests(TenancyFixture fx) : IAsyncLifetime
{
    private IdentityHarness h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string A => "i" + fx.Suffix;

    public async ValueTask InitializeAsync()
    {
        await IdentityTests.ProvisionGate.WaitAsync(Ct);
        try
        {
            if (IdentityTests.provisionedFor != fx.Suffix)
            {
                await fx.ProvisionAsync(A, Ct);
                await fx.ProvisionAsync("j" + fx.Suffix, Ct);
                IdentityTests.provisionedFor = fx.Suffix;
            }
        }
        finally
        {
            IdentityTests.ProvisionGate.Release();
        }

        h = await IdentityHarness.StartAsync(fx, Ct);
    }

    public async ValueTask DisposeAsync() => await h.App.DisposeAsync();

    [Fact]
    public async Task S112a_KRN_IAM_04_a_browser_gets_the_refresh_token_only_as_an_HttpOnly_cookie_for_the_sign_in_paths()
    {
        var (_, username, _) = await h.ActiveUserAsync(A, Ct);
        var browser = new TestBrowser(h, A);
        var signIn = await browser.SignInAsync(username, Ct);

        Assert.Equal(HttpStatusCode.OK, signIn.Status);
        Assert.Equal("tokens", signIn.Text("outcome"));
        Assert.NotNull(signIn.Text("accessToken"));
        Assert.Null(signIn.Text("refreshToken"));
        Assert.Equal(30, signIn.Json.GetProperty("idleTimeoutMinutes").GetInt32());
        var cookie = browser.SetCookie("__Secure-erp_rt")!;
        Assert.NotNull(cookie);
        var flags = cookie.ToLowerInvariant();
        Assert.Contains("httponly", flags, StringComparison.Ordinal);
        Assert.Contains("secure", flags, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", flags, StringComparison.Ordinal);
        Assert.Contains("path=/api/iam/auth", flags, StringComparison.Ordinal);
        Assert.DoesNotContain("expires=", flags, StringComparison.Ordinal); // forgotten when the browser closes
        Assert.DoesNotContain(browser.Cookie("__Secure-erp_rt")!, signIn.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S112a_KRN_IAM_04_refresh_reads_the_cookie_rotates_it_and_a_reused_old_cookie_ends_the_session()
    {
        var (_, username, _) = await h.ActiveUserAsync(A, Ct);
        var browser = new TestBrowser(h, A);
        await browser.SignInAsync(username, Ct);
        var first = browser.Cookie("__Secure-erp_rt")!;

        var refreshed = await browser.RefreshAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal("tokens", refreshed.Text("outcome"));
        Assert.Null(refreshed.Text("refreshToken"));
        Assert.Equal(30, refreshed.Json.GetProperty("idleTimeoutMinutes").GetInt32());
        var second = browser.Cookie("__Secure-erp_rt")!;
        Assert.NotEqual(first, second);

        // A stolen earlier cookie comes back: the session ends (ruling P6) and the browser is told to forget its cookie.
        var thief = new TestBrowser(h, A);
        thief.Put("__Secure-erp_rt", first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.RefreshAsync(Ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.RefreshAsync(Ct)).Status);
        Assert.False(browser.Has("__Secure-erp_rt"));
    }

    [Fact]
    public async Task S112a_KRN_IAM_04_the_cookie_is_refused_without_the_web_header_and_a_browser_body_token_is_ignored()
    {
        var (_, username, _) = await h.ActiveUserAsync(A, Ct);
        var browser = new TestBrowser(h, A);
        await browser.SignInAsync(username, Ct);
        var cookie = browser.Cookie("__Secure-erp_rt")!;

        // Another site could make the browser send the cookie, but not the custom header.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/iam/auth/refresh", new { }, header: false, ct: Ct)).Status);

        // With the header, only the cookie counts: a token in the body is not read.
        var bodyOnly = new TestBrowser(h, A);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bodyOnly.PostAsync("/api/iam/auth/refresh", new { refreshToken = cookie }, ct: Ct)).Status);

        // The cookie itself is still good (neither refusal used it up).
        Assert.Equal(HttpStatusCode.OK, (await browser.RefreshAsync(Ct)).Status);
    }

    [Fact]
    public async Task S112a_KRN_IAM_01_clients_without_the_web_header_keep_the_json_body_form()
    {
        var (_, username, _) = await h.ActiveUserAsync(A, Ct);
        var app = new TestBrowser(h, A);
        var signIn = await app.PostAsync("/api/iam/auth/sign-in", new { login = username, password = IdentityHarness.Password }, header: false, ct: Ct);
        Assert.Equal("tokens", signIn.Text("outcome"));
        Assert.NotNull(signIn.Text("refreshToken"));
        Assert.Empty(app.LastSetCookies);
        var refreshed = await h.RefreshAsync(A, signIn.Text("refreshToken")!, Ct);
        Assert.Equal("tokens", refreshed.Text("outcome"));
        Assert.NotNull(refreshed.Text("refreshToken"));
    }

    [Fact]
    public async Task S112a_KRN_IAM_04_sign_out_ends_the_session_and_clears_the_cookie()
    {
        var (_, username, _) = await h.ActiveUserAsync(A, Ct);
        var browser = new TestBrowser(h, A);
        var signIn = await browser.SignInAsync(username, Ct);
        var cookie = browser.Cookie("__Secure-erp_rt")!;

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/iam/auth/sign-out", token: signIn.Text("accessToken"), ct: Ct)).Status);
        Assert.False(browser.Has("__Secure-erp_rt"));
        browser.Put("__Secure-erp_rt", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.RefreshAsync(Ct)).Status);
    }
}

/// <summary>
/// "Remember this device" (slice S1.12a, ruling Q4, FR-ADM-USR-24, FR-KRN-IAM-02; data design of <c>iam.trusted_devices</c>
/// approved 2026-11-20): the password is always asked; a remembered browser skips the second factor while the customer's
/// <c>iam.session.remember_device_days</c> allows it; the trust ends early on password change or reset, MFA reset, revoked
/// sessions or removal; privileged users are never remembered; every use is a security event.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class TrustedDeviceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Days = "iam.session.remember_device_days";

    private IdentityHarness h = null!;
    private Func<Task>? undo;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The identity tests' customer: tests of this collection run one at a time and each test undoes its setting change.
    private string A => "i" + fx.Suffix;

    public async ValueTask InitializeAsync()
    {
        await IdentityTests.ProvisionGate.WaitAsync(Ct);
        try
        {
            if (IdentityTests.provisionedFor != fx.Suffix)
            {
                await fx.ProvisionAsync(A, Ct);
                await fx.ProvisionAsync("j" + fx.Suffix, Ct);
                IdentityTests.provisionedFor = fx.Suffix;
            }
        }
        finally
        {
            IdentityTests.ProvisionGate.Release();
        }

        h = await IdentityHarness.StartAsync(fx, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (undo is not null)
        {
            await undo();
        }

        await h.App.DisposeAsync();
    }

    /// <summary>An active user with an authenticator; returns its id, name and secret.</summary>
    private async Task<(Guid Id, string Username, string Secret)> UserWithAuthenticatorAsync()
    {
        var (id, username, tokens) = await h.ActiveUserAsync(A, Ct);
        var (secret, _) = await h.EnrolAsync(A, tokens.Text("accessToken")!, Ct);
        return (id, username, secret); // the enrolment used the current step: sign-ins use the next one (no clock move, so tokens stay valid)
    }

    /// <summary>Signs the browser in with the second factor, asking to remember it; returns the final answer.</summary>
    private async Task<Answer> SignInRememberingAsync(TestBrowser browser, string username, string secret)
    {
        var first = await browser.SignInAsync(username, Ct);
        Assert.Equal("mfa_required", first.Text("outcome"));
        var done = await browser.PostAsync("/api/iam/auth/mfa", new { challengeToken = first.Text("challengeToken"), code = h.Code(secret, TimeSpan.FromSeconds(30)), rememberDevice = true }, ct: Ct);
        Assert.Equal("tokens", done.Text("outcome"));
        return done;
    }

    private Task<long> TrustsAsync(Guid user) => h.CountAsync(A, "select count(*) from iam.trusted_devices where user_id = $1 and revoked_at is null", Ct, user);

    [Fact]
    public async Task S112a_ADM_USR_24_a_remembered_browser_skips_the_second_factor_and_another_browser_is_still_asked()
    {
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        var (id, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);

        var first = await browser.SignInAsync(username, Ct);
        Assert.Equal("mfa_required", first.Text("outcome"));
        Assert.Equal(30, first.Json.GetProperty("rememberDeviceDays").GetInt32());
        var done = await browser.PostAsync("/api/iam/auth/mfa", new { challengeToken = first.Text("challengeToken"), code = h.Code(secret, TimeSpan.FromSeconds(30)), rememberDevice = true }, ct: Ct);
        Assert.Equal("tokens", done.Text("outcome"));
        var cookie = browser.SetCookie("__Secure-erp_td")!.ToLowerInvariant();
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/iam/auth", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=", cookie, StringComparison.Ordinal);
        Assert.Equal(1, await TrustsAsync(id));
        var stored = await h.CountAsync(A, "select count(*) from iam.trusted_devices where user_id = $1 and token_hash = $2", Ct, id, browser.Cookie("__Secure-erp_td")!);
        Assert.Equal(0, stored); // only the hash is kept

        // The same browser, next time: password only.
        h.Clock.Advance(TimeSpan.FromHours(1));
        var again = await browser.SignInAsync(username, Ct);
        Assert.Equal("tokens", again.Text("outcome"));
        Assert.Contains("iam.trusted_device_used", await h.EventsAsync(A, id, Ct));
        Assert.Contains("iam.trusted_device_added", await h.EventsAsync(A, id, Ct));

        // The password is still asked: a wrong one fails as before.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/iam/auth/sign-in", new { login = username, password = "wrong-password-1" }, ct: Ct)).Status);

        // Another browser still gets the second factor.
        Assert.Equal("mfa_required", (await new TestBrowser(h, A).SignInAsync(username, Ct)).Text("outcome"));

        // The device cookie without the web header is not used.
        var noHeader = await browser.PostAsync("/api/iam/auth/sign-in", new { login = username, password = IdentityHarness.Password }, header: false, ct: Ct);
        Assert.Equal("mfa_required", noHeader.Text("outcome"));
    }

    [Fact]
    public async Task S112a_ADM_USR_24_with_the_setting_at_zero_nothing_is_offered_or_remembered()
    {
        var (id, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);
        var first = await browser.SignInAsync(username, Ct);
        Assert.Equal(0, first.Json.GetProperty("rememberDeviceDays").GetInt32());
        var done = await browser.PostAsync("/api/iam/auth/mfa", new { challengeToken = first.Text("challengeToken"), code = h.Code(secret, TimeSpan.FromSeconds(30)), rememberDevice = true }, ct: Ct);
        Assert.Equal("tokens", done.Text("outcome"));
        Assert.Null(browser.SetCookie("__Secure-erp_td"));
        Assert.Equal(0, await TrustsAsync(id));

        // A client without the web header is never remembered either.
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        h.Clock.Advance(TimeSpan.FromSeconds(60)); // a fresh authenticator step (no token is used after this)
        var app = new TestBrowser(h, A);
        var signIn = await app.PostAsync("/api/iam/auth/sign-in", new { login = username, password = IdentityHarness.Password }, header: false, ct: Ct);
        Assert.Equal(0, signIn.Json.GetProperty("rememberDeviceDays").GetInt32());
        await app.PostAsync("/api/iam/auth/mfa", new { challengeToken = signIn.Text("challengeToken"), code = h.Code(secret), rememberDevice = true }, header: false, ct: Ct);
        Assert.Equal(0, await TrustsAsync(id));
    }

    [Fact]
    public async Task S112a_ADM_USR_24_the_trust_ends_with_its_period_and_at_once_when_the_setting_is_shortened_or_turned_off()
    {
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        var (_, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);
        await SignInRememberingAsync(browser, username, secret);

        h.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal("tokens", (await browser.SignInAsync(username, Ct)).Text("outcome"));

        await h.SetSettingAsync(A, Days, "2", null, Ct); // shortened: the 3-day-old trust no longer counts
        Assert.Equal("mfa_required", (await browser.SignInAsync(username, Ct)).Text("outcome"));

        await h.SetSettingAsync(A, Days, "30", null, Ct);
        Assert.Equal("tokens", (await browser.SignInAsync(username, Ct)).Text("outcome"));
        await h.SetSettingAsync(A, Days, "0", null, Ct); // turned off
        Assert.Equal("mfa_required", (await browser.SignInAsync(username, Ct)).Text("outcome"));

        await h.SetSettingAsync(A, Days, "30", null, Ct);
        h.Clock.Advance(TimeSpan.FromDays(28)); // 31 days after it was given
        Assert.Equal("mfa_required", (await browser.SignInAsync(username, Ct)).Text("outcome"));
    }

    [Fact]
    public async Task S112a_ADM_USR_24_a_password_change_an_mfa_reset_or_revoked_sessions_end_the_trust()
    {
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        var admin = h.AdminToken(A);

        // Self-service password change.
        var (id, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);
        var done = await SignInRememberingAsync(browser, username, secret);
        const string Changed = "Another-Pass-93";
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, "/api/iam/me/password", new { currentPassword = IdentityHarness.Password, newPassword = Changed }, done.Text("accessToken"), Ct)).Status);
        Assert.Equal(0, await TrustsAsync(id));
        Assert.Contains("iam.trusted_device_revoked", await h.EventsAsync(A, id, Ct));
        Assert.Equal("mfa_required", (await browser.PostAsync("/api/iam/auth/sign-in", new { login = username, password = Changed }, ct: Ct)).Text("outcome"));

        // Administrator revokes the sessions.
        (id, username, secret) = await UserWithAuthenticatorAsync();
        browser = new TestBrowser(h, A);
        await SignInRememberingAsync(browser, username, secret);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{id}/revoke-sessions", new { reason = "lost laptop" }, admin, Ct)).Status);
        Assert.Equal("mfa_required", (await browser.SignInAsync(username, Ct)).Text("outcome"));

        // Administrator resets the person's MFA: the next sign-in has no factor left to skip, and no trust either.
        (id, username, secret) = await UserWithAuthenticatorAsync();
        browser = new TestBrowser(h, A);
        await SignInRememberingAsync(browser, username, secret);
        Assert.Equal(1, await TrustsAsync(id));
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{id}/reset-mfa", new { reason = "new phone" }, admin, Ct)).Status);
        Assert.Equal(0, await TrustsAsync(id));
        Assert.Equal("mfa_reset", await ScalarTextAsync("select revoked_reason from iam.trusted_devices where user_id = $1", id));
    }

    [Fact]
    public async Task S112a_ADM_USR_24_a_status_change_that_ends_the_sessions_also_ends_the_trust()
    {
        // CR-063 S1.12a detail, deviation (a), confirmed 2026-11-20: disabling (or archiving) a person counts as revoked
        // sessions; enabling the person again does not bring the trust back (review S1.12a, test gap). Disabling answers
        // 200 with the person's open approval tasks (S1.10b ruling P7), enabling 204.
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        var admin = h.AdminToken(A);
        var (id, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);
        await SignInRememberingAsync(browser, username, secret);
        Assert.Equal(1, await TrustsAsync(id));

        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync(A, $"/api/iam/users/{id}/status", new { status = "disabled", reason = "left for a month" }, admin, Ct)).Status);
        Assert.Equal(0, await TrustsAsync(id));
        Assert.Equal("sessions_revoked", await ScalarTextAsync("select revoked_reason from iam.trusted_devices where user_id = $1", id));

        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, $"/api/iam/users/{id}/status", new { status = "active", reason = "back" }, admin, Ct)).Status);
        Assert.Equal("mfa_required", (await browser.SignInAsync(username, Ct)).Text("outcome"));
    }

    [Fact]
    public async Task S112a_ADM_USR_24_the_person_lists_and_removes_their_remembered_browsers()
    {
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        var (id, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);
        var done = await SignInRememberingAsync(browser, username, secret);
        var token = done.Text("accessToken");

        var list = await h.GetAsync(A, "/api/iam/me/trusted-devices", token, Ct);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var device = Assert.Single(list.Json.EnumerateArray());
        Assert.DoesNotContain("tokenHash", list.Body, StringComparison.OrdinalIgnoreCase);
        var deviceId = device.GetProperty("id").GetGuid();

        // Another person cannot remove it.
        var (_, _, other) = await h.ActiveUserAsync(A, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await h.DeleteAsync(A, $"/api/iam/me/trusted-devices/{deviceId}", other.Text("accessToken"), Ct)).Status);
        Assert.Equal(1, await TrustsAsync(id));

        Assert.Equal(HttpStatusCode.NoContent, (await h.DeleteAsync(A, $"/api/iam/me/trusted-devices/{deviceId}", token, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.DeleteAsync(A, $"/api/iam/me/trusted-devices/{deviceId}", token, Ct)).Status);
        Assert.Empty((await h.GetAsync(A, "/api/iam/me/trusted-devices", token, Ct)).Json.EnumerateArray());
        Assert.Equal("mfa_required", (await browser.SignInAsync(username, Ct)).Text("outcome"));
    }

    [Fact]
    public async Task S112a_ADM_USR_24_a_person_whose_mfa_is_required_by_a_privileged_role_is_never_remembered()
    {
        undo = await h.SetSettingAsync(A, Days, "30", null, Ct);
        var (id, username, secret) = await UserWithAuthenticatorAsync();
        var browser = new TestBrowser(h, A);
        await SignInRememberingAsync(browser, username, secret); // remembered while not privileged

        await using (var owner = await h.OwnerAsync(A, Ct))
        {
            await IdentityHarness.ExecAsync(owner, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, r.id, null, 'test', current_date - 1, now(), $3, now(), $3 from iam.roles r where lower(r.code) = 'tenant_admin'
                """, Ct, Guid.CreateVersion7(), id, BaselineSeed.SystemUserId);
        }

        var signIn = await browser.SignInAsync(username, Ct);
        Assert.Equal("mfa_required", signIn.Text("outcome")); // the earlier trust no longer counts
        Assert.Equal(0, signIn.Json.GetProperty("rememberDeviceDays").GetInt32());
        h.Clock.Advance(TimeSpan.FromSeconds(60)); // a fresh authenticator step (no token is used after this)
        var done = await browser.PostAsync("/api/iam/auth/mfa", new { challengeToken = signIn.Text("challengeToken"), code = h.Code(secret), rememberDevice = true }, ct: Ct);
        Assert.Equal("tokens", done.Text("outcome"));
        Assert.Null(browser.SetCookie("__Secure-erp_td"));
        Assert.Equal(1, await TrustsAsync(id)); // no new trust was added
    }

    private async Task<string?> ScalarTextAsync(string sql, Guid id)
    {
        await using var owner = await h.OwnerAsync(A, Ct);
#pragma warning disable CA2100 // test SQL text
        await using var command = new Npgsql.NpgsqlCommand(sql, owner);
#pragma warning restore CA2100
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = id });
        return (string?)await command.ExecuteScalarAsync(Ct);
    }
}
