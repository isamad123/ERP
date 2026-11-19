using System.Net;
using Erp.IntegrationTests.Tenancy;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Fixes of the S1.10b fresh-session review (`docs/reviews/S1.10b-review.md`, product owner's rulings 2026-11-19: findings
/// 1 and 3 as recommended, 2(a), all low items) through the real API pipeline, the real access engine and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class WorkflowReviewFixTests(TenancyFixture fx) : IAsyncLifetime
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

    private static string PositionApprover(string position) => $$"""{"kind": "position", "position": "{{position}}"}""";

    /// <summary>The one of the tasks assigned to the user.</summary>
    private async Task<Guid> TaskOfAsync(List<Guid> tasks, Guid user)
    {
        var mine = new List<Guid>();
        foreach (var task in tasks)
        {
            if (await _w.TaskValueAsync<Guid>(task, "assignee_user_id", Ct) == user)
            {
                mine.Add(task);
            }
        }

        return Assert.Single(mine);
    }

    /// <summary>A running expense request on a one-step route to <paramref name="approver"/>, and its task.</summary>
    private async Task<(Guid Instance, Guid Task)> RequestForAsync(Guid admin, string approver, Guid requester, Guid? company = null)
    {
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", approver)), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company ?? await CompanyAsync(), new { amount = "10" }, ct: Ct));
        return (instance, (await _w.OpenTasksAsync(instance, Ct))[0]);
    }

    [Fact]
    public async Task FR_KRN_WFL_05_FR_HRM_ROL_05_review_finding_1_a_disabled_delegators_rights_are_not_used_by_the_delegate()
    {
        var admin = await _w.AdminAsync(Ct);
        var (sara, ali, requester) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        var (instance, task) = await RequestForAsync(admin, UserApprover(sara), requester);
        await _w.DelegateAsync(sara, ali, Ct);
        Assert.Contains(task, await _w.InboxAsync(ali, Ct));

        var disabled = await _w.PostAsync(admin, $"/api/iam/users/{sara}/status", new { status = "disabled", reason = "dismissed" }, Ct);
        Assert.True(disabled.Status == HttpStatusCode.OK, disabled.Body);
        Assert.DoesNotContain(task, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(ali, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(ali, $"/api/wf/tasks/{task}", Ct)).Status);
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));

        // Active again within the delegation's dates: the delegation applies again.
        Assert.Equal(HttpStatusCode.NoContent, (await _w.PostAsync(admin, $"/api/iam/users/{sara}/status", new { status = "active", reason = "rehired" }, Ct)).Status);
        Assert.Contains(task, await _w.InboxAsync(ali, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(ali, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }

    [Fact]
    public async Task FR_ADM_ROL_09_FR_XM_32_review_finding_2_an_administrator_cannot_name_themselves_as_delegate()
    {
        var admin = await _w.AdminAsync(Ct);
        var (sara, ali) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var today = _w.KarachiToday();
        var before = await _w.H.ScalarAsync<long>("select count(*) from iam.delegations where from_user_id = $1", Ct, sara);

        var refused = await _w.PostAsync(admin, "/api/iam/delegations", new { fromUserId = sara, toUserId = admin, from = today, to = today, reason = "cover" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.delegation.to_self_admin", refused.Text("title"));
        Assert.Equal(before, await _w.H.ScalarAsync<long>("select count(*) from iam.delegations where from_user_id = $1", Ct, sara));

        // For someone else it works as before; and the administrator's own self-service delegation is unaffected.
        var created = await _w.PostAsync(admin, "/api/iam/delegations", new { fromUserId = sara, toUserId = ali, from = today, to = today, reason = "cover" }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        await _w.DelegateAsync(admin, ali, Ct);
    }

    [Fact]
    public async Task FR_XM_32_review_finding_3_the_late_assignee_cannot_take_the_task_escalated_to_a_role_they_hold()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, bob, carl) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, bob, carl);
        await _w.ActivateRouteAsync(admin, Expense, Route(
            $$"""{"key": "one", "name": {"en": "One", "ur": "ایک"}, "approver": {{UserApprover(bob)}}, "time_limit_working_days": 1, "escalation": {{RoleApprover(role.Code)}}}"""), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var late = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        _w.H.Clock.Advance(TimeSpan.FromDays(5));
        await _w.EscalateAsync(Ct);
        Assert.Equal("escalated", await _w.TaskValueAsync<string>(late, "status", Ct));
        var queue = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(role.Id, await _w.TaskValueAsync<Guid>(queue, "assignee_role_id", Ct));

        Assert.DoesNotContain(queue, await _w.InboxAsync(bob, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(bob, $"/api/wf/tasks/{queue}/claim", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(bob, $"/api/wf/tasks/{queue}/approve", new { }, Ct)).Status);
        Assert.Equal("pending", await _w.TaskValueAsync<string>(queue, "status", Ct));

        // Another holder takes it; once Carl has claimed it, Bob is still told nothing about it.
        Assert.Contains(queue, await _w.InboxAsync(carl, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(carl, $"/api/wf/tasks/{queue}/claim", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.PostAsync(bob, $"/api/wf/tasks/{queue}/approve", new { }, Ct)).Status);

        // An administrator may still give it to Bob by name, on purpose.
        var reassigned = await _w.PostAsync(admin, $"/api/wf/tasks/{queue}/reassign", new { toUserId = bob, reason = "Bob is back" }, Ct);
        Assert.True(reassigned.Status == HttpStatusCode.OK, reassigned.Body);
        var named = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(bob, $"/api/wf/tasks/{named}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }

    [Fact]
    public async Task FR_XM_32_Q9_review_finding_4_escalation_skips_a_next_level_who_may_not_decide()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, manager, backup) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct), await _w.UserAsync(Ct, "approver"));
        _w.Directory.Managers[approver] = [manager]; // the manager holds no right to decide approval tasks
        await _w.ActivateRouteAsync(admin, Expense, Route(
            $$"""{"key": "one", "name": {"en": "One", "ur": "ایک"}, "approver": {{UserApprover(approver)}}, "time_limit_working_days": 1, "escalation": {{UserApprover(backup)}}}"""), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));

        _w.H.Clock.Advance(TimeSpan.FromDays(5));
        var run = await _w.EscalateAsync(Ct);
        Assert.True(run.Escalated >= 1);
        var next = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(backup, await _w.TaskValueAsync<Guid>(next, "assignee_user_id", Ct)); // not the manager who cannot decide
        Assert.Contains(await _w.HistoryAsync(instance, Ct), h => h.Event == "escalated" && h.Details.Contains("\"escalation\"", StringComparison.Ordinal));

        // When nobody above may decide, the task stays open and overdue (never approved).
        var (lonely, powerless) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        _w.Directory.Managers[lonely] = [powerless];
        var (other, task) = await RequestForAsync(admin, UserApprover(lonely), requester);
        _w.H.Clock.Advance(TimeSpan.FromDays(10));
        await _w.EscalateAsync(Ct);
        Assert.Equal("pending", await _w.TaskValueAsync<string>(task, "status", Ct));
        Assert.Contains(await _w.HistoryAsync(other, Ct), h => h.Event == "escalation_not_possible" && h.Details.Contains("nobody_above", StringComparison.Ordinal));
        Assert.Equal("running", await _w.StatusAsync(other, Ct));
    }

    [Fact]
    public async Task FR_KRN_WFL_07_R1_review_finding_5_a_reassignment_never_gives_one_person_two_tasks_of_a_step()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, ann, ben, carl) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var position = "review-r5-" + Guid.CreateVersion7().ToString("N")[..8];
        _w.Directory.Positions[position] = [ann, ben];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("hr", PositionApprover(position))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var tasks = await _w.OpenTasksAsync(instance, Ct);
        Assert.Equal(2, tasks.Count);
        var annTask = await TaskOfAsync(tasks, ann);

        var refused = await _w.PostAsync(ann, $"/api/wf/tasks/{annTask}/reassign", new { toUserId = ben, reason = "holiday" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.workflow.reassign_target_has_task", refused.Text("title"));
        Assert.Equal("pending", await _w.TaskValueAsync<string>(annTask, "status", Ct));

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(ann, $"/api/wf/tasks/{annTask}/reassign", new { toUserId = carl, reason = "holiday" }, Ct)).Status);
        Assert.Equal(2, (await _w.OpenTasksAsync(instance, Ct)).Count);
    }

    [Fact]
    public async Task FR_KRN_WFL_07_review_finding_6_a_withdrawal_that_loses_a_deadlock_to_the_module_is_retried()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var (instance, _) = await RequestForAsync(admin, UserApprover(approver), requester);
        var key = BitConverter.ToInt64(Guid.CreateVersion7().ToByteArray(), 8);
        _w.Outcomes.LockKey = key;
        try
        {
            // The "module" holds its document (the advisory lock) and, below, wants the request: the opposite order.
            await using var module = await _w.H.OwnerAsync(ct: Ct);
            await using var tx = await module.BeginTransactionAsync(Ct);
            await using (var take = new Npgsql.NpgsqlCommand("select pg_advisory_xact_lock($1)", module, tx))
            {
                take.Parameters.Add(new Npgsql.NpgsqlParameter { Value = key });
                await take.ExecuteNonQueryAsync(Ct);
            }

            var withdraw = _w.PostAsync(requester, $"/api/wf/instances/{instance}/withdraw", new { reason = "not needed" }, Ct);
            var waiting = false;
            for (var i = 0; i < 200 && !waiting; i++)
            {
                waiting = await _w.H.ScalarAsync<long>("select count(*) from pg_locks where locktype = 'advisory' and not granted", Ct) > 0;
                if (!waiting)
                {
                    await Task.Delay(25, Ct);
                }
            }

            Assert.True(waiting, "the withdrawal never reached the module's document lock");

            // The engine waited first, so PostgreSQL ends the engine's transaction as the deadlock victim; it must retry.
            await using (var want = new Npgsql.NpgsqlCommand("select id from wf.workflow_instances where id = $1 for update", module, tx))
            {
                want.Parameters.Add(new Npgsql.NpgsqlParameter { Value = instance });
                await want.ExecuteScalarAsync(Ct);
            }

            await tx.CommitAsync(Ct);
            var answer = await withdraw;
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        }
        finally
        {
            _w.Outcomes.LockKey = null;
        }

        Assert.Equal("cancelled", await _w.StatusAsync(instance, Ct));
        Assert.Single(_w.Outcomes.For(instance)); // the module heard the outcome once (the first attempt rolled back)
    }

    [Fact]
    public async Task FR_ADM_USR_03_P7_review_finding_7_the_open_task_count_on_disabling_follows_the_callers_list()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, leaving) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var other = await _w.H.CompanyAsync(Ct);
        await RequestForAsync(admin, UserApprover(leaving), requester);
        await RequestForAsync(admin, UserApprover(leaving), requester, other);

        // An administrator of the other company only: one task there, the default company's task is not counted.
        var role = await _w.H.RoleAsync(Ct, ["iam.users.change_status", "wf.workflow_tasks.reassign"], [("wf.workflow_instances", "all")]);
        var companyAdmin = await _w.UserAsync(Ct);
        await _w.H.AssignAsync(companyAdmin, role, other, _w.H.Today.AddDays(-1), null, Ct);
        var disabled = await _w.PostAsync(companyAdmin, $"/api/iam/users/{leaving}/status", new { status = "disabled", reason = "left" }, Ct);
        Assert.True(disabled.Status == HttpStatusCode.OK, disabled.Body);
        Assert.Equal(1, disabled.Json.GetProperty("openWorkflowTasks").GetInt32());
        Assert.Equal(1, (await _w.GetAsync(companyAdmin, $"/api/wf/users/{leaving}/open-tasks", Ct)).Json.GetArrayLength());

        // A caller who reassigns no tasks is told 0; the tenant administrator sees both.
        var statusOnly = await _w.H.RoleAsync(Ct, ["iam.users.change_status"]);
        var clerk = await _w.UserAsync(Ct);
        await _w.H.AssignAsync(clerk, statusOnly, null, _w.H.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await _w.PostAsync(admin, $"/api/iam/users/{leaving}/status", new { status = "active", reason = "back" }, Ct)).Status);
        var byClerk = await _w.PostAsync(clerk, $"/api/iam/users/{leaving}/status", new { status = "disabled", reason = "left" }, Ct);
        Assert.True(byClerk.Status == HttpStatusCode.OK, byClerk.Body);
        Assert.Equal(0, byClerk.Json.GetProperty("openWorkflowTasks").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await _w.PostAsync(admin, $"/api/iam/users/{leaving}/status", new { status = "active", reason = "back" }, Ct)).Status);
        var byAdmin = await _w.PostAsync(admin, $"/api/iam/users/{leaving}/status", new { status = "disabled", reason = "left" }, Ct);
        Assert.Equal(2, byAdmin.Json.GetProperty("openWorkflowTasks").GetInt32());
    }

    [Fact]
    public async Task FR_KRN_WFL_07_FR_HRM_SSP_02_review_finding_8_my_requests_shows_each_tasks_own_question()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, ann, ben) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var position = "review-r8-" + Guid.CreateVersion7().ToString("N")[..8];
        _w.Directory.Positions[position] = [ann, ben];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("hr", PositionApprover(position))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await CompanyAsync(), new { amount = "1" }, ct: Ct));
        var tasks = await _w.OpenTasksAsync(instance, Ct);
        var annTask = await TaskOfAsync(tasks, ann);
        var benTask = Assert.Single(tasks, t => t != annTask);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(ann, $"/api/wf/tasks/{annTask}/ask", new { question = "Which trip?" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(ben, $"/api/wf/tasks/{benTask}/ask", new { question = "Receipt number?" }, Ct)).Status);

        var mine = await _w.GetAsync(requester, "/api/wf/my-requests", Ct);
        var request = Assert.Single(mine.Json.EnumerateArray(), r => r.GetProperty("id").GetGuid() == instance);
        var questions = request.GetProperty("questions").EnumerateArray().ToDictionary(q => q.GetProperty("taskId").GetGuid(), q => q.GetProperty("question").GetString());
        Assert.Equal("Which trip?", questions[annTask]);
        Assert.Equal("Receipt number?", questions[benTask]);
    }
}
