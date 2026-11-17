using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Workflow;

/// <summary>
/// Workflow endpoints of slice S1.10a (ruling Q14): routes (document types, definitions, drafts, check, activate,
/// deactivate), the inbox list and task details, decisions, and the requester's answer and withdrawal. Error titles are
/// localization keys (<c>Workflow/Localization/workflow.*.json</c>); a refused route check lists its problems in
/// <c>details</c>. The generated endpoints of the <c>wf.*</c> tables only read (CR-041).
/// </summary>
public static class WorkflowEndpoints
{
    private const string Definitions = "wf.workflow_definitions";
    private const string Versions = "wf.workflow_versions";
    private const string Tasks = "wf.workflow_tasks";
    private const string Instances = "wf.workflow_instances";

    /// <summary>Refusals that mean "the record moved on": 409.</summary>
    private static readonly HashSet<string> Conflicts = new(StringComparer.Ordinal)
    {
        "errors.workflow.task_not_pending", "errors.workflow.request_not_running", "errors.workflow.request_not_open", "errors.workflow.version_changed",
        "errors.workflow.version_not_draft", "errors.workflow.info_pending", "errors.workflow.no_question", "errors.workflow.definition_not_active",
    };

    /// <summary>Refusals about the caller's rights: 403.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal) { "errors.workflow.no_decide_permission" };

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

        // Inbox and decisions (build item 5 and the inbox list).
        Tag(g.MapGet("/inbox", (IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksRead, async () => Results.Ok(await s.InboxAsync(ct).ConfigureAwait(false)), ct)), "catalog", Tasks, WorkflowPermissions.TasksRead);
        Tag(g.MapGet("/tasks/{id:guid}", (Guid id, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksRead, async () => await s.TaskAsync(id, ct).ConfigureAwait(false) is { } t ? Results.Ok(t) : NotFound("errors.workflow.task_not_found"), ct)),
            "record_action", Tasks, WorkflowPermissions.TasksRead);
        Decision(g, "approve", (s, id, body, ct) => s.ApproveAsync(id, body, ct));
        Decision(g, "reject", (s, id, body, ct) => s.RejectAsync(id, body, ct));
        Decision(g, "return", (s, id, body, ct) => s.ReturnAsync(id, body, ct));
        Tag(g.MapPost("/tasks/{id:guid}/reassign", (Guid id, ReassignRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await s.ReassignAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);
        Tag(g.MapPost("/tasks/{id:guid}/ask", (Guid id, AskRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await s.AskAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);

        // The requester's side (ruling Q7): answer a question, withdraw the request.
        Tag(g.MapPost("/instances/{id:guid}/answer", (Guid id, AnswerRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => Results.Ok(await s.AnswerAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Instances, WorkflowPermissions.RequestsRead);
        Tag(g.MapPost("/instances/{id:guid}/withdraw", (Guid id, WithdrawRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.RequestsRead, async () => Results.Ok(await s.WithdrawAsync(id, body, ct).ConfigureAwait(false)), ct)), "record_action", Instances, WorkflowPermissions.RequestsRead);
        return app;
    }

    /// <summary>Approve, reject and return: the body is a comment, or the reason (required for reject and return).</summary>
    private static void Decision(RouteGroupBuilder g, string action, Func<IWorkflowInbox, Guid, DecisionRequest, CancellationToken, Task<DecisionResult>> decide) =>
        Tag(g.MapPost("/tasks/{id:guid}/" + action, (Guid id, DecisionRequest body, IWorkflowInbox s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, WorkflowPermissions.TasksDecide, async () => Results.Ok(await decide(s, id, body, ct).ConfigureAwait(false)), ct)), "record_action", Tasks, WorkflowPermissions.TasksDecide);

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, string permission, Func<Task<IResult>> action, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
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
    }

    private static IResult NotFound(string key) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: key);

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new WorkflowEndpointMetadata(kind, resource, permission));
}
