using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Workflow;

/// <summary>
/// The workflow engine of slice S1.10a in the real API pipeline with the real access engine, on the access tests' customer.
/// The rig adds what a module would add: test document types (<see cref="Manifest"/>), outcome handlers that record what
/// they see inside the deciding transaction, an organisation directory the tests fill in, and two "module" endpoints that
/// start and resubmit a request inside their own transaction, as a module's save would.
/// </summary>
public sealed class WorkflowRig
{
    private static readonly ConcurrentDictionary<TenancyFixture, Lazy<Task<WorkflowRig>>> Instances = new();

    public const string Expense = "test.expense";
    public const string Payroll = "test.payroll";
    public const string Guarded = "test.guarded";
    public const string Unlicensed = "test.unlicensed";

    /// <summary>The test module's manifest: one general type, one sensitive (maker-checker), one with its own decide permission, one unlicensed.</summary>
    public const string Manifest = """
        {"module": "test", "document_types": [
          {"code": "test.expense", "entitlement_key": "platform.base", "name": {"en": "Expense claim", "ur": "اخراجات کا دعویٰ"},
           "fields": [
             {"name": "company", "type": "company", "label": {"en": "Company", "ur": "کمپنی"}},
             {"name": "employee", "type": "user", "label": {"en": "Employee", "ur": "ملازم"}},
             {"name": "partner", "type": "user", "label": {"en": "HR partner", "ur": "ایچ آر پارٹنر"}},
             {"name": "amount", "type": "money", "label": {"en": "Amount", "ur": "رقم"}},
             {"name": "category", "type": "choice", "choices": ["travel", "meals", "other"], "label": {"en": "Category", "ur": "زمرہ"}}
           ],
           "company_field": "company", "subject_fields": ["employee"],
           "title": {"en": "Expense claim of {amount}", "ur": "اخراجات کا دعویٰ {amount}"}, "link": "/test/expenses/{document_id}",
           "bulk_approval": true, "requires_approval": false,
           "default_route": {"steps": [{"key": "admin", "name": {"en": "Administrator", "ur": "منتظم"}, "approver": {"kind": "role", "role": "tenant_admin"}}]}},
          {"code": "test.payroll", "entitlement_key": "platform.base", "name": {"en": "Payroll change", "ur": "پے رول میں تبدیلی"},
           "fields": [{"name": "company", "type": "company", "label": {"en": "Company", "ur": "کمپنی"}}],
           "company_field": "company", "subject_fields": [], "title": {"en": "Payroll change", "ur": "پے رول میں تبدیلی"}, "link": "/test/payroll/{document_id}",
           "bulk_approval": false, "requires_approval": true,
           "default_route": {"steps": [{"key": "admin", "name": {"en": "Administrator", "ur": "منتظم"}, "approver": {"kind": "role", "role": "tenant_admin"}}]}},
          {"code": "test.guarded", "entitlement_key": "platform.base", "name": {"en": "Guarded document", "ur": "محفوظ دستاویز"},
           "fields": [{"name": "company", "type": "company", "label": {"en": "Company", "ur": "کمپنی"}}],
           "company_field": "company", "subject_fields": [], "title": {"en": "Guarded", "ur": "محفوظ"}, "link": "/test/guarded/{document_id}",
           "bulk_approval": false, "requires_approval": false, "decide_permission": "org.companies.update",
           "default_route": {"steps": [{"key": "admin", "name": {"en": "Administrator", "ur": "منتظم"}, "approver": {"kind": "role", "role": "tenant_admin"}}]}},
          {"code": "test.unlicensed", "entitlement_key": "hrm.recruitment", "name": {"en": "Unlicensed", "ur": "غیر لائسنس یافتہ"},
           "fields": [{"name": "company", "type": "company", "label": {"en": "Company", "ur": "کمپنی"}}],
           "company_field": "company", "subject_fields": [], "title": {"en": "Unlicensed", "ur": "غیر لائسنس یافتہ"}, "link": "/test/unlicensed/{document_id}",
           "bulk_approval": false, "requires_approval": false,
           "default_route": {"steps": [{"key": "admin", "name": {"en": "Administrator", "ur": "منتظم"}, "approver": {"kind": "role", "role": "tenant_admin"}}]}}
        ]}
        """;

