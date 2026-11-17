#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Tenancy;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Time limits and escalation of slice S1.10b (build item 7; ruling Q9, rulings P3, P4 and P6) with the real escalation
/// runner (as the worker host runs it), the real access engine and a real PostgreSQL. FR-XM-32 (escalate after the time
/// limit, never auto-approve), FR-HRM-NTF-11 (escalation event), spec 02 section 14 AC ("escalates after two working days").
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EscalationTests(TenancyFixture fx) : IAsyncLifetime
{
    private static readonly TimeSpan Karachi = TimeSpan.FromHours(5);

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

    private const string HeadApprover = """{"kind": "department_head"}""";
    private const string AboveThreshold = """{"field": "amount", "op": "gt", "value": "1000.00"}""";

    /// <summary>The next Thursday (at least a week ahead) at the given Karachi time.</summary>
    private DateTimeOffset NextThursday(int hour)
    {
        var today = _w.KarachiToday().AddDays(7);
        while (today.DayOfWeek != DayOfWeek.Thursday)
        {
            today = today.AddDays(1);
        }

        return new DateTimeOffset(today.ToDateTime(new TimeOnly(hour, 0)), Karachi);
    }

    /// <summary>A new company whose holiday calendar has the given public holidays.</summary>
    private async Task<Guid> CompanyWithHolidaysAsync(params DateOnly[] holidays)
    {
        var company = await _w.H.CompanyAsync(Ct);
        var calendar = (Guid)(await _w.H.RowAsync("ref.calendars", Guid.Empty, new Dictionary<string, object?> { ["source"] = "tenant", ["country_code"] = "PK" }, Ct))["id"]!;
        foreach (var day in holidays)
        {
            await _w.H.RowAsync("ref.holidays", Guid.Empty, new Dictionary<string, object?> { ["calendar_id"] = calendar, ["holiday_date"] = day, ["kind"] = "public", ["is_tentative"] = false }, Ct);
        }

        // An optional holiday does not stop the clock (ruling P4).
        await _w.H.RowAsync("ref.holidays", Guid.Empty, new Dictionary<string, object?> { ["calendar_id"] = calendar, ["holiday_date"] = holidays.Max().AddDays(1), ["kind"] = "optional", ["is_tentative"] = false }, Ct);
        await _w.H.ExecAsync("update org.companies set holiday_calendar_id = $2 where id = $1", Ct, company, calendar);
        return company;
    }

    [Fact]
    public async Task FR_XM_32_Spec02_WFL_AC_escalates_after_two_working_days_weekend_and_company_holiday_not_counted_and_shows_the_full_history()
    {
        var thursday = NextThursday(10);
        var monday = DateOnly.FromDateTime(thursday.DateTime).AddDays(4);
        var company = await CompanyWithHolidaysAsync(monday);
        var admin = await _w.AdminAsync(Ct);
        var (requester, head, boss, finance) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, finance);
        _w.Directory.Heads[requester] = [head];
        _w.Directory.Managers[head] = [boss];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("head", HeadApprover, AboveThreshold), Step("finance", RoleApprover(role.Code))), Ct);

        _w.ClockAt(thursday);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "5000.00" }, ct: Ct));
        var headTask = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        // Friday and Tuesday are the two working days (Saturday, Sunday and the Monday holiday are not): late from Wednesday 00:00.
        var wednesday = thursday.Date.AddDays(6);
        Assert.Equal(new DateTimeOffset(wednesday, Karachi), await _w.TaskTimeAsync(headTask, "due_at", Ct));
        Assert.Contains(headTask, await _w.InboxAsync(head, Ct));

        _w.ClockAt(new DateTimeOffset(wednesday, Karachi).AddMinutes(-1)); // Tuesday 23:59: not yet
        await _w.EscalateAsync(Ct);
        Assert.Equal("pending", await _w.TaskValueAsync<string>(headTask, "status", Ct));

        _w.ClockAt(new DateTimeOffset(wednesday, Karachi).AddMinutes(1));
        Assert.Contains(headTask, await _w.InboxAsync(head, Ct, "?overdue=true"));
        await _w.EscalateAsync(Ct);
        Assert.Equal("escalated", await _w.TaskValueAsync<string>(headTask, "status", Ct));
        var bossTask = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(boss, await _w.TaskValueAsync<Guid>(bossTask, "assignee_user_id", Ct));
        Assert.Equal(1, await _w.TaskValueAsync<int>(bossTask, "escalation_level", Ct));
        Assert.True(await _w.TaskTimeAsync(bossTask, "due_at", Ct) > _w.H.Clock.UtcNow); // a fresh time limit
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from wf.outbox_messages where event_type = 'kernel.workflow.task_escalated' and aggregate_id = $1", Ct, headTask));
        Assert.Contains(boss.ToString(), await _w.H.ScalarAsync<string>("select payload::text from wf.outbox_messages where event_type = 'kernel.workflow.task_escalated' and aggregate_id = $1", Ct, headTask), StringComparison.Ordinal);

        // The original assignee can no longer decide it; escalating again changes nothing (escalated once).
        var late = await _w.PostAsync(head, $"/api/wf/tasks/{headTask}/approve", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.Status);
        Assert.DoesNotContain(headTask, await _w.InboxAsync(head, Ct));
        await _w.EscalateAsync(Ct);
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_history where instance_id = $1 and event = 'escalated'", Ct, instance));
        Assert.Equal("running", await _w.StatusAsync(instance, Ct)); // never approved automatically

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(boss, $"/api/wf/tasks/{bossTask}/approve", new { comment = "ok" }, Ct)).Status);
        var queue = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(finance, $"/api/wf/tasks/{queue}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
        var history = await _w.HistoryAsync(instance, Ct);
        Assert.Equal(["started", "task_assigned", "escalated", "task_assigned", "approved", "task_assigned", "approved", "completed"], history.Select(h => h.Event));
    }

    [Fact]
    public async Task FR_XM_32_a_task_with_nobody_above_stays_open_is_handled_once_and_is_listed_as_overdue_for_administrators()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, lonely) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(lonely))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "1" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        _w.H.Clock.Advance(TimeSpan.FromDays(10));
        await _w.EscalateAsync(Ct);
        await _w.EscalateAsync(Ct);
        Assert.Equal("pending", await _w.TaskValueAsync<string>(task, "status", Ct));
        Assert.NotNull(await _w.TaskTimeAsync(task, "escalated_at", Ct));
        var notPossible = Assert.Single(await _w.HistoryAsync(instance, Ct), h => h.Event == "escalation_not_possible");
        Assert.Contains("nobody_above", notPossible.Details, StringComparison.Ordinal);
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));

        // The administrators' monitor lists it as overdue; the assignee can still decide it.
        var overdue = await _w.GetAsync(admin, "/api/wf/instances?overdue=true&limit=500", Ct);
        Assert.True(overdue.Status == HttpStatusCode.OK, overdue.Body);
        Assert.Contains(overdue.Json.EnumerateArray(), i => i.GetProperty("id").GetGuid() == instance && i.GetProperty("overdue").GetBoolean());
        Assert.Contains(task, await _w.InboxAsync(lonely, Ct, "?overdue=true"));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(lonely, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }

    [Fact]
    public async Task FR_XM_32_Q9_escalation_goes_up_at_most_three_levels_and_to_the_steps_escalation_role_when_there_is_no_manager()
    {
        var admin = await _w.AdminAsync(Ct);
        var requester = await _w.UserAsync(Ct, "business_user");
        var chain = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            chain.Add(await _w.UserAsync(Ct, "approver"));
        }

        for (var i = 0; i < 4; i++)
        {
            _w.Directory.Managers[chain[i]] = [chain[i + 1]];
        }

        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(chain[0]))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "1" }, ct: Ct));
        for (var level = 1; level <= 3; level++)
        {
            _w.H.Clock.Advance(TimeSpan.FromDays(7));
            await _w.EscalateAsync(Ct);
            var open = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
            Assert.Equal(chain[level], await _w.TaskValueAsync<Guid>(open, "assignee_user_id", Ct));
            Assert.Equal(level, await _w.TaskValueAsync<int>(open, "escalation_level", Ct));
        }

        _w.H.Clock.Advance(TimeSpan.FromDays(7));
        await _w.EscalateAsync(Ct);
        var last = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(chain[3], await _w.TaskValueAsync<Guid>(last, "assignee_user_id", Ct)); // not to chain[4]: three levels at most
        Assert.Contains(await _w.HistoryAsync(instance, Ct), h => h.Event == "escalation_not_possible" && h.Details.Contains("max_level", StringComparison.Ordinal));

        // No manager: the step's escalation role gets a queue task; the requester is never chosen, even when they hold the role.
        _w.H.Clock.Reset();
        var (approver, holder) = (await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, holder, requester);
        await _w.ActivateRouteAsync(admin, Expense, Route(
            $$"""{"key": "one", "name": {"en": "One", "ur": "ایک"}, "approver": {{UserApprover(approver)}}, "time_limit_working_days": 1, "escalation": {{RoleApprover(role.Code)}}}"""), Ct);
        var second = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "1" }, ct: Ct));
        _w.H.Clock.Advance(TimeSpan.FromDays(5));
        await _w.EscalateAsync(Ct);
        var queue = Assert.Single(await _w.OpenTasksAsync(second, Ct));
        Assert.Equal(role.Id, await _w.TaskValueAsync<Guid>(queue, "assignee_role_id", Ct));
        Assert.Contains(queue, await _w.InboxAsync(holder, Ct));
        Assert.DoesNotContain(queue, await _w.InboxAsync(requester, Ct));
    }

    [Fact]
    public async Task FR_XM_32_P6_a_waiting_question_moves_with_the_request_and_tasks_without_a_time_limit_get_one()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, boss) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        _w.Directory.Managers[approver] = [boss];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "1" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "Which trip?" }, Ct)).Status);

        // A task opened before S1.10b has no time limit: the next run gives it one, counted from when it arrived.
        await _w.H.ExecAsync("update wf.workflow_tasks set due_at = null where id = $1", Ct, task);
        var run = await _w.EscalateAsync(Ct);
        Assert.True(run.DueDatesSet >= 1);
        var due = (await _w.TaskTimeAsync(task, "due_at", Ct))!.Value;
        Assert.True(due > await _w.TaskTimeAsync(task, "created_at", Ct));

        _w.ClockAt(due.AddMinutes(1));
        await _w.EscalateAsync(Ct);
        var moved = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(boss, await _w.TaskValueAsync<Guid>(moved, "assignee_user_id", Ct));
        Assert.NotNull(await _w.TaskTimeAsync(moved, "info_requested_at", Ct));
        var mine = await _w.GetAsync(requester, "/api/wf/my-requests", Ct);
        var request = Assert.Single(mine.Json.EnumerateArray(), r => r.GetProperty("id").GetGuid() == instance);
        Assert.Equal("Which trip?", Assert.Single(request.GetProperty("questions").EnumerateArray()).GetProperty("question").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(requester, $"/api/wf/instances/{instance}/answer", new { answer = "Lahore" }, Ct)).Status);
        Assert.Null(await _w.TaskTimeAsync(moved, "info_requested_at", Ct));
    }
}
