using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// The fixes of the S1.10a fresh-session review (`docs/reviews/S1.10a-review.md`, rulings "accept all" of 2026-11-19,
/// CR-059), through the real API pipeline, the real access engine and a real PostgreSQL. Each test reproduces the
/// review's scenario and shows the fixed behaviour.
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

    private const string Partner = """{"kind": "document_user", "field": "partner"}""";

    // ------------------------------------------------------------------ finding 1

    [Fact]
    public async Task FR_KRN_WFL_01_Q10_CR059_changing_a_route_needs_the_route_right_for_all_companies_reading_does_not()
    {
        var admin = await _w.AdminAsync(Ct);
        var approver = await _w.UserAsync(Ct, "approver");
        var other = await _w.H.CompanyAsync(Ct);
        var editorRole = await _w.H.RoleAsync(Ct, ["wf.workflow_definitions.read", "wf.workflow_definitions.update", "wf.workflow_versions.read"]);
        var editor = await _w.UserAsync(Ct);
        await _w.H.AssignAsync(editor, editorRole, other, _w.H.Today.AddDays(-1), null, Ct); // for one company only

        var definition = await _w.DefinitionIdAsync(Expense, Ct);
        var route = Route(Step("x", UserApprover(approver)));
        var adminDraft = await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{route}}}""", Ct);
        Assert.Equal(HttpStatusCode.Created, adminDraft.Status);
        var draft = adminDraft.Json.GetProperty("id").GetGuid();
        var rowVersion = await _w.H.ScalarAsync<long>("select row_version from wf.workflow_definitions where id = $1", Ct, definition);
        var activeBefore = await _w.H.ScalarAsync<Guid>("select active_version_id from wf.workflow_definitions where id = $1", Ct, definition);

        foreach (var (method, path, body) in new (string, string, string)[]
        {
            ("POST", $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{route}}}"""),
            ("PUT", $"/api/wf/versions/{draft}", $$"""{"rowVersion": 1, "route": {{route}}}"""),
            ("POST", $"/api/wf/versions/{draft}/activate", """{"rowVersion": 1}"""),
            ("POST", $"/api/wf/definitions/{definition}/deactivate", $$"""{"reason": "limited", "rowVersion": {{rowVersion}}}"""),
        })
        {
            var refused = method == "PUT" ? await _w.PutAsync(editor, path, body, Ct) : await _w.PostAsync(editor, path, body, Ct);
            Assert.True(refused.Status == HttpStatusCode.Forbidden, $"{method} {path}: {refused.Body}");
            Assert.Equal(WorkflowAdministrationServiceKeys.AllCompaniesRequired, refused.Text("title"));
        }

        Assert.Equal(("active", activeBefore), (await _w.H.ScalarAsync<string>("select status from wf.workflow_definitions where id = $1", Ct, definition),
            await _w.H.ScalarAsync<Guid>("select active_version_id from wf.workflow_definitions where id = $1", Ct, definition)));
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select row_version from wf.workflow_versions where id = $1", Ct, draft));

        // Reading and checking stay open to a company-limited holder (ruling Q14: Company Admin reads routes).
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(editor, $"/api/wf/definitions/{definition}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(editor, $"/api/wf/versions/{draft}/check", new { }, Ct)).Status);

        // A Tenant Admin (all companies) still changes routes.
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(admin, $"/api/wf/versions/{draft}/activate", new { rowVersion = 1 }, Ct)).Status);
    }

    // ------------------------------------------------------------------ finding 2

    [Fact]
    public async Task FR_XM_32_FR_KRN_WFL_02_CR059_a_route_naming_approvers_who_cannot_decide_the_type_is_not_activated()
    {
        var admin = await _w.AdminAsync(Ct);
        var (auditor, nobody, approver) = (await _w.UserAsync(Ct, "auditor"), await _w.UserAsync(Ct), await _w.UserAsync(Ct, "approver"));
        _ = auditor; // the auditor role has a holder, so only "cannot decide" refuses it
        var decidesTasksOnly = await _w.DecidingRoleAsync(Ct, approver);

        foreach (var (type, step) in new[]
        {
            (Expense, Step("audit", RoleApprover("auditor"))),                 // role without wf.workflow_tasks.update
            (Expense, Step("named", UserApprover(nobody))),                    // active user without any right
            (Guarded, Step("named", UserApprover(approver))),                  // lacks the type's own decide permission
            (Guarded, Step("queue", RoleApprover(decidesTasksOnly.Code))),     // role without the type's own decide permission
            (Expense, Step("ok", UserApprover(approver), fallback: UserApprover(nobody))), // a fallback is an approver too
        })
        {
            var definition = await _w.DefinitionIdAsync(type, Ct);
            var draft = await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{Route(step)}}}""", Ct);
            Assert.Equal(HttpStatusCode.Created, draft.Status);
            var id = draft.Json.GetProperty("id").GetGuid();
            var check = await _w.PostAsync(admin, $"/api/wf/versions/{id}/check", new { }, Ct);
            Assert.Contains("errors.workflow.approver_cannot_decide", check.Body, StringComparison.Ordinal);
            var activate = await _w.PostAsync(admin, $"/api/wf/versions/{id}/activate", new { rowVersion = 1 }, Ct);
            Assert.True(activate.Status == HttpStatusCode.BadRequest, activate.Body);
            Assert.Equal("errors.workflow.route_not_ready", activate.Text("title"));
            Assert.Contains("errors.workflow.approver_cannot_decide", activate.Body, StringComparison.Ordinal);
        }

        // With the type's own decide permission the same Guarded steps are accepted.
        await _w.MayDecideGuardedAsync(approver, Ct);
        var grant = await _w.H.RoleAsync(Ct, ["wf.workflow_tasks.read", "wf.workflow_tasks.update", "org.companies.update"], [("wf.workflow_tasks", "own"), ("org.companies", "all")]);
        await _w.H.AssignAsync(approver, grant, null, _w.H.Today.AddDays(-1), null, Ct);
        var code = await _w.H.ScalarAsync<string>("select code from iam.roles where id = $1", Ct, grant);
        await _w.ActivateRouteAsync(admin, Guarded, Route(Step("named", UserApprover(approver)), Step("queue", RoleApprover(code))), Ct);
    }

    [Fact]
    public async Task FR_XM_32_FR_KRN_WFL_02_CR059_at_run_time_a_person_who_cannot_decide_counts_as_not_found()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, manager, powerless) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct));
        var company = await _w.H.DefaultCompanyAsync(Ct);

        // A document user field naming someone without the right to decide: the step's fallback gets the task.
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("partner", Partner, fallback: UserApprover(approver))), Ct);
        var viaFallback = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10", partner = powerless.ToString() }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(viaFallback, Ct));
        Assert.Equal(approver, await _w.TaskValueAsync<Guid>(task, "assignee_user_id", Ct));
        Assert.Contains(await _w.HistoryAsync(viaFallback, Ct), h => h.Event == "task_assigned" && h.Details.Contains("\"fallback\"", StringComparison.Ordinal));

        // Without a fallback the task waits for an administrator ("cannot assign"); it never reaches the powerless person.
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("partner", Partner)), Ct);
        var waiting = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10", partner = powerless.ToString() }, ct: Ct));
        var unassigned = Assert.Single(await _w.OpenTasksAsync(waiting, Ct));
        Assert.Equal(0L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_tasks where id = $1 and (assignee_user_id is not null or assignee_role_id is not null)", Ct, unassigned));
        Assert.Contains(await _w.HistoryAsync(waiting, Ct), h => h.Event == "cannot_assign" && h.Details.Contains("no_approver", StringComparison.Ordinal));

        // A step only the requester could decide is not skipped in favour of a later step nobody can decide: it moves up.
        var backup = await _w.UserAsync(Ct, "approver");
        _w.Directory.Heads[requester] = [requester];
        _w.Directory.Managers[requester] = [manager];
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("head", """{"kind": "department_head"}"""), Step("partner", Partner, fallback: UserApprover(backup))), Ct);
        await using (var c = await _w.H.OwnerAsync(null, Ct))
        {
            // The later step's fallback loses the right after activation, so the later step has nobody who can decide.
            await AccessHarness.ExecAsync(c, "update iam.user_roles set valid_to = $2 where user_id = $1", Ct, backup, _w.H.Today);
        }

        var movedUp = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10", partner = powerless.ToString() }, ct: Ct));
        var up = Assert.Single(await _w.OpenTasksAsync(movedUp, Ct));
        Assert.Equal(("head", manager), (await _w.TaskValueAsync<string>(up, "step_key", Ct), await _w.TaskValueAsync<Guid>(up, "assignee_user_id", Ct)));
        Assert.Contains(await _w.HistoryAsync(movedUp, Ct), h => h.Event == "task_assigned" && h.Details.Contains("moved_up", StringComparison.Ordinal));
        Assert.DoesNotContain(await _w.HistoryAsync(movedUp, Ct), h => h.Event == "step_skipped");
    }

    // ------------------------------------------------------------------ finding 3

    [Fact]
    public async Task FR_XM_32_FR_KRN_WFL_07_CR059_only_the_requester_resubmits_a_returned_request()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("only", UserApprover(approver))), Ct);
        var company = await _w.H.DefaultCompanyAsync(Ct);
        var document = Guid.CreateVersion7();
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "50" }, documentId: document, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/return", new { comment = "fix" }, Ct)).Status);

        // The review's scenario: the approver sends the document back with values they chose. Refused, nothing changes.
        var byApprover = await _w.PostAsync(approver, "/test/wf/resubmit", new { documentType = Expense, documentId = document, companyId = company, values = new { amount = "99999" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, byApprover.Status);
        Assert.Equal("errors.workflow.not_requester", byApprover.Text("title"));
        Assert.Equal("returned", await _w.StatusAsync(instance, Ct));
        Assert.Empty(await _w.OpenTasksAsync(instance, Ct));
        Assert.DoesNotContain("99999", await _w.H.ScalarAsync<string>("select context::text from wf.workflow_instances where id = $1", Ct, instance), StringComparison.Ordinal);
        Assert.DoesNotContain(await _w.HistoryAsync(instance, Ct), h => h.Event == "resubmitted");

        // The requester resubmits as before.
        var byRequester = await _w.PostAsync(requester, "/test/wf/resubmit", new { documentType = Expense, documentId = document, companyId = company, values = new { amount = "55" } }, Ct);
        Assert.True(byRequester.Status == HttpStatusCode.OK, byRequester.Body);
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));
    }

    // ------------------------------------------------------------------ finding 5

    [Fact]
    public async Task FR_KRN_WFL_01_Q10_CR059_a_type_marked_sensitive_after_its_route_was_seeded_still_needs_a_second_administrator()
    {
        var admin = await _w.AdminAsync(Ct);
        var approver = await _w.UserAsync(Ct, "approver");
        var definition = await _w.DefinitionIdAsync(Payroll, Ct);
        await using var c = await _w.H.OwnerAsync(null, Ct);
        await AccessHarness.ExecAsync(c, "update wf.workflow_definitions set requires_approval = false where id = $1", Ct, definition); // seeded before the type became sensitive
        try
        {
            var draft = await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{Route(Step("x", UserApprover(approver)))}}}""", Ct);
            Assert.Equal(HttpStatusCode.Created, draft.Status);
            var id = draft.Json.GetProperty("id").GetGuid();
            var activate = await _w.PostAsync(admin, $"/api/wf/versions/{id}/activate", new { rowVersion = 1 }, Ct);
            Assert.True(activate.Status == HttpStatusCode.Accepted, activate.Body); // held for a second administrator
            Assert.Equal("pending", activate.Text("outcome"));
            Assert.Equal("draft", await _w.H.ScalarAsync<string>("select status from wf.workflow_versions where id = $1", Ct, id));
        }
        finally
        {
            await AccessHarness.ExecAsync(c, "update wf.workflow_definitions set requires_approval = true where id = $1", Ct, definition);
        }
    }

    // ------------------------------------------------------------------ finding 7

    [Fact]
    public async Task FR_KRN_WFL_07_R050_when_the_database_cancels_a_decision_as_a_deadlock_the_answer_is_409_busy_and_nothing_is_written()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("only", UserApprover(approver))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "50" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        _w.Outcomes.RaiseSqlStateNext = "40P01"; // the module's handler meets a deadlock in the deciding transaction
        var busy = await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct);
        Assert.True(busy.Status == HttpStatusCode.Conflict, busy.Body);
        Assert.Equal("errors.workflow.busy", busy.Text("title"));
        Assert.Equal(("running", "pending"), (await _w.StatusAsync(instance, Ct), await _w.TaskValueAsync<string>(task, "status", Ct)));
        Assert.Empty(_w.Outcomes.For(instance));

        var again = await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct); // trying again works
        Assert.True(again.Status == HttpStatusCode.OK, again.Body);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }

    /// <summary>The refusal key of finding 1 (kept here so the test reads without the service's namespace).</summary>
    private static class WorkflowAdministrationServiceKeys
    {
        public const string AllCompaniesRequired = Kernel.Infrastructure.Workflow.WorkflowAdministrationService.AllCompaniesRequired;
    }
}
