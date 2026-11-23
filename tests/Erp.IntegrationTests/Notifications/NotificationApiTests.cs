using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Notifications;

namespace Erp.IntegrationTests.Notifications;

/// <summary>
/// The notification API of slice S1.11b1 (Build items 9 to 11 and 13) in the real API pipeline with the real access engine:
/// the person's own items, preferences and quiet hours; texts with versions, the field check, back to product text and
/// preview; audience rules; and who may do what. Every refusal is tested.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class NotificationApiTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;
    private string _code = null!;

    /// <summary>One customer per test: two random characters could repeat in a run, and a second test would then share the first one's customer.</summary>
    private static int _next;

    private static readonly string[] Sms = ["sms"];
    private static readonly string[] ApproverRole = ["approver"];
    private static readonly string[] InApp = ["in_app"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _code = await _h.ProvisionAsync("na" + (Interlocked.Increment(ref _next) & 0xff).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> ScalarAsync<T>(string sql, params object?[] values)
    {
        await using var c = await _h.OwnerAsync(_code, Ct);
        return await AccessHarness.ScalarAsync<T>(c, sql, Ct, values);
    }

    private async Task<Guid> ItemAsync(Guid user, string title, string group = "approvals", DateTimeOffset? at = null)
    {
        var id = Guid.CreateVersion7();
        await using var c = await _h.OwnerAsync(_code, Ct);
        await AccessHarness.ExecAsync(c, """
            insert into ntf.in_app_items (id, user_id, event_code, title, body, event_group, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'workflow.task_assigned', $3, 'Body of ' || $3, $4, $5, $2, $5, $2)
            """, Ct, id, user, title, group, at ?? _h.Clock.UtcNow);
        return id;
    }

    [Fact]
    public async Task S111b1_HRM_NTF_09_a_person_lists_searches_reads_and_marks_only_their_own_items()
    {
        var me = await _h.UserAsync(Ct, _code, "business_user");
        var other = await _h.UserAsync(Ct, _code, "business_user");
        var first = await ItemAsync(me, "Leave request waiting", at: _h.Clock.UtcNow.AddMinutes(-5));
        await ItemAsync(me, "Loan decided", "my_requests", _h.Clock.UtcNow);
        var theirs = await ItemAsync(other, "Not for me");

        var all = await _h.GetAsync(me, "/api/ntf/centre/items", Ct, _code);
        Assert.True(all.Status == HttpStatusCode.OK, all.Body);
        Assert.Equal(2, all.Json.GetProperty("total").GetInt32());
        Assert.Equal(2, all.Json.GetProperty("unread").GetInt32());
        Assert.Equal("Loan decided", all.Json.GetProperty("items")[0].GetProperty("title").GetString()); // newest first
        Assert.DoesNotContain("Not for me", all.Body, StringComparison.Ordinal);

        Assert.Equal(1, (await _h.GetAsync(me, "/api/ntf/centre/items?search=LEAVE", Ct, _code)).Json.GetProperty("total").GetInt32());
        Assert.Equal(1, (await _h.GetAsync(me, "/api/ntf/centre/items?group=my_requests", Ct, _code)).Json.GetProperty("total").GetInt32());
        Assert.Equal(0, (await _h.GetAsync(me, "/api/ntf/centre/items?search=100%25", Ct, _code)).Json.GetProperty("total").GetInt32()); // % is text, not a wildcard

        Assert.Equal(HttpStatusCode.NoContent, (await _h.PostAsync(me, $"/api/ntf/centre/items/{first}/read", null, Ct, _code)).Status);
        Assert.Equal(1, (await _h.GetAsync(me, "/api/ntf/centre/unread-count", Ct, _code)).Json.GetProperty("unread").GetInt32());
        Assert.Equal(1, (await _h.GetAsync(me, "/api/ntf/centre/items?unread=true", Ct, _code)).Json.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await _h.PostAsync(me, $"/api/ntf/centre/items/{first}/unread", null, Ct, _code)).Status);

        // Nobody reads or marks another person's item: it is simply not found.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(me, $"/api/ntf/centre/items/{theirs}/read", null, Ct, _code)).Status);
        Assert.False(await ScalarAsync<bool>("select read_at is not null from ntf.in_app_items where id = $1", theirs));

        var marked = await _h.PostAsync(me, "/api/ntf/centre/items/read-all", null, Ct, _code);
        Assert.Equal(2, marked.Json.GetProperty("marked").GetInt32());
        Assert.Equal(0, (await _h.GetAsync(me, "/api/ntf/centre/unread-count", Ct, _code)).Json.GetProperty("unread").GetInt32());
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1 and read_at is null", other));
    }

    [Fact]
    public async Task S111b1_KRN_NTF_04_a_person_switches_email_off_per_group_sets_quiet_hours_and_cannot_switch_off_the_centre()
    {
        var me = await _h.UserAsync(Ct, _code, "business_user");
        var start = await _h.GetAsync(me, "/api/ntf/centre/preferences", Ct, _code);
        Assert.True(start.Status == HttpStatusCode.OK, start.Body);
        var preferences = start.Json.GetProperty("preferences").EnumerateArray().ToList();
        Assert.Equal(8, preferences.Count);
        Assert.True(preferences.Single(p => p.GetProperty("eventGroup").GetString() == "approvals" && p.GetProperty("channel").GetString() == "email").GetProperty("enabled").GetBoolean()); // e-mail is on by default
        Assert.True(preferences.Single(p => p.GetProperty("eventGroup").GetString() == "system" && p.GetProperty("channel").GetString() == "email").GetProperty("mandatory").GetBoolean());
        Assert.False(start.Json.GetProperty("quietHours").GetProperty("enabled").GetBoolean());

        var updated = await _h.PutAsync(me, "/api/ntf/centre/preferences", new
        {
            preferences = new[] { new { eventGroup = "my_requests", channel = "email", enabled = false } },
            quietHours = new { enabled = true, from = "21:30", to = "06:45" },
        }, Ct, _code);
        Assert.True(updated.Status == HttpStatusCode.OK, updated.Body);
        Assert.False(updated.Json.GetProperty("preferences").EnumerateArray().Single(p => p.GetProperty("eventGroup").GetString() == "my_requests" && p.GetProperty("channel").GetString() == "email").GetProperty("enabled").GetBoolean());
        Assert.Equal("21:30", (await _h.GetAsync(me, "/api/ntf/centre/preferences", Ct, _code)).Json.GetProperty("quietHours").GetProperty("from").GetString());

        var centreOff = await _h.PutAsync(me, "/api/ntf/centre/preferences", new { preferences = new[] { new { eventGroup = "approvals", channel = "in_app", enabled = false } } }, Ct, _code);
        Assert.Equal("errors.notification.mandatory_preference", centreOff.Text("title"));
        var badGroup = await _h.PutAsync(me, "/api/ntf/centre/preferences", new { preferences = new[] { new { eventGroup = "everything", channel = "email", enabled = false } } }, Ct, _code);
        Assert.Equal(HttpStatusCode.BadRequest, badGroup.Status);
        var badHours = await _h.PutAsync(me, "/api/ntf/centre/preferences", new { quietHours = new { enabled = true, from = "7pm", to = "06:45" } }, Ct, _code);
        Assert.Equal("errors.notification.quiet_hours_invalid", badHours.Text("title"));
        var sameHours = await _h.PutAsync(me, "/api/ntf/centre/preferences", new { quietHours = new { enabled = true, from = "08:00", to = "08:00" } }, Ct, _code);
        Assert.Equal("errors.notification.quiet_hours_invalid", sameHours.Text("title"));
    }

    [Fact]
    public async Task S111b1_KRN_NTF_02_HRM_NTF_14_a_customer_edit_is_a_new_version_back_to_product_text_works_and_a_product_update_never_overwrites_the_edit()
    {
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        var edit = await _h.PostAsync(admin, "/api/ntf/text-templates", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en", subject = "Please decide", body = "Open the request and decide." }, Ct, _code);
        Assert.True(edit.Status == HttpStatusCode.Created, edit.Body);
        Assert.Equal(2, edit.Json.GetProperty("version").GetInt32());
        Assert.Equal("tenant", edit.Json.GetProperty("source").GetString());
        Assert.Equal("active", await ScalarAsync<string>("select status from ntf.templates where event_code = 'workflow.task_assigned' and channel = 'in_app' and language = 'en' and version = 2"));
        Assert.Equal("retired", await ScalarAsync<string>("select status from ntf.templates where event_code = 'workflow.task_assigned' and channel = 'in_app' and language = 'en' and version = 1"));
        Assert.Equal(2, await ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'ntf.templates' and actor_user_id = $1", admin));

        // A later product text replaces version 1 only; the customer's edit stays the active text.
        var product = NotificationCatalog.Parse(NotificationCatalog.KernelJson).Select(s => s.Code == "workflow.task_assigned"
            ? s with { Texts = new Dictionary<string, NotificationTextSpec> { ["en"] = new("Approval needed", "A new product text."), ["ur"] = s.Texts["ur"] } }
            : s).ToList();
        await using (var c = await _h.OwnerAsync(_code, Ct))
        {
            Assert.Equal(2, await NotificationSetup.EnsureAsync(c, null, product, _h.Clock.UtcNow, Ct)); // in-app and e-mail text (S1.11b2: the e-mail reuses it)
            Assert.Equal(0, await NotificationSetup.EnsureAsync(c, null, product, _h.Clock.UtcNow, Ct)); // idempotent
        }

        Assert.Equal("A new product text.", await ScalarAsync<string>("select body from ntf.templates where event_code = 'workflow.task_assigned' and channel = 'in_app' and language = 'en' and version = 1"));
        Assert.Equal("Open the request and decide.", await ScalarAsync<string>("select body from ntf.templates where event_code = 'workflow.task_assigned' and channel = 'in_app' and language = 'en' and status = 'active'"));

        var back = await _h.PostAsync(admin, "/api/ntf/text-templates/restore", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en" }, Ct, _code);
        Assert.True(back.Status == HttpStatusCode.OK, back.Body);
        Assert.Equal("product", back.Json.GetProperty("source").GetString());
        Assert.Equal("active", await ScalarAsync<string>("select status from ntf.templates where event_code = 'workflow.task_assigned' and channel = 'in_app' and language = 'en' and version = 1"));
        Assert.Equal("retired", await ScalarAsync<string>("select status from ntf.templates where event_code = 'workflow.task_assigned' and channel = 'in_app' and language = 'en' and version = 2"));

        var list = await _h.GetAsync(admin, "/api/ntf/text-templates?eventCode=workflow.task_assigned", Ct, _code);
        Assert.Equal(5, list.Json.GetArrayLength()); // in-app: product en, customer en (now retired) and product ur; e-mail (S1.11b2): product en and ur
    }

    [Fact]
    public async Task S111b1_GHR_06_a_text_with_a_field_the_event_does_not_offer_is_refused_and_a_preview_shows_the_text_in_the_language()
    {
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        var unknown = await _h.PostAsync(admin, "/api/ntf/text-templates", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en", subject = "Hello", body = "Salary is {salary}" }, Ct, _code);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal("errors.notification.field_unknown", unknown.Text("title"));
        Assert.Equal(0, await ScalarAsync<long>("select count(*) from ntf.templates where source = 'tenant'"));
        Assert.Equal("errors.notification.event_unknown", (await _h.PostAsync(admin, "/api/ntf/text-templates", new { eventCode = "nothing.here", channel = "in_app", language = "en", subject = "x", body = "y" }, Ct, _code)).Text("title"));
        Assert.Equal("errors.notification.language_unknown", (await _h.PostAsync(admin, "/api/ntf/text-templates", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "fr", subject = "x", body = "y" }, Ct, _code)).Text("title"));
        Assert.Equal("errors.notification.template_empty", (await _h.PostAsync(admin, "/api/ntf/text-templates", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en", subject = " ", body = "y" }, Ct, _code)).Text("title"));

        var preview = await _h.PostAsync(admin, "/api/ntf/text-templates/preview", new { eventCode = "licence.limit_warning", channel = "in_app", language = "ur", subject = "حد", body = "{used} / {max} ({percent}%)", sample = new { used = 4500 } }, Ct, _code);
        Assert.True(preview.Status == HttpStatusCode.OK, preview.Body);
        Assert.Equal("4,500 / 1,250 (1,250%)", preview.Text("body"));
        // Every event has product e-mail texts from S1.11b2; an identity e-mail has no in-app text, so there is none to go back to.
        Assert.Equal("errors.notification.no_product_text", (await _h.PostAsync(admin, "/api/ntf/text-templates/restore", new { eventCode = "identity.email_code", channel = "in_app", language = "en" }, Ct, _code)).Text("title"));
    }

    [Fact]
    public async Task S111b1_KRN_NTF_03_an_administrator_adds_changes_and_removes_rules_and_every_change_is_audited()
    {
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        var watcher = await _h.UserAsync(Ct, _code, "business_user");
        var rule = new
        {
            name = "Tell HR",
            eventCode = "workflow.task_escalated",
            condition = new { field = "document_type", op = "eq", value = "leave.request" },
            audience = new { users = new[] { watcher }, roles = ApproverRole },
            channels = new[] { "in_app", "email" },
            mandatory = false,
        };
        var created = await _h.PostAsync(admin, "/api/ntf/rules", rule, Ct, _code);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        var version = created.Json.GetProperty("rowVersion").GetInt64();
        Assert.Equal("document_type", created.Json.GetProperty("condition").GetProperty("field").GetString());
        Assert.Single(created.Json.GetProperty("audience").GetProperty("users").EnumerateArray());

        var bad = await _h.PostAsync(admin, "/api/ntf/rules", rule with { condition = new { field = "salary", op = "eq", value = "1" } }, Ct, _code);
        Assert.Equal("errors.notification.condition_invalid", bad.Text("title"));
        Assert.Equal("errors.notification.audience_unknown", (await _h.PostAsync(admin, "/api/ntf/rules", rule with { audience = new { users = new[] { Guid.CreateVersion7() }, roles = Array.Empty<string>() } }, Ct, _code)).Text("title"));
        Assert.Equal("errors.notification.audience_empty", (await _h.PostAsync(admin, "/api/ntf/rules", rule with { audience = new { users = Array.Empty<Guid>(), roles = Array.Empty<string>() } }, Ct, _code)).Text("title"));
        Assert.Equal("errors.notification.channel_unknown", (await _h.PostAsync(admin, "/api/ntf/rules", rule with { channels = Sms }, Ct, _code)).Text("title"));
        Assert.Equal("errors.notification.event_unknown", (await _h.PostAsync(admin, "/api/ntf/rules", rule with { eventCode = "nothing.here" }, Ct, _code)).Text("title"));

        var changed = await _h.PutAsync(admin, $"/api/ntf/rules/{id}", new { rule.name, rule.eventCode, rule.audience, channels = InApp, mandatory = true, rowVersion = version }, Ct, _code);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.True(changed.Json.GetProperty("mandatory").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(admin, $"/api/ntf/rules/{id}", new { rule.name, rule.eventCode, rule.audience, rule.channels, mandatory = false, rowVersion = version }, Ct, _code)).Status);

        Assert.Single((await _h.GetAsync(admin, "/api/ntf/rules", Ct, _code)).Json.EnumerateArray());
        var newVersion = changed.Json.GetProperty("rowVersion").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await _h.DeleteAsync(admin, $"/api/ntf/rules/{id}?rowVersion={version}", Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(admin, $"/api/ntf/rules/{id}?rowVersion={newVersion}", Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.DeleteAsync(admin, $"/api/ntf/rules/{id}?rowVersion={newVersion}", Ct, _code)).Status);
        Assert.Equal(3, await ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'ntf.subscriptions' and entity_id = $1", id));
    }

    [Fact]
    public async Task S111b1_HRM_NTF_02_the_catalogue_names_each_event_with_its_group_and_fields_and_ordinary_users_cannot_administer_notifications()
    {
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        var user = await _h.UserAsync(Ct, _code, "business_user");

        var catalogue = await _h.GetAsync(admin, "/api/ntf/catalogue", Ct, _code);
        Assert.True(catalogue.Status == HttpStatusCode.OK, catalogue.Body);
        var assigned = catalogue.Json.EnumerateArray().Single(e => e.GetProperty("code").GetString() == "workflow.task_assigned");
        Assert.Equal("approvals", assigned.GetProperty("group").GetString());
        Assert.True(assigned.GetProperty("audited").GetBoolean());
        Assert.Contains(assigned.GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "document_type");
        Assert.True(catalogue.Json.EnumerateArray().Single(e => e.GetProperty("code").GetString() == "alert.job_failed").GetProperty("mandatory").GetBoolean());

        foreach (var path in new[] { "/api/ntf/catalogue", "/api/ntf/text-templates", "/api/ntf/rules" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, path, Ct, _code)).Status);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(user, "/api/ntf/text-templates", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en", subject = "x", body = "y" }, Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(user, "/api/ntf/rules", new { eventCode = "workflow.task_assigned", audience = new { roles = ApproverRole }, channels = InApp }, Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(user, "/api/ntf/text-templates/restore", new { eventCode = "workflow.task_assigned", channel = "in_app", language = "en" }, Ct, _code)).Status);

        // The generated endpoints only read: no writes through them.
        Assert.Contains((await _h.PostAsync(admin, "/api/ntf/in-app-items", new { }, Ct, _code)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains((await _h.PostAsync(admin, "/api/ntf/subscriptions", new { }, Ct, _code)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        var someone = await _h.UserAsync(Ct, _code);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(someone, "/api/ntf/centre/items", Ct, _code)).Status); // no role: no centre
    }
}
