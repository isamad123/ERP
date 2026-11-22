#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// What the inbox screen of slice S1.12d (Build items 20 and 21) reads from the workflow API, through the real API pipeline,
/// the real access engine and a real PostgreSQL: names of the requester, of the person acted for and of the actors in the
/// history; the document type's declared fields with their values; and the actions the engine would accept now, which
/// must agree with what the engine does (FR-KRN-WFL-08, FR-HRM-SSP-02, FR-XM-32: nobody is offered a decision the engine
/// refuses).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class InboxScreenDataTests(TenancyFixture fx) : IAsyncLifetime
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

    private Task<string> NameAsync(Guid user) => _w.H.ScalarAsync<string>("select display_name from iam.users where id = $1", Ct, user);

    private static List<string> Actions(Answer detail) =>
        [.. detail.Json.GetProperty("context").GetProperty("actions").EnumerateArray().Select(a => a.GetString()!)];

    [Fact]
    public async Task FR_KRN_WFL_08_S112d_task_detail_names_people_shows_declared_fields_and_the_actions_the_engine_allows()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver, employee) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"), await _w.UserAsync(Ct, "business_user"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(approver))), Ct);
        var company = await _w.H.DefaultCompanyAsync(Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { company, employee, amount = "1250.5000", category = "travel" }, [employee], ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        var inbox = Assert.Single((await _w.GetAsync(approver, "/api/wf/inbox", Ct)).Json.EnumerateArray(), i => i.GetProperty("taskId").GetGuid() == task);
        Assert.Equal(await NameAsync(requester), inbox.GetProperty("requesterName").GetString());
        Assert.Equal(JsonValueKind.Null, inbox.GetProperty("actingForName").ValueKind);

        var detail = await _w.GetAsync(approver, $"/api/wf/tasks/{task}", Ct);
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
        var context = detail.Json.GetProperty("context");
        Assert.Equal("test", context.GetProperty("module").GetString());
        Assert.Equal("Expense claim", context.GetProperty("documentTypeName").GetProperty("en").GetString());
        Assert.Equal(await NameAsync(requester), context.GetProperty("requesterName").GetString());
        Assert.False(context.GetProperty("queue").GetBoolean());
        Assert.Equal(["reject", "return", "reassign"], context.GetProperty("reasonRequired").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(["approve", "reject", "return", "reassign", "ask"], Actions(detail));

        var fields = context.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        Assert.Equal(["company", "employee", "partner", "amount", "category"], fields.Keys);
        Assert.Equal("1250.5000", fields["amount"].GetProperty("value").GetString()); // exact decimal text, as frozen in the request
        Assert.Equal("Amount", fields["amount"].GetProperty("label").GetProperty("en").GetString());
        Assert.Equal(await NameAsync(employee), fields["employee"].GetProperty("displayName").GetString());
        Assert.False(string.IsNullOrEmpty(fields["company"].GetProperty("displayNames").GetProperty("en").GetString()));
        Assert.Equal(JsonValueKind.Null, fields["partner"].GetProperty("value").ValueKind);

        // While the question waits, "ask" is no longer offered (the engine refuses a second one); the history names the asker.
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "Receipt?" }, Ct)).Status);
        detail = await _w.GetAsync(approver, $"/api/wf/tasks/{task}", Ct);
        Assert.Equal(["approve", "reject", "return", "reassign"], Actions(detail));
        Assert.Equal("errors.workflow.info_pending", (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/ask", new { question = "Again?" }, Ct)).Text("title"));
        var asked = Assert.Single(detail.Json.GetProperty("history").EnumerateArray(), h => h.GetProperty("event").GetString() == "info_requested");
        Assert.Equal(await NameAsync(approver), asked.GetProperty("actorName").GetString());

        // The requester and the person the document is about get no task to open at all (FR-XM-32).
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(requester, $"/api/wf/tasks/{task}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(employee, $"/api/wf/tasks/{task}", Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_WFL_08_FR_XM_32_S112d_an_approver_who_lost_the_right_to_decide_is_offered_no_decision_and_the_engine_refuses_one()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        var guarded = await _w.MayDecideGuardedAsync(approver, Ct);
        await _w.ActivateRouteAsync(admin, Guarded, Route(Step("one", UserApprover(approver))), Ct);
        var task = Assert.Single(await _w.OpenTasksAsync(InstanceOf(await _w.StartAsync(requester, Guarded, await _w.H.DefaultCompanyAsync(Ct), new { }, ct: Ct)), Ct));
        Assert.Equal(["approve", "reject", "return", "reassign", "ask"], Actions(await _w.GetAsync(approver, $"/api/wf/tasks/{task}", Ct)));

        await _w.EndAssignmentAsync(guarded, Ct);
        Assert.Empty(Actions(await _w.GetAsync(approver, $"/api/wf/tasks/{task}", Ct)));
        Assert.Equal("errors.workflow.no_decide_permission", (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { }, Ct)).Text("title"));
    }

    [Fact]
    public async Task FR_KRN_WFL_02_S112d_queue_tasks_offer_claim_then_release_to_the_claimer()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, first, second) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct), await _w.UserAsync(Ct));
        var role = await _w.DecidingRoleAsync(Ct, first, second);
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("queue", RoleApprover(role.Code))), Ct);
        var task = Assert.Single(await _w.OpenTasksAsync(InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "5" }, ct: Ct)), Ct));

        var detail = await _w.GetAsync(first, $"/api/wf/tasks/{task}", Ct);
        Assert.True(detail.Json.GetProperty("context").GetProperty("queue").GetBoolean());
        Assert.Equal(["approve", "reject", "return", "reassign", "ask", "claim"], Actions(detail));

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(first, $"/api/wf/tasks/{task}/claim", null, Ct)).Status);
        Assert.Equal(["approve", "reject", "return", "reassign", "ask", "release"], Actions(await _w.GetAsync(first, $"/api/wf/tasks/{task}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, (await _w.GetAsync(second, $"/api/wf/tasks/{task}", Ct)).Status); // taken: no longer theirs to open
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(first, $"/api/wf/tasks/{task}/release", null, Ct)).Status);
        Assert.Contains("claim", Actions(await _w.GetAsync(second, $"/api/wf/tasks/{task}", Ct)));
    }

    [Fact]
    public async Task FR_KRN_WFL_05_FR_KRN_WFL_08_S112d_a_delegate_sees_the_task_for_the_delegator_by_name_and_history_records_both()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, sara, deputy) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "business_user"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(sara))), Ct);
        await _w.DelegateAsync(sara, deputy, Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, await _w.H.DefaultCompanyAsync(Ct), new { amount = "7" }, ct: Ct));
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));

        var item = Assert.Single((await _w.GetAsync(deputy, "/api/wf/inbox", Ct)).Json.EnumerateArray(), i => i.GetProperty("taskId").GetGuid() == task);
        Assert.Equal(await NameAsync(sara), item.GetProperty("actingForName").GetString());
        var detail = await _w.GetAsync(deputy, $"/api/wf/tasks/{task}", Ct);
        Assert.Equal(await NameAsync(sara), detail.Json.GetProperty("context").GetProperty("actingForName").GetString());
        Assert.Contains("approve", Actions(detail));

        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(deputy, $"/api/wf/tasks/{task}/approve", new { comment = "ok" }, Ct)).Status);
        var history = await _w.GetAsync(admin, $"/api/wf/instances/{instance}", Ct);
        var approved = Assert.Single(history.Json.GetProperty("history").EnumerateArray(), h => h.GetProperty("event").GetString() == "approved");
        Assert.Equal(await NameAsync(deputy), approved.GetProperty("actorName").GetString());
        Assert.Equal(await NameAsync(sara), approved.GetProperty("actingForName").GetString());
    }
}