    private WorkflowRig(AccessHarness harness, WebApplication app, TestDirectory directory, TestOutcomes outcomes)
    {
        H = harness;
        App = app;
        Directory = directory;
        Outcomes = outcomes;
    }

    public AccessHarness H { get; }

    public WebApplication App { get; }

    public TestDirectory Directory { get; }

    public TestOutcomes Outcomes { get; }

    public static IReadOnlyList<WorkflowDocumentType> Types { get; } = WorkflowManifest.Parse(Manifest);

    public static Task<WorkflowRig> ForAsync(TenancyFixture fx) =>
        Instances.GetOrAdd(fx, f => new Lazy<Task<WorkflowRig>>(() => StartAsync(f))).Value;

    /// <summary>Registers the test module's document types and outcome handlers (used by the shared fixture host too).</summary>
    public static void RegisterTestModule(IServiceCollection services) => RegisterTestModule(services, new TestOutcomes());

    private static void RegisterTestModule(IServiceCollection services, TestOutcomes outcomes)
    {
        services.AddSingleton<IWorkflowDocumentTypeSource>(new WorkflowManifestSource(Manifest));
        foreach (var type in Types)
        {
            services.AddSingleton<IWorkflowOutcomeHandler>(new TestHandler(type.Code, outcomes));
        }
    }

    private static async Task<WorkflowRig> StartAsync(TenancyFixture fx)
    {
        var h = await AccessHarness.ForAsync(fx);
        var directory = new TestDirectory();
        var outcomes = new TestOutcomes();
        var app = await fx.StartAppAsync(MapModule, CancellationToken.None, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(h.Clock);
            RegisterTestModule(services, outcomes);
            services.RemoveAll<IApproverDirectory>();
            services.AddSingleton<IApproverDirectory>(directory);
        }, realAccess: true);
        await using (var c = await h.OwnerAsync())
        {
            await WorkflowSetup.EnsureAsync(c, null, Types, h.Clock.UtcNow, CancellationToken.None);
        }

