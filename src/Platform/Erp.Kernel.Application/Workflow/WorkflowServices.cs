using System.Text.Json;

namespace Erp.Kernel.Application.Workflow;

/// <summary>One field a document type offers to conditions (labels in English and Urdu).</summary>
public sealed record WorkflowFieldView(string Name, string Type, IReadOnlyDictionary<string, string> Label, IReadOnlyList<string> Choices);

/// <summary>A registered, licensed document type (slice S1.10 build item 1).</summary>
public sealed record WorkflowDocumentTypeView(
    string Code,
    string Module,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyList<WorkflowFieldView> Fields,
    bool BulkApproval,
    bool RequiresApproval,
    string? DecidePermission);

/// <summary>A problem of a route: where and what (a localization key).</summary>
public sealed record RouteProblemView(string Path, string Error);

/// <summary>
/// A route (definition) with its active version and its readiness: <see cref="Ready"/> is false and <see cref="Problems"/>
/// says why when the active version could not be activated today, or when no version is active (ADM-ONB-03 "approvers
/// defined", data for S1.18).
/// </summary>
public sealed record WorkflowDefinitionView(
    Guid Id,
    string Code,
    string DocumentType,
    IReadOnlyDictionary<string, string> Name,
    string Status,
    Guid? ActiveVersionId,
    int? ActiveVersion,
    bool RequiresApproval,
    long RowVersion,
    bool Ready,
    IReadOnlyList<RouteProblemView> Problems);

/// <summary>One version of a route: draft (editable), active (frozen) or retired.</summary>
public sealed record WorkflowVersionView(
    Guid Id,
    Guid DefinitionId,
    int Version,
    string Status,
    JsonElement Route,
    DateTimeOffset? ActivatedAt,
    Guid? ActivatedBy,
    long RowVersion);

public sealed record WorkflowDefinitionDetail(WorkflowDefinitionView Definition, IReadOnlyList<WorkflowVersionView> Versions);

/// <summary>A new draft: a copy of <see cref="CopyOf"/> (a version of the same route), or <see cref="Route"/>, or a copy of the active version.</summary>
public sealed record NewDraftRequest(Guid? CopyOf, JsonElement? Route);

public sealed record EditDraftRequest(JsonElement? Route, long? RowVersion);

public sealed record RouteCheckView(bool Ready, IReadOnlyList<RouteProblemView> Problems);

public sealed record ActivateVersionRequest(long? RowVersion, string? Reason);

/// <summary><see cref="Outcome"/> is <c>done</c>, or <c>pending</c> when a second administrator must approve (maker-checker).</summary>
public sealed record ActivationResult(string Outcome, WorkflowVersionView? Version, Guid? RequestId);

public sealed record DeactivateDefinitionRequest(string? Reason, long? RowVersion);

/// <summary>Route administration (slice S1.10 build item 2, rulings Q2, Q10).</summary>
public interface IWorkflowAdministration
{
    Task<IReadOnlyList<WorkflowDocumentTypeView>> DocumentTypesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowDefinitionView>> DefinitionsAsync(CancellationToken cancellationToken);

    Task<WorkflowDefinitionDetail?> DefinitionAsync(Guid id, CancellationToken cancellationToken);

    Task<WorkflowVersionView> NewDraftAsync(Guid definitionId, NewDraftRequest request, CancellationToken cancellationToken);

    Task<WorkflowVersionView> EditDraftAsync(Guid versionId, EditDraftRequest request, CancellationToken cancellationToken);

    Task<RouteCheckView> CheckAsync(Guid versionId, CancellationToken cancellationToken);

    Task<ActivationResult> ActivateAsync(Guid versionId, ActivateVersionRequest request, CancellationToken cancellationToken);

    Task<WorkflowDefinitionView> DeactivateAsync(Guid definitionId, DeactivateDefinitionRequest request, CancellationToken cancellationToken);
}

/// <summary>A task in the caller's inbox: their own, or a queue task of a role they hold that they may take.</summary>
public sealed record InboxItem(
    Guid TaskId,
    Guid InstanceId,
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Title,
    string Link,
    string StepKey,
    IReadOnlyDictionary<string, string> StepName,
    Guid RequesterUserId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? DueAt,
    bool Queue,
    Guid? RoleId,
    bool WaitingForInformation);

public sealed record WorkflowTaskView(
    Guid Id,
    string StepKey,
    IReadOnlyDictionary<string, string> StepName,
    string Status,
    Guid? AssigneeUserId,
    Guid? AssigneeRoleId,
    Guid? ActingForUserId,
    Guid? ActedBy,
    DateTimeOffset? ActedAt,
    string? Comment,
    DateTimeOffset? DueAt,
    DateTimeOffset? InfoRequestedAt,
    Guid? InfoRequestedBy,
    DateTimeOffset? ClaimedAt,
    long RowVersion);

public sealed record WorkflowInstanceView(
    Guid Id,
    Guid VersionId,
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    string Status,
    Guid RequesterUserId,
    IReadOnlyList<Guid> SubjectUserIds,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    JsonElement Values,
    IReadOnlyDictionary<string, string> Title,
    string Link);

/// <summary>One row of the decision history (append-only): event, who, for whom, when, and its details.</summary>
public sealed record WorkflowHistoryView(Guid Id, Guid? TaskId, string Event, Guid? ActorId, Guid? ActingForUserId, DateTimeOffset At, JsonElement Details);

public sealed record WorkflowTaskDetail(WorkflowTaskView Task, WorkflowInstanceView Request, IReadOnlyList<WorkflowHistoryView> History);

/// <summary>A comment (approve) or reason (reject, return); reject and return need one.</summary>
public sealed record DecisionRequest(string? Comment);

public sealed record ReassignRequest(Guid? ToUserId, string? Reason);

public sealed record AskRequest(string? Question);

public sealed record AnswerRequest(string? Answer);

public sealed record WithdrawRequest(string? Reason);

/// <summary>The request after a decision: its status and the tasks now open.</summary>
public sealed record DecisionResult(Guid InstanceId, string Status, IReadOnlyList<Guid> OpenTaskIds);

/// <summary>The approver's and the requester's side of a request (slice S1.10 build item 5 and the inbox list, rulings Q7, Q12).</summary>
public interface IWorkflowInbox
{
    Task<IReadOnlyList<InboxItem>> InboxAsync(CancellationToken cancellationToken);

    Task<WorkflowTaskDetail?> TaskAsync(Guid taskId, CancellationToken cancellationToken);

    Task<DecisionResult> ApproveAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> RejectAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> ReturnAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> ReassignAsync(Guid taskId, ReassignRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> AskAsync(Guid taskId, AskRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> AnswerAsync(Guid instanceId, AnswerRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> WithdrawAsync(Guid instanceId, WithdrawRequest request, CancellationToken cancellationToken);
}
