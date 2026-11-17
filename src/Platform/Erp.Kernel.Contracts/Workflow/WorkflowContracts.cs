using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Workflow;

/// <summary>
/// What a module sends for approval (slice S1.10 ruling Q11): its document, the document's company, the people the
/// document is about (excluded from approving, like the requester, ruling Q6) and the values the route's conditions may
/// test, keyed by the field names the document type declares. The values are frozen in the request (ruling Q5).
/// </summary>
public sealed record WorkflowStartRequest(
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    IReadOnlyList<Guid> SubjectUserIds,
    IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>The request the engine opened (or resumed): its id, status and the first open tasks.</summary>
public sealed record WorkflowStarted(Guid InstanceId, string Status, IReadOnlyList<Guid> OpenTaskIds);

/// <summary>
/// The kernel workflow engine as modules use it (FR-KRN-WFL-01, FR-HRM-LVE-05; slice S1.10 ruling Q11). In-process only.
/// Both calls run inside the module's own open transaction, so the document and its request are saved together or not at
/// all. The requester is the signed-in user. Refusals are <see cref="WorkflowException"/> with a localization key.
/// </summary>
public interface IWorkflowService
{
    /// <summary>Opens a request on the document type's active route (refused when no route is active, ruling R3).</summary>
    Task<WorkflowStarted> StartAsync(WorkflowStartRequest request, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a returned request back after the requester corrected the document (ruling Q7): the route starts again from
    /// the first step on the same version, with the new values; the history is kept. Requester and subjects stay.
    /// </summary>
    Task<WorkflowStarted> ResubmitAsync(string documentType, Guid documentId, IReadOnlyDictionary<string, JsonElement> values, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>How a request ended, as the module's outcome handler receives it.</summary>
public static class WorkflowOutcomes
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Returned = "returned";
    public const string Cancelled = "cancelled";
}

/// <summary>The end (or return) of a request: who decided, for whom, when and why.</summary>
public sealed record WorkflowOutcome(
    Guid InstanceId,
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    string Outcome,
    Guid DecidedBy,
    Guid? ActingFor,
    string? Comment,
    DateTimeOffset DecidedAt);

/// <summary>
/// A module's reaction to the end of its requests (ruling Q11). Called in the same transaction as the final decision, so
/// the document's status and the request never disagree: if the handler throws, the decision is rolled back. One handler
/// per document type.
/// </summary>
public interface IWorkflowOutcomeHandler
{
    string DocumentType { get; }

    Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>Approver kinds the organisation directory answers (slice S1.10 ruling Q4).</summary>
public static class DirectoryKinds
{
    public const string Manager = "manager";
    public const string DepartmentHead = "department_head";
    public const string Position = "position";
}

/// <summary>
/// The questions about the organisation an approval route needs (KRN-WFL-02; slice S1.10 ruling Q4). Managers, department
/// heads and positions live in HR (D-10), so the HR module implements this (S1.20 manager and department head, S1.22
/// position); the kernel sees user ids only. Without it, a route using these kinds cannot be activated.
/// Every answer is as of a business date.
/// </summary>
public interface IApproverDirectory
{
    /// <summary>Whether the directory can answer the kind (<see cref="DirectoryKinds"/>).</summary>
    bool Supports(string kind);

    /// <summary>The user(s) <paramref name="levels"/> manager levels above the user (1 = line manager).</summary>
    Task<IReadOnlyList<Guid>> ManagersAsync(Guid userId, int levels, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>The head(s) of the user's department in the company.</summary>
    Task<IReadOnlyList<Guid>> DepartmentHeadsAsync(Guid userId, Guid companyId, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>The users holding the position (a reference the HR module understands) in the company.</summary>
    Task<IReadOnlyList<Guid>> PositionHoldersAsync(string position, Guid companyId, DateOnly asOf, CancellationToken cancellationToken);
}

/// <summary>Field types a document type may declare for its route conditions (ruling Q5).</summary>
public static class WorkflowFieldTypes
{
    public const string Number = "number";
    public const string Money = "money";
    public const string Date = "date";
    public const string Text = "text";
    public const string Choice = "choice";
    public const string Bool = "bool";
    public const string Company = "company";
    public const string OrgUnit = "org_unit";
    public const string User = "user";

    public static IReadOnlyList<string> All { get; } = [Number, Money, Date, Text, Choice, Bool, Company, OrgUnit, User];
}

/// <summary>One field a document type offers to route conditions and approver resolution.</summary>
public sealed record WorkflowField(string Name, string Type, IReadOnlyDictionary<string, string> Label, IReadOnlyList<string> Choices);

/// <summary>
/// A document type a module sends for approval (slice S1.10 build item 1, ruling Q11), from the module's manifest
/// <c>Access/workflow.json</c>. <see cref="Title"/> and <see cref="Link"/> are templates with <c>{field}</c> and
/// <c>{document_id}</c> placeholders. <see cref="DefaultRoute"/> is the route format of <c>docs/registers/contracts.md</c>.
/// </summary>
public sealed record WorkflowDocumentType(
    string Code,
    string Module,
    string EntitlementKey,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyList<WorkflowField> Fields,
    string CompanyField,
    IReadOnlyList<string> SubjectFields,
    IReadOnlyDictionary<string, string> Title,
    string Link,
    bool BulkApproval,
    bool RequiresApproval,
    string? DecidePermission,
    JsonElement DefaultRoute);

/// <summary>A module's document types, registered by the host (like numbering's <c>IDocumentTypeSource</c>).</summary>
public interface IWorkflowDocumentTypeSource
{
    IReadOnlyList<WorkflowDocumentType> Types { get; }
}

/// <summary>A workflow rule refused the request; <see cref="Exception.Message"/> is a localization key (<c>errors.workflow.*</c>).</summary>
public sealed class WorkflowException : Exception
{
    public WorkflowException(string errorKey)
        : base(errorKey) => ErrorKey = errorKey;

    public WorkflowException(string errorKey, IReadOnlyList<string> details)
        : base(errorKey)
    {
        ErrorKey = errorKey;
        Details = details;
    }

    public string ErrorKey { get; }

    /// <summary>Further keys (for example the problems of a route check).</summary>
    public IReadOnlyList<string> Details { get; } = [];
}

/// <summary>Events of the engine in <c>wf.outbox_messages</c> (ruling Q15); S1.11 turns them into notices.</summary>
public static class WorkflowEvents
{
    public const string TaskAssigned = "kernel.workflow.task_assigned";
    public const string InfoRequested = "kernel.workflow.info_requested";
    public const string InstanceReturned = "kernel.workflow.instance_returned";
    public const string InstanceCompleted = "kernel.workflow.instance_completed";
    public const string VersionActivated = "kernel.workflow.version_activated";

    /// <summary>A task passed its time limit and moved to the next level (FR-XM-32, HRM-NTF-11; slice S1.10b, ruling Q9).</summary>
    public const string TaskEscalated = "kernel.workflow.task_escalated";
    public const string DefinitionDeactivated = "kernel.workflow.definition_deactivated";
}

/// <summary>
/// Permissions of the workflow endpoints (ruling Q14). The generated writes of the <c>wf.*</c> tables and of
/// <c>iam.delegations</c> are closed (CR-041); these codes stay because the purpose-built services use them.
/// </summary>
public static class WorkflowPermissions
{
    public const string RoutesRead = "wf.workflow_definitions.read";
    public const string RoutesUpdate = "wf.workflow_definitions.update";
    public const string TasksRead = "wf.workflow_tasks.read";
    public const string TasksDecide = "wf.workflow_tasks.update";
    public const string RequestsRead = "wf.workflow_instances.read";
    public const string DelegationsRead = "iam.delegations.read";
    public const string DelegationsCreate = "iam.delegations.create";
    public const string DelegationsUpdate = "iam.delegations.update";

    /// <summary>Pass any task of a request in the administrator's companies to another person (slice S1.10b, ruling Q13).</summary>
    public const string TasksReassign = "wf.workflow_tasks.reassign";

    /// <summary>Cancel a running or returned request of the administrator's companies, with a reason (ruling Q13).</summary>
    public const string RequestsCancel = "wf.workflow_instances.cancel";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(RoutesUpdate, "wf", "permissions.wf.workflow_definitions.update", false),
        new(TasksDecide, "wf", "permissions.wf.workflow_tasks.update", false),
        new(TasksReassign, "wf", "permissions.wf.workflow_tasks.reassign", false),
        new(RequestsCancel, "wf", "permissions.wf.workflow_instances.cancel", false),
        new(DelegationsCreate, "iam", "permissions.iam.delegations.create", false),
        new(DelegationsUpdate, "iam", "permissions.iam.delegations.update", false),
    ];
}

/// <summary>A task waiting for the person's decision (FR-XM-29).</summary>
public sealed record OpenApprovalTask(Guid TaskId, Guid InstanceId, string DocumentType, Guid DocumentId, Guid CompanyId, string StepKey, DateTimeOffset? DueAt);

/// <summary>A request of the person, or about the person, that has not ended (running, or returned for correction).</summary>
public sealed record OpenApprovalRequest(Guid InstanceId, string DocumentType, Guid DocumentId, Guid CompanyId, string Status, Guid RequesterUserId, bool IsRequester);

/// <summary>What is still open for one person in the approval engine.</summary>
public sealed record OpenApprovals(IReadOnlyList<OpenApprovalTask> TasksToDecide, IReadOnlyList<OpenApprovalRequest> OpenRequests)
{
    public bool Any => TasksToDecide.Count > 0 || OpenRequests.Count > 0;
}

/// <summary>
/// Questions other modules ask the approval engine (slice S1.10b, ruling Q13). Final settlement (S1.25) is blocked while a
/// person has open approvals (FR-XM-29): tasks they must still decide, and requests they raised or that are about them.
/// Every document type is counted, licensed or not. Read on the caller's transaction when given.
/// </summary>
public interface IWorkflowQueries
{
    Task<OpenApprovals> OpenApprovalsOfAsync(Guid userId, DbTransaction? transaction, CancellationToken cancellationToken);
}
