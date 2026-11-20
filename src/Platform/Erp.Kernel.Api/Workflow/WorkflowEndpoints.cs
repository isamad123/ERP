using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Workflow;

/// <summary>
/// Workflow endpoints of slices S1.10a and S1.10b (ruling Q14): routes (document types, definitions, drafts, check, activate,
/// deactivate), the inbox with filters and counts, task details, decisions, claim and release, bulk approval, the
/// requester's answer, withdrawal and "my requests", and administration (monitor, cancel, a user's open tasks). Error titles are
/// localization keys (<c>Workflow/Localization/workflow.*.json</c>); a refused route check lists its problems in
/// <c>details</c>. The generated endpoints of the <c>wf.*</c> tables only read (CR-041).
/// </summary>
public static class WorkflowEndpoints
{
    private const string Definitions = "wf.workflow_definitions";
    private const string Versions = "wf.workflow_versions";
    private const string Tasks = "wf.workflow_tasks";
    private const string Instances = "wf.workflow_instances";

    /// <summary>
    /// Two transactions waited on each other (PostgreSQL deadlock 40P01 or serialization failure 40001) and the database
    /// cancelled this one: nothing was written, the caller may try again (409; review S1.10a finding 7, R-050).
    /// </summary>
    public const string Busy = "errors.workflow.busy";

    /// <summary>Refusals that mean "the record moved on": 409.</summary>
    private static readonly HashSet<string> Conflicts = new(StringComparer.Ordinal)
    {
        "errors.workflow.task_not_pending", "errors.workflow.request_not_running", "errors.workflow.request_not_open", "errors.workflow.version_changed",
        "errors.workflow.version_not_draft", "errors.workflow.info_pending", "errors.workflow.no_question", "errors.workflow.definition_not_active",
        "errors.workflow.task_claimed", "errors.delegation.not_in_force", "errors.delegation.overlap", Busy, "errors.workflow.reassign_target_has_task",
    };

    /// <summary>Refusals about the caller's rights: 403.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal) { "errors.workflow.no_decide_permission", "errors.workflow.own_request", "errors.workflow.all_companies_required", "errors.workflow.not_requester" };

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/wf").WithTags("wf.workflow");

