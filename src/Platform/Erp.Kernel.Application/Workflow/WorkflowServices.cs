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

/// <summary>
/// A task in the caller's inbox (slice S1.10 rulings Q12, Q8): their own, a queue task of a role they hold that they may
/// claim (<see cref="Queue"/>), or a task of someone who delegated to them (<see cref="ActingForUserId"/>: "for Sara").
/// <see cref="BulkApproval"/> says whether the document type and the step allow bulk approval.
/// </summary>
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
    bool WaitingForInformation,
    string Module,
    Guid? ActingForUserId,
    bool Overdue,
    int EscalationLevel,
    bool BulkApproval,
    string? RequesterName = null,
    string? ActingForName = null);

/// <summary>
/// Inbox filters (ruling Q12); all optional. <see cref="Sort"/> is <c>received</c> (default, oldest first) or <c>due</c>
/// (soonest first, tasks without a time limit last). <see cref="Limit"/> is 1 to 1000 (default 1000).
/// </summary>
public sealed record InboxQuery(
    string? Module = null,
    string? DocumentType = null,
    Guid? CompanyId = null,
    Guid? RequesterUserId = null,
    string? Step = null,
    bool? Waiting = null,
    bool? Overdue = null,
    DateTimeOffset? ReceivedFrom = null,
    DateTimeOffset? ReceivedTo = null,
    string? Sort = null,
    int? Limit = null);

/// <summary>Open tasks of one document type in the caller's inbox, and how many are past their time limit.</summary>
public sealed record InboxTypeCount(string DocumentType, string Module, IReadOnlyDictionary<string, string> Name, int Count, int Overdue);

/// <summary>
/// Counts for menus and badges (ruling Q12). <see cref="ChangeApprovals"/> is the number of pending maker-checker
/// requests of other people the caller may be asked to decide (S1.06; decided on their own endpoints).
/// </summary>
public sealed record InboxCounts(int Total, int Overdue, IReadOnlyList<InboxTypeCount> DocumentTypes, int ChangeApprovals);

/// <summary>Approve several tasks at once (ruling Q12): each is checked and decided on its own.</summary>
public sealed record BulkApproveRequest(IReadOnlyList<Guid>? TaskIds, string? Comment);

/// <summary>The result for one task: <c>approved</c>, or <c>refused</c> with the reason's localization key.</summary>
public sealed record TaskActionResult(Guid TaskId, string Outcome, string? Status, string? Error);

public sealed record BulkResult(int Done, int Refused, IReadOnlyList<TaskActionResult> Results);

/// <summary>A task now open on one of the caller's requests: who must decide it and by when.</summary>
public sealed record MyRequestTask(Guid TaskId, string StepKey, IReadOnlyDictionary<string, string> StepName, Guid? AssigneeUserId, Guid? AssigneeRoleId, DateTimeOffset? DueAt, bool Overdue);

/// <summary>A question an approver asked that waits for the requester's answer.</summary>
public sealed record WaitingQuestion(Guid TaskId, Guid? AskedBy, DateTimeOffset AskedAt, string? Question);

/// <summary>One of the caller's own requests ("my requests", ruling Q12).</summary>
public sealed record MyRequestView(
    Guid Id,
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Title,
    string Link,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<MyRequestTask> CurrentTasks,
    IReadOnlyList<WaitingQuestion> Questions);

/// <summary><see cref="Status"/>: running, returned, approved, rejected, cancelled (default: running and returned). <see cref="Limit"/> 1 to 200.</summary>
public sealed record MyRequestsQuery(string? Status = null, int? Limit = null);

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
    long RowVersion,
    int EscalationLevel,
    DateTimeOffset? EscalatedAt);

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

/// <summary>
/// One row of the decision history (append-only): event, who, for whom, when, and its details. S1.12d: the display names
/// of the actor and of the person acted for (null when not known), for the inbox screen.
/// </summary>
public sealed record WorkflowHistoryView(Guid Id, Guid? TaskId, string Event, Guid? ActorId, Guid? ActingForUserId, DateTimeOffset At, JsonElement Details,
    string? ActorName = null, string? ActingForName = null);

/// <summary>
/// A declared field of the document type (manifest <c>fields</c>) with its value in the request (slice S1.12d Build item 20).
/// <see cref="DisplayName"/> is the person's display name of a <c>user</c> field; <see cref="DisplayNames"/> the name of a
/// <c>company</c> or <c>org_unit</c> field in each language; both null for other types or when not found.
/// </summary>
public sealed record WorkflowFieldValue(string Name, string Type, IReadOnlyDictionary<string, string> Label, JsonElement? Value, string? DisplayName,
    IReadOnlyDictionary<string, string>? DisplayNames);

/// <summary>
/// What the inbox screen shows beside a task (slice S1.12d Build items 20 and 21; FR-XM-32): the document type, the
/// requester and the person acted for by name, the declared fields, and the actions the engine would accept from the caller
/// on this task now (<c>approve</c>, <c>reject</c>, <c>return</c>, <c>ask</c>, <c>reassign</c>, <c>claim</c>, <c>release</c>);
/// the server still checks every decision. <see cref="ReasonRequired"/> lists the actions that need a reason.
/// </summary>
public sealed record WorkflowTaskContext(
    string Module,
    IReadOnlyDictionary<string, string> DocumentTypeName,
    string? RequesterName,
    string? ActingForName,
    bool Queue,
    bool BulkApproval,
    IReadOnlyList<WorkflowFieldValue> Fields,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ReasonRequired);

