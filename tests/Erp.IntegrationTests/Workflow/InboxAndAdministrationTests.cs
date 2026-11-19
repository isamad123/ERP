#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Queue claim, bulk approval, inbox filters and counts, "my requests" and administration of slice S1.10b (build items 8 to
/// 10; rulings Q4, Q12, Q13, Q14, P1, P7) through the real API pipeline, the real access engine and a real PostgreSQL.
/// FR-KRN-WFL-02 (queue with claim), FR-KRN-WFL-08 (inbox, bulk approval), FR-HRM-SSP-02, FR-ADM-USR-03, FR-XM-29, FR-XM-32.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class InboxAndAdministrationTests(TenancyFixture fx) : IAsyncLifetime
{
    private WorkflowRig _w = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _w = await ForAsync(fx);
        _w.H.Clock.Reset();
    }

    public ValueTask DisposeAsync()
    {
        _w.H.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private Task<Guid> CompanyAsync() => _w.H.DefaultCompanyAsync(Ct);

    private static string BulkStep(string key, string approver, bool bulk) =>
        $$"""{"key": "{{key}}", "name": {"en": "{{key}}", "ur": "مرحلہ"}, "approver": {{approver}}, "bulk_approval": {{(bulk ? "true" : "false")}}}""";

    // ------------------------------------------------------------------ queue claim (KRN-WFL-02, ruling Q4)

    [Fact]
    public async Task FR_KRN_WFL_02_Q4_one_holder_claims_a_queue_task_the_other_cannot_take_or_decide_it_until_it_is_released()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, first, second) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, first, second);
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("queue", RoleApprover(role.Code))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "5" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        // Both claim at the same moment: exactly one wins, the other is told it was taken.
        var answers = await Task.WhenAll(_w.PostAsync(first, $"/api/wf/tasks/{task}/claim", null, Ct), _w.PostAsync(second, $"/api/wf/tasks/{task}/claim", null, Ct));
        Assert.Single(answers, a => a.Status == HttpStatusCode.OK);
        var loser = Assert.Single(answers, a => a.Status == HttpStatusCode.Conflict);
        Assert.Equal("errors.workflow.task_claimed", loser.Text("title"));
        var (winner, other) = answers[0].Status == HttpStatusCode.OK ? (first, second) : (second, first);
        Assert.Equal(winner, await _w.TaskValueAsync<Guid>(task, "assignee_user_id", Ct));
        Assert.Contains(task, await _w.InboxAsync(winner, Ct));
        Assert.DoesNotContain(task, await _w.InboxAsync(other, Ct));
        Assert.Equal("errors.workflow.task_claimed", (await _w.PostAsync(other, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Text("title"));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(other, $"/api/wf/tasks/{task}/release", null, Ct)).Status);

        // A question waiting keeps it with the claimer; once answered it can be given back, and the other holder takes it.
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(winner, $"/api/wf/tasks/{task}/ask", new { question = "Receipt?" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _w.PostAsync(winner, $"/api/wf/tasks/{task}/release", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(requester, $"/api/wf/instances/{instance}/answer", new { answer = "Attached" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(winner, $"/api/wf/tasks/{task}/release", null, Ct)).Status);
        Assert.Null(await _w.TaskTimeAsync(task, "claimed_at", Ct));
        Assert.Contains(task, await _w.InboxAsync(other, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(other, $"/api/wf/tasks/{task}/claim", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(other, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
        var events = (await _w.HistoryAsync(instance, Ct)).Select(h => h.Event).ToList();
        Assert.Contains("claimed", events);
        Assert.Contains("released", events);

        // The requester, even holding the role, can never claim their own request (FR-XM-32).
        var mine = await _w.DecidingRoleAsync(Ct, requester, first);
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("queue", RoleApprover(mine.Code))), Ct);
        var own = Assert.Single(await _w.OpenTasksAsync(InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "5" }, ct: Ct)), Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(requester, $"/api/wf/tasks/{own}/claim", null, Ct)).Status);
    }

    // ------------------------------------------------------------------ bulk approval (KRN-WFL-08, ruling Q12)

    [Fact]
    public async Task FR_KRN_WFL_08_Q12_bulk_approval_decides_only_where_type_and_step_allow_it_with_a_result_per_task_and_a_size_limit()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(BulkStep("one", UserApprover(approver), bulk: true)), Ct);
        var a = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var b = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "2" }, ct: Ct));
        await _w.ActivateRouteAsync(admin, Expense, Route(BulkStep("one", UserApprover(approver), bulk: false)), Ct);
        var c = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "3" }, ct: Ct));
        await _w.MayDecideGuardedAsync(approver, Ct); // a route names only people who can decide (review S1.10a finding 2)
        await _w.ActivateRouteAsync(admin, Guarded, Route(Step("one", UserApprover(approver))), Ct); // type without bulk approval
        var d = InstanceOf(await _w.StartAsync(requester, Guarded, await CompanyAsync(), new { }, ct: Ct));
        var tasks = new List<Guid>();
        foreach (var instance in new[] { a, b, c, d })
        {
            tasks.Add(Assert.Single(await _w.OpenTasksAsync(instance, Ct)));
        }

        var inbox = (await _w.GetAsync(approver, "/api/wf/inbox", Ct)).Json.EnumerateArray().ToDictionary(i => i.GetProperty("taskId").GetGuid(), i => i.GetProperty("bulkApproval").GetBoolean());
        Assert.Equal([true, true, false, false], tasks.Select(t => inbox[t]));

        var unknown = Guid.CreateVersion7();
        var bulk = await _w.PostAsync(approver, "/api/wf/tasks/bulk-approve", new { taskIds = tasks.Append(unknown).ToArray(), comment = "month end" }, Ct);
        Assert.True(bulk.Status == HttpStatusCode.OK, bulk.Body);
        Assert.Equal(2, bulk.Json.GetProperty("done").GetInt32());
        Assert.Equal(3, bulk.Json.GetProperty("refused").GetInt32());
        var results = bulk.Json.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("taskId").GetGuid(), r => (r.GetProperty("outcome").GetString(), r.TryGetProperty("error", out var e) ? e.GetString() : null));
        Assert.Equal(("approved", null), results[tasks[0]]);
        Assert.Equal(("approved", null), results[tasks[1]]);
        Assert.Equal(("refused", "errors.workflow.bulk_not_allowed"), results[tasks[2]]);
        Assert.Equal(("refused", "errors.workflow.bulk_not_allowed"), results[tasks[3]]);
        Assert.Equal(("refused", "errors.workflow.task_not_found"), results[unknown]);
        Assert.Equal(["approved", "approved", "running", "running"], new[] { await _w.StatusAsync(a, Ct), await _w.StatusAsync(b, Ct), await _w.StatusAsync(c, Ct), await _w.StatusAsync(d, Ct) });
        Assert.Contains("\"bulk\": true", Assert.Single(await _w.HistoryAsync(a, Ct), h => h.Event == "approved").Details, StringComparison.Ordinal);

        // At most wf.bulk_approval.max_items per call (the setting, changed through the settings service).
        Assert.Equal("errors.workflow.bulk_empty", (await _w.PostAsync(approver, "/api/wf/tasks/bulk-approve", new { taskIds = Array.Empty<Guid>() }, Ct)).Text("title"));
        var set = await _w.PutAsync(admin, "/api/cfg/settings/wf.bulk_approval.max_items", new { layer = "tenant", value = 2, reason = "test" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        try
        {
            var tooMany = await _w.PostAsync(approver, "/api/wf/tasks/bulk-approve", new { taskIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
            Assert.Equal("errors.workflow.bulk_too_many", tooMany.Text("title"));
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(admin, "/api/cfg/settings/wf.bulk_approval.max_items/reset", new { layer = "tenant", reason = "test" }, Ct)).Status);
        }
    }

    // ------------------------------------------------------------------ inbox filters, counts, my requests (KRN-WFL-08, HRM-SSP-02, ruling Q12)

    [Fact]
    public async Task FR_KRN_WFL_08_FR_HRM_SSP_02_Q12_inbox_filters_counts_with_change_approvals_and_my_requests()
    {
        var admin = await _w.AdminAsync(Ct);
        var (alice, bob, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("check", UserApprover(approver))), Ct);
        await _w.MayDecideGuardedAsync(approver, Ct); // a route names only people who can decide (review S1.10a finding 2)
        await _w.ActivateRouteAsync(admin, Guarded, Route(Step("look", UserApprover(approver))), Ct);
        var early = InstanceOf(await _w.StartAsync(alice, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var earlyTask = Assert.Single(await _w.OpenTasksAsync(early, Ct));
        _w.H.Clock.Advance(TimeSpan.FromDays(10));
        var fromBob = InstanceOf(await _w.StartAsync(bob, Expense, await CompanyAsync(), new { amount = "2" }, ct: Ct));
        var bobTask = Assert.Single(await _w.OpenTasksAsync(fromBob, Ct));
        _w.H.Clock.Advance(TimeSpan.FromMinutes(1)); // the test clock stands still: one minute apart keeps the order "received" defined
        var guarded = InstanceOf(await _w.StartAsync(alice, Guarded, await CompanyAsync(), new { }, ct: Ct));
        var guardedTask = Assert.Single(await _w.OpenTasksAsync(guarded, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{bobTask}/ask", new { question = "Which client?" }, Ct)).Status);

        Assert.Equal([earlyTask, bobTask, guardedTask], await _w.InboxAsync(approver, Ct));
        Assert.Equal([bobTask], await _w.InboxAsync(approver, Ct, $"?requesterUserId={bob}"));
        Assert.Equal([guardedTask], await _w.InboxAsync(approver, Ct, $"?documentType={Guarded}"));
        Assert.Equal([earlyTask, bobTask, guardedTask], await _w.InboxAsync(approver, Ct, "?module=test"));
        Assert.Empty(await _w.InboxAsync(approver, Ct, "?module=hrm"));
        Assert.Equal([bobTask], await _w.InboxAsync(approver, Ct, "?waiting=true"));
        Assert.Equal([earlyTask], await _w.InboxAsync(approver, Ct, "?overdue=true"));
        Assert.Equal([guardedTask], await _w.InboxAsync(approver, Ct, "?step=look"));
        Assert.Equal([bobTask, guardedTask], await _w.InboxAsync(approver, Ct, "?receivedFrom=" + Uri.EscapeDataString(_w.H.Clock.UtcNow.AddMinutes(-1).ToString("O"))));
        Assert.Equal(earlyTask, (await _w.InboxAsync(approver, Ct, "?sort=due"))[0]);
        Assert.Equal(HttpStatusCode.BadRequest, (await _w.GetAsync(approver, "/api/wf/inbox?sort=size", Ct)).Status);

        var counts = await _w.GetAsync(approver, "/api/wf/inbox/counts", Ct);
        Assert.True(counts.Status == HttpStatusCode.OK, counts.Body);
        Assert.Equal(3, counts.Json.GetProperty("total").GetInt32());
        Assert.Equal(1, counts.Json.GetProperty("overdue").GetInt32());
        var byType = counts.Json.GetProperty("documentTypes").EnumerateArray().ToDictionary(t => t.GetProperty("documentType").GetString()!, t => t.GetProperty("count").GetInt32());
        Assert.Equal(2, byType[Expense]);
        Assert.Equal(1, byType[Guarded]);
        Assert.Equal(0, counts.Json.GetProperty("changeApprovals").GetInt32()); // an approver decides no changes

        // Administrators also see the pending maker-checker requests of other people as one group.
        var adminBefore = (await _w.GetAsync(admin, "/api/wf/inbox/counts", Ct)).Json.GetProperty("changeApprovals").GetInt32();
        await _w.H.RowAsync("iam.maker_checker_requests", Guid.Empty, new Dictionary<string, object?> { ["status"] = "pending", ["requested_by"] = alice, ["decided_by"] = null, ["decided_at"] = null, ["created_at"] = _w.H.Clock.UtcNow }, Ct);
        Assert.Equal(adminBefore + 1, (await _w.GetAsync(admin, "/api/wf/inbox/counts", Ct)).Json.GetProperty("changeApprovals").GetInt32());

        // My requests: Bob sees his request with its open task and the question waiting for him; Alice does not see Bob's.
        var bobs = await _w.GetAsync(bob, "/api/wf/my-requests", Ct);
        Assert.True(bobs.Status == HttpStatusCode.OK, bobs.Body);
        var mine = Assert.Single(bobs.Json.EnumerateArray());
        Assert.Equal(fromBob, mine.GetProperty("id").GetGuid());
        Assert.Equal(approver, Assert.Single(mine.GetProperty("currentTasks").EnumerateArray()).GetProperty("assigneeUserId").GetGuid());
        Assert.Equal("Which client?", Assert.Single(mine.GetProperty("questions").EnumerateArray()).GetProperty("question").GetString());
        Assert.DoesNotContain(fromBob.ToString(), (await _w.GetAsync(alice, "/api/wf/my-requests", Ct)).Body, StringComparison.Ordinal);
        Assert.Equal(2, (await _w.GetAsync(alice, "/api/wf/my-requests", Ct)).Json.GetArrayLength());
        Assert.Equal(0, (await _w.GetAsync(alice, "/api/wf/my-requests?status=approved", Ct)).Json.GetArrayLength());
    }

    [Fact]
    public async Task FR_KRN_ENT_03_Q11_tasks_of_an_unlicensed_document_type_are_not_listed_counted_or_decided()
    {
        var (approver, requester) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        var (instance, task, company) = (Guid.CreateVersion7(), Guid.CreateVersion7(), await CompanyAsync());
        var version = await _w.H.ScalarAsync<Guid>("""
            select v.id from wf.workflow_versions v join wf.workflow_definitions d on d.id = v.definition_id where d.document_type = $1 order by v.version limit 1
            """, Ct, Unlicensed);
        await _w.H.ExecAsync("""
            insert into wf.workflow_instances (id, company_id, version_id, document_type, document_id, requester_user_id, started_at, context, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, now(), '{"values": {}}', 'running', now(), $6, now(), $6)
            """, Ct, instance, company, version, Unlicensed, Guid.CreateVersion7(), requester);
        await _w.H.ExecAsync("""
            insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, status, due_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'admin', $3, 'pending', now() - interval '1 day', now(), $3, now(), $3)
            """, Ct, task, instance, approver);

        Assert.DoesNotContain(task, await _w.InboxAsync(approver, Ct));
        Assert.DoesNotContain(Unlicensed, (await _w.GetAsync(approver, "/api/wf/inbox/counts", Ct)).Body, StringComparison.Ordinal);
        var bulk = await _w.PostAsync(approver, "/api/wf/tasks/bulk-approve", new { taskIds = new[] { task } }, Ct);
        Assert.Equal("errors.workflow.task_not_found", bulk.Json.GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal("pending", await _w.TaskValueAsync<string>(task, "status", Ct));

        // The FR-XM-29 query still counts it: final settlement must not miss an approval because of the licence.
        var open = await _w.InCustomerScopeAsync(sp => sp.GetRequiredService<IWorkflowQueries>().OpenApprovalsOfAsync(approver, null, Ct), Ct);
        Assert.Contains(open.TasksToDecide, t => t.TaskId == task);
    }

    // ------------------------------------------------------------------ administration (ADM-USR-03, FR-XM-29, rulings Q13, P1, P7)

    [Fact]
    public async Task FR_ADM_USR_03_P7_disabling_a_user_reports_their_open_tasks_and_an_administrator_reassigns_them_all_in_one_call()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, leaving, successor) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(leaving))), Ct);
        var first = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var second = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "2" }, ct: Ct));
        var successorsOwn = InstanceOf(await _w.StartAsync(successor, Expense, await CompanyAsync(), new { amount = "3" }, ct: Ct));

        var disabled = await _w.PostAsync(admin, $"/api/iam/users/{leaving}/status", new { status = "disabled", reason = "left the company" }, Ct);
        Assert.True(disabled.Status == HttpStatusCode.OK, disabled.Body);
        Assert.Equal(3, disabled.Json.GetProperty("openWorkflowTasks").GetInt32());
        var locked = await _w.PostAsync(admin, $"/api/iam/users/{successor}/status", new { status = "locked", reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, locked.Status); // other changes answer as before
        Assert.Equal(HttpStatusCode.NoContent, (await _w.PostAsync(admin, $"/api/iam/users/{successor}/status", new { status = "active", reason = "test" }, Ct)).Status);

        var open = await _w.GetAsync(admin, $"/api/wf/users/{leaving}/open-tasks", Ct);
        Assert.True(open.Status == HttpStatusCode.OK, open.Body);
        Assert.Equal(3, open.Json.GetArrayLength());

        // The requester cannot receive their own request's task: refused for that task, the others go on (review finding 9).
        Assert.Equal("errors.workflow.reason_required", (await _w.PostAsync(admin, $"/api/wf/users/{leaving}/reassign-open-tasks", new { toUserId = successor }, Ct)).Text("title"));
        var moved = await _w.PostAsync(admin, $"/api/wf/users/{leaving}/reassign-open-tasks", new { toUserId = successor, reason = "left the company" }, Ct);
        Assert.True(moved.Status == HttpStatusCode.OK, moved.Body);
        Assert.Equal(2, moved.Json.GetProperty("done").GetInt32());
        Assert.Equal(1, moved.Json.GetProperty("refused").GetInt32());
        var ownTask = Assert.Single(await _w.OpenTasksAsync(successorsOwn, Ct));
        var refusal = Assert.Single(moved.Json.GetProperty("results").EnumerateArray(), r => r.GetProperty("outcome").GetString() == "refused");
        Assert.Equal(ownTask, refusal.GetProperty("taskId").GetGuid());
        Assert.Equal("errors.workflow.assignee_excluded", refusal.GetProperty("error").GetString());
        Assert.Equal(leaving, await _w.TaskValueAsync<Guid>(ownTask, "assignee_user_id", Ct));
        Assert.Equal(ownTask, Assert.Single((await _w.GetAsync(admin, $"/api/wf/users/{leaving}/open-tasks", Ct)).Json.EnumerateArray()).GetProperty("taskId").GetGuid());
        foreach (var instance in new[] { first, second })
        {
            var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
            Assert.Equal(successor, await _w.TaskValueAsync<Guid>(task, "assignee_user_id", Ct));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _w.GetAsync(successor, $"/api/wf/users/{leaving}/open-tasks", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(successor, $"/api/wf/users/{leaving}/reassign-open-tasks", new { toUserId = successor, reason = "x" }, Ct)).Status);
    }

    [Fact]
    public async Task FR_XM_29_the_open_approvals_query_lists_tasks_to_decide_and_requests_raised_by_or_about_the_person()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, subject, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1", employee = subject.ToString() }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        var queries = (Func<Guid, Task<OpenApprovals>>)(user => _w.InCustomerScopeAsync(sp => sp.GetRequiredService<IWorkflowQueries>().OpenApprovalsOfAsync(user, null, Ct), Ct));

        Assert.Equal(task, Assert.Single((await queries(approver)).TasksToDecide).TaskId);
        Assert.True(Assert.Single((await queries(requester)).OpenRequests).IsRequester);
        Assert.False(Assert.Single((await queries(subject)).OpenRequests).IsRequester);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.False((await queries(approver)).Any);
        Assert.False((await queries(requester)).Any);
    }

    [Fact]
    public async Task FR_ADM_USR_03_Q13_the_monitor_cancel_and_administrator_reassign_follow_the_callers_companies()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, auditor) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "auditor"));
        var (home, elsewhere) = (await CompanyAsync(), await _w.H.CompanyAsync(Ct));
        var companyAdmin = await _w.UserAsync(Ct);
        await _w.H.AssignAsync(companyAdmin, await _w.H.RoleIdAsync("company_admin", Ct), home, _w.H.Today.AddDays(-1), null, Ct);

        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(approver))), Ct);
        var inHome = InstanceOf(await _w.StartAsync(requester, Expense, home, new { amount = "1" }, ct: Ct));
        var inOther = InstanceOf(await _w.StartAsync(requester, Expense, elsewhere, new { amount = "2" }, ct: Ct));

        async Task<HashSet<Guid>> MonitorAsync(Guid user)
        {
            var answer = await _w.GetAsync(user, "/api/wf/instances?limit=500", Ct);
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
            return [.. answer.Json.EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
        }

        // Tenant Admin and Auditor: every company. Company Admin: its company only (P1), also through the generated read.
        // The requester (scope own): their own requests. An approver who raised nothing: none.
        Assert.Superset(new HashSet<Guid> { inHome, inOther }, await MonitorAsync(admin));
        Assert.Superset(new HashSet<Guid> { inHome, inOther }, await MonitorAsync(auditor));
        var companyView = await MonitorAsync(companyAdmin);
        Assert.Contains(inHome, companyView);
        Assert.DoesNotContain(inOther, companyView);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(companyAdmin, $"/api/wf/instances/{inOther}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(companyAdmin, $"/api/wf/workflow-instances/{inOther}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(companyAdmin, $"/api/wf/workflow-instances/{inHome}", Ct)).Status);
        Assert.Equal(new HashSet<Guid> { inHome, inOther }, await MonitorAsync(requester));
        Assert.DoesNotContain(inHome, await MonitorAsync(approver));
        var detail = await _w.GetAsync(companyAdmin, $"/api/wf/instances/{inHome}", Ct);
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
        Assert.Single(detail.Json.GetProperty("tasks").EnumerateArray());

        // Reassign: the Company Admin within its company only; the Auditor never.
        var homeTask = Assert.Single(await _w.OpenTasksAsync(inHome, Ct));
        var otherTask = Assert.Single(await _w.OpenTasksAsync(inOther, Ct));
        var colleague = await _w.UserAsync(Ct, "approver");
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(companyAdmin, $"/api/wf/tasks/{otherTask}/reassign", new { toUserId = colleague, reason = "cover" }, Ct)).Status);
        var reassigned = await _w.PostAsync(companyAdmin, $"/api/wf/tasks/{homeTask}/reassign", new { toUserId = colleague, reason = "cover" }, Ct);
        Assert.True(reassigned.Status == HttpStatusCode.OK, reassigned.Body);
        Assert.Equal(companyAdmin, await _w.TaskValueAsync<Guid>(homeTask, "acted_by", Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(auditor, $"/api/wf/tasks/{otherTask}/reassign", new { toUserId = colleague, reason = "x" }, Ct)).Status);

        // Cancel: Tenant Admin with a reason; the module hears "cancelled"; the requester's own route is withdraw.
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(companyAdmin, $"/api/wf/instances/{inHome}/cancel", new { reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(requester, $"/api/wf/instances/{inOther}/cancel", new { reason = "x" }, Ct)).Status);
        Assert.Equal("errors.workflow.reason_required", (await _w.PostAsync(admin, $"/api/wf/instances/{inOther}/cancel", new { }, Ct)).Text("title"));
        var cancelled = await _w.PostAsync(admin, $"/api/wf/instances/{inOther}/cancel", new { reason = "duplicate" }, Ct);
        Assert.True(cancelled.Status == HttpStatusCode.OK, cancelled.Body);
        Assert.Equal("cancelled", await _w.StatusAsync(inOther, Ct));
        Assert.Equal(WorkflowOutcomes.Cancelled, Assert.Single(_w.Outcomes.For(inOther)).Outcome.Outcome);
        Assert.Empty(await _w.OpenTasksAsync(inOther, Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await _w.PostAsync(admin, $"/api/wf/instances/{inOther}/cancel", new { reason = "again" }, Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_IAM_10_Q14_every_new_workflow_endpoint_refuses_a_user_without_its_permission()
    {
        var nobody = await _w.UserAsync(Ct); // signed in, no role
        var auditor = await _w.UserAsync(Ct, "auditor"); // reads everything, decides and changes nothing
        var (id, user) = (Guid.CreateVersion7(), await _w.UserAsync(Ct, "approver"));
        var calls = new (string Method, string Path, object? Body)[]
        {
            ("GET", "/api/wf/inbox/counts", null),
            ("GET", "/api/wf/my-requests", null),
            ("POST", $"/api/wf/tasks/{id}/claim", null),
            ("POST", $"/api/wf/tasks/{id}/release", null),
            ("POST", "/api/wf/tasks/bulk-approve", new { taskIds = new[] { id } }),
            ("GET", "/api/wf/instances", null),
            ("GET", $"/api/wf/instances/{id}", null),
            ("POST", $"/api/wf/instances/{id}/cancel", new { reason = "x" }),
            ("GET", $"/api/wf/users/{user}/open-tasks", null),
            ("POST", $"/api/wf/users/{user}/reassign-open-tasks", new { toUserId = user, reason = "x" }),
            ("GET", "/api/iam/me/delegations", null),
            ("POST", "/api/iam/me/delegations", new { toUserId = user }),
            ("POST", $"/api/iam/me/delegations/{id}/revoke", new { }),
            ("POST", "/api/iam/delegations", new { fromUserId = user, toUserId = user }),
            ("POST", $"/api/iam/delegations/{id}/revoke", new { }),
        };
        foreach (var (method, path, body) in calls)
        {
            var answer = method == "GET" ? await _w.GetAsync(nobody, path, Ct) : await _w.PostAsync(nobody, path, body, Ct);
            Assert.True(answer.Status == HttpStatusCode.Forbidden, $"{method} {path}: {answer.Status} {answer.Body}");
        }

        // The auditor reads requests but cannot decide, claim, cancel, reassign or delegate for others.
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(auditor, "/api/wf/instances", Ct)).Status);
        foreach (var (method, path, body) in calls.Where(c => c.Method == "POST" && !c.Path.StartsWith("/api/iam/me/", StringComparison.Ordinal)))
        {
            var answer = await _w.PostAsync(auditor, path, body, Ct);
            Assert.True(answer.Status == HttpStatusCode.Forbidden, $"auditor {method} {path}: {answer.Status} {answer.Body}");
        }
    }

    [Fact]
    public async Task FR_XM_32_Q13_a_task_nobody_could_be_found_for_is_listed_and_an_administrator_assigns_it_but_never_to_the_requester()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, colleague) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var empty = await _w.DecidingRoleAsync(Ct, requester); // only the requester holds it
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("only", RoleApprover(empty.Code))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Contains(await _w.HistoryAsync(instance, Ct), h => h.Event == "cannot_assign");

        var listed = await _w.GetAsync(admin, "/api/wf/instances?cannotAssign=true&limit=500", Ct);
        Assert.Contains(listed.Json.EnumerateArray(), i => i.GetProperty("id").GetGuid() == instance && i.GetProperty("cannotAssign").GetBoolean());
        Assert.Equal("errors.workflow.assignee_excluded", (await _w.PostAsync(admin, $"/api/wf/tasks/{task}/reassign", new { toUserId = requester, reason = "x" }, Ct)).Text("title"));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(admin, $"/api/wf/tasks/{task}/reassign", new { toUserId = colleague, reason = "nobody else" }, Ct)).Status);
        var assigned = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(colleague, $"/api/wf/tasks/{assigned}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }
}