        // Routes (build item 2).
        Tag(g.MapGet("/document-types", (IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesRead, async () => Results.Ok(await s.DocumentTypesAsync(ct).ConfigureAwait(false)), ct)), "catalog", Definitions, WorkflowPermissions.RoutesRead);
        Tag(g.MapGet("/definitions", (IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesRead, async () => Results.Ok(await s.DefinitionsAsync(ct).ConfigureAwait(false)), ct)), "catalog", Definitions, WorkflowPermissions.RoutesRead);
        Tag(g.MapGet("/definitions/{id:guid}", (Guid id, IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesRead, async () => await s.DefinitionAsync(id, ct).ConfigureAwait(false) is { } d ? Results.Ok(d) : NotFound("errors.workflow.definition_not_found"), ct)),
            "record_action", Definitions, WorkflowPermissions.RoutesRead);
        Tag(g.MapPost("/definitions/{id:guid}/versions", (Guid id, NewDraftRequest body, IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesUpdate, async () =>
            {
                var view = await s.NewDraftAsync(id, body, ct).ConfigureAwait(false);
                return Results.Created("/api/wf/versions/" + view.Id, view);
            }, ct)), "record_action", Versions, WorkflowPermissions.RoutesUpdate);
        Tag(g.MapPut("/versions/{id:guid}", (Guid id, EditDraftRequest body, IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesUpdate, async () => Results.Ok(await s.EditDraftAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Versions, WorkflowPermissions.RoutesUpdate);
        Tag(g.MapPost("/versions/{id:guid}/check", (Guid id, IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesRead, async () => Results.Ok(await s.CheckAsync(id, ct).ConfigureAwait(false)), ct)), "record_action", Versions, WorkflowPermissions.RoutesRead);
        Tag(g.MapPost("/versions/{id:guid}/activate", (Guid id, ActivateVersionRequest body, IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesUpdate, async () =>
            {
                var result = await s.ActivateAsync(id, body, ct).ConfigureAwait(false);
                return result.Outcome == "pending" ? Results.Accepted(null, result) : Results.Ok(result);
            }, ct)), "record_action", Versions, WorkflowPermissions.RoutesUpdate);
        Tag(g.MapPost("/definitions/{id:guid}/deactivate", (Guid id, DeactivateDefinitionRequest body, IWorkflowAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RoutesUpdate, async () => Results.Ok(await s.DeactivateAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Definitions, WorkflowPermissions.RoutesUpdate);

        // Inbox and decisions (build items 5 and 8).
        Tag(g.MapGet("/inbox", ([AsParameters] InboxParameters q, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksRead, async () => Results.Ok(await s.InboxAsync(q.ToQuery(), ct).ConfigureAwait(false)), ct)), "catalog", Tasks, WorkflowPermissions.TasksRead);
        Tag(g.MapGet("/inbox/counts", (IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksRead, async () => Results.Ok(await s.CountsAsync(ct).ConfigureAwait(false)), ct)), "catalog", Tasks, WorkflowPermissions.TasksRead);
        Tag(g.MapGet("/tasks/{id:guid}", (Guid id, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksRead, async () => await s.TaskAsync(id, ct).ConfigureAwait(false) is { } t ? Results.Ok(t) : NotFound("errors.workflow.task_not_found"), ct)),
            "record_action", Tasks, WorkflowPermissions.TasksRead);
        Decision(g, "approve", (s, id, body, ct) => s.ApproveAsync(id, body, ct));
        Decision(g, "reject", (s, id, body, ct) => s.RejectAsync(id, body, ct));
        Decision(g, "return", (s, id, body, ct) => s.ReturnAsync(id, body, ct));

        // Reassign: the assignee (or their delegate) with the decide permission, or an administrator with the reassign permission (ruling Q13).
        Tag(g.MapPost("/tasks/{id:guid}/reassign", (Guid id, ReassignRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAnyAsync(p, [WorkflowPermissions.TasksDecide, WorkflowPermissions.TasksReassign], async () => Results.Ok(await s.ReassignAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", Tasks, WorkflowPermissions.TasksDecide);
        Tag(g.MapPost("/tasks/{id:guid}/ask", (Guid id, AskRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await s.AskAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);
        Tag(g.MapPost("/tasks/{id:guid}/claim", (Guid id, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await s.ClaimAsync(id, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);
        Tag(g.MapPost("/tasks/{id:guid}/release", (Guid id, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await s.ReleaseAsync(id, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);
        Tag(g.MapPost("/tasks/bulk-approve", (BulkApproveRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await s.BulkApproveAsync(body, ct).ConfigureAwait(false)), ct)), "bulk", Tasks, WorkflowPermissions.TasksDecide);

        // The requester's side (ruling Q7, Q12): my requests, answer a question, withdraw the request.
        Tag(g.MapGet("/my-requests", ([AsParameters] MyRequestsParameters q, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => Results.Ok(await s.MyRequestsAsync(new MyRequestsQuery(q.Status, q.Limit), ct).ConfigureAwait(false)), ct)),
            "catalog", Instances, WorkflowPermissions.RequestsRead);
        Tag(g.MapPost("/instances/{id:guid}/answer", (Guid id, AnswerRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => Results.Ok(await s.AnswerAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Instances, WorkflowPermissions.RequestsRead);
        Tag(g.MapPost("/instances/{id:guid}/withdraw", (Guid id, WithdrawRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => Results.Ok(await s.WithdrawAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Instances, WorkflowPermissions.RequestsRead);

        // Administration (build item 9, ruling Q13).
        Tag(g.MapGet("/instances", ([AsParameters] MonitorParameters q, IWorkflowMonitor s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => Results.Ok(await s.RequestsAsync(q.ToQuery(), ct).ConfigureAwait(false)), ct)), "catalog", Instances, WorkflowPermissions.RequestsRead);
        Tag(g.MapGet("/instances/{id:guid}", (Guid id, IWorkflowMonitor s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => await s.RequestAsync(id, ct).ConfigureAwait(false) is { } r ? Results.Ok(r) : NotFound("errors.workflow.request_not_found"), ct)),
            "record_action", Instances, WorkflowPermissions.RequestsRead);
        Tag(g.MapPost("/instances/{id:guid}/cancel", (Guid id, CancelRequest body, IWorkflowMonitor s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsCancel, async () => Results.Ok(await s.CancelAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Instances, WorkflowPermissions.RequestsCancel);
        Tag(g.MapGet("/users/{id:guid}/open-tasks", (Guid id, IWorkflowMonitor s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksReassign, async () => Results.Ok(await s.OpenTasksOfAsync(id, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksReassign);
        Tag(g.MapPost("/users/{id:guid}/reassign-open-tasks", (Guid id, ReassignAllRequest body, IWorkflowMonitor s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksReassign, async () => Results.Ok(await s.ReassignOpenTasksAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksReassign);

        // Delegation (build item 6, ruling Q8): self-service under /api/iam/me, administrators under /api/iam/delegations
        // (the list is the generated GET /api/iam/delegations, filtered by data scope).
        const string delegations = "iam.delegations";
        var me = app.MapGroup("/api/iam/me/delegations").WithTags(delegations);
        Tag(me.MapGet("/", (IDelegationService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.DelegationsRead, async () => Results.Ok(await s.MineAsync(ct).ConfigureAwait(false)), ct)), "catalog", delegations, WorkflowPermissions.DelegationsRead);
        Tag(me.MapPost("/", (DelegationRequest body, IDelegationService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.DelegationsCreate, async () =>
            {
                var view = await s.CreateMineAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/iam/delegations/" + view.Id, view);
            }, ct)), "create", delegations, WorkflowPermissions.DelegationsCreate);
        Tag(me.MapPost("/{id:guid}/revoke", (Guid id, RevokeDelegationRequest body, IDelegationService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.DelegationsUpdate, async () => Results.Ok(await s.RevokeMineAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", delegations, WorkflowPermissions.DelegationsUpdate);
        var admin = app.MapGroup("/api/iam/delegations").WithTags(delegations);
        Tag(admin.MapPost("/", (DelegationRequest body, IDelegationService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.DelegationsCreate, async () =>
            {
                var view = await s.CreateAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/iam/delegations/" + view.Id, view);
            }, ct)), "create", delegations, WorkflowPermissions.DelegationsCreate);
        Tag(admin.MapPost("/{id:guid}/revoke", (Guid id, RevokeDelegationRequest body, IDelegationService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.DelegationsUpdate, async () => Results.Ok(await s.RevokeAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", delegations, WorkflowPermissions.DelegationsUpdate);
        return app;
    }

    /// <summary>Approve, reject and return: the body is a comment, or the reason (required for reject and return).</summary>
    private static void Decision(RouteGroupBuilder g, string action, Func<IWorkflowInbox, Guid, DecisionRequest, CancellationToken, Task<DecisionResult>> decide) =>
        Tag(g.MapPost("/tasks/{id:guid}/" + action, (Guid id, DecisionRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await decide(s, id, body, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);

    private static Task<IResult> RunAsync(IPermissionChecker permissions, string permission, Func<Task<IResult>> action, CancellationToken cancellationToken) =>
        RunAnyAsync(permissions, [permission], action, cancellationToken);

    /// <summary>Runs the action when the caller holds at least one of the permissions; workflow refusals become problem answers.</summary>
    private static async Task<IResult> RunAnyAsync(IPermissionChecker permissions, string[] anyOf, Func<Task<IResult>> action, CancellationToken cancellationToken)
    {
        var allowed = false;
        foreach (var permission in anyOf)
        {
            allowed = allowed || await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false);
        }

        if (!allowed)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (WorkflowException ex)
        {
            var status = Conflicts.Contains(ex.ErrorKey) ? StatusCodes.Status409Conflict : Forbidden.Contains(ex.ErrorKey) ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
            return Results.Problem(statusCode: status, title: ex.ErrorKey, extensions: ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (KeyNotFoundException ex) when (ex.Message.StartsWith("errors.", StringComparison.Ordinal))
        {
            return NotFound(ex.Message);
        }
        catch (AccessDeniedException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.ErrorCode);
        }
        catch (DbException ex) when (ex.SqlState is "40P01" or "40001")
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: Busy);
        }
    }

    private static IResult NotFound(string key) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: key);

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new WorkflowEndpointMetadata(kind, resource, permission));

    /// <summary>Query string of <c>GET /api/wf/inbox</c> (ruling Q12).</summary>
    public sealed class InboxParameters
    {
        public string? Module { get; init; }

        public string? DocumentType { get; init; }

        public Guid? CompanyId { get; init; }

        public Guid? RequesterUserId { get; init; }

        public string? Step { get; init; }

        public bool? Waiting { get; init; }

        public bool? Overdue { get; init; }

        public DateTimeOffset? ReceivedFrom { get; init; }

        public DateTimeOffset? ReceivedTo { get; init; }

        public string? Sort { get; init; }

        public int? Limit { get; init; }

        public InboxQuery ToQuery() => new(Module, DocumentType, CompanyId, RequesterUserId, Step, Waiting, Overdue, ReceivedFrom, ReceivedTo, Sort, Limit);
    }

    /// <summary>Query string of <c>GET /api/wf/my-requests</c>.</summary>
    public sealed class MyRequestsParameters
    {
        public string? Status { get; init; }

        public int? Limit { get; init; }
    }

    /// <summary>Query string of <c>GET /api/wf/instances</c> (ruling Q13).</summary>
    public sealed class MonitorParameters
    {
        public string? DocumentType { get; init; }

        public string? Status { get; init; }

        public Guid? CompanyId { get; init; }

        public bool? Overdue { get; init; }

        public bool? CannotAssign { get; init; }

        public Guid? After { get; init; }

        public int? Limit { get; init; }

        public MonitorQuery ToQuery() => new(DocumentType, Status, CompanyId, Overdue, CannotAssign, After, Limit);
    }
}
