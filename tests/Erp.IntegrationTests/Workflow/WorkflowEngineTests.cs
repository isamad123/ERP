#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Workflow;
using Npgsql;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Slice S1.10a through the real API pipeline, the real access engine and a real PostgreSQL: routing with conditions and
/// approver kinds, nobody approving their own request (also in the database), decisions and their history, the outcome in
/// the deciding transaction, versions, and concurrency. FR-KRN-WFL-01, 02, 03, 07, 08, 09, 10; FR-XM-32; FR-HRM-ROL-04;
/// FR-HRM-LVE-05; spec 02 section 14 AC (with a test document type: no purchase requisitions in 1.0).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class WorkflowEngineTests(TenancyFixture fx) : IAsyncLifetime
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

    private const string HeadApprover = """{"kind": "department_head"}""";
    private const string AboveThreshold = """{"field": "amount", "op": "gt", "value": "1000.00"}""";

    // ------------------------------------------------------------------ spec 02 section 14 AC

    [Fact]
    public async Task FR_KRN_WFL_01_02_03_Spec02_WFL_AC_above_the_threshold_routes_to_the_department_head_then_a_second_role_with_the_full_history()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, head, finance) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, finance);
        _w.Directory.Heads[requester] = [head];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("head", HeadApprover, AboveThreshold), Step("finance", RoleApprover(role.Code))), Ct);
        var company = await CompanyAsync();

        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "5000.00", category = "travel" }, ct: Ct));
        var headTask = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Contains(headTask, await _w.InboxAsync(head, Ct));
        Assert.DoesNotContain(headTask, await _w.InboxAsync(finance, Ct));
        var item = (await _w.GetAsync(head, "/api/wf/inbox", Ct)).Json.EnumerateArray().Single(i => i.GetProperty("taskId").GetGuid() == headTask);
        Assert.Equal("Expense claim of 5000.00", item.GetProperty("title").GetProperty("en").GetString());
        Assert.Equal($"/test/expenses/{item.GetProperty("documentId").GetGuid()}", item.GetProperty("link").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(head, $"/api/wf/tasks/{headTask}/approve", new { comment = "fine" }, Ct)).Status);
        var queueTask = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Contains(queueTask, await _w.InboxAsync(finance, Ct)); // the role's queue: any holder may take it
        Assert.Equal(role.Id, await _w.H.ScalarAsync<Guid>("select assignee_role_id from wf.workflow_tasks where id = $1", Ct, queueTask));

        var decided = await _w.PostAsync(finance, $"/api/wf/tasks/{queueTask}/approve", new { }, Ct);
        Assert.True(decided.Status == HttpStatusCode.OK, decided.Body);
        Assert.Equal("approved", decided.Text("status"));
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
        Assert.Equal(finance, await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1 and claimed_at is not null", Ct, queueTask));

        // The full decision history, with who acted.
        var history = await _w.HistoryAsync(instance, Ct);
        Assert.Equal(["started", "task_assigned", "approved", "task_assigned", "approved", "completed"], history.Select(h => h.Event));
        Assert.Equal(head, history[2].Actor);
        Assert.Equal(finance, history[4].Actor);
        var detail = await _w.GetAsync(head, $"/api/wf/tasks/{headTask}", Ct);
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Equal(6, detail.Json.GetProperty("history").GetArrayLength());
        Assert.Contains("fine", detail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_WFL_03_a_step_whose_condition_is_false_is_skipped_and_the_history_says_why_a_step_without_condition_always_applies()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, head, finance) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, finance);
        _w.Directory.Heads[requester] = [head];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("head", HeadApprover, AboveThreshold), Step("finance", RoleApprover(role.Code))), Ct);

        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1000.00" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal("finance", await _w.H.ScalarAsync<string>("select step_key from wf.workflow_tasks where id = $1", Ct, task));
        var skipped = Assert.Single(await _w.HistoryAsync(instance, Ct), h => h.Event == "step_skipped");
        Assert.Contains("\"step\": \"head\"", skipped.Details, StringComparison.Ordinal);
        Assert.Contains("condition_not_met", skipped.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_XM_32_Spec02_WFL_AC_the_department_head_step_is_skipped_when_the_department_head_is_the_requester()
    {
        var admin = await _w.AdminAsync(Ct);
        var (headRequester, finance) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, finance);
        _w.Directory.Heads[headRequester] = [headRequester];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("head", HeadApprover, AboveThreshold), Step("finance", RoleApprover(role.Code))), Ct);

        var instance = InstanceOf(await _w.StartAsync(headRequester, Expense, await CompanyAsync(), new { amount = "5000" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal("finance", await _w.H.ScalarAsync<string>("select step_key from wf.workflow_tasks where id = $1", Ct, task));
        Assert.Contains(await _w.HistoryAsync(instance, Ct), h => h.Event == "step_skipped" && h.Details.Contains("approver_excluded", StringComparison.Ordinal));
        Assert.DoesNotContain(task, await _w.InboxAsync(headRequester, Ct));
    }

    // ------------------------------------------------------------------ nobody approves their own request (FR-XM-32, KRN-WFL-09, HRM-ROL-04)

    [Fact]
    public async Task FR_XM_32_FR_KRN_WFL_09_FR_HRM_ROL_04_requester_and_subject_never_decide_not_from_the_queue_and_not_by_reassignment()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, subject, other) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, requester, subject, other);
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("queue", RoleApprover(role.Code))), Ct);

        // The requester enters an expense on behalf of the subject (the employee): both are excluded.
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50", employee = subject.ToString() }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.DoesNotContain(task, await _w.InboxAsync(requester, Ct));
        Assert.DoesNotContain(task, await _w.InboxAsync(subject, Ct));
        Assert.Contains(task, await _w.InboxAsync(other, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(requester, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(subject, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(subject, $"/api/wf/tasks/{task}", Ct)).Status);

        var toRequester = await _w.PostAsync(other, $"/api/wf/tasks/{task}/reassign", new { toUserId = requester, reason = "cover" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, toRequester.Status);
        Assert.Contains("errors.workflow.assignee_excluded", toRequester.Body, StringComparison.Ordinal);
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));
        Assert.Contains(subject, await _w.H.ScalarAsync<Guid[]>("select subject_user_ids from wf.workflow_instances where id = $1", Ct, instance));
    }

    [Fact]
    public async Task FR_XM_32_the_database_refuses_a_requester_or_subject_as_assignee_or_decider_and_a_change_of_the_requester_even_when_the_engine_is_bypassed()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, subject, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("named", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, [subject], ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        await using var c = await _w.H.OwnerAsync(null, Ct);
        foreach (var (sql, who) in new (string, Guid)[]
        {
            ("update wf.workflow_tasks set assignee_user_id = $2 where id = $1", requester),
            ("update wf.workflow_tasks set assignee_user_id = $2 where id = $1", subject),
            ("update wf.workflow_tasks set acted_by = $2, status = 'approved' where id = $1", requester),
            ("insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, status, created_at, created_by, updated_at, updated_by) select gen_random_uuid(), instance_id, 'x', $2, 'pending', now(), $2, now(), $2 from wf.workflow_tasks where id = $1", subject),
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(c, sql, task, who));
            Assert.Equal("23514", ex.SqlState);
            Assert.Equal("ck_workflow_tasks_no_self_approval", ex.ConstraintName);
        }

        var parties = await Assert.ThrowsAsync<PostgresException>(() => Exec(c, "update wf.workflow_instances set requester_user_id = $2 where id = $1", instance, approver));
        Assert.Equal("ck_workflow_instances_parties_fixed", parties.ConstraintName);
        await Assert.ThrowsAsync<PostgresException>(() => Exec(c, "update wf.workflow_instances set subject_user_ids = '{}' where id = $1 and $2::uuid is not null", instance, approver));
        Assert.Equal(approver, await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, task));
    }

    [Fact]
    public async Task FR_XM_32_when_only_the_requester_can_decide_the_last_step_it_goes_one_level_up_and_with_nobody_above_it_waits_unassigned_never_approved()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, manager) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var role = await _w.DecidingRoleAsync(Ct, requester); // the requester is the only holder
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("only", RoleApprover(role.Code))), Ct);
        _w.Directory.Managers[requester] = [manager];

        var up = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(up, Ct));
        Assert.Equal(manager, await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, task));
        Assert.Contains(await _w.HistoryAsync(up, Ct), h => h.Event == "task_assigned" && h.Details.Contains("moved_up", StringComparison.Ordinal));

        _w.Directory.Managers.TryRemove(requester, out _);
        var stuck = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        var waiting = Assert.Single(await _w.OpenTasksAsync(stuck, Ct));
        Assert.Equal(0L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_tasks where id = $1 and (assignee_user_id is not null or assignee_role_id is not null)", Ct, waiting));
        Assert.Contains(await _w.HistoryAsync(stuck, Ct), h => h.Event == "cannot_assign" && h.Details.Contains("only_excluded", StringComparison.Ordinal));
        Assert.Equal("running", await _w.StatusAsync(stuck, Ct));
        Assert.DoesNotContain(waiting, await _w.InboxAsync(requester, Ct));
    }

    [Fact]
    public async Task FR_KRN_WFL_02_S110a_R1_several_holders_of_a_position_each_get_the_task_and_the_first_decision_closes_the_others()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, first, second) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var position = "pos-" + Guid.NewGuid().ToString("N")[..8];
        _w.Directory.Positions[position] = [first, second];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("hr", $$"""{"kind": "position", "position": "{{position}}"}""")), Ct);

        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        var tasks = await _w.OpenTasksAsync(instance, Ct);
        Assert.Equal(2, tasks.Count);
        var firstTask = await _w.H.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and assignee_user_id = $2", Ct, instance, first);
        var secondTask = tasks.Single(t => t != firstTask);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(first, $"/api/wf/tasks/{firstTask}/approve", new { }, Ct)).Status);
        Assert.Equal("skipped", await _w.H.ScalarAsync<string>("select status from wf.workflow_tasks where id = $1", Ct, secondTask));
        Assert.Contains(await _w.HistoryAsync(instance, Ct), h => h.Event == "task_closed" && h.Details.Contains("decided_by_other", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, (await _w.PostAsync(second, $"/api/wf/tasks/{secondTask}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }

    [Fact]
    public async Task FR_KRN_WFL_02_a_user_field_of_the_document_names_the_approver_and_a_fallback_is_used_when_the_field_is_empty()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, partner, fallback) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("partner", """{"kind": "document_user", "field": "partner"}""", fallback: UserApprover(fallback))), Ct);

        var named = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50", partner = partner.ToString() }, ct: Ct));
        Assert.Equal(partner, await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where instance_id = $1", Ct, named));
        var empty = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        Assert.Equal(fallback, await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where instance_id = $1", Ct, empty));
        Assert.Contains(await _w.HistoryAsync(empty, Ct), h => h.Event == "task_assigned" && h.Details.Contains("fallback", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ decisions (FR-KRN-WFL-07)

    [Fact]
    public async Task FR_KRN_WFL_07_reject_and_return_need_a_reason_the_module_hears_the_outcome_in_the_same_transaction_and_a_returned_request_restarts_on_its_version()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, second) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var version = await _w.ActivateRouteAsync(admin, Expense, Route(Step("first", UserApprover(approver)), Step("second", UserApprover(second))), Ct);
        var company = await CompanyAsync();
        var document = Guid.CreateVersion7();

        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "50" }, documentId: document, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        foreach (var action in new[] { "reject", "return" })
        {
            var noReason = await _w.PostAsync(approver, $"/api/wf/tasks/{task}/{action}", new { comment = " " }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
            Assert.Contains("errors.workflow.reason_required", noReason.Body, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/return", new { comment = "attach the receipt" }, Ct)).Status);
        Assert.Equal("returned", await _w.StatusAsync(instance, Ct));
        Assert.Equal("changes_requested", await _w.H.ScalarAsync<string>("select status from wf.workflow_tasks where id = $1", Ct, task));
        var returned = Assert.Single(_w.Outcomes.For(instance));
        Assert.Equal((WorkflowOutcomes.Returned, "returned", approver), (returned.Outcome.Outcome, returned.StatusInTransaction, returned.Outcome.DecidedBy));
        Assert.Equal("attach the receipt", returned.Outcome.Comment);

        // A new version meanwhile changes nothing for this request: it restarts on its own version.
        var newer = await _w.ActivateRouteAsync(admin, Expense, Route(Step("other", UserApprover(second))), Ct);
        var again = await _w.PostAsync(requester, "/test/wf/resubmit", new { documentType = Expense, documentId = document, companyId = company, values = new { amount = "60" } }, Ct);
        Assert.True(again.Status == HttpStatusCode.OK, again.Body);
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));
        Assert.Equal(version, await _w.H.ScalarAsync<Guid>("select version_id from wf.workflow_instances where id = $1", Ct, instance));
        var restarted = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(("first", approver), (await _w.H.ScalarAsync<string>("select step_key from wf.workflow_tasks where id = $1", Ct, restarted),
            await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, restarted)));
        Assert.Contains("\"60\"", await _w.H.ScalarAsync<string>("select context::text from wf.workflow_instances where id = $1", Ct, instance), StringComparison.Ordinal);
        Assert.NotEqual(version, newer);

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{restarted}/reject", new { comment = "duplicate claim" }, Ct)).Status);
        Assert.Equal("rejected", await _w.StatusAsync(instance, Ct));
        Assert.Contains(_w.Outcomes.For(instance), o => o.Outcome.Outcome == WorkflowOutcomes.Rejected && o.StatusInTransaction == "rejected");
        Assert.Equal(["started", "task_assigned", "returned", "completed", "resubmitted", "task_assigned", "rejected", "completed"], (await _w.HistoryAsync(instance, Ct)).Select(h => h.Event));
    }

    [Fact]
    public async Task FR_KRN_WFL_07_ask_for_information_answer_reassign_and_withdraw_each_leave_a_history_row_with_actor_and_reason()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, colleague, stranger) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("named", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        Assert.Equal(HttpStatusCode.BadRequest, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "Which project?" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "And?" }, Ct)).Status);
        var inbox = (await _w.GetAsync(approver, "/api/wf/inbox", Ct)).Json.EnumerateArray().Single(i => i.GetProperty("taskId").GetGuid() == task);
        Assert.True(inbox.GetProperty("waitingForInformation").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(stranger, $"/api/wf/instances/{instance}/answer", new { answer = "Alpha" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(requester, $"/api/wf/instances/{instance}/answer", new { answer = "Project Alpha" }, Ct)).Status);
        Assert.Equal(0L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_tasks where id = $1 and info_requested_at is not null", Ct, task));

        Assert.Equal(HttpStatusCode.BadRequest, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/reassign", new { toUserId = colleague }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/reassign", new { toUserId = colleague, reason = "on leave" }, Ct)).Status);
        Assert.Equal("reassigned", await _w.H.ScalarAsync<string>("select status from wf.workflow_tasks where id = $1", Ct, task));
        var moved = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Contains(moved, await _w.InboxAsync(colleague, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(approver, $"/api/wf/tasks/{moved}/approve", new { }, Ct)).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(stranger, $"/api/wf/instances/{instance}/withdraw", new { reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(requester, $"/api/wf/instances/{instance}/withdraw", new { reason = "no longer needed" }, Ct)).Status);
        Assert.Equal("cancelled", await _w.StatusAsync(instance, Ct));
        Assert.Contains(_w.Outcomes.For(instance), o => o.Outcome.Outcome == WorkflowOutcomes.Cancelled && o.StatusInTransaction == "cancelled");

        var history = await _w.HistoryAsync(instance, Ct);
        Assert.Equal(["started", "task_assigned", "info_requested", "info_answered", "reassigned", "task_assigned", "task_closed", "withdrawn", "completed"], history.Select(h => h.Event));
        Assert.Equal([requester, requester, approver, requester, approver, approver, requester, requester, requester], history.Select(h => h.Actor!.Value));
        Assert.Contains("Which project?", history[2].Details, StringComparison.Ordinal);
        Assert.Contains("Project Alpha", history[3].Details, StringComparison.Ordinal);
        Assert.Contains("on leave", history[4].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_WFL_07_Q11_when_the_module_refuses_the_outcome_the_decision_is_rolled_back()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("named", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        _w.Outcomes.FailNext = true;
        try
        {
            // A host answers 500; the in-process test server hands the module's exception to the caller instead.
            var failed = await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.Status);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("refused the outcome", StringComparison.Ordinal))
        {
        }


        Assert.Equal("running", await _w.StatusAsync(instance, Ct));
        Assert.Equal("pending", await _w.H.ScalarAsync<string>("select status from wf.workflow_tasks where id = $1", Ct, task));
        Assert.DoesNotContain(await _w.HistoryAsync(instance, Ct), h => h.Event == "approved");

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", Assert.Single(_w.Outcomes.For(instance)).StatusInTransaction);
    }

    [Fact]
    public async Task FR_HRM_LVE_05_Q11_a_request_started_in_a_module_transaction_that_rolls_back_leaves_nothing_behind()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("named", UserApprover(approver))), Ct);
        var document = Guid.CreateVersion7();
        var started = await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, documentId: document, rollBack: true, ct: Ct);
        Assert.Equal(HttpStatusCode.OK, started.Status);
        Assert.Equal(0L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_instances where document_id = $1", Ct, document));

        // Started for real, then a second start of the same document is refused while the first is open.
        InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, documentId: document, ct: Ct));
        var twice = await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, documentId: document, ct: Ct);
        Assert.Contains("errors.workflow.request_open", twice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_WFL_03_Q5_a_module_may_send_only_declared_values_of_the_right_type_for_its_own_company()
    {
        var requester = await _w.UserAsync(Ct, "business_user");
        var company = await CompanyAsync();
        var unknown = await _w.StartAsync(requester, Expense, company, new { salary = 1 }, ct: Ct);
        Assert.Contains("errors.workflow.values_invalid", unknown.Body, StringComparison.Ordinal);
        Assert.Contains("values.salary", unknown.Body, StringComparison.Ordinal);
        Assert.Contains("errors.workflow.values_invalid", (await _w.StartAsync(requester, Expense, company, new { category = "cars" }, ct: Ct)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.workflow.values_disagree", (await _w.StartAsync(requester, Expense, company, new { company = Guid.CreateVersion7().ToString() }, ct: Ct)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.workflow.type_unknown", (await _w.StartAsync(requester, "test.nothing", company, new { }, ct: Ct)).Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ concurrency and outbox

    [Fact]
    public async Task FR_KRN_WFL_07_two_approvers_deciding_the_same_queue_task_at_the_same_time_cannot_both_succeed()
    {
        var admin = await _w.AdminAsync(Ct);
        var requester = await _w.UserAsync(Ct, "business_user");
        var holders = new[] { await _w.UserAsync(Ct), await _w.UserAsync(Ct), await _w.UserAsync(Ct), await _w.UserAsync(Ct) };
        var role = await _w.DecidingRoleAsync(Ct, holders);
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("queue", RoleApprover(role.Code))), Ct);
        for (var round = 0; round < 3; round++)
        {
            var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
            var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
            var answers = await Task.WhenAll(holders.Select(u => _w.PostAsync(u, $"/api/wf/tasks/{task}/" + (u == holders[0] ? "reject" : "approve"), new { comment = "race" }, Ct)));
            Assert.Single(answers, a => a.Status == HttpStatusCode.OK);
            Assert.All(answers.Where(a => a.Status != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.Conflict, a.Status));
            Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_history where instance_id = $1 and event in ('approved', 'rejected')", Ct, instance));
            Assert.Single(_w.Outcomes.For(instance));
        }
    }

    [Fact]
    public async Task FR_KRN_WFL_08_Q15_assignments_returns_and_completions_reach_the_outbox_and_the_change_log()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("named", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "50" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        _w.H.Clock.Advance(TimeSpan.FromMinutes(5));
        await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "Why?" }, Ct);
        _w.H.Clock.Advance(TimeSpan.FromMinutes(5));
        await _w.PostAsync(requester, $"/api/wf/instances/{instance}/answer", new { answer = "Because" }, Ct);
        _w.H.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);

        var events = new List<(string Type, string Payload)>();
        await using (var c = await _w.H.OwnerAsync(null, Ct))
        await using (var command = new NpgsqlCommand("select event_type, payload::text from wf.outbox_messages where payload ->> 'instance_id' = $1 order by occurred_at, id", c))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = instance.ToString() });
            await using var r = await command.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct))
            {
                events.Add((r.GetString(0), r.GetString(1)));
            }
        }

        Assert.Equal([WorkflowEvents.TaskAssigned, WorkflowEvents.InfoRequested, WorkflowEvents.InstanceCompleted], events.Select(e => e.Type));
        Assert.Contains(approver.ToString(), events[0].Payload, StringComparison.Ordinal);
        Assert.Contains(requester.ToString(), events[1].Payload, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"approved\"", events[2].Payload.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("/test/expenses/", events[2].Payload, StringComparison.Ordinal);

        Assert.True(await _w.H.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'wf.workflow_instances' and entity_id = $1", Ct, instance) >= 2);
        Assert.True(await _w.H.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'wf.workflow_tasks' and entity_id = $1", Ct, task) >= 2);
    }

    private static async Task Exec(NpgsqlConnection c, string sql, Guid a, Guid b)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, c);
#pragma warning restore CA2100
        command.Parameters.Add(new NpgsqlParameter { Value = a });
        command.Parameters.Add(new NpgsqlParameter { Value = b });
        await command.ExecuteNonQueryAsync(Ct);
    }

    internal static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
