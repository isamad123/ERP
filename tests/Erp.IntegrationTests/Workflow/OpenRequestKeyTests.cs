using System.Net;
using Erp.IntegrationTests.Tenancy;
using Npgsql;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// R-023 (slice S1.11f2, CR-054): "one open request per document" holds in the database too. The engine already refuses
/// a second request under its document lock; the partial unique index on wf.workflow_instances (document_type,
/// document_id) where status in ('running', 'returned') stops a write that goes around the engine. The tests write as the
/// customer's owner login, which the engine's checks do not cover.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class OpenRequestKeyTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string OpenRequestIndex = "uq_workflow_instances_document_type_document_id";

    /// <summary>Copies a request row under a new id with the given status, as a write that bypasses the engine would.</summary>
    private const string CopyRequest = """
        insert into wf.workflow_instances
        select (jsonb_populate_record(null::wf.workflow_instances, to_jsonb(w) || jsonb_build_object('id', $2::uuid, 'status', $3::text))).*
        from wf.workflow_instances w where w.id = $1
        """;

    private WorkflowRig _w = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _w = await ForAsync(fx);
        _w.H.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(Guid Instance, Guid Approver)> StartAsync()
    {
        var admin = await _w.AdminAsync(Ct);
        var (requester, approver) = (await _w.UserAsync(Ct, "business_user"), await _w.UserAsync(Ct, "approver"));
        await _w.ActivateRouteAsync(admin, Expense, Route(Step("first", UserApprover(approver))), Ct);
        var company = await _w.H.DefaultCompanyAsync(Ct);
        var instance = InstanceOf(await _w.StartAsync(requester, Expense, company, new { amount = "50" }, documentId: Guid.CreateVersion7(), ct: Ct));
        Assert.Equal("running", await _w.StatusAsync(instance, Ct));
        return (instance, approver);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("returned")]
    public async Task FR_KRN_WFL_01_R023_a_write_that_bypasses_the_engine_cannot_open_a_second_request_for_a_document(string status)
    {
        var (instance, _) = await StartAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => _w.H.ExecAsync(CopyRequest, Ct, instance, Guid.CreateVersion7(), status));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(OpenRequestIndex, error.ConstraintName);
        Assert.Equal(1L, await _w.H.ScalarAsync<long>(
            "select count(*) from wf.workflow_instances where (document_type, document_id) = (select document_type, document_id from wf.workflow_instances where id = $1)", Ct, instance));
    }

    [Fact]
    public async Task FR_KRN_WFL_01_R023_a_returned_request_is_still_open_so_a_second_running_request_is_refused()
    {
        var (instance, approver) = await StartAsync();
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/return", new { comment = "attach the receipt" }, Ct)).Status);
        Assert.Equal("returned", await _w.StatusAsync(instance, Ct));

        var error = await Assert.ThrowsAsync<PostgresException>(() => _w.H.ExecAsync(CopyRequest, Ct, instance, Guid.CreateVersion7(), "running"));
        Assert.Equal(OpenRequestIndex, error.ConstraintName);
    }

    [Fact]
    public async Task FR_KRN_WFL_01_R023_closed_requests_do_not_count_so_a_document_can_have_a_new_request_after_one_was_decided()
    {
        var (instance, approver) = await StartAsync();
        var task = Assert.Single(await _w.OpenTasksAsync(instance, Ct));
        Assert.Equal(HttpStatusCode.OK, (await _w.PostAsync(approver, $"/api/wf/tasks/{task}/reject", new { comment = "not this one" }, Ct)).Status);
        Assert.Equal("rejected", await _w.StatusAsync(instance, Ct));

        // Closed rows of any number are allowed; one open row beside them is allowed too.
        await _w.H.ExecAsync(CopyRequest, Ct, instance, Guid.CreateVersion7(), "cancelled");
        await _w.H.ExecAsync(CopyRequest, Ct, instance, Guid.CreateVersion7(), "running");
        var second = await Assert.ThrowsAsync<PostgresException>(() => _w.H.ExecAsync(CopyRequest, Ct, instance, Guid.CreateVersion7(), "running"));
        Assert.Equal(OpenRequestIndex, second.ConstraintName);
    }
}