        return new WorkflowRig(h, app, directory, outcomes);
    }

    /// <summary>The test module: starts or resubmits a request inside its own transaction, as a module's save would.</summary>
    internal static void MapModule(WebApplication app)
    {
        app.MapPost("/test/wf/start", async (StartBody body, ITenantConnectionProvider tenant, IWorkflowService workflow, CancellationToken ct) =>
            await InTransactionAsync(tenant, tx => workflow.StartAsync(new WorkflowStartRequest(body.DocumentType, body.DocumentId, body.CompanyId, body.Subjects ?? [], body.Values ?? []), tx, ct), body.RollBack, ct))
            .RequireAuthorization();
        app.MapPost("/test/wf/resubmit", async (StartBody body, ITenantConnectionProvider tenant, IWorkflowService workflow, CancellationToken ct) =>
            await InTransactionAsync(tenant, tx => workflow.ResubmitAsync(body.DocumentType, body.DocumentId, body.Values ?? [], tx, ct), body.RollBack, ct))
            .RequireAuthorization();
    }

    private static async Task<IResult> InTransactionAsync(ITenantConnectionProvider tenant, Func<DbTransaction, Task<WorkflowStarted>> work, bool rollBack, CancellationToken ct)
    {
        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        try
        {
            var started = await work(tx);
            if (!rollBack)
            {
                await tx.CommitAsync(ct);
            }

            return Results.Ok(started);
        }
        catch (WorkflowException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.ErrorKey, extensions: new Dictionary<string, object?> { ["details"] = ex.Details });
        }
    }

    public sealed record StartBody(string DocumentType, Guid DocumentId, Guid CompanyId, List<Guid>? Subjects, Dictionary<string, JsonElement>? Values, bool RollBack = false);

    // ------------------------------------------------------------------ HTTP

    public Task<Answer> GetAsync(Guid user, string path, CancellationToken ct) => SendAsync(HttpMethod.Get, user, path, null, ct);

    public Task<Answer> PostAsync(Guid user, string path, object? body, CancellationToken ct) => SendAsync(HttpMethod.Post, user, path, body ?? new { }, ct);

    public Task<Answer> PutAsync(Guid user, string path, object body, CancellationToken ct) => SendAsync(HttpMethod.Put, user, path, body, ct);

    private async Task<Answer> SendAsync(HttpMethod method, Guid user, string path, object? body, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(App, TenancyFixture.HostOf(H.Code), H.Token(user));
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = body is string json ? new StringContent(json, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        using var response = await client.SendAsync(request, ct);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    // ------------------------------------------------------------------ people and routes

    /// <summary>An active user with the given baseline roles (for all companies).</summary>
    public Task<Guid> UserAsync(CancellationToken ct, params string[] roles) => H.UserAsync(ct, null, roles);

    /// <summary>A role that may decide tasks (own scope), its code, and the users holding it.</summary>
    public async Task<(Guid Id, string Code)> DecidingRoleAsync(CancellationToken ct, params Guid[] holders)
    {
        var role = await H.RoleAsync(ct, ["wf.workflow_tasks.read", "wf.workflow_tasks.update"], [("wf.workflow_tasks", "own")]);
        foreach (var user in holders)
        {
            await H.AssignAsync(user, role, null, H.Today.AddDays(-1), null, ct);
        }

        return (role, await H.ScalarAsync<string>("select code from iam.roles where id = $1", ct, role));
    }

    public async Task<Guid> AdminAsync(CancellationToken ct) => await UserAsync(ct, "tenant_admin");

    /// <summary>
    /// Lets the user decide <see cref="Guarded"/> requests (its own decide permission <c>org.companies.update</c>, for all
    /// companies); returns the assignment. A route naming someone without it is refused (review S1.10a finding 2).
    /// </summary>
    public async Task<Guid> MayDecideGuardedAsync(Guid user, CancellationToken ct)
    {
        var role = await H.RoleAsync(ct, ["org.companies.update"], [("org.companies", "all")]);
        return await H.AssignAsync(user, role, null, H.Today.AddDays(-1), null, ct);
    }

    /// <summary>Ends a role assignment today (the user loses its rights from now on).</summary>
    public async Task EndAssignmentAsync(Guid assignment, CancellationToken ct)
    {
        await using var c = await H.OwnerAsync(null, ct);
        await AccessHarness.ExecAsync(c, "update iam.user_roles set valid_to = $2 where id = $1", ct, assignment, H.Today);
    }

    public Task<Guid> DefinitionIdAsync(string documentType, CancellationToken ct) =>
        H.ScalarAsync<Guid>("select id from wf.workflow_definitions where document_type = $1", ct, documentType);

    /// <summary>Puts a new route in force for the document type (new draft, activate); returns the version id.</summary>
    public async Task<Guid> ActivateRouteAsync(Guid admin, string documentType, string routeJson, CancellationToken ct)
    {
        var definition = await DefinitionIdAsync(documentType, ct);
        var draft = await PostAsync(admin, $"/api/wf/definitions/{definition}/versions", $$"""{"route": {{routeJson}}}""", ct);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        var id = draft.Json.GetProperty("id").GetGuid();
        var activated = await PostAsync(admin, $"/api/wf/versions/{id}/activate", new { rowVersion = 1 }, ct);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
        return id;
    }

    /// <summary>A route step as JSON (approver JSON as given).</summary>
    public static string Step(string key, string approver, string? condition = null, string? fallback = null) =>
        $$"""{"key": "{{key}}", "name": {"en": "{{key}}", "ur": "مرحلہ"}, "approver": {{approver}}{{(condition is null ? "" : ", \"condition\": " + condition)}}{{(fallback is null ? "" : ", \"fallback\": " + fallback)}}}""";

    public static string Route(params string[] steps) => $$"""{"steps": [{{string.Join(", ", steps)}}]}""";

    public static string RoleApprover(string code) => $$"""{"kind": "role", "role": "{{code}}"}""";

    public static string UserApprover(Guid id) => $$"""{"kind": "user", "user_id": "{{id}}"}""";

    /// <summary>The test module starts a request as <paramref name="requester"/>.</summary>
    public async Task<Answer> StartAsync(Guid requester, string documentType, Guid company, object values, Guid[]? subjects = null, Guid? documentId = null, bool rollBack = false, CancellationToken ct = default) =>
        await PostAsync(requester, "/test/wf/start", new { documentType, documentId = documentId ?? Guid.CreateVersion7(), companyId = company, subjects = subjects ?? [], values, rollBack }, ct);

    public static Guid InstanceOf(Answer started)
    {
        Assert.True(started.Status == HttpStatusCode.OK, started.Body);
        return started.Json.GetProperty("instanceId").GetGuid();
    }

    public async Task<List<Guid>> OpenTasksAsync(Guid instance, CancellationToken ct)
    {
        await using var c = await H.OwnerAsync(null, ct);
        await using var command = new NpgsqlCommand("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at, id", c);
        command.Parameters.Add(new NpgsqlParameter { Value = instance });
        var list = new List<Guid>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetGuid(0));
        }

        return list;
    }

    /// <summary>The decision history of a request: event, and the details' step or reason, in order.</summary>
    public async Task<List<(string Event, Guid? Actor, string Details)>> HistoryAsync(Guid instance, CancellationToken ct)
    {
        await using var c = await H.OwnerAsync(null, ct);
        await using var command = new NpgsqlCommand("select event, actor_id, details::text from wf.workflow_history where instance_id = $1 order by created_at, id", c);
        command.Parameters.Add(new NpgsqlParameter { Value = instance });
        var list = new List<(string, Guid?, string)>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2)));
        }

        return list;
    }

    public Task<string> StatusAsync(Guid instance, CancellationToken ct) => H.ScalarAsync<string>("select status from wf.workflow_instances where id = $1", ct, instance);

    public async Task<List<Guid>> InboxAsync(Guid user, CancellationToken ct, string query = "")
    {
        var inbox = await GetAsync(user, "/api/wf/inbox" + query, ct);
        Assert.True(inbox.Status == HttpStatusCode.OK, inbox.Body);
        return [.. inbox.Json.EnumerateArray().Select(i => i.GetProperty("taskId").GetGuid())];
    }

    // ------------------------------------------------------------------ S1.10b

    /// <summary>One escalation run for the rig's customer, as the worker's loop runs it (real runner, real scope binding).</summary>
    public async Task<EscalationRun> EscalateAsync(CancellationToken ct)
    {
        var runner = new WorkflowEscalationRunner(H.Fixture.Options, App.Services.GetRequiredService<ITenantDataSourceFactory>(),
            App.Services.GetRequiredService<ILicenceResolver>(), App.Services.GetRequiredService<IServiceScopeFactory>());
        var outcome = Assert.Single(await runner.RunAsync(H.Code, ct));
        Assert.True(outcome.Error is null && outcome.Skipped is null, outcome.Error ?? outcome.Skipped);
        return outcome.Run!;
    }

    /// <summary>Runs code in a service scope bound to the rig's customer, as a request or the escalation runner is (for in-process contracts).</summary>
    public async Task<T> InCustomerScopeAsync<T>(Func<IServiceProvider, Task<T>> action, CancellationToken ct)
    {
        ResolvedTenant tenant;
        await using (var cp = new NpgsqlConnection(H.Fixture.Options.ConnectionString))
        {
            await cp.OpenAsync(ct);
            tenant = (await Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory.FindByCodeAsync(cp, H.Code, ct))!;
        }

        var dataSource = await App.Services.GetRequiredService<ITenantDataSourceFactory>().GetAsync(tenant, ct);
        var licence = await App.Services.GetRequiredService<ILicenceResolver>().ResolveAsync(tenant, dataSource, ct);
        await using var scope = App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, TenantAccessMode.Full, dataSource, licence);
        return await action(scope.ServiceProvider);
    }

    /// <summary>Moves the test clock to a moment.</summary>
    public void ClockAt(DateTimeOffset at) => H.Clock.Advance(at - H.Clock.UtcNow);

    /// <summary>A task row's column.</summary>
    public Task<T> TaskValueAsync<T>(Guid task, string column, CancellationToken ct) =>
        H.ScalarAsync<T>($"select {column} from wf.workflow_tasks where id = $1", ct, task);

    /// <summary>A task row's time column (UTC), or null.</summary>
    public async Task<DateTimeOffset?> TaskTimeAsync(Guid task, string column, CancellationToken ct)
    {
        var value = await H.ScalarAsync<object>($"select {column} from wf.workflow_tasks where id = $1", ct, task);
        return value is DateTime time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;
    }

    /// <summary>Creates a delegation through the self-service endpoint; returns its id.</summary>
    public async Task<Guid> DelegateAsync(Guid from, Guid to, CancellationToken ct, DateOnly? first = null, DateOnly? last = null, string[]? types = null, Guid[]? companies = null)
    {
        var today = KarachiToday();
        var created = await PostAsync(from, "/api/iam/me/delegations", new { toUserId = to, from = first ?? today, to = last ?? today.AddDays(5), documentTypes = types, companyIds = companies, reason = "leave" }, ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return created.Json.GetProperty("id").GetGuid();
    }

    /// <summary>Today in the customer's time zone (Asia/Karachi by default).</summary>
    public DateOnly KarachiToday() => DateOnly.FromDateTime(H.Clock.UtcNow.ToOffset(TimeSpan.FromHours(5)).DateTime);
}

