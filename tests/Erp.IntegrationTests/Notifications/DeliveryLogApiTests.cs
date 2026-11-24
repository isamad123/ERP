using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Notifications;

/// <summary>
/// The delivery log API of slice S1.11b2 (KRN-NTF-05, ADM-OPS-04, ADM-OPS-05, KRN-NTF-02 test send; plan point P8) in the
/// real API pipeline with the real access engine: list with filters, detail, resend, the numbers, test send to oneself,
/// company scope, and every refusal.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class DeliveryLogApiTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await AccessHarness.ForAsync(fx);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> MessageAsync(string status, string eventCode, Guid? user = null, Guid? company = null, string subject = "Approval waiting")
    {
        var id = Guid.CreateVersion7();
        await using var c = await _h.OwnerAsync(null, Ct);
        await AccessHarness.ExecAsync(c, """
            insert into ntf.outbound_messages (id, created_at, channel, recipient, payload, attempts, status, error, user_id, event_code, company_id, created_by, updated_at, updated_by)
            values ($1, $2, 'email', 'someone@mail.test', $3::jsonb, 1, $4, case when $4 = 'failed' then '550 5.1.1 no such user' end, $5, $6, $7, $8, $2, $8)
            """, Ct, id, _h.Clock.UtcNow, JsonSerializer.Serialize(new { subject, body = "Open the request.", link = "/approvals/x", language = "en" }), status,
            (object?)user ?? DBNull.Value, eventCode, (object?)company ?? DBNull.Value, BaselineSeed.SystemUserId);
        return id;
    }

    private static List<Guid> Ids(Answer answer) => [.. answer.Json.EnumerateArray().Select(m => m.GetProperty("id").GetGuid())];

    [Fact]
    public async Task S111b2_KRN_NTF_05_ADM_OPS_04_an_admin_lists_filters_opens_and_resends_and_others_are_refused()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var employee = await _h.UserAsync(Ct, null, "business_user");
        var failed = await MessageAsync("failed", "workflow.task_assigned", employee);
        var sent = await MessageAsync("sent", "workflow.task_assigned", employee);

        var list = await _h.GetAsync(admin, $"/api/ntf/deliveries?userId={employee}&status=failed", Ct);
        Assert.True(list.Status == HttpStatusCode.OK, list.Body);
        Assert.Equal([failed], Ids(list));
        var row = list.Json[0];
        Assert.Equal("550 5.1.1 no such user", row.GetProperty("error").GetString());
        Assert.Equal("Approval waiting", row.GetProperty("subject").GetString());
        Assert.True(row.GetProperty("resendable").GetBoolean());

        var detail = await _h.GetAsync(admin, $"/api/ntf/deliveries/{sent}", Ct);
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
        Assert.Equal("Open the request.", detail.Json.GetProperty("body").GetString());
        Assert.Equal("/approvals/x", detail.Json.GetProperty("link").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(admin, $"/api/ntf/deliveries/{Guid.CreateVersion7()}", Ct)).Status);

        var resent = await _h.PostAsync(admin, $"/api/ntf/deliveries/{failed}/resend", null, Ct);
        Assert.True(resent.Status == HttpStatusCode.Created, resent.Body);
        var copy = resent.Json.GetProperty("id").GetGuid();
        Assert.NotEqual(failed, copy);
        Assert.Equal("queued", resent.Json.GetProperty("status").GetString());
        var copyDetail = await _h.GetAsync(admin, $"/api/ntf/deliveries/{copy}", Ct);
        Assert.Equal(failed, copyDetail.Json.GetProperty("resendOf").GetGuid());
        Assert.Equal("failed", (await _h.GetAsync(admin, $"/api/ntf/deliveries/{failed}", Ct)).Json.GetProperty("message").GetProperty("status").GetString());

        // Refusals: a message still waiting, an identity e-mail, and people without the permissions.
        Assert.Equal("errors.notification.delivery_waiting", (await _h.PostAsync(admin, $"/api/ntf/deliveries/{copy}/resend", null, Ct)).Text("title"));
        var code = await MessageAsync("sent", "identity.password_reset", employee, subject: "Reset your password");
        var identity = await _h.PostAsync(admin, $"/api/ntf/deliveries/{code}/resend", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, identity.Status);
        Assert.Equal("errors.notification.delivery_not_resendable", identity.Text("title"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(employee, "/api/ntf/deliveries", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(employee, $"/api/ntf/deliveries/{sent}", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(employee, $"/api/ntf/deliveries/{failed}/resend", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(employee, "/api/ntf/deliveries/summary", Ct)).Status);
    }

    [Fact]
    public async Task S111b2_ADM_OPS_04_a_role_limited_to_one_company_sees_only_that_companys_messages_and_not_the_numbers()
    {
        var mine = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var limited = await _h.UserAsync(Ct, null);
        await _h.AssignAsync(limited, await _h.RoleIdAsync("tenant_admin", Ct), mine, _h.Today.AddDays(-1), null, Ct);
        var inMine = await MessageAsync("failed", "workflow.task_assigned", company: mine);
        var inOther = await MessageAsync("failed", "workflow.task_assigned", company: other);
        var noCompany = await MessageAsync("failed", "licence.read_only");

        var list = Ids(await _h.GetAsync(limited, "/api/ntf/deliveries?limit=200", Ct));
        Assert.Contains(inMine, list);
        Assert.DoesNotContain(inOther, list);
        Assert.DoesNotContain(noCompany, list);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(limited, $"/api/ntf/deliveries/{inOther}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(limited, $"/api/ntf/deliveries/{noCompany}/resend", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(limited, $"/api/ntf/deliveries/{inMine}/resend", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(limited, "/api/ntf/deliveries/summary", Ct)).Status);
    }

    [Fact]
    public async Task S111b2_ADM_OPS_05_KRN_NTF_02_the_numbers_count_waiting_and_recent_results_and_a_test_send_goes_to_the_admin_only()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var before = await _h.GetAsync(admin, "/api/ntf/deliveries/summary", Ct);
        Assert.True(before.Status == HttpStatusCode.OK, before.Body);
        await MessageAsync("failed", "workflow.task_assigned");
        await MessageAsync("queued", "workflow.task_assigned");
        var after = await _h.GetAsync(admin, "/api/ntf/deliveries/summary", Ct);
        Assert.Equal(before.Json.GetProperty("failed24h").GetInt32() + 1, after.Json.GetProperty("failed24h").GetInt32());
        Assert.Equal(before.Json.GetProperty("queued").GetInt32() + 1, after.Json.GetProperty("queued").GetInt32());

        var test = await _h.PostAsync(admin, "/api/ntf/text-templates/test-send", new { eventCode = "workflow.task_assigned", language = "ur", subject = "آزمائش", body = "قسم: {document_type}", sample = new { document_type = "leave.request" } }, Ct);
        Assert.True(test.Status == HttpStatusCode.Accepted, test.Body);
        Assert.Equal("[TEST] آزمائش", test.Json.GetProperty("subject").GetString());
        var id = test.Json.GetProperty("id").GetGuid();
        await using var c = await _h.OwnerAsync(null, Ct);
        Assert.Equal(admin, await AccessHarness.ScalarAsync<Guid>(c, "select user_id from ntf.outbound_messages where id = $1", Ct, id));
        Assert.EndsWith("@access.test", await AccessHarness.ScalarAsync<string>(c, "select recipient from ntf.outbound_messages where id = $1", Ct, id), StringComparison.Ordinal);
        Assert.Equal("قسم: leave.request", await AccessHarness.ScalarAsync<string>(c, "select payload->>'body' from ntf.outbound_messages where id = $1", Ct, id));
        // The sender is asked to run now, so the test e-mail leaves within seconds.
        Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(c, "select count(*) from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'kernel.email_send' and r.status = 'queued'", Ct));

        Assert.Equal("errors.notification.field_unknown", (await _h.PostAsync(admin, "/api/ntf/text-templates/test-send", new { eventCode = "workflow.task_assigned", language = "en", subject = "x", body = "{salary}" }, Ct)).Text("title"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(await _h.UserAsync(Ct, null, "business_user"), "/api/ntf/text-templates/test-send", new { eventCode = "workflow.task_assigned", language = "en", subject = "x", body = "y" }, Ct)).Status);
    }

    [Fact]
    public async Task S116c_FR_ADM_OPS_04_GHR_06_identity_and_salary_message_text_is_withheld_and_the_daily_summary_counts_results_per_day()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var employee = await _h.UserAsync(Ct, null, "business_user");
        var reset = await MessageAsync("sent", "identity.password_reset", employee, subject: "Reset your password");
        var slip = await MessageAsync("sent", "payroll.payslip_ready", employee, subject: "Your payslip");
        var plain = await MessageAsync("sent", "workflow.task_assigned", employee);

        foreach (var id in new[] { reset, slip })
        {
            var detail = await _h.GetAsync(admin, $"/api/ntf/deliveries/{id}", Ct);
            Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
            Assert.True(detail.Json.GetProperty("bodyWithheld").GetBoolean());
            Assert.Equal(string.Empty, detail.Json.GetProperty("body").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.Json.GetProperty("link").ValueKind);
            Assert.DoesNotContain("Open the request", detail.Body, StringComparison.Ordinal);
        }

        var open = await _h.GetAsync(admin, $"/api/ntf/deliveries/{plain}", Ct);
        Assert.False(open.Json.GetProperty("bodyWithheld").GetBoolean());
        Assert.Equal("Open the request.", open.Json.GetProperty("body").GetString());

        await MessageAsync("failed", "workflow.task_assigned", employee);
        var daily = await _h.GetAsync(admin, "/api/ntf/deliveries/daily?days=3", Ct);
        Assert.True(daily.Status == HttpStatusCode.OK, daily.Body);
        var today = daily.Json.EnumerateArray().First();
        Assert.True(today.GetProperty("sent").GetInt32() >= 3);
        Assert.True(today.GetProperty("failed").GetInt32() >= 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(employee, "/api/ntf/deliveries/daily", Ct)).Status);
    }
}
