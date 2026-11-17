#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Tenancy;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Delegation and out-of-office substitution of slice S1.10b (build item 6; ruling Q8, rulings P2 and P5) through the real
/// API pipeline, the real access engine and a real PostgreSQL. FR-KRN-WFL-05, FR-HRM-ROL-05, FR-HRM-SSP-07, FR-ADM-ROL-09,
/// FR-XM-32 (nobody approves their own request, not as a delegate).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class DelegationTests(TenancyFixture fx) : IAsyncLifetime
{
    private WorkflowRig _w = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _w = await ForAsync(fx);
        _w.H.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<Guid> CompanyAsync() => _w.H.DefaultCompanyAsync(Ct);

    /// <summary>A running expense request whose one task is assigned to <paramref name="approver"/>.</summary>
    private async Task<(Guid Instance, Guid Task)> TaskForAsync(Guid approver, Guid requester, string type = Expense, Guid? company = null)
    {
        var admin = await _w.AdminAsync(Ct);
        await _w.ActivateRouteAsync(admin, type, Route(Step("one", UserApprover(approver))), Ct);
        var values = type == Expense ? (object)new { amount = "10" } : new { };
        var instance = InstanceOf(await _w.StartAsync(requester, type, company ?? await CompanyAsync(), values, ct: Ct));
        return (instance, Assert.Single(await _w.OpenTasksAsync(instance, Ct)));
    }

    [Fact]
    public async Task FR_KRN_WFL_05_FR_HRM_ROL_05_the_delegate_sees_and_decides_the_delegators_task_and_every_record_says_acting_for()
    {
        var (sara, ali, requester) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        var (instance, task) = await TaskForAsync(sara, requester);
        Assert.DoesNotContain(task, await _w.InboxAsync(ali, Ct));

        await _w.DelegateAsync(sara, ali, Ct);
        var item = (await _w.GetAsync(ali, "/api/wf/inbox", Ct)).Json.EnumerateArray().Single(i => i.GetProperty("taskId").GetGuid() == task);
        Assert.Equal(sara, item.GetProperty("actingForUserId").GetGuid()); // "for Sara"
        Assert.Contains(task, await _w.InboxAsync(sara, Ct)); // the delegator still sees and may decide it
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(ali, $"/api/wf/tasks/{task}", Ct)).Status);

        var approved = await _w.PostAsync(ali, $"/api/wf/tasks/{task}/approve", new { comment = "for Sara" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
        Assert.Equal(ali, await _w.TaskValueAsync<Guid>(task, "acted_by", Ct));
        Assert.Equal(sara, await _w.TaskValueAsync<Guid>(task, "acting_for_user_id", Ct));
        Assert.Equal(sara, await _w.TaskValueAsync<Guid>(task, "assignee_user_id", Ct));
        Assert.Equal(sara, await _w.H.ScalarAsync<Guid>("select acting_for_user_id from wf.workflow_history where instance_id = $1 and event = 'approved'", Ct, instance));
        Assert.Equal(ali, await _w.H.ScalarAsync<Guid>("select actor_id from wf.workflow_history where instance_id = $1 and event = 'approved'", Ct, instance));
        var outcome = Assert.Single(_w.Outcomes.For(instance));
        Assert.Equal((ali, sara), (outcome.Outcome.DecidedBy, outcome.Outcome.ActingFor));
    }

    [Fact]
    public async Task FR_HRM_ROL_05_the_delegate_decides_with_the_delegators_rights_and_their_own_rights_do_not_widen_them()
    {
        // test.guarded needs org.companies.update to decide. The delegate holds it; the delegator does not.
        var (sara, ali, requester) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "tenant_admin"), await _w.UserAsync(Ct, "business_user"));
        var (_, task) = await TaskForAsync(sara, requester, Guarded);
        await _w.DelegateAsync(sara, ali, Ct);
        var refused = await _w.PostAsync(ali, $"/api/wf/tasks/{task}/approve", new { }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.workflow.no_decide_permission", refused.Text("title"));
        Assert.Equal("pending", await _w.TaskValueAsync<string>(task, "status", Ct));
    }

    [Fact]
    public async Task FR_KRN_WFL_05_FR_XM_32_outside_the_dates_after_revocation_or_for_the_delegates_own_request_nothing_is_delegated()
    {
        var (sara, ali, requester) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        var (_, task) = await TaskForAsync(sara, requester);

        // Starts tomorrow: not yet in force.
        var later = await _w.DelegateAsync(sara, ali, Ct, _w.KarachiToday().AddDays(1), _w.KarachiToday().AddDays(2));
        Assert.DoesNotContain(task, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(ali, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);

        // In force from tomorrow; after its last day it ends by itself.
        _w.H.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Contains(task, await _w.InboxAsync(ali, Ct));
        _w.H.Clock.Advance(TimeSpan.FromDays(2));
        Assert.DoesNotContain(task, await _w.InboxAsync(ali, Ct));
        _w.H.Clock.Reset();

        // Revoked: at once nothing.
        var revoke = await _w.PostAsync(sara, $"/api/iam/me/delegations/{later}/revoke", new { reason = "back early" }, Ct);
        Assert.True(revoke.Status == HttpStatusCode.OK, revoke.Body);
        var now = await _w.DelegateAsync(sara, ali, Ct);
        Assert.Contains(task, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(sara, $"/api/iam/me/delegations/{now}/revoke", new { }, Ct)).Status);
        Assert.DoesNotContain(task, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await _w.PostAsync(sara, $"/api/iam/me/delegations/{now}/revoke", new { }, Ct)).Status);

        // A request the delegate raised, or is the subject of, is never theirs to decide (FR-XM-32).
        await _w.DelegateAsync(sara, ali, Ct);
        var (_, own) = await TaskForAsync(sara, ali);
        Assert.DoesNotContain(own, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(ali, $"/api/wf/tasks/{own}/approve", new { }, Ct)).Status);
        var admin = await _w.AdminAsync(Ct);
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(sara))), Ct);
        var about = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1", employee = ali.ToString() }, ct: Ct));
        var aboutTask = Assert.Single(await _w.OpenTasksAsync(about, Ct));
        Assert.DoesNotContain(aboutTask, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(ali, $"/api/wf/tasks/{aboutTask}/approve", new { }, Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_WFL_05_delegations_are_not_passed_on_and_follow_their_document_types_and_companies()
    {
        var (sara, ali, chris, requester) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        var (_, task) = await TaskForAsync(sara, requester);
        await _w.DelegateAsync(sara, ali, Ct);
        await _w.DelegateAsync(ali, chris, Ct);
        Assert.Contains(task, await _w.InboxAsync(ali, Ct));
        Assert.DoesNotContain(task, await _w.InboxAsync(chris, Ct)); // Ali holds it only through Sara's delegation
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(chris, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);

        // Only payroll documents, or only another company: the expense task of the default company is not delegated.
        var (dana, eve) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var (_, expense) = await TaskForAsync(dana, requester);
        await _w.DelegateAsync(dana, eve, Ct, types: [Payroll]);
        Assert.DoesNotContain(expense, await _w.InboxAsync(eve, Ct));
        var (fay, gus) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var (_, other) = await TaskForAsync(fay, requester);
        await _w.DelegateAsync(fay, gus, Ct, companies: [await _w.H.CompanyAsync(Ct)]);
        Assert.DoesNotContain(other, await _w.InboxAsync(gus, Ct));
        var (hal, ivy) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var (_, matching) = await TaskForAsync(hal, requester);
        await _w.DelegateAsync(hal, ivy, Ct, types: [Expense], companies: [await CompanyAsync()]);
        Assert.Contains(matching, await _w.InboxAsync(ivy, Ct));
    }

    [Fact]
    public async Task FR_KRN_WFL_05_Q8_limits_delegate_active_not_self_dates_in_order_not_in_the_past_at_most_max_days_no_overlap()
    {
        var (sara, ali, ben) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var today = _w.KarachiToday();
        async Task RefusedAsync(object body, string key, HttpStatusCode status = HttpStatusCode.BadRequest)
        {
            var answer = await _w.PostAsync(sara, "/api/iam/me/delegations", body, Ct);
            Assert.True(answer.Status == status, answer.Body);
            Assert.Equal(key, answer.Text("title"));
        }

        await RefusedAsync(new { toUserId = sara, from = today, to = today }, "errors.delegation.to_self");
        await RefusedAsync(new { toUserId = Guid.CreateVersion7(), from = today, to = today }, "errors.delegation.to_not_active");
        await RefusedAsync(new { toUserId = ali, from = today, to = today.AddDays(-1) }, "errors.delegation.dates_order");
        await RefusedAsync(new { toUserId = ali, from = today.AddDays(-1), to = today }, "errors.delegation.start_in_past");
        await RefusedAsync(new { toUserId = ali, from = today, to = today.AddDays(90) }, "errors.delegation.too_long"); // 91 days; default 90
        await RefusedAsync(new { toUserId = ali, from = today, to = today, documentTypes = new[] { "test.nothing" } }, "errors.delegation.type_unknown");

        var created = await _w.PostAsync(sara, "/api/iam/me/delegations", new { toUserId = ali, from = today, to = today.AddDays(89), documentTypes = new[] { Expense } }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        Assert.Equal(today.AddDays(89).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), created.Text("to"));
        Assert.True(created.Json.GetProperty("inForce").GetBoolean());

        // The same documents to anyone for overlapping days: refused; other documents: allowed.
        await RefusedAsync(new { toUserId = ben, from = today.AddDays(10), to = today.AddDays(12) }, "errors.delegation.overlap", HttpStatusCode.Conflict);
        await RefusedAsync(new { toUserId = ben, from = today.AddDays(10), to = today.AddDays(12), documentTypes = new[] { Expense } }, "errors.delegation.overlap", HttpStatusCode.Conflict);
        var payroll = await _w.PostAsync(sara, "/api/iam/me/delegations", new { toUserId = ben, from = today.AddDays(10), to = today.AddDays(12), documentTypes = new[] { Payroll } }, Ct);
        Assert.True(payroll.Status == HttpStatusCode.Created, payroll.Body);

        // Both sides see it in "my delegations"; the change log has the creation.
        var mine = await _w.GetAsync(sara, "/api/iam/me/delegations", Ct);
        Assert.Equal(2, mine.Json.GetProperty("given").GetArrayLength());
        var received = (await _w.GetAsync(ali, "/api/iam/me/delegations", Ct)).Json.GetProperty("received");
        Assert.Equal(sara, Assert.Single(received.EnumerateArray()).GetProperty("fromUserId").GetGuid());
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.delegations' and entity_id = $1 and action = 'create'", Ct, created.Json.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task FR_ADM_ROL_09_an_administrator_delegates_for_anyone_a_self_service_user_cannot_and_the_generated_list_follows_scope()
    {
        var (admin, sara, ali, other) = (await _w.AdminAsync(Ct), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var today = _w.KarachiToday();
        var byAdmin = await _w.PostAsync(admin, "/api/iam/delegations", new { fromUserId = sara, toUserId = ali, from = today, to = today.AddDays(3), reason = "sick leave" }, Ct);
        Assert.True(byAdmin.Status == HttpStatusCode.Created, byAdmin.Body);
        Assert.Equal(sara, byAdmin.Json.GetProperty("fromUserId").GetGuid());

        // A self-service user (scope own) may not act for someone else, nor revoke another's delegation.
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(other, "/api/iam/delegations", new { fromUserId = sara, toUserId = other, from = today, to = today }, Ct)).Status);
        var id = byAdmin.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(other, $"/api/iam/delegations/{id}/revoke", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(other, $"/api/iam/me/delegations/{id}/revoke", new { }, Ct)).Status);

        // The generated list: the administrator sees it, the delegator sees their own, another user sees nothing of it.
        Assert.Contains(id.ToString(), (await _w.GetAsync(admin, "/api/iam/delegations?limit=500", Ct)).Body, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), (await _w.GetAsync(sara, "/api/iam/delegations", Ct)).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(id.ToString(), (await _w.GetAsync(other, "/api/iam/delegations", Ct)).Body, StringComparison.Ordinal);

        var revoked = await _w.PostAsync(admin, $"/api/iam/delegations/{id}/revoke", new { reason = "back" }, Ct);
        Assert.True(revoked.Status == HttpStatusCode.OK, revoked.Body);
        Assert.Equal("revoked", revoked.Text("status"));
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.delegations' and entity_id = $1 and action = 'status'", Ct, id));

        // Without the permission at all: 403 on every delegation endpoint.
        var nobody = await _w.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.GetAsync(nobody, "/api/iam/me/delegations", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(nobody, "/api/iam/me/delegations", new { toUserId = ali, from = today, to = today }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(nobody, $"/api/iam/me/delegations/{id}/revoke", new { }, Ct)).Status);
    }
}
