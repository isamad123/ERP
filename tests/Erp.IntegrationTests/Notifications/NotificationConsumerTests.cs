using System.Text.Json;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Notifications;

/// <summary>
/// The notification consumer of slice S1.11b1 (Build items 8 to 10): outbox events of the kernel become in-app items for the
/// right people, in their language, once, with their link. The events are written as the kernel writes them (the payloads of
/// <c>docs/registers/contracts.md</c>) and handled by the real event dispatcher and consumer on a real database.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class NotificationConsumerTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;
    private EventDispatcher _dispatcher = null!;

    private static readonly string[] AdminAudience = ["tenant_admin"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _rig = await JobRig.StartAsync(fx, "nc", Ct);
        _dispatcher = _rig.Provider().GetRequiredService<EventDispatcher>();
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<Guid> UserAsync(string language = "en", string status = "active", string? role = null)
    {
        var id = Guid.CreateVersion7();
        var name = "nu" + Guid.NewGuid().ToString("N")[..12];
        await _rig.ExecAsync("""
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@notify.test', $2, $3, 'Asia/Karachi', false, $4, now(), $5, now(), $5)
            """, Ct, null, id, name, language, status, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        if (role is not null)
        {
            await _rig.ExecAsync("""
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, r.id, null, 'test', current_date - 1, now(), $4, now(), $4 from iam.roles r where r.code = $3
                """, Ct, null, Guid.CreateVersion7(), id, role, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        }

        return id;
    }

    private async Task<Guid> EventAsync(string schema, string type, object payload)
    {
        var id = Guid.CreateVersion7();
#pragma warning disable CA2100 // test SQL with a fixed schema name
        await _rig.ExecAsync($"insert into {schema}.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload) values ($1, $2, $3, 'test', $1, $4::jsonb)",
            Ct, null, id, _rig.Clock.UtcNow, type, JsonSerializer.Serialize(payload));
#pragma warning restore CA2100
        return id;
    }

    private async Task DispatchAsync()
    {
        foreach (var outcome in await _dispatcher.DispatchOnceAsync(_rig.Code, Ct))
        {
            Assert.True(outcome.Error is null, outcome.Error);
        }
    }

    private Task<long> ItemsAsync(Guid user) => _rig.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1", Ct, null, user);

    private static object Assigned(Guid[] recipients, string documentType = "leave.request", Guid? company = null, string link = "/approvals/task-1") => new
    {
        instance_id = Guid.CreateVersion7(),
        task_id = Guid.CreateVersion7(),
        document_type = documentType,
        document_id = Guid.CreateVersion7(),
        company_id = company ?? Guid.CreateVersion7(),
        requester_user_id = Guid.CreateVersion7(),
        recipients,
        outcome = (string?)null,
        link,
    };

    [Fact]
    public async Task S111b1_HRM_NTF_03_KRN_LOC_07_KRN_WFL_08_a_task_assigned_to_a_manager_whose_language_is_urdu_gives_an_urdu_item_with_the_working_link()
    {
        var manager = await UserAsync("ur");
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([manager]));
        await DispatchAsync();

        var item = (await _rig.ScalarAsync<string>("select title || '|' || coalesce(link, '') || '|' || event_group || '|' || event_code from ntf.in_app_items where user_id = $1", Ct, null, manager)).Split('|');
        Assert.Equal("ایک درخواست آپ کی منظوری کی منتظر ہے", item[0]);
        Assert.Equal("/approvals/task-1", item[1]);
        Assert.Equal("approvals", item[2]);
        Assert.Equal("workflow.task_assigned", item[3]);
    }

    [Fact]
    public async Task S111b1_KRN_LOC_07_a_person_whose_language_has_no_text_gets_the_english_text()
    {
        var person = await UserAsync("ur");
        await _rig.ExecAsync("update ntf.templates set status = 'retired' where event_code = 'workflow.task_assigned' and language = 'ur'", Ct);
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([person]));
        await DispatchAsync();

        Assert.Equal("A request is waiting for your approval", await _rig.ScalarAsync<string>("select title from ntf.in_app_items where user_id = $1", Ct, null, person));
    }

    [Fact]
    public async Task S111b1_KRN_NTF_05_one_event_never_gives_the_same_person_two_items_even_when_it_is_handled_again()
    {
        var approver = await UserAsync();
        var e = await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([approver, approver]));
        await DispatchAsync();
        Assert.Equal(1, await ItemsAsync(approver));

        // The delivery record is lost and the event read again: the item's own key still stops a second one.
        await _rig.ExecAsync("delete from wf.inbox_messages where message_id = $1", Ct, null, e);
        await _rig.ExecAsync("update wf.outbox_messages set published_at = null where id = $1", Ct, null, e);
        await DispatchAsync();
        Assert.Equal(1, await ItemsAsync(approver));
    }

    [Fact]
    public async Task S111b1_HRM_NTF_11_an_escalation_tells_the_next_level_and_the_former_assignee_and_a_disabled_person_nobody()
    {
        var next = await UserAsync();
        var former = await UserAsync();
        var gone = await UserAsync(status: "disabled");
        await EventAsync("wf", "kernel.workflow.task_escalated", Assigned([next, former, gone]));
        await DispatchAsync();

        Assert.Equal(1, await ItemsAsync(next));
        Assert.Equal(1, await ItemsAsync(former));
        Assert.Equal(0, await ItemsAsync(gone));
        Assert.Equal("workflow.task_escalated", await _rig.ScalarAsync<string>("select event_code from ntf.in_app_items where user_id = $1", Ct, null, next));
    }

    [Fact]
    public async Task S111b1_HRM_NTF_02_a_request_that_is_rejected_tells_the_requester_and_a_cancelled_one_only_the_waiting_approver()
    {
        var requester = await UserAsync();
        var approver = await UserAsync();
        var payload = new { instance_id = Guid.CreateVersion7(), document_type = "leave.request", document_id = Guid.CreateVersion7(), company_id = Guid.CreateVersion7(), requester_user_id = requester, recipients = new[] { requester }, outcome = "rejected", link = "/requests/1" };
        await EventAsync("wf", "kernel.workflow.instance_completed", payload);
        await EventAsync("wf", "kernel.workflow.instance_completed", payload with { outcome = "cancelled", recipients = new[] { approver } });
        await DispatchAsync();

        Assert.Equal("Your request was rejected", await _rig.ScalarAsync<string>("select title from ntf.in_app_items where user_id = $1", Ct, null, requester));
        Assert.Equal(1, await ItemsAsync(requester));
        Assert.Equal("workflow.request_cancelled", await _rig.ScalarAsync<string>("select event_code from ntf.in_app_items where user_id = $1", Ct, null, approver));
    }

    [Fact]
    public async Task S111b1_HRM_NTF_02_a_licence_that_turns_read_only_tells_the_tenant_admins_in_plain_words()
    {
        var admin = await UserAsync(role: "tenant_admin");
        var other = await UserAsync();
        await EventAsync("meta", "kernel.licence.state_changed", new { version = 2, to = "read_only", from = "grace", expires_at = "2026-11-30", next_state = "locked", next_change = "2026-12-15" });
        await EventAsync("meta", "kernel.licence.limit_warning", new { limit = "users.named", used = 45, max = 50, percent = 90, warning_percent = 90 });
        await DispatchAsync();

        var titles = await _rig.ScalarAsync<string>("select string_agg(title, '|' order by title) from ntf.in_app_items where user_id = $1", Ct, null, admin);
        Assert.Equal("A licence limit is almost reached|Your system is now read-only", titles);
        Assert.Equal("45 of 50 are in use (90%). Ask for a higher limit before it is reached.", await _rig.ScalarAsync<string>("select body from ntf.in_app_items where event_code = 'licence.limit_warning' and user_id = $1", Ct, null, admin));
        Assert.Equal(0, await ItemsAsync(other));
    }

    [Fact]
    public async Task S111b1_KRN_JOB_06_an_alert_reaches_the_job_owner_and_the_tenant_admins_and_only_for_its_own_kind()
    {
        var owner = await UserAsync();
        var admin = await UserAsync(role: "tenant_admin");
        await EventAsync("job", "kernel.alert.raised", new { kind = "job_failed", key = "k1", severity = "error", details = new { run_id = Guid.CreateVersion7(), code = "hrm.import", error = "disk full" }, owner_user_id = owner, audience = AdminAudience, mandatory = true });
        await DispatchAsync();

        Assert.Equal("The job hrm.import failed: disk full", await _rig.ScalarAsync<string>("select body from ntf.in_app_items where user_id = $1", Ct, null, owner));
        Assert.Equal(1, await ItemsAsync(admin));
        Assert.Equal("alert.job_failed", await _rig.ScalarAsync<string>("select event_code from ntf.in_app_items where user_id = $1", Ct, null, admin));
    }

    private async Task<Guid> RuleAsync(string eventCode, Guid? company, string audienceJson, string? conditionJson = null, string channels = "{in_app}")
    {
        var id = Guid.CreateVersion7();
        await _rig.ExecAsync("""
            insert into ntf.subscriptions (id, event_code, audience, channels, company_id, is_mandatory, condition, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3::jsonb, $4::text[], $5, false, $6::jsonb, now(), $7, now(), $7)
            """, Ct, null, id, eventCode, audienceJson, channels, company, conditionJson, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        return id;
    }

    [Fact]
    public async Task S111b1_KRN_NTF_03_a_rule_adds_the_hr_audience_only_for_its_company_and_only_when_its_condition_holds()
    {
        var hr = await UserAsync();
        var approver = await UserAsync();
        var (companyA, companyB) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await RuleAsync("workflow.task_escalated", companyA, $$"""{"users":["{{hr}}"]}""");
        await EventAsync("wf", "kernel.workflow.task_escalated", Assigned([approver], company: companyB));
        await DispatchAsync();
        Assert.Equal(0, await ItemsAsync(hr));

        await EventAsync("wf", "kernel.workflow.task_escalated", Assigned([approver], company: companyA));
        await DispatchAsync();
        Assert.Equal(1, await ItemsAsync(hr));
        Assert.Equal(2, await ItemsAsync(approver));

        // A condition on the event's fields: only leave requests.
        var watcher = await UserAsync();
        await RuleAsync("workflow.task_assigned", null, $$"""{"users":["{{watcher}}"]}""", """{"field":"document_type","op":"eq","value":"leave.request"}""");
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([approver], documentType: "loan.request"));
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([approver], documentType: "leave.request"));
        await DispatchAsync();
        Assert.Equal(1, await ItemsAsync(watcher));
    }

    [Fact]
    public async Task S111b1_KRN_NTF_03_a_rule_names_a_role_a_group_or_a_line_manager_and_a_person_in_two_ways_gets_one_item()
    {
        var holder = await UserAsync(role: "approver");
        var member = await UserAsync();
        var group = Guid.CreateVersion7();
        await _rig.ExecAsync("""
            insert into iam.user_groups (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, '{"en":"Probe group"}', now(), $3, now(), $3)
            """, Ct, null, group, "g" + Guid.NewGuid().ToString("N")[..8], Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        await _rig.ExecAsync("insert into iam.user_group_members (group_id, user_id) values ($1, $2)", Ct, null, group, member);
        await RuleAsync("workflow.task_assigned", null, $$"""{"roles":["approver"],"groups":["{{group}}"],"relationships":["line_manager"],"users":["{{holder}}"]}""");
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([Guid.CreateVersion7()]));
        await DispatchAsync();

        Assert.Equal(1, await ItemsAsync(holder));
        Assert.Equal(1, await ItemsAsync(member));
    }
}
