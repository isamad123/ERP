using System.Net;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Route administration of slice S1.10a (build item 2; rulings Q2, Q10, Q14, R3): drafts, checks, activation (with
/// maker-checker for sensitive document types), versions that leave running requests alone, deactivation, seeding,
/// licensing, closed generated writes and permissions. FR-KRN-WFL-01, FR-KRN-WFL-10, FR-KRN-TEN-02, FR-ADM-ONB-03,
/// FR-KRN-ENT-03, spec 02 section 14 BR.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class WorkflowAdministrationTests(TenancyFixture fx) : IAsyncLifetime
{
    private WorkflowRig _w = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _w = await ForAsync(fx);
        _w.H.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task FR_KRN_WFL_10_a_new_version_changes_nothing_for_running_requests_new_requests_use_it_and_an_active_version_is_frozen()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, oldApprover, newApprover) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var company = await _w.H.DefaultCompanyAsync(Ct);
        var v1 = await _w.ActivateRouteAsync(admin, Expense, Route(Step("a", UserApprover(oldApprover)), Step("b", UserApprover(oldApprover))), Ct);
        var running = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10" }, ct: Ct));

        var v2 = await _w.ActivateRouteAsync(admin, Expense, Route(Step("a", UserApprover(newApprover))), Ct);
        Assert.Equal("retired", await _w.H.ScalarAsync<string>("select status from wf.workflow_versions where id = $1", Ct, v1));
        Assert.Equal(v2, await _w.H.ScalarAsync<Guid>("select active_version_id from wf.workflow_definitions where document_type = $1", Ct, Expense));

        // The running request finishes on version 1: two steps, the old approver.
        var first = Assert.Single(await _w.OpenTasksAsync(running, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(oldApprover, $"/api/wf/tasks/{first}/approve", new { }, Ct)).Status);
        var second = Assert.Single(await _w.OpenTasksAsync(running, Ct));
        Assert.Equal(("b", oldApprover), (await _w.H.ScalarAsync<string>("select step_key from wf.workflow_tasks where id = $1", Ct, second),
            await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, second)));

        var fresh = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10" }, ct: Ct));
        Assert.Equal(v2, await _w.H.ScalarAsync<Guid>("select version_id from wf.workflow_instances where id = $1", Ct, fresh));
        Assert.Equal(newApprover, await _w.H.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where instance_id = $1", Ct, fresh));

        var edit = await _w.PutAsync(admin, $"/api/wf/versions/{v2}", $$"""{"rowVersion": 2, "route": {{Route(Step("x", UserApprover(oldApprover)))}}}""", Ct);
        Assert.Equal(HttpStatusCode.Conflict, edit.Status);
        Assert.Contains("errors.workflow.version_not_draft", edit.Body, StringComparison.Ordinal);
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_versions v join wf.workflow_definitions d on d.id = v.definition_id where d.document_type = $1 and v.status = 'active'", Ct, Expense));
    }

    [Fact]
    public async Task FR_KRN_WFL_01_a_draft_is_copied_edited_with_its_row_version_checked_and_stored_in_the_canonical_shape()
    {
        var admin = await _w.AdminAsync(Ct);
        var approver = await _w.UserAsync(Ct, "approver");
        var active = await _w.ActivateRouteAsync(admin, Expense, Route(Step("a", UserApprover(approver))), Ct);
        var definition = await _w.DefinitionIdAsync(Expense, Ct);

        var copy = await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions", new { copyOf = active }, Ct);
        Assert.Equal(HttpStatusCode.Created, copy.Status);
        var draft = copy.Json.GetProperty("id").GetGuid();
        Assert.Equal("draft", copy.Text("status"));

        var stale = await _w.PutAsync(admin, $"/api/wf/versions/{draft}", $$"""{"rowVersion": 7, "route": {{Route(Step("b", UserApprover(approver)))}}}""", Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var bad = await _w.PutAsync(admin, $"/api/wf/versions/{draft}", """{"rowVersion": 1, "route": {"steps": [{"key": "b", "name": {"en": "b", "ur": "ب"}, "approver": {"kind": "role", "role": "x"}, "condition": {"field": "amount", "op": "gt", "value": 1}}]}}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("errors.workflow.route_needs_unconditional_step", bad.Body, StringComparison.Ordinal);
        var edited = await _w.PutAsync(admin, $"/api/wf/versions/{draft}", $$"""{"rowVersion": 1, "route": {{Route(Step("b", UserApprover(approver)))}}}""", Ct);
        Assert.True(edited.Status == HttpStatusCode.OK, edited.Body);
        Assert.Equal(2, edited.Json.GetProperty("rowVersion").GetInt64());
        Assert.False(edited.Json.GetProperty("route").GetProperty("steps")[0].GetProperty("bulk_approval").GetBoolean()); // written in full

        var detail = await _w.GetAsync(admin, $"/api/wf/definitions/{definition}", Ct);
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Contains(detail.Json.GetProperty("versions").EnumerateArray(), v => v.GetProperty("id").GetGuid() == draft && v.GetProperty("status").GetString() == "draft");
        Assert.True(await _w.H.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'wf.workflow_versions' and entity_id = $1", Ct, draft) >= 2);
    }

    [Fact]
    public async Task Spec02_WFL_BR_activation_is_refused_while_a_step_cannot_find_any_approver_and_the_check_says_which_step()
    {
        var admin = await _w.AdminAsync(Ct);
        var empty = await _w.H.RoleAsync(Ct, ["wf.workflow_tasks.read"]);
        var emptyCode = await _w.H.ScalarAsync<string>("select code from iam.roles where id = $1", Ct, empty);
        var definition = await _w.DefinitionIdAsync(Expense, Ct);
        var draft = (await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions",
            $$"""{"route": {{Route(Step("nobody", RoleApprover(emptyCode)), Step("ghost", RoleApprover("no_such_role")), Step("gone", UserApprover(Guid.CreateVersion7())))}}}""", Ct)).Json.GetProperty("id").GetGuid();

        var check = await _w.PostAsync(admin, $"/api/wf/versions/{draft}/check", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, check.Status);
        Assert.False(check.Json.GetProperty("ready").GetBoolean());
        var problems = check.Json.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("path").GetString() + " " + p.GetProperty("error").GetString()).ToList();
        Assert.Equal(["steps[0].approver errors.workflow.role_no_holder", "steps[1].approver errors.workflow.role_unknown", "steps[2].approver errors.workflow.user_not_active"], problems);

        var refused = await _w.PostAsync(admin, $"/api/wf/versions/{draft}/activate", new { rowVersion = 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.workflow.route_not_ready", refused.Body, StringComparison.Ordinal);
        Assert.Contains("role_no_holder", refused.Body, StringComparison.Ordinal);
        Assert.Equal("draft", await _w.H.ScalarAsync<string>("select status from wf.workflow_versions where id = $1", Ct, draft));
    }

    [Fact]
    public async Task FR_KRN_WFL_02_Q4_a_manager_department_head_or_position_step_cannot_be_activated_when_no_module_answers_that_kind()
    {
        var admin = await _w.AdminAsync(Ct);
        var definition = await _w.DefinitionIdAsync(Expense, Ct);
        var draft = (await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions",
            $$"""{"route": {{Route(Step("pos", """{"kind": "position", "position": "p1"}"""), Step("mgr", """{"kind": "manager"}"""))}}}""", Ct)).Json.GetProperty("id").GetGuid();
        _w.Directory.Unsupported["position"] = true;
        try
        {
            var check = await _w.PostAsync(admin, $"/api/wf/versions/{draft}/check", new { }, Ct);
            var problem = Assert.Single(check.Json.GetProperty("problems").EnumerateArray());
            Assert.Equal(("steps[0].approver", "errors.workflow.kind_unavailable"), (problem.GetProperty("path").GetString(), problem.GetProperty("error").GetString()));
        }
        finally
        {
            _w.Directory.Unsupported.TryRemove("position", out _);
        }
    }

    [Fact]
    public async Task FR_KRN_WFL_01_Q10_a_route_of_a_sensitive_document_type_is_activated_only_when_a_second_administrator_approves()
    {
        var (maker, checker) = (await _w.AdminAsync(Ct), await _w.AdminAsync(Ct));
        var approver = await _w.UserAsync(Ct, "approver");
        var definition = await _w.DefinitionIdAsync(Payroll, Ct);
        Assert.True(await _w.H.ScalarAsync<bool>("select requires_approval from wf.workflow_definitions where id = $1", Ct, definition));
        var draft = (await _w.PostAsync(maker, $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{Route(Step("payroll", UserApprover(approver)))}}}""", Ct)).Json.GetProperty("id").GetGuid();

        var held = await _w.PostAsync(maker, $"/api/wf/versions/{draft}/activate", new { rowVersion = 1, reason = "new payroll approver" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        Assert.Equal("pending", held.Text("outcome"));
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal("draft", await _w.H.ScalarAsync<string>("select status from wf.workflow_versions where id = $1", Ct, draft));
        Assert.Equal("wf.version.activate", await _w.H.ScalarAsync<string>("select action_type from iam.maker_checker_requests where id = $1", Ct, request));

        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(maker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct)).Status);
        var approved = await _w.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("active", await _w.H.ScalarAsync<string>("select status from wf.workflow_versions where id = $1", Ct, draft));
        Assert.Equal(checker, await _w.H.ScalarAsync<Guid>("select activated_by from wf.workflow_versions where id = $1", Ct, draft));

        // A draft edited after the request was made cannot be activated by approving the old request.
        var second = (await _w.PostAsync(maker, $"/api/wf/definitions/{definition}/versions", new { copyOf = draft }, Ct)).Json.GetProperty("id").GetGuid();
        var request2 = (await _w.PostAsync(maker, $"/api/wf/versions/{second}/activate", new { rowVersion = 1 }, Ct)).Json.GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _w.PutAsync(maker, $"/api/wf/versions/{second}", $$"""{"rowVersion": 1, "route": {{Route(Step("other", UserApprover(maker)))}}}""", Ct)).Status);
        var stale = await _w.PostAsync(checker, $"/api/iam/maker-checker-requests/{request2}/approve", new { reason = "ok" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, stale.Status);
        Assert.Contains("errors.workflow.version_changed", stale.Body, StringComparison.Ordinal);
        Assert.Equal("draft", await _w.H.ScalarAsync<string>("select status from wf.workflow_versions where id = $1", Ct, second));
    }

    [Fact]
    public async Task FR_XM_32_S110a_R3_a_deactivated_route_refuses_new_requests_keeps_running_ones_and_activating_a_version_restores_it()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var company = await _w.H.DefaultCompanyAsync(Ct);
        var version = await _w.ActivateRouteAsync(admin, Expense, Route(Step("a", UserApprover(approver))), Ct);
        var running = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10" }, ct: Ct));
        var definition = await _w.DefinitionIdAsync(Expense, Ct);
        var rowVersion = await _w.H.ScalarAsync<long>("select row_version from wf.workflow_definitions where id = $1", Ct, definition);

        Assert.Equal(HttpStatusCode.BadRequest, (await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/deactivate", new { rowVersion }, Ct)).Status);
        var off = await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/deactivate", new { rowVersion, reason = "reorganising" }, Ct);
        Assert.True(off.Status == HttpStatusCode.OK, off.Body);
        Assert.False(off.Json.GetProperty("ready").GetBoolean());
        var refused = await _w.StartAsync(requester, Expense, company, new { amount = "10" }, ct: Ct);
        Assert.Contains("errors.workflow.no_active_route", refused.Body, StringComparison.Ordinal);

        var task = Assert.Single(await _w.OpenTasksAsync(running, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(running, Ct));

        var again = (await _w.PostAsync(admin, $"/api/wf/definitions/{definition}/versions", new { copyOf = version }, Ct)).Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(admin, $"/api/wf/versions/{again}/activate", new { rowVersion = 1 }, Ct)).Status);
        InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "10" }, ct: Ct));
    }

    [Fact]
    public async Task FR_KRN_TEN_02_FR_ADM_ONB_03_default_routes_are_seeded_once_per_document_type_customer_edits_are_kept_and_readiness_is_reported()
    {
        var admin = await _w.AdminAsync(Ct);
        var approver = await _w.UserAsync(Ct, "approver");
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("select count(*) from wf.workflow_definitions where code = $1", Ct, Guarded));
        Assert.Equal(1L, await _w.H.ScalarAsync<long>("""
            select count(*) from wf.workflow_versions v join wf.workflow_definitions d on d.id = v.definition_id
            where d.code = $1 and v.version = 1 and v.activated_by = $2
            """, Ct, Guarded, Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId));
        await _w.MayDecideGuardedAsync(approver, Ct); // a route may name only people who can decide the type (review S1.10a finding 2)
        var edited = await _w.ActivateRouteAsync(admin, Guarded, Route(Step("named", UserApprover(approver))), Ct);

        await using (var c = await _w.H.OwnerAsync(null, Ct))
        {
            Assert.Equal(0, await WorkflowSetup.EnsureAsync(c, null, Types, _w.H.Clock.UtcNow, Ct));
        }

        Assert.Equal(edited, await _w.H.ScalarAsync<Guid>("select active_version_id from wf.workflow_definitions where code = $1", Ct, Guarded));

        var list = await _w.GetAsync(admin, "/api/wf/definitions", Ct);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var guarded = list.Json.EnumerateArray().Single(d => d.GetProperty("documentType").GetString() == Guarded);
        Assert.True(guarded.GetProperty("ready").GetBoolean());
        Assert.Equal(await _w.H.ScalarAsync<int>("select version from wf.workflow_versions where id = $1", Ct, edited), guarded.GetProperty("activeVersion").GetInt32());
    }

    [Fact]
    public async Task FR_KRN_ENT_03_Q11_a_document_type_of_an_unlicensed_module_is_not_offered_not_listed_and_cannot_be_started()
    {
        var admin = await _w.AdminAsync(Ct);
        var types = await _w.GetAsync(admin, "/api/wf/document-types", Ct);
        Assert.Equal(HttpStatusCode.OK, types.Status);
        var codes = types.Json.EnumerateArray().Select(t => t.GetProperty("code").GetString()).ToList();
        Assert.Contains(Expense, codes);
        Assert.DoesNotContain(Unlicensed, codes);
        Assert.DoesNotContain(Unlicensed, (await _w.GetAsync(admin, "/api/wf/definitions", Ct)).Body, StringComparison.Ordinal);
        var definition = await _w.DefinitionIdAsync(Unlicensed, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(admin, $"/api/wf/definitions/{definition}", Ct)).Status);
        var start = await _w.StartAsync(await _w.UserAsync(Ct, "business_user"), Unlicensed, await _w.H.DefaultCompanyAsync(Ct), new { }, ct: Ct);
        Assert.Contains("errors.workflow.type_not_licensed", start.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_WFL_09_Q14_a_document_types_own_decide_permission_is_checked_on_the_decider()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, plain, entitled) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "approver"));
        var extra = await _w.H.RoleAsync(Ct, ["org.companies.update"], [("org.companies", "all")]);
        await _w.H.AssignAsync(entitled, extra, null, _w.H.Today.AddDays(-1), null, Ct);
        // "plain" may decide when the route is activated and the task reaches them, and has lost the right when they act
        // (a route may name only people who can decide, review S1.10a finding 2): the right is checked at decision time.
        var plainGrant = await _w.MayDecideGuardedAsync(plain, Ct);
        await _w.ActivateRouteAsync(admin, Guarded, Route(Step("a", UserApprover(plain)), Step("b", UserApprover(entitled))), Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Guarded, await _w.H.DefaultCompanyAsync(Ct), new { }, ct: Ct));
        await _w.EndAssignmentAsync(plainGrant, Ct);

        var first = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        var refused = await _w.PostAsync(plain, $"/api/wf/tasks/{first}/approve", new { }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("errors.workflow.no_decide_permission", refused.Body, StringComparison.Ordinal);
        var passOn = await _w.PostAsync(plain, $"/api/wf/tasks/{first}/reassign", new { toUserId = entitled, reason = "needs the right" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, passOn.Status);
    }

    [Fact]
    public async Task FR_KRN_WFL_01_Q14_permissions_routes_need_definitions_update_inbox_and_decisions_need_task_rights_auditor_only_reads()
    {
        var (requester, auditor, nobody) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "auditor"), await _w.UserAsync(Ct));
        var definition = await _w.DefinitionIdAsync(Expense, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.GetAsync(requester, "/api/wf/definitions", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(auditor, "/api/wf/definitions", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(auditor, $"/api/wf/definitions/{definition}/versions", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(auditor, $"/api/wf/tasks/{Guid.CreateVersion7()}/approve", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(requester, "/api/wf/inbox", Ct)).Status); // Business User decides own tasks (Q14)
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.GetAsync(nobody, "/api/wf/inbox", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _w.PostAsync(nobody, $"/api/wf/instances/{Guid.CreateVersion7()}/withdraw", new { }, Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_WFL_09_CR041_the_generated_writes_of_the_workflow_tables_and_delegations_are_closed()
    {
        var admin = await _w.AdminAsync(Ct);
        foreach (var path in new[] { "/api/wf/workflow-definitions", "/api/wf/workflow-versions", "/api/wf/workflow-instances", "/api/wf/workflow-tasks", "/api/wf/workflow-history" })
        {
            var write = await _w.PostAsync(admin, path, new { }, Ct);
            Assert.Contains(write.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(admin, path, Ct)).Status);
        }

        // No generated endpoint of these tables writes (CR-041). POST /api/iam/delegations is the delegation service of
        // S1.10b (ruling Q14), not a generated create: an empty body is refused and nothing is written.
        var generatedWrites = _w.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<GeneratedEndpointMetadata>())
            .Where(m => m is not null && (m.Resource.StartsWith("wf.", StringComparison.Ordinal) || m.Resource == "iam.delegations") && m.Action != "read")
            .ToList();
        Assert.Empty(generatedWrites);
        var before = await _w.H.ScalarAsync<long>("select count(*) from iam.delegations", Ct);
        var refused = await _w.PostAsync(admin, "/api/iam/delegations", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.delegation.from_required", refused.Body, StringComparison.Ordinal);
        Assert.Equal(before, await _w.H.ScalarAsync<long>("select count(*) from iam.delegations", Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.GetAsync(admin, "/api/iam/delegations", Ct)).Status);
    }
}
