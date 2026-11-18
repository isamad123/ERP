using System.Diagnostics;
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Notifications;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Identity;

/// <summary>
/// The identity e-mails of slice S1.11b2 (Build item 16; KRN-IAM-01, KRN-IAM-02, ADM-USR-02, R-003; ruling Q16; plan points
/// P9 to P12) through the real API pipeline and real databases: forgot password, the reset link, the e-mail code as second
/// factor, the e-mailed confirmation of a late authenticator enrolment, and the administrator's invitation and reset link.
/// Links and codes are read the way the sender reads them: from the encrypted secret of the queued e-mail.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class IdentityEmailTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string NewPassword = "Brand-New-Pass-81";
    private static readonly string[] EmailAllowed = ["totp", "email"];

    private IdentityHarness h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The identity tests' own customer (see IdentityTests), provisioned once per fixture.
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

    private Task<long> ResetMailsAsync(Guid user) =>
        h.CountAsync(A, "select count(*) from ntf.outbound_messages where user_id = $1 and event_code = 'identity.password_reset'", Ct, user);

    private async Task<string> ResetTokenAsync(Guid user)
    {
        var link = (await h.EmailedAsync(A, user, "identity.password_reset", Ct))!["link"];
        Assert.StartsWith(IdentityEmailService.ResetLinkPath, link, StringComparison.Ordinal);
        return Uri.UnescapeDataString(link[IdentityEmailService.ResetLinkPath.Length..]);
    }

    [Fact]
    public async Task S111b2_KRN_IAM_01_forgot_password_answers_the_same_for_any_account_and_the_reset_link_works_once_and_ends_the_sessions()
    {
        var (user, username, tokens) = await h.ActiveUserAsync(A, Ct);
        var (disabled, disabledName, _) = await h.ActiveUserAsync(A, Ct);
        await using (var owner = await h.OwnerAsync(A, Ct))
        {
            await IdentityHarness.ExecAsync(owner, "update iam.users set status = 'disabled' where id = $1", Ct, disabled);
        }

        // The same answer, after at least the same time, for a real account, an unknown one and a disabled one.
        foreach (var login in new[] { username, "nobody-" + Guid.NewGuid().ToString("N")[..8], disabledName, username.ToUpperInvariant() + "@EXAMPLE.TEST" })
        {
            var watch = Stopwatch.StartNew();
            var answer = await h.PostAsync(A, "/api/iam/auth/password/forgot", new { login }, ct: Ct);
            Assert.Equal(HttpStatusCode.Accepted, answer.Status);
            Assert.Equal(string.Empty, answer.Body);
            Assert.True(watch.Elapsed >= IdentityEmailService.ForgotMinimumTime - TimeSpan.FromMilliseconds(20), $"{login} answered in {watch.Elapsed}");
        }

        Assert.Equal(2, await ResetMailsAsync(user)); // by username and by e-mail address
        Assert.Equal(0, await ResetMailsAsync(disabled));
        var token = await ResetTokenAsync(user);

        // The new password must follow the policy; the link stays usable until it is used.
        var weak = await h.PostAsync(A, "/api/iam/auth/password/reset", new { token, newPassword = "short" }, ct: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);
        Assert.Contains("errors.password.too_short", weak.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, "/api/iam/auth/password/reset", new { token, newPassword = NewPassword }, ct: Ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/password/reset", new { token, newPassword = "Another-Pass-92" }, ct: Ct)).Status);

        Assert.Equal("tokens", (await h.SignInAsync(A, username, NewPassword, Ct)).Text("outcome"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SignInAsync(A, username, IdentityHarness.Password, Ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/refresh", new { refreshToken = tokens.Text("refreshToken") }, ct: Ct)).Status);
        Assert.Contains(IdentityEvents.PasswordResetLinkSent, await h.EventsAsync(A, user, Ct));
        Assert.Contains(IdentityEvents.PasswordChanged, await h.EventsAsync(A, user, Ct));
    }

    [Fact]
    public async Task S111b2_KRN_IAM_01_a_reset_link_expires_an_hour_limit_stops_more_emails_and_it_ends_an_automatic_lock_but_never_an_administrators()
    {
        var (user, username, _) = await h.ActiveUserAsync(A, Ct);
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(A, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct)).Status);
        var expired = await ResetTokenAsync(user);
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/password/reset", new { token = expired, newPassword = NewPassword }, ct: Ct)).Status);

        // At most five reset e-mails an hour (ruling Q16); more requests get the same answer and no e-mail.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(A, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct)).Status);
        }

        Assert.Equal(6, await ResetMailsAsync(user)); // one an hour ago, five now

        // An automatic lock ends with the reset; an administrator's lock stays and gets no link.
        await using (var owner = await h.OwnerAsync(A, Ct))
        {
            await IdentityHarness.ExecAsync(owner, "update iam.users set status = 'locked' where id = $1", Ct, user);
            await IdentityHarness.ExecAsync(owner, "update iam.user_credentials set failed_attempts = 5, locked_until = $2 where user_id = $1", Ct, user, h.Clock.UtcNow.AddMinutes(30));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(A, "/api/iam/auth/password/reset", new { token = await ResetTokenAsync(user), newPassword = NewPassword }, ct: Ct)).Status);
        Assert.Equal("active", await h.StatusAsync(A, user, Ct));

        var (adminLocked, lockedName, _) = await h.ActiveUserAsync(A, Ct);
        await using (var owner = await h.OwnerAsync(A, Ct))
        {
            await IdentityHarness.ExecAsync(owner, "update iam.users set status = 'locked' where id = $1", Ct, adminLocked);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(A, "/api/iam/auth/password/forgot", new { login = lockedName }, ct: Ct)).Status);
        Assert.Equal(0, await ResetMailsAsync(adminLocked));
    }

    [Fact]
    public async Task S111b2_KRN_IAM_02_the_email_code_is_accepted_once_within_its_time_and_within_the_try_limit()
    {
        var (user, username, tokens) = await h.ActiveUserAsync(A, Ct);
        var access = tokens.Text("accessToken")!;

        // Not allowed by the customer: refused.
        var refused = await h.PostAsync(A, "/api/iam/me/mfa-devices", new { kind = "email", currentPassword = IdentityHarness.Password }, access, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.mfa.method_not_allowed", refused.Body, StringComparison.Ordinal);

        var undo = await h.SetSettingAsync(A, IdentitySettings.MfaAllowedMethods, System.Text.Json.JsonSerializer.Serialize(EmailAllowed), null, Ct);
        try
        {
            var start = await h.PostAsync(A, "/api/iam/me/mfa-devices", new { kind = "email", currentPassword = IdentityHarness.Password }, access, Ct);
            Assert.True(start.Status == HttpStatusCode.OK, start.Body);
            Assert.Equal("email", start.Text("kind"));
            Assert.Null(start.Text("secret"));
            var device = start.Text("deviceId");
            Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync(A, $"/api/iam/me/mfa-devices/{device}/confirm", new { code = "000000" }, access, Ct)).Status);
            var confirm = await h.PostAsync(A, $"/api/iam/me/mfa-devices/{device}/confirm", new { code = await h.EmailedCodeAsync(A, user, "identity.email_code", Ct) }, access, Ct);
            Assert.True(confirm.Status == HttpStatusCode.OK, confirm.Body);
            Assert.Equal(10, confirm.Json.GetProperty("recoveryCodes").GetArrayLength());

            // Sign-in now asks for the second factor and names the e-mail method.
            var signIn = await h.SignInAsync(A, username, IdentityHarness.Password, Ct);
            Assert.Equal("mfa_required", signIn.Text("outcome"));
            Assert.Equal("email", signIn.Json.GetProperty("mfaMethods")[0].GetString());
            var challenge = signIn.Text("challengeToken")!;
            Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(A, "/api/iam/auth/mfa/email-code", new { challengeToken = challenge }, ct: Ct)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa/email-code", new { challengeToken = "not-a-challenge" }, ct: Ct)).Status);
            var code = await h.EmailedCodeAsync(A, user, "identity.email_code", Ct);
            var done = await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code }, ct: Ct);
            Assert.Equal("tokens", done.Text("outcome"));

            // Once only: the same code with a new challenge fails.
            var again = (await h.SignInAsync(A, username, IdentityHarness.Password, Ct)).Text("challengeToken")!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = again, code }, ct: Ct)).Status);

            // Within its time: ten minutes.
            var late = (await h.SignInAsync(A, username, IdentityHarness.Password, Ct)).Text("challengeToken")!;
            await h.PostAsync(A, "/api/iam/auth/mfa/email-code", new { challengeToken = late }, ct: Ct);
            var lateCode = await h.EmailedCodeAsync(A, user, "identity.email_code", Ct);
            h.Clock.Advance(TimeSpan.FromMinutes(9));
            var lateChallenge = (await h.SignInAsync(A, username, IdentityHarness.Password, Ct)).Text("challengeToken")!; // a code is bound to its challenge
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = lateChallenge, code = lateCode }, ct: Ct)).Status);
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = late, code = lateCode }, ct: Ct)).Status);

            Assert.Contains(IdentityEvents.EmailCodeSent, await h.EventsAsync(A, user, Ct));
            Assert.Equal(0, await h.CountAsync(A, "select count(*) from iam.outbox_messages where payload::text like '%' || $1 || '%'", Ct, code));
        }
        finally
        {
            await undo();
        }
    }

    [Fact]
    public async Task S111b2_KRN_IAM_02_after_the_set_number_of_wrong_entries_the_email_code_stops_working_and_a_new_one_is_needed()
    {
        var (user, username, tokens) = await h.ActiveUserAsync(A, Ct);
        var undoMethods = await h.SetSettingAsync(A, IdentitySettings.MfaAllowedMethods, System.Text.Json.JsonSerializer.Serialize(EmailAllowed), null, Ct);
        var undoTries = await h.SetSettingAsync(A, IdentitySettings.EmailCodeMaxAttempts, "3", null, Ct);
        try
        {
            var access = tokens.Text("accessToken")!;
            var start = await h.PostAsync(A, "/api/iam/me/mfa-devices", new { kind = "email", currentPassword = IdentityHarness.Password }, access, Ct);
            await h.PostAsync(A, $"/api/iam/me/mfa-devices/{start.Text("deviceId")}/confirm", new { code = await h.EmailedCodeAsync(A, user, "identity.email_code", Ct) }, access, Ct);

            var challenge = (await h.SignInAsync(A, username, IdentityHarness.Password, Ct)).Text("challengeToken")!;
            await h.PostAsync(A, "/api/iam/auth/mfa/email-code", new { challengeToken = challenge }, ct: Ct);
            var right = await h.EmailedCodeAsync(A, user, "identity.email_code", Ct);
            var wrong = right == "111111" ? "222222" : "111111";
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code = wrong }, ct: Ct)).Status);
            }

            Assert.Equal("active", await h.StatusAsync(A, user, Ct)); // three failures do not lock the account (threshold 5)
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code = right }, ct: Ct)).Status);

            // A new code for the same challenge works.
            Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(A, "/api/iam/auth/mfa/email-code", new { challengeToken = challenge }, ct: Ct)).Status);
            var fresh = await h.EmailedCodeAsync(A, user, "identity.email_code", Ct);
            Assert.Equal("tokens", (await h.PostAsync(A, "/api/iam/auth/mfa", new { challengeToken = challenge, code = fresh }, ct: Ct)).Text("outcome"));
        }
        finally
        {
            await undoTries();
            await undoMethods();
        }
    }

    [Fact]
    public async Task S111b2_R_003_an_authenticator_added_at_sign_in_after_the_grace_period_needs_the_code_emailed_to_the_person()
    {
        var (user, username, _) = await h.ActiveUserAsync(A, Ct);
        var undo = await h.SetSettingAsync(A, IdentitySettings.MfaRequired, "true", user, Ct);
        try
        {
            h.Clock.Advance(TimeSpan.FromDays(8));
            var challenge = (await h.SignInAsync(A, username, IdentityHarness.Password, Ct)).Text("challengeToken")!;
            var start = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment", new { challengeToken = challenge }, ct: Ct);
            Assert.True(start.Json.GetProperty("emailCodeSent").GetBoolean());
            var secret = start.Text("secret")!;
            var device = start.Text("deviceId");
            var emailCode = await h.EmailedCodeAsync(A, user, "identity.enrolment_code", Ct);
            var mail = await h.EmailedAsync(A, user, "identity.enrolment_code", Ct);
            Assert.NotNull(mail);

            // Someone who knows only the password: without (or with a wrong) e-mailed code the authenticator is not added.
            var without = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment/confirm", new { challengeToken = challenge, deviceId = device, code = h.Code(secret) }, ct: Ct);
            Assert.Equal(HttpStatusCode.BadRequest, without.Status);
            Assert.Contains("errors.mfa.email_code_invalid", without.Body, StringComparison.Ordinal);
            var wrongMail = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment/confirm", new { challengeToken = challenge, deviceId = device, code = h.Code(secret), emailCode = emailCode == "111111" ? "222222" : "111111" }, ct: Ct);
            Assert.Contains("errors.mfa.email_code_invalid", wrongMail.Body, StringComparison.Ordinal);
            Assert.Equal(0, await h.CountAsync(A, "select count(*) from iam.mfa_devices where user_id = $1 and status = 'active'", Ct, user));

            // The right e-mailed code but a wrong authenticator code: refused, and the e-mailed code stays usable.
            var wrongApp = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment/confirm", new { challengeToken = challenge, deviceId = device, code = "000000", emailCode }, ct: Ct);
            Assert.Contains("errors.mfa.code_invalid", wrongApp.Body, StringComparison.Ordinal);
            var done = await h.PostAsync(A, "/api/iam/auth/mfa-enrolment/confirm", new { challengeToken = challenge, deviceId = device, code = h.Code(secret), emailCode }, ct: Ct);
            Assert.Equal("tokens", done.Text("outcome"));
        }
        finally
        {
            await undo();
        }
    }

    [Fact]
    public async Task S111b2_ADM_USR_02_an_administrator_sends_the_invitation_and_a_reset_link_and_others_are_refused()
    {
        var access = await AccessHarness.ForAsync(fx);
        var admin = await access.UserAsync(Ct, null, "tenant_admin");
        var employee = await access.UserAsync(Ct, null, "business_user");
        var invited = Guid.CreateVersion7();
        await using (var owner = await access.OwnerAsync(null, Ct))
        {
            await AccessHarness.ExecAsync(owner, """
                insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $2 || '@invite.test', $2, 'ur', 'Asia/Karachi', false, 'invited', now(), $3, now(), $3)
                """, Ct, invited, "inv" + Guid.NewGuid().ToString("N")[..10], BaselineSeed.SystemUserId);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await access.PostAsync(employee, $"/api/iam/users/{invited}/invitation", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await access.PostAsync(admin, $"/api/iam/users/{invited}/invitation", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await access.PostAsync(admin, $"/api/iam/users/{Guid.CreateVersion7()}/invitation", null, Ct)).Status);
        Assert.Equal("errors.user.not_invited", (await access.PostAsync(admin, $"/api/iam/users/{employee}/invitation", null, Ct)).Text("title"));

        // The e-mailed link activates the account; the e-mail is in the person's language (Urdu).
        var link = (await h.EmailedAsync(access.Code, invited, "identity.invitation", Ct))!["link"];
        Assert.StartsWith(IdentityEmailService.ActivationLinkPath, link, StringComparison.Ordinal);
        await using (var owner = await access.OwnerAsync(null, Ct))
        {
            Assert.Equal("اپنا اکاؤنٹ فعال کریں", await AccessHarness.ScalarAsync<string>(owner, "select payload->>'subject' from ntf.outbound_messages where user_id = $1", Ct, invited));
        }

        var activate = await h.PostAsync(access.Code, "/api/iam/auth/activate", new { token = Uri.UnescapeDataString(link[IdentityEmailService.ActivationLinkPath.Length..]), newPassword = NewPassword }, ct: Ct);
        Assert.Equal("tokens", activate.Text("outcome"));

        // A reset link for an active person; never for a disabled one.
        Assert.Equal(HttpStatusCode.Forbidden, (await access.PostAsync(employee, $"/api/iam/users/{invited}/password-reset-link", new { reason = "asked by phone" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await access.PostAsync(admin, $"/api/iam/users/{invited}/password-reset-link", new { reason = "asked by phone" }, Ct)).Status);
        Assert.NotNull(await h.EmailedAsync(access.Code, invited, "identity.password_reset", Ct));
        await using (var owner = await access.OwnerAsync(null, Ct))
        {
            await AccessHarness.ExecAsync(owner, "update iam.users set status = 'disabled' where id = $1", Ct, invited);
        }

        Assert.Equal("errors.user.reset_not_possible", (await access.PostAsync(admin, $"/api/iam/users/{invited}/password-reset-link", new { reason = "x" }, Ct)).Text("title"));
    }

    [Fact]
    public async Task S111b2_KRN_IAM_01_KRN_NTF_07_the_reset_email_leaves_with_the_full_link_and_its_secret_is_wiped_once_sent()
    {
        await using var rig = await JobRig.StartAsync(fx, "ie", Ct);
        await using var mail = new TestMailServer();
        var worker = rig.Provider(extra: s => TestMailServer.Use(s, mail.Port)).GetRequiredService<Kernel.Infrastructure.Jobs.JobWorker>();
        var (user, username, _) = await h.ActiveUserAsync(rig.Code, Ct);
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(rig.Code, "/api/iam/auth/password/forgot", new { login = username }, ct: Ct)).Status);
        Assert.NotNull(await h.EmailedAsync(rig.Code, user, "identity.password_reset", Ct));

        // The forgot request asked for a send run; the worker sends it (no wait for the next minute).
        await rig.RunUntilIdleAsync(worker, Ct);
        var message = Assert.Single(mail.To(username + "@example.test"));
        var prefix = "https://" + TenancyFixture.HostOf(rig.Code) + IdentityEmailService.ResetLinkPath;
        var text = message.TextBody!;
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, text);
        var token = Uri.UnescapeDataString(text[(start + prefix.Length)..].Split('\n')[0].Trim());
        Assert.Null(await h.EmailedAsync(rig.Code, user, "identity.password_reset", Ct)); // wiped once sent
        Assert.Equal(HttpStatusCode.NoContent, (await h.PostAsync(rig.Code, "/api/iam/auth/password/reset", new { token, newPassword = NewPassword }, ct: Ct)).Status);
    }
}
