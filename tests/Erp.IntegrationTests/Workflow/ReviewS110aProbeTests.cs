using System.Net;
using Erp.IntegrationTests.Tenancy;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// Review probes of S1.10a (fresh-session review, 2026-11-19). Each test asserts the behaviour as it is today, so it
/// passes while the defect exists. Temporary: replaced by the fix tests once the product owner rules.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ReviewS110aProbeTests(TenancyFixture fx) : IAsyncLifetime
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
    public async Task Probe_1_a_route_editor_limited_to_one_company_changes_and_deactivates_the_customer_wide_route()
    {
        var other = await _w.H.CompanyAsync(Ct);
        var editorRole = await _w.H.RoleAsync(Ct, ["wf.workflow_definitions.read", "wf.workflow_definitions.update", "wf.workflow_versions.read"]);
        var editor = await _w.UserAsync(Ct);
        await _w.H.AssignAsync(editor, editorRole, other, _w.H.Today.AddDays(-1), null, Ct); // for one company only
        var approver = await _w.UserAsync(Ct, "approver");

        var definition = await _w.DefinitionIdAsync(Expense, Ct);
        var draft = await _w.PostAsync(editor, $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{Route(Step("x", UserApprover(approver)))}}}""", Ct);
        Assert.Equal(HttpStatusCode.Created, draft.Status);
        var activated = await _w.PostAsync(editor, $"/api/wf/versions/{draft.Json.GetProperty("id").GetGuid()}/activate", new { rowVersion = 1 }, Ct);
        Assert.Equal(HttpStatusCode.OK, activated.Status); // the route of every company now names the editor's choice

        var rowVersion = await _w.H.ScalarAsync<long>("select row_version from wf.workflow_definitions where id = $1", Ct, definition);
        var off = await _w.PostAsync(editor, $"/api/wf/definitions/{definition}/deactivate", new { reason = "probe", rowVersion }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.Status); // every company's new requests of this type are now refused (ruling R3)

        // Restore for the other tests of the collection.
        await _w.ActivateRouteAsync(await _w.AdminAsync(Ct), Expense, Route(Step("x", UserApprover(approver))), Ct);
    }

    [Fact]
    public async Task Probe_2_a_route_naming_a_role_that_cannot_decide_is_activated_and_its_requests_reach_nobody_who_can_decide()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, auditor) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "auditor"));
        _ = auditor;
        var activated = await _w.ActivateRouteAsync(admin, Expense, Route(Step("audit", RoleApprover("auditor"))), Ct);
        Assert.NotEqual(Guid.Empty, activated); // activation check passed: the role "has a holder"

        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "50" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Contains(task, await _w.InboxAsync(auditor, Ct)); // shown in the auditor's inbox ...
        var claim = await _w.PostAsync(auditor, $"/api/wf/tasks/{task}/claim", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, claim.Status); // ... but nobody holding the role may take or decide it
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));
    }

    [Fact]
    public async Task Probe_3_an_approver_who_returned_a_request_resubmits_it_and_then_approves_it_alone()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("only", UserApprover(approver))), Ct);
        var company = await _w.H.DefaultCompanyAsync(Ct);
        var document = Guid.CreateVersion7();
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "50" }, documentId: document, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/return", new { comment = "fix" }, Ct)).Status);

        // The approver, not the requester, sends the corrected document back with new values (a module that lets the
        // approver edit the document): the engine accepts it ...
        var again = await _w.PostAsync(approver, "/test/wf/resubmit", new { documentType = Expense, documentId = document, companyId = company, values = new { amount = "99999" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        var restarted = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        // ... and the same person approves the values they wrote themselves.
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{restarted}/approve", new { }, Ct)).Status);
        Assert.Equal("approved", await _w.StatusAsync(instance, Ct));
    }

    [Fact]
    public async Task Probe_4_the_generated_read_endpoints_show_tasks_and_requests_of_an_unlicensed_document_type()
    {
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var version = await _w.H.ScalarAsync<Guid>("select active_version_id from wf.workflow_definitions where document_type = $1", Ct, Unlicensed);
        var (instance, task) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await using (var c = await _w.H.OwnerAsync(null, Ct))
        {
            // A request of a module whose licence has since ended (written as the engine would have written it).
            await Erp.IntegrationTests.Access.AccessHarness.ExecAsync(c, """
                insert into wf.workflow_instances (id, company_id, version_id, document_type, document_id, requester_user_id, started_at, context, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, now(), '{"values": {"secret": "x"}}', 'running', now(), $6, now(), $6)
                """, Ct, instance, await _w.H.DefaultCompanyAsync(Ct), version, Unlicensed, Guid.CreateVersion7(), requester);
            await Erp.IntegrationTests.Access.AccessHarness.ExecAsync(c, """
                insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 'admin', $3, 'pending', now(), $4, now(), $4)
                """, Ct, task, instance, approver, requester);
        }

        Assert.DoesNotContain(task, await _w.InboxAsync(approver, Ct)); // the engine hides it (ruling Q11) ...
        Assert.Contains(task.ToString(), (await _w.GetAsync(approver, "/api/wf/workflow-tasks", Ct)).Body, StringComparison.Ordinal); // ... the generated read does not
        Assert.Contains(instance.ToString(), (await _w.GetAsync(requester, "/api/wf/workflow-instances", Ct)).Body, StringComparison.Ordinal);
    }
}