public sealed record WorkflowTaskDetail(WorkflowTaskView Task, WorkflowInstanceView Request, IReadOnlyList<WorkflowHistoryView> History, WorkflowTaskContext? Context = null);

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
    Task<IReadOnlyList<InboxItem>> InboxAsync(InboxQuery query, CancellationToken cancellationToken);

    Task<InboxCounts> CountsAsync(CancellationToken cancellationToken);

    Task<WorkflowTaskDetail?> TaskAsync(Guid taskId, CancellationToken cancellationToken);

    Task<DecisionResult> ApproveAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> RejectAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> ReturnAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> ReassignAsync(Guid taskId, ReassignRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> AskAsync(Guid taskId, AskRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> AnswerAsync(Guid instanceId, AnswerRequest request, CancellationToken cancellationToken);

    Task<DecisionResult> WithdrawAsync(Guid instanceId, WithdrawRequest request, CancellationToken cancellationToken);

    /// <summary>Takes a role-queue task (KRN-WFL-02 "queue with claim"): only one holder can have it.</summary>
    Task<DecisionResult> ClaimAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Gives a claimed queue task back to the role's queue (the claimer only, not while their question waits).</summary>
    Task<DecisionResult> ReleaseAsync(Guid taskId, CancellationToken cancellationToken);

    Task<BulkResult> BulkApproveAsync(BulkApproveRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<MyRequestView>> MyRequestsAsync(MyRequestsQuery query, CancellationToken cancellationToken);
}

// ====================================================================== administration (build item 9, ruling Q13)

/// <summary>
/// Monitor filters: <see cref="Status"/> (default running and returned), <see cref="Overdue"/> (a task past its time
/// limit), <see cref="CannotAssign"/> (a task nobody could be found for). <see cref="Limit"/> 1 to 500 (default 200);
/// <see cref="After"/> continues from the last id of the previous page (newest first).
/// </summary>
public sealed record MonitorQuery(
    string? DocumentType = null,
    string? Status = null,
    Guid? CompanyId = null,
    bool? Overdue = null,
    bool? CannotAssign = null,
    Guid? After = null,
    int? Limit = null);

/// <summary>A request as the monitor lists it.</summary>
public sealed record MonitorItem(
    Guid Id,
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    string Status,
    Guid RequesterUserId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyDictionary<string, string> Title,
    string Link,
    int OpenTasks,
    bool Overdue,
    bool CannotAssign);

/// <summary>A request with all its tasks and its decision history.</summary>
public sealed record RequestDetail(WorkflowInstanceView Request, IReadOnlyList<WorkflowTaskView> Tasks, IReadOnlyList<WorkflowHistoryView> History);

public sealed record CancelRequest(string? Reason);

/// <summary>An open task of a user, for the administrator (ADM-USR-03).</summary>
public sealed record UserOpenTask(
    Guid TaskId,
    Guid InstanceId,
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Title,
    string StepKey,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? DueAt,
    bool Overdue);

public sealed record ReassignAllRequest(Guid? ToUserId, string? Reason);

/// <summary>Administration of requests (ADM-USR-03, FR-XM-29; slice S1.10 ruling Q13).</summary>
public interface IWorkflowMonitor
{
    Task<IReadOnlyList<MonitorItem>> RequestsAsync(MonitorQuery query, CancellationToken cancellationToken);

    Task<RequestDetail?> RequestAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<DecisionResult> CancelAsync(Guid instanceId, CancelRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserOpenTask>> OpenTasksOfAsync(Guid userId, CancellationToken cancellationToken);

    Task<BulkResult> ReassignOpenTasksAsync(Guid userId, ReassignAllRequest request, CancellationToken cancellationToken);
}

// ====================================================================== delegation (build item 6, ruling Q8)

/// <summary>
/// A delegation: <see cref="FromUserId"/> (administrators only; self-service delegates the caller's own approvals) hands
/// approvals to <see cref="ToUserId"/> from the first day to the last day (whole days in the customer's time zone),
/// for the document types and companies listed (empty or absent: all).
/// </summary>
public sealed record DelegationRequest(
    Guid? FromUserId,
    Guid? ToUserId,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<string>? DocumentTypes,
    IReadOnlyList<Guid>? CompanyIds,
    string? Reason);

/// <summary>A delegation; <see cref="InForce"/> is true while it is active and today is within its days.</summary>
public sealed record DelegationView(
    Guid Id,
    Guid FromUserId,
    Guid ToUserId,
    DateOnly From,
    DateOnly To,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    IReadOnlyList<string> DocumentTypes,
    IReadOnlyList<Guid> CompanyIds,
    string? Reason,
    string Status,
    bool InForce,
    long RowVersion);

/// <summary>The caller's delegations: given to others, and received from others.</summary>
public sealed record MyDelegations(IReadOnlyList<DelegationView> Given, IReadOnlyList<DelegationView> Received);

public sealed record RevokeDelegationRequest(string? Reason);

/// <summary>Delegation and out-of-office substitution (KRN-WFL-05, HRM-SSP-07, ADM-ROL-09; ruling Q8).</summary>
public interface IDelegationService
{
    Task<MyDelegations> MineAsync(CancellationToken cancellationToken);

    Task<DelegationView> CreateMineAsync(DelegationRequest request, CancellationToken cancellationToken);

    Task<DelegationView> RevokeMineAsync(Guid id, RevokeDelegationRequest request, CancellationToken cancellationToken);

    /// <summary>An administrator delegates on behalf of <see cref="DelegationRequest.FromUserId"/>.</summary>
    Task<DelegationView> CreateAsync(DelegationRequest request, CancellationToken cancellationToken);

    Task<DelegationView> RevokeAsync(Guid id, RevokeDelegationRequest request, CancellationToken cancellationToken);
}