/// <summary>The organisation the tests describe: managers, department heads and position holders by user (HR's job from S1.20).</summary>
public sealed class TestDirectory : IApproverDirectory
{
    public ConcurrentDictionary<Guid, Guid[]> Managers { get; } = new();

    public ConcurrentDictionary<Guid, Guid[]> Heads { get; } = new();

    public ConcurrentDictionary<string, Guid[]> Positions { get; } = new(StringComparer.Ordinal);

    /// <summary>Kinds this directory pretends not to know (a customer without the HR part that answers them).</summary>
    public ConcurrentDictionary<string, bool> Unsupported { get; } = new(StringComparer.Ordinal);

    public bool Supports(string kind) => !Unsupported.ContainsKey(kind);

    public Task<IReadOnlyList<Guid>> ManagersAsync(Guid userId, int levels, DateOnly asOf, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> current = [userId];
        for (var i = 0; i < levels; i++)
        {
            current = [.. current.SelectMany(u => Managers.TryGetValue(u, out var m) ? m : [])];
        }

        return Task.FromResult(current);
    }

    public Task<IReadOnlyList<Guid>> DepartmentHeadsAsync(Guid userId, Guid companyId, DateOnly asOf, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Heads.TryGetValue(userId, out var h) ? h : []);

    public Task<IReadOnlyList<Guid>> PositionHoldersAsync(string position, Guid companyId, DateOnly asOf, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Positions.TryGetValue(position, out var p) ? p : []);
}

