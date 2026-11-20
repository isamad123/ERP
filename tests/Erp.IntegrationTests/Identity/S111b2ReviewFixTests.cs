using System.Net;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Notifications;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Identity;

/// <summary>
/// The fixes of the fresh-session review of slice S1.11b2 (docs/reviews/S1.11b2-review.md, CR-062; product owner's
/// rulings of 2026-11-20: A yes, B option (a), C fix all). Each test is a reproduction of the review turned round: it
/// failed on the merged code and passes now.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class S111b2ReviewFixTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string NewPassword = "Fresh-Review-Pass-58";
    private IdentityHarness h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => h = await IdentityHarness.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await h.App.DisposeAsync();

    private static string Token(string link) => Uri.UnescapeDataString(link[IdentityEmailService.ResetLinkPath.Length..]);

    // ------------------------------------------------------------------ finding 1 (CR-062)

    [Fact]
    public async Task S111b2_review_1_CR_062_KRN_INT_05_a_mail_connector_written_through_the_api_never_reads_a_secret_or_reaches_its_server()
    {
        await using var rig = await JobRig.StartAsync(fx, "rl", Ct);
        await using var relay = new TestMailServer();
        var access = await AccessHarness.ForAsync(fx);
        var admin = await access.UserAsync(Ct, rig.Code, "tenant_admin");
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync(Ct).AsTask();

        // A Tenant Admin writes a mail connector that names another customer's key and their own server.
        var created = await access.PostAsync(admin, "/api/itg/connectors", new
        {
            code = "email",
            nameI18n = new { en = "Mail" },
            kind = "email",
            provider = "smtp",
            config = new { host = "127.0.0.1", port, security = "none", username = "x", from_address = "x@evil.test" },
            credentialsRef = TenantFieldKeys.Reference(access.Code),
            status = "active",
            isActive = true,
        }, Ct, rig.Code);
        Assert.True(created.Status is HttpStatusCode.OK or HttpStatusCode.Created, created.Body);
        Assert.Equal(0, await rig.ScalarAsync<long>("select count(*) from itg.connectors where credentials_ref is not null", Ct));

        // The worker ignores the row (not written by the operator's mail command) and sends through the relay.
        var worker = rig.Provider(extra: s => TestMailServer.Use(s, relay.Port)).GetRequiredService<JobWorker>();
        var (_, username, _) = await h.ActiveUserAsync(rig.Code, Ct);
        await h.PostAsync(rig.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Single(relay.To(username + "@example.test"));
        Assert.False(accepted.IsCompleted); // nothing ever connected to the customer's "server"
    }

    // ------------------------------------------------------------------ finding 2 (CR-062, ruling B option a)

    private static async Task<JsonObject> UserBodyAsync(AccessHarness access, Guid caller, Guid user)
    {
        var current = await access.GetAsync(caller, $"/api/iam/users/{user}", Ct);
        Assert.True(current.Status == HttpStatusCode.OK, current.Body);
        return JsonNode.Parse(current.Body)!.AsObject();
    }

    [Fact]
    public async Task S111b2_review_2_CR_062_ADM_USR_01_the_user_api_never_changes_an_email_and_a_company_admin_cannot_change_one()
    {
        var access = await AccessHarness.ForAsync(fx);
        var (victim, victimName, _) = await h.ActiveUserAsync(access.Code, Ct);
        await access.AssignAsync(victim, await access.RoleIdAsync("tenant_admin", Ct), null, access.Today.AddDays(-1), null, Ct);
        var companyAdmin = await access.UserAsync(Ct, null, "company_admin");

        var body = await UserBodyAsync(access, companyAdmin, victim);
        var original = body["email"]!.GetValue<string>();
        body["email"] = "attacker-mailbox@evil.test";
        var put = await access.PutAsync(companyAdmin, $"/api/iam/users/{victim}", body, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, put.Status);
        Assert.Contains("email", put.Body, StringComparison.Ordinal);

        // The same address is accepted (other fields still editable), and the guarded action needs its own permission.
        var same = await UserBodyAsync(access, companyAdmin, victim);
        same["displayName"] = "Renamed by the company admin";
        Assert.Equal(HttpStatusCode.OK, (await access.PutAsync(companyAdmin, $"/api/iam/users/{victim}", same, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await access.PostAsync(companyAdmin, $"/api/iam/users/{victim}/email", new { email = "attacker-mailbox@evil.test", reason = "x" }, Ct)).Status);

        // Forgot password still mails the Tenant Admin's own address.
        await h.PostAsync(access.Code, "/api/iam/auth/password/forgot", new { login = victimName }, ct: Ct);
        Assert.Equal(original, await access.ScalarAsync<string>(
            "select recipient from ntf.outbound_messages where user_id = $1 and event_code = 'identity.password_reset' order by created_at desc limit 1", Ct, victim));
    }

    [Fact]
    public async Task S111b2_review_2_CR_062_KRN_IAM_01_an_email_change_ends_links_and_codes_removes_the_email_factor_and_tells_the_old_address()
    {
        var access = await AccessHarness.ForAsync(fx);
        var admin = await access.UserAsync(Ct, null, "tenant_admin");
        var undo = await h.SetSettingAsync(access.Code, IdentitySettings.MfaAllowedMethods, "[\"totp\",\"email\"]", null, Ct);
        try
        {
            var (user, username, tokens) = await h.ActiveUserAsync(access.Code, Ct);
            var start = await h.PostAsync(access.Code, "/api/iam/me/mfa-devices", new { kind = "email", currentPassword = IdentityHarness.Password }, tokens.Text("accessToken"), Ct);
            await h.PostAsync(access.Code, $"/api/iam/me/mfa-devices/{start.Text("deviceId")}/confirm",
                new { code = await h.EmailedCodeAsync(access.Code, user, "identity.email_code", Ct) }, tokens.Text("accessToken"), Ct);
            await h.PostAsync(access.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
            var openLink = (await h.EmailedAsync(access.Code, user, "identity.password_reset", Ct))!["link"];
            var old = username + "@example.test";

            // Refusals: no reason, not an address, the same address, another user's address or username.
            async Task<Answer> ChangeAsync(string email, string? reason = "asked by phone") =>
                await access.PostAsync(admin, $"/api/iam/users/{user}/email", new { email, reason }, Ct);
            Assert.Contains(IdentityEmailService.ReasonRequired, (await ChangeAsync("new@example.test", null)).Body, StringComparison.Ordinal);
            Assert.Contains(IdentityEmailService.EmailInvalid, (await ChangeAsync("a,b@example.test")).Body, StringComparison.Ordinal);
            Assert.Contains(IdentityEmailService.EmailUnchanged, (await ChangeAsync(old.ToUpperInvariant())).Body, StringComparison.Ordinal);
            var (_, otherName, _) = await h.ActiveUserAsync(access.Code, Ct);
            Assert.Contains(IdentityEmailService.EmailTaken, (await ChangeAsync(otherName + "@example.test")).Body, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, (await access.PostAsync(admin, $"/api/iam/users/{Guid.CreateVersion7()}/email", new { email = "n@example.test", reason = "x" }, Ct)).Status);

            var changed = await ChangeAsync("moved-" + username + "@example.test");
            Assert.True(changed.Status == HttpStatusCode.NoContent, changed.Body);
            Assert.Equal("moved-" + username + "@example.test", await access.ScalarAsync<string>("select email from iam.users where id = $1", Ct, user));

            // The open reset link and the e-mail factor are gone; the old address is told; the change is an event.
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(access.Code, "/api/iam/auth/password/reset", new { token = Token(openLink), newPassword = NewPassword }, ct: Ct)).Status);
            Assert.Equal(0, await h.CountAsync(access.Code, "select count(*) from iam.mfa_devices where user_id = $1 and kind = 'email' and status <> 'revoked'", Ct, user));
            Assert.Equal(old, await access.ScalarAsync<string>("select recipient from ntf.outbound_messages where user_id = $1 and event_code = 'identity.email_changed'", Ct, user));
            Assert.Contains(IdentityEvents.EmailChanged, await h.EventsAsync(access.Code, user, Ct));
            Assert.Equal("tokens", (await h.SignInAsync(access.Code, username, IdentityHarness.Password, Ct)).Text("outcome")); // no factor left (not required)
        }
        finally
        {
            await undo();
        }
    }

    [Fact]
    public async Task S111b2_review_2_CR_062_FR_XM_32_a_privileged_users_email_change_waits_for_a_second_administrator()
    {
        var access = await AccessHarness.ForAsync(fx);
        var first = await access.UserAsync(Ct, null, "tenant_admin");
        var second = await access.UserAsync(Ct, null, "tenant_admin");
        var target = await access.UserAsync(Ct, null, "tenant_admin");
        var before = await access.ScalarAsync<string>("select email from iam.users where id = $1", Ct, target);

        var held = await access.PostAsync(first, $"/api/iam/users/{target}/email", new { email = "held-" + before, reason = "new mailbox" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal("pending", held.Text("outcome"));
        var request = held.Text("requestId")!;
        Assert.Equal(before, await access.ScalarAsync<string>("select email from iam.users where id = $1", Ct, target));

        // Nobody approves their own request, nor a change of their own address (FR-XM-32).
        Assert.Equal(HttpStatusCode.Forbidden, (await access.PostAsync(first, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "ok" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await access.PostAsync(target, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "ok" }, Ct)).Status);

        var approved = await access.PostAsync(second, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked with the person" }, Ct);
        Assert.True(approved.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, approved.Body);
        Assert.Equal("held-" + before, await access.ScalarAsync<string>("select email from iam.users where id = $1", Ct, target));
        Assert.Equal(before, await access.ScalarAsync<string>("select recipient from ntf.outbound_messages where user_id = $1 and event_code = 'identity.email_changed'", Ct, target));
    }

    // ------------------------------------------------------------------ findings 3, 4, 6

    [Fact]
    public async Task S111b2_review_3_KRN_IAM_01_the_reset_email_of_an_automatically_locked_account_is_sent()
    {
        await using var rig = await JobRig.StartAsync(fx, "rv", Ct);
        await using var mail = new TestMailServer();
        var worker = rig.Provider(extra: s => TestMailServer.Use(s, mail.Port)).GetRequiredService<JobWorker>();
        var (user, username, _) = await h.ActiveUserAsync(rig.Code, Ct);
        for (var i = 0; i < 5; i++)
        {
            await h.SignInAsync(rig.Code, username, "Wrong-Password-" + i, Ct);
        }

        Assert.Equal("locked", await h.StatusAsync(rig.Code, user, Ct));
        await h.PostAsync(rig.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Single(mail.To(username + "@example.test"));
        Assert.Equal("sent", await rig.ScalarAsync<string>("select status from ntf.outbound_messages where user_id = $1 and event_code = 'identity.password_reset'", Ct, null, user));

        // An administrator's lock (no end time) still gets nothing, even if a message was queued earlier.
        var (held, heldName, _) = await h.ActiveUserAsync(rig.Code, Ct);
        await h.PostAsync(rig.Code, "/api/iam/auth/password/forgot", new { login = heldName }, ct: Ct);
        await rig.ExecAsync("update iam.users set status = 'locked' where id = $1", Ct, null, held);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Empty(mail.To(heldName + "@example.test"));
    }

    [Fact]
    public async Task S111b2_review_4_KRN_NTF_05_identity_emails_go_before_older_notifications_and_outside_the_budget()
    {
        await using var rig = await JobRig.StartAsync(fx, "rq", Ct);
        await using var mail = new TestMailServer();
        var worker = rig.Provider(extra: s => TestMailServer.Use(s, mail.Port)).GetRequiredService<JobWorker>();
        var undo = await h.SetSettingAsync(rig.Code, "ntf.email.per_minute", "2", null, Ct);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                await rig.ExecAsync("""
                    insert into ntf.outbound_messages (id, created_at, channel, recipient, payload, attempts, status, event_code, created_by, updated_at, updated_by, row_version)
                    values (gen_random_uuid(), $1, 'email', $2, '{"subject":"Task","body":"A task","language":"en"}'::jsonb, 0, 'queued', 'workflow.task_assigned', $3, $1, $3, 1)
                    """, Ct, null, rig.Clock.UtcNow.AddMinutes(-1), "manager" + i + "@example.test", BaselineSeed.SystemUserId);
            }

            var (_, username, _) = await h.ActiveUserAsync(rig.Code, Ct);
            await h.PostAsync(rig.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
            await rig.RunUntilIdleAsync(worker, Ct);
            Assert.Single(mail.To(username + "@example.test"));
            Assert.Equal(2, mail.Received.Count(r => r.To.StartsWith("manager", StringComparison.Ordinal))); // the budget still holds for the rest
        }
        finally
        {
            await undo();
        }
    }

    [Fact]
    public async Task S111b2_review_6_KRN_NTF_07_a_reply_to_the_mail_library_refuses_is_left_out_and_email_keeps_flowing()
    {
        await using var rig = await JobRig.StartAsync(fx, "rr", Ct);
        await using var mail = new TestMailServer();
        var worker = rig.Provider(extra: s => TestMailServer.Use(s, mail.Port)).GetRequiredService<JobWorker>();
        var undo = await h.SetSettingAsync(rig.Code, "ntf.email.reply_to", "\"hr,payroll@acme.test\"", null, Ct); // stored directly, as an older value could be
        try
        {
            var (_, username, _) = await h.ActiveUserAsync(rig.Code, Ct);
            await h.PostAsync(rig.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
            await rig.RunUntilIdleAsync(worker, Ct);
            var message = Assert.Single(mail.To(username + "@example.test"));
            Assert.Empty(message.ReplyTo);
        }
        finally
        {
            await undo();
        }
    }

    // ------------------------------------------------------------------ findings 7, 8, 9, 10

    [Fact]
    public async Task S111b2_review_7_R_003_wrong_enrolment_codes_count_towards_the_lockout()
    {
        var code = "k" + fx.Suffix;
        await fx.ProvisionAsync(code, Ct);
        var (user, username, _) = await h.ActiveUserAsync(code, Ct);
        var undo = await h.SetSettingAsync(code, IdentitySettings.MfaRequired, "true", user, Ct);
        try
        {
            h.Clock.Advance(TimeSpan.FromDays(8));
            var challenge = (await h.SignInAsync(code, username, IdentityHarness.Password, Ct)).Text("challengeToken")!;
            var start = await h.PostAsync(code, "/api/iam/auth/mfa-enrolment", new { challengeToken = challenge }, ct: Ct);
            var right = int.Parse(await h.EmailedCodeAsync(code, user, "identity.enrolment_code", Ct), System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < 5; i++)
            {
                var guess = ((right + 1 + i) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                await h.PostAsync(code, "/api/iam/auth/mfa-enrolment/confirm",
                    new { challengeToken = challenge, deviceId = start.Text("deviceId"), code = h.Code(start.Text("secret")!), emailCode = guess }, ct: Ct);
            }

            Assert.Equal("locked", await h.StatusAsync(code, user, Ct)); // threshold 5, like wrong sign-in codes
        }
        finally
        {
            await undo();
        }
    }

    [Fact]
    public async Task S111b2_review_8_KRN_IAM_01_forgot_password_and_reset_at_the_same_moment_never_fail()
    {
        var access = await AccessHarness.ForAsync(fx);
        var (user, username, _) = await h.ActiveUserAsync(access.Code, Ct);
        for (var round = 0; round < 8; round++)
        {
            await h.PostAsync(access.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
            var token = Token((await h.EmailedAsync(access.Code, user, "identity.password_reset", Ct))!["link"]);
            var forgot = h.PostAsync(access.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct);
            var reset = h.PostAsync(access.Code, "/api/iam/auth/password/reset", new { token, newPassword = "short" }, ct: Ct); // refused, so the link stays
            var answers = await Task.WhenAll(forgot, reset);
            Assert.Equal(HttpStatusCode.Accepted, answers[0].Status);
            Assert.True(answers[1].Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, $"{answers[1].Status} {answers[1].Body}");
            h.Clock.Advance(TimeSpan.FromHours(1)); // stay under the hourly limit
        }
    }

    [Fact]
    public async Task S111b2_review_9_KRN_IAM_02_an_email_factor_the_customer_no_longer_allows_does_not_strand_its_owner()
    {
        var access = await AccessHarness.ForAsync(fx);
        var (user, username, tokens) = await h.ActiveUserAsync(access.Code, Ct);
        var undo = await h.SetSettingAsync(access.Code, IdentitySettings.MfaAllowedMethods, "[\"totp\",\"email\"]", null, Ct);
        try
        {
            var start = await h.PostAsync(access.Code, "/api/iam/me/mfa-devices", new { kind = "email", currentPassword = IdentityHarness.Password }, tokens.Text("accessToken"), Ct);
            await h.PostAsync(access.Code, $"/api/iam/me/mfa-devices/{start.Text("deviceId")}/confirm",
                new { code = await h.EmailedCodeAsync(access.Code, user, "identity.email_code", Ct) }, tokens.Text("accessToken"), Ct);
            Assert.Equal("mfa_required", (await h.SignInAsync(access.Code, username, IdentityHarness.Password, Ct)).Text("outcome"));
        }
        finally
        {
            await undo();
        }

        // Only authenticators are allowed now: the person is no longer asked for a code nobody can send.
        Assert.Equal("tokens", (await h.SignInAsync(access.Code, username, IdentityHarness.Password, Ct)).Text("outcome"));
    }

    [Fact]
    public async Task S111b2_review_10_KRN_IAM_02_an_email_code_is_stored_as_a_keyed_hash()
    {
        var access = await AccessHarness.ForAsync(fx);
        var (user, _, tokens) = await h.ActiveUserAsync(access.Code, Ct);
        var undo = await h.SetSettingAsync(access.Code, IdentitySettings.MfaAllowedMethods, "[\"totp\",\"email\"]", null, Ct);
        try
        {
            var start = await h.PostAsync(access.Code, "/api/iam/me/mfa-devices", new { kind = "email", currentPassword = IdentityHarness.Password }, tokens.Text("accessToken"), Ct);
            var device = Guid.Parse(start.Text("deviceId")!);
            var code = await h.EmailedCodeAsync(access.Code, user, "identity.email_code", Ct);
            var stored = await access.ScalarAsync<string>("select token_hash from iam.one_time_tokens where user_id = $1 and purpose = 'email_code' and used_at is null", Ct, user);
            Assert.NotEqual(OpaqueTokens.Hash(device.ToString("N") + ":" + code), stored); // not the plain SHA-256 a database copy could reverse
            Assert.Equal(0, await h.CountAsync(access.Code, "select count(*) from ntf.outbound_messages where user_id = $1 and payload::text like '%' || $2 || '%'", Ct, user, code));

            // The code still works.
            var confirm = await h.PostAsync(access.Code, $"/api/iam/me/mfa-devices/{device}/confirm", new { code }, tokens.Text("accessToken"), Ct);
            Assert.True(confirm.Status == HttpStatusCode.OK, confirm.Body);
        }
        finally
        {
            await undo();
        }
    }
}