/// <summary>What the module's outcome handlers saw: the outcome and the request's status read inside the deciding transaction.</summary>
public sealed class TestOutcomes
{
    public ConcurrentQueue<(WorkflowOutcome Outcome, string StatusInTransaction)> Seen { get; } = new();

    /// <summary>When set, the next handler call fails (the module refuses), so the decision must roll back.</summary>
    public bool FailNext { get; set; }

    /// <summary>When set, the next handler call makes the database raise this SQLSTATE in the deciding transaction (for example 40P01, a deadlock).</summary>
    public string? RaiseSqlStateNext { get; set; }

    /// <summary>When set, the handler first takes this transaction-level advisory lock, as a module locks its document (review finding 6).</summary>
    public long? LockKey { get; set; }

    public IReadOnlyList<(WorkflowOutcome Outcome, string StatusInTransaction)> For(Guid instance) => [.. Seen.Where(s => s.Outcome.InstanceId == instance)];
}

internal sealed class TestHandler(string documentType, TestOutcomes outcomes) : IWorkflowOutcomeHandler
{
    public string DocumentType => documentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var tx = (NpgsqlTransaction)transaction;
        if (outcomes.LockKey is { } key)
        {
            await using var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock($1)", tx.Connection, tx);
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = key });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand("select status from wf.workflow_instances where id = $1", tx.Connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = outcome.InstanceId });
        var status = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (outcomes.RaiseSqlStateNext is { } state)
        {
            outcomes.RaiseSqlStateNext = null;
#pragma warning disable CA2100 // a five-character SQLSTATE set by the test itself
            await using var raise = new NpgsqlCommand($"do $$ begin raise exception 'test: database cancelled the transaction' using errcode = '{state}'; end $$", tx.Connection, tx);
#pragma warning restore CA2100
            await raise.ExecuteNonQueryAsync(cancellationToken);
        }

        if (outcomes.FailNext)
        {
            outcomes.FailNext = false;
            throw new InvalidOperationException("the test module refused the outcome");
        }

        outcomes.Seen.Enqueue((outcome, status));
    }
}
