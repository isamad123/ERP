using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// The kernel approval engine (slice S1.10 build items 3 to 5 and the inbox list; FR-KRN-WFL-01, 02, 03, 07, 08, 09, 10;
/// FR-XM-32; FR-HRM-ROL-04; FR-HRM-LVE-05).
/// <list type="bullet">
/// <item>A module starts a request inside its own transaction; the route version active at that moment is used to the
/// end (KRN-WFL-10). Values are frozen in the request (ruling Q5).</item>
/// <item>Steps run top to bottom; a step whose condition is false is skipped and the history says why (ruling Q3).</item>
/// <item>Nobody approves their own request (ruling Q6): the requester and the people the document is about are removed
/// from every step's approvers. A step left with only them is skipped when a later step will be decided by someone else,
/// otherwise it goes one level up (their manager), else to the step's fallback; if nobody can be found the task waits
/// unassigned ("cannot assign") for an administrator. A request never ends approved without a decision by someone else.</item>
/// <item>Several people found for one step each get a task; the first decision closes the others (ruling R1).</item>
/// <item>Decisions lock the request, then the task, so two people deciding the same request run one after the other.
/// The module's outcome handler runs in the same transaction as the final decision (ruling Q11).</item>
/// </list>
/// </summary>
public sealed class WorkflowEngine(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    ILicensedFeatures licence,
    WorkflowCatalog catalog,
    IApproverDirectory directory,
    IEnumerable<IWorkflowOutcomeHandler> handlers)
    : IWorkflowService, IWorkflowInbox
{
    public const string TaskNotFound = "errors.workflow.task_not_found";
    public const string RequestNotFound = "errors.workflow.request_not_found";

    private readonly ApproverResolution _people = new(directory);
    private readonly Dictionary<string, bool> _licensed = new(StringComparer.Ordinal);

    /// <summary>A request with what the engine needs to route it.</summary>
    private sealed record Request(InstanceRow Instance, WorkflowDocumentType Type, Route Route, IReadOnlyDictionary<string, RouteField> Fields, IReadOnlyDictionary<string, JsonElement> Values);

    // ================================================================== module side (IWorkflowService)

    public async Task<WorkflowStarted> StartAsync(WorkflowStartRequest request, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var work = Work(transaction);
        var type = catalog.Find(request.DocumentType) ?? throw new WorkflowException("errors.workflow.type_unknown");
        if (!await LicensedAsync(type, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowException("errors.workflow.type_not_licensed");
        }

        await LockDocumentAsync(work, type.Code, request.DocumentId, cancellationToken).ConfigureAwait(false);
        if (await work.OpenInstanceAsync(type.Code, request.DocumentId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new WorkflowException("errors.workflow.request_open");
        }

        Guid versionId;
        string json;
        await using (var command = work.Command("""
            select v.id, v.definition::text from wf.workflow_definitions d
            join wf.workflow_versions v on v.id = d.active_version_id and v.status = 'active'
            where d.document_type = $1 and d.status = 'active'
            """, type.Code))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException("errors.workflow.no_active_route"); // ruling R3
            }

            (versionId, json) = (r.GetGuid(0), r.GetString(1));
        }

        var fields = catalog.Fields(type.Code);
        var subjects = new HashSet<Guid>(request.SubjectUserIds);
        var values = PrepareValues(type, fields, request.Values, request.CompanyId, subjects, fixedSubjects: false);
        var route = ParseRoute(json, fields, type);
        var instance = new InstanceRow(work.NewId(), request.CompanyId, versionId, type.Code, request.DocumentId, work.Actor, [.. subjects.Order()],
            work.Now, null, Context(values), "running", 1);
        await work.ExecuteAsync("""
            insert into wf.workflow_instances (id, company_id, version_id, document_type, document_id, requester_user_id, subject_user_ids,
                started_at, context, status, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'running', $8, $6, $8, $6, 1)
            """, cancellationToken, instance.Id, instance.CompanyId, versionId, type.Code, instance.DocumentId, instance.RequesterUserId, instance.SubjectUserIds,
            work.Now, new JsonPayload(instance.ContextJson)).ConfigureAwait(false);
        work.Change("wf.workflow_instances", instance.Id, instance.CompanyId, "create",
            ("version_id", null, versionId), ("document_type", null, type.Code), ("document_id", null, instance.DocumentId),
            ("requester_user_id", null, instance.RequesterUserId), ("subject_user_ids", null, instance.SubjectUserIds), ("status", null, "running"));
        await work.HistoryAsync(instance.Id, null, "started", work.Actor, null, new { version_id = versionId, values }, cancellationToken).ConfigureAwait(false);

        var opened = await AdvanceAsync(work, new Request(instance, type, route, fields, values), 0, cancellationToken).ConfigureAwait(false);
        if (opened.Count == 0)
        {
            throw new WorkflowException("errors.workflow.no_step_applies"); // never approved without a decision (FR-XM-32)
        }

        await work.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new WorkflowStarted(instance.Id, "running", opened);
    }

    public async Task<WorkflowStarted> ResubmitAsync(string documentType, Guid documentId, IReadOnlyDictionary<string, JsonElement> values, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var work = Work(transaction);
        var type = catalog.Find(documentType) ?? throw new WorkflowException("errors.workflow.type_unknown");
        if (!await LicensedAsync(type, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowException("errors.workflow.type_not_licensed");
        }

        await LockDocumentAsync(work, type.Code, documentId, cancellationToken).ConfigureAwait(false);
        var instance = await work.OpenInstanceAsync(type.Code, documentId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Status != "returned")
        {
            throw new WorkflowException("errors.workflow.not_returned");
        }

        var fields = catalog.Fields(type.Code);
        var prepared = PrepareValues(type, fields, values, instance.CompanyId, [.. instance.SubjectUserIds], fixedSubjects: true);
        var json = await work.VersionJsonAsync(instance.VersionId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("request without version");
        var route = ParseRoute(json, fields, type);
        var context = Context(prepared);
        await work.ExecuteAsync("""
            update wf.workflow_instances set status = 'running', context = $2, completed_at = null, updated_at = $3, updated_by = $4, row_version = row_version + 1
            where id = $1
            """, cancellationToken, instance.Id, new JsonPayload(context), work.Now, work.Actor).ConfigureAwait(false);
        using (var before = JsonDocument.Parse(instance.ContextJson))
        using (var after = JsonDocument.Parse(context))
        {
            work.Change("wf.workflow_instances", instance.Id, instance.CompanyId, "status", ("status", "returned", "running"), ("context", before.RootElement.Clone(), after.RootElement.Clone()));
        }

        await work.HistoryAsync(instance.Id, null, "resubmitted", work.Actor, null, new { values = prepared }, cancellationToken).ConfigureAwait(false);
        var running = instance with { Status = "running", ContextJson = context };
        var opened = await AdvanceAsync(work, new Request(running, type, route, fields, prepared), 0, cancellationToken).ConfigureAwait(false);
        if (opened.Count == 0)
        {
            throw new WorkflowException("errors.workflow.no_step_applies");
        }

        await work.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new WorkflowStarted(instance.Id, "running", opened);
    }

    // ================================================================== routing

    /// <summary>Runs the route from step <paramref name="from"/> until a step gets a task; returns the tasks opened (none = route finished).</summary>
    private async Task<List<Guid>> AdvanceAsync(WorkflowWork work, Request request, int from, CancellationToken ct)
    {
        var steps = request.Route.Steps;
        for (var i = from; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!Applies(step, request))
            {
                await work.HistoryAsync(request.Instance.Id, null, "step_skipped", work.Actor, null, new { step = step.Key, reason = "condition_not_met" }, ct).ConfigureAwait(false);
                continue;
            }

            var (target, via) = await TargetAsync(work, request, step, ct).ConfigureAwait(false);
            var eligible = target.People.Where(p => !request.Instance.Excludes(p)).ToList();
            if (eligible.Count > 0)
            {
                return await AssignAsync(work, request, step, target.RoleId, eligible, via, ct).ConfigureAwait(false);
            }

            if (target.IsEmpty)
            {
                return [await CannotAssignAsync(work, request, step, "no_approver", ct).ConfigureAwait(false)];
            }

            // Only the requester or people the document is about were found (ruling Q6).
            if (await LaterStepDecidableAsync(work, request, i + 1, ct).ConfigureAwait(false))
            {
                await work.HistoryAsync(request.Instance.Id, null, "step_skipped", work.Actor, null, new { step = step.Key, reason = "approver_excluded" }, ct).ConfigureAwait(false);
                continue;
            }

            var up = (await _people.ManagersOfAsync(work, target.People.Where(request.Instance.Excludes), ct).ConfigureAwait(false))
                .Where(p => !request.Instance.Excludes(p)).ToList();
            if (up.Count > 0)
            {
                return await AssignAsync(work, request, step, null, up, "moved_up", ct).ConfigureAwait(false);
            }

            if (via != "fallback" && step.Fallback is not null)
            {
                var fallback = await _people.ResolveAsync(work, step.Fallback, request.Instance, request.Values, ct).ConfigureAwait(false);
                var others = fallback.People.Where(p => !request.Instance.Excludes(p)).ToList();
                if (others.Count > 0)
                {
                    return await AssignAsync(work, request, step, fallback.RoleId, others, "fallback", ct).ConfigureAwait(false);
                }
            }

            return [await CannotAssignAsync(work, request, step, "only_excluded", ct).ConfigureAwait(false)];
        }

        return [];
    }

    private static bool Applies(RouteStep step, Request request) =>
        step.Condition is null || ConditionEvaluator.Evaluate(step.Condition, request.Values, request.Fields);

    private async Task<(ApproverTarget Target, string Via)> TargetAsync(WorkflowWork work, Request request, RouteStep step, CancellationToken ct)
    {
        var target = await _people.ResolveAsync(work, step.Approver, request.Instance, request.Values, ct).ConfigureAwait(false);
        if (target.IsEmpty && step.Fallback is not null)
        {
            return (await _people.ResolveAsync(work, step.Fallback, request.Instance, request.Values, ct).ConfigureAwait(false), "fallback");
        }

        return (target, "approver");
    }

    /// <summary>Whether a later step applies and has someone other than the excluded people to decide it.</summary>
    private async Task<bool> LaterStepDecidableAsync(WorkflowWork work, Request request, int from, CancellationToken ct)
    {
        for (var k = from; k < request.Route.Steps.Count; k++)
        {
            var step = request.Route.Steps[k];
            if (Applies(step, request) && (await TargetAsync(work, request, step, ct).ConfigureAwait(false)).Target.People.Any(p => !request.Instance.Excludes(p)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A role queue gets one task; people get one task each (ruling R1).</summary>
    private static async Task<List<Guid>> AssignAsync(WorkflowWork work, Request request, RouteStep step, Guid? roleId, List<Guid> people, string via, CancellationToken ct)
    {
        if (roleId is { } role)
        {
            return [await InsertTaskAsync(work, request, step.Key, null, role, people, via, ct).ConfigureAwait(false)];
        }

        var ids = new List<Guid>();
        foreach (var person in people)
        {
            ids.Add(await InsertTaskAsync(work, request, step.Key, person, null, [person], via, ct).ConfigureAwait(false));
        }

        return ids;
    }

    private static async Task<Guid> InsertTaskAsync(WorkflowWork work, Request request, string stepKey, Guid? userId, Guid? roleId, IReadOnlyList<Guid> recipients, string via, CancellationToken ct)
    {
        var id = work.NewId();
        await work.ExecuteAsync("""
            insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, assignee_role_id, status, escalation_level, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, 'pending', 0, $6, $7, $6, $7, 1)
            """, ct, id, request.Instance.Id, stepKey, userId, roleId, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", id, request.Instance.CompanyId, "create",
            ("instance_id", null, request.Instance.Id), ("step_key", null, stepKey), ("assignee_user_id", null, userId), ("assignee_role_id", null, roleId), ("status", null, "pending"));
        await work.HistoryAsync(request.Instance.Id, id, "task_assigned", work.Actor, null, new { step = stepKey, assignee_user_id = userId, assignee_role_id = roleId, via }, ct).ConfigureAwait(false);
        await work.RaiseAsync(WorkflowEvents.TaskAssigned, "wf.workflow_task", id, Payload(request, id, recipients, null), ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Nobody can decide the step: the task waits unassigned for an administrator (ruling Q6), never approved.</summary>
    private static async Task<Guid> CannotAssignAsync(WorkflowWork work, Request request, RouteStep step, string reason, CancellationToken ct)
    {
        var id = work.NewId();
        await work.ExecuteAsync("""
            insert into wf.workflow_tasks (id, instance_id, step_key, status, escalation_level, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, 'pending', 0, $4, $5, $4, $5, 1)
            """, ct, id, request.Instance.Id, step.Key, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", id, request.Instance.CompanyId, "create", ("instance_id", null, request.Instance.Id), ("step_key", null, step.Key), ("status", null, "pending"));
        await work.HistoryAsync(request.Instance.Id, id, "cannot_assign", work.Actor, null, new { step = step.Key, reason }, ct).ConfigureAwait(false);
        return id;
    }

    // ================================================================== decisions (IWorkflowInbox)

    public Task<DecisionResult> ApproveAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, ct) =>
        {
            await ActOnTaskAsync(work, req, task, "approved", Trim(request?.Comment), ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "approved", work.Actor, null, new { step = task.StepKey, comment = Trim(request?.Comment) }, ct).ConfigureAwait(false);
            await CloseOthersAsync(work, req, task.Id, "decided_by_other", ct).ConfigureAwait(false);
            var index = req.Route.Steps.ToList().FindIndex(s => s.Key == task.StepKey);
            var opened = await AdvanceAsync(work, req, index + 1, ct).ConfigureAwait(false);
            if (opened.Count == 0)
            {
                await CompleteAsync(work, req, WorkflowOutcomes.Approved, Trim(request?.Comment), ct).ConfigureAwait(false);
                return new DecisionResult(req.Instance.Id, "approved", []);
            }

            return new DecisionResult(req.Instance.Id, "running", opened);
        }, cancellationToken);

    public Task<DecisionResult> RejectAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, ct) =>
        {
            var reason = Trim(request?.Comment) ?? throw new WorkflowException("errors.workflow.reason_required");
            await ActOnTaskAsync(work, req, task, "rejected", reason, ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "rejected", work.Actor, null, new { step = task.StepKey, reason }, ct).ConfigureAwait(false);
            await CloseOthersAsync(work, req, task.Id, "decided_by_other", ct).ConfigureAwait(false);
            await CompleteAsync(work, req, WorkflowOutcomes.Rejected, reason, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "rejected", []);
        }, cancellationToken);

    public Task<DecisionResult> ReturnAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, ct) =>
        {
            var reason = Trim(request?.Comment) ?? throw new WorkflowException("errors.workflow.reason_required");
            await ActOnTaskAsync(work, req, task, "changes_requested", reason, ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "returned", work.Actor, null, new { step = task.StepKey, reason }, ct).ConfigureAwait(false);
            await CloseOthersAsync(work, req, task.Id, "decided_by_other", ct).ConfigureAwait(false);
            await CompleteAsync(work, req, WorkflowOutcomes.Returned, reason, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "returned", []);
        }, cancellationToken);

    public Task<DecisionResult> ReassignAsync(Guid taskId, ReassignRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, ct) =>
        {
            var reason = Trim(request?.Reason) ?? throw new WorkflowException("errors.workflow.reason_required");
            var to = request!.ToUserId ?? throw new WorkflowException("errors.workflow.reassign_target_required");
            if (to == work.Actor)
            {
                throw new WorkflowException("errors.workflow.reassign_to_self");
            }

            if (req.Instance.Excludes(to))
            {
                throw new WorkflowException("errors.workflow.assignee_excluded"); // FR-XM-32: not even by reassignment
            }

            if (!await ApproverResolution.ActiveUserAsync(work, to, ct).ConfigureAwait(false))
            {
                throw new WorkflowException("errors.workflow.assignee_not_active");
            }

            if (!await CanDecideAsync(work, req, to, ct).ConfigureAwait(false))
            {
                throw new WorkflowException("errors.workflow.assignee_cannot_decide");
            }

            await ActOnTaskAsync(work, req, task, "reassigned", reason, ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "reassigned", work.Actor, null, new { step = task.StepKey, to_user_id = to, reason }, ct).ConfigureAwait(false);
            var opened = await InsertTaskAsync(work, req, task.StepKey, to, null, [to], "reassigned", ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "running", [.. (await OpenTasksAsync(work, req.Instance.Id, ct).ConfigureAwait(false)).Where(id => id != task.Id).Append(opened).Distinct()]);
        }, cancellationToken);

    public Task<DecisionResult> AskAsync(Guid taskId, AskRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, ct) =>
        {
            var question = Trim(request?.Question) ?? throw new WorkflowException("errors.workflow.question_required");
            if (task.InfoRequestedAt is not null)
            {
                throw new WorkflowException("errors.workflow.info_pending");
            }

            await work.ExecuteAsync("""
                update wf.workflow_tasks set assignee_user_id = coalesce(assignee_user_id, $2), claimed_at = case when assignee_user_id is null then $3 else claimed_at end,
                    info_requested_at = $3, info_requested_by = $2, updated_at = $3, updated_by = $2, row_version = row_version + 1
                where id = $1
                """, ct, task.Id, work.Actor, work.Now).ConfigureAwait(false);
            work.Change("wf.workflow_tasks", task.Id, req.Instance.CompanyId, "update", ("info_requested_at", null, work.Now), ("info_requested_by", null, work.Actor),
                ("assignee_user_id", task.AssigneeUserId, task.AssigneeUserId ?? work.Actor));
            await work.HistoryAsync(req.Instance.Id, task.Id, "info_requested", work.Actor, null, new { step = task.StepKey, question }, ct).ConfigureAwait(false);
            await work.RaiseAsync(WorkflowEvents.InfoRequested, "wf.workflow_instance", req.Instance.Id, Payload(req, task.Id, [req.Instance.RequesterUserId], null), ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "running", await OpenTasksAsync(work, req.Instance.Id, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<DecisionResult> AnswerAsync(Guid instanceId, AnswerRequest request, CancellationToken cancellationToken) =>
        RequesterActionAsync(instanceId, async (work, req, ct) =>
        {
            var answer = Trim(request?.Answer) ?? throw new WorkflowException("errors.workflow.answer_required");
            if (req.Instance.Status != "running")
            {
                throw new WorkflowException("errors.workflow.request_not_running");
            }

            var waiting = (await work.TasksOfAsync(req.Instance.Id, ct).ConfigureAwait(false)).Where(t => t.Status == "pending" && t.InfoRequestedAt is not null).ToList();
            if (waiting.Count == 0)
            {
                throw new WorkflowException("errors.workflow.no_question");
            }

            foreach (var task in waiting)
            {
                await work.ExecuteAsync("""
                    update wf.workflow_tasks set info_requested_at = null, info_requested_by = null, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
                    """, ct, task.Id, work.Now, work.Actor).ConfigureAwait(false);
                work.Change("wf.workflow_tasks", task.Id, req.Instance.CompanyId, "update", ("info_requested_at", task.InfoRequestedAt, null), ("info_requested_by", task.InfoRequestedBy, null));
                await work.HistoryAsync(req.Instance.Id, task.Id, "info_answered", work.Actor, null, new { step = task.StepKey, answer }, ct).ConfigureAwait(false);
            }

            return new DecisionResult(req.Instance.Id, "running", await OpenTasksAsync(work, req.Instance.Id, ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<DecisionResult> WithdrawAsync(Guid instanceId, WithdrawRequest request, CancellationToken cancellationToken) =>
        RequesterActionAsync(instanceId, async (work, req, ct) =>
        {
            if (req.Instance.Status is not ("running" or "returned"))
            {
                throw new WorkflowException("errors.workflow.request_not_open");
            }

            var reason = Trim(request?.Reason);
            await CloseOthersAsync(work, req, null, "withdrawn", ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, null, "withdrawn", work.Actor, null, new { reason }, ct).ConfigureAwait(false);
            await CompleteAsync(work, req, WorkflowOutcomes.Cancelled, reason, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "cancelled", []);
        }, cancellationToken);

    /// <summary>
    /// One decision on one task, in its own transaction: the request is locked, then the task; the caller must be the task's
    /// assignee or, for a queue task, an eligible holder of its role (who then claims it); never the requester or a subject;
    /// and must hold the document type's decide permission for the request's company.
    /// </summary>
    private async Task<DecisionResult> DecideAsync(Guid taskId, Func<WorkflowWork, Request, TaskRow, CancellationToken, Task<DecisionResult>> action, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var instanceId = await work.ScalarAsync<Guid?>("select instance_id from wf.workflow_tasks where id = $1", ct, taskId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(TaskNotFound);
        var instance = await work.InstanceAsync(instanceId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(TaskNotFound);
        var task = await work.TaskAsync(taskId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(TaskNotFound);
        var request = await LoadAsync(work, instance, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(TaskNotFound);
        if (!await MayDecideAsync(work, request, task, work.Actor, ct).ConfigureAwait(false))
        {
            // A holder of the queue who lost the race to another holder is told it was decided (409), not "not found".
            if (task.Status != "pending" && task.AssigneeRoleId is not null && task.ClaimedAt is not null
                && await MayDecideAsync(work, request, task with { AssigneeUserId = null }, work.Actor, ct).ConfigureAwait(false))
            {
                throw new WorkflowException("errors.workflow.task_not_pending");
            }

            throw new KeyNotFoundException(TaskNotFound);
        }

        if (task.Status != "pending")
        {
            throw new WorkflowException("errors.workflow.task_not_pending");
        }

        if (instance.Status != "running")
        {
            throw new WorkflowException("errors.workflow.request_not_running");
        }

        if (!await CanDecideAsync(work, request, work.Actor, ct).ConfigureAwait(false))
        {
            throw new WorkflowException("errors.workflow.no_decide_permission");
        }

        var result = await action(work, request, task, ct).ConfigureAwait(false);
        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>An action of the requester on their own request (answer a question, withdraw).</summary>
    private async Task<DecisionResult> RequesterActionAsync(Guid instanceId, Func<WorkflowWork, Request, CancellationToken, Task<DecisionResult>> action, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var instance = await work.InstanceAsync(instanceId, lockRow: true, ct).ConfigureAwait(false);
        if (instance is null || instance.RequesterUserId != work.Actor || await LoadAsync(work, instance, ct).ConfigureAwait(false) is not { } request)
        {
            throw new KeyNotFoundException(RequestNotFound);
        }

        var result = await action(work, request, ct).ConfigureAwait(false);
        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Whether the user may act on the task: its assignee, or (unassigned queue task) an eligible holder of its role.</summary>
    private static async Task<bool> MayDecideAsync(WorkflowWork work, Request request, TaskRow task, Guid userId, CancellationToken ct)
    {
        if (request.Instance.Excludes(userId))
        {
            return false;
        }

        if (task.AssigneeUserId is { } assignee)
        {
            return assignee == userId;
        }

        return task.AssigneeRoleId is { } role
            && (await ApproverResolution.RoleHoldersAsync(work, role, request.Instance.CompanyId, ApproverResolution.Today(work), ct).ConfigureAwait(false)).Contains(userId);
    }

    /// <summary>The right to decide (ruling Q14): <c>wf.workflow_tasks.update</c> and the document type's own decide permission, for the request's company.</summary>
    private static async Task<bool> CanDecideAsync(WorkflowWork work, Request request, Guid userId, CancellationToken ct) =>
        await ApproverResolution.HoldsAsync(work, userId, WorkflowPermissions.TasksDecide, request.Instance.CompanyId, ct).ConfigureAwait(false)
        && (request.Type.DecidePermission is not { } own || await ApproverResolution.HoldsAsync(work, userId, own, request.Instance.CompanyId, ct).ConfigureAwait(false));

    /// <summary>Records the caller's decision on the task (claiming a queue task in the same statement).</summary>
    private static async Task ActOnTaskAsync(WorkflowWork work, Request request, TaskRow task, string status, string? comment, CancellationToken ct)
    {
        await work.ExecuteAsync("""
            update wf.workflow_tasks set assignee_user_id = coalesce(assignee_user_id, $2), claimed_at = case when assignee_user_id is null then $3 else claimed_at end,
                status = $4, acted_by = $2, acted_at = $3, comment = $5, updated_at = $3, updated_by = $2, row_version = row_version + 1
            where id = $1
            """, ct, task.Id, work.Actor, work.Now, status, comment).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", task.Id, request.Instance.CompanyId, "status", ("status", task.Status, status), ("acted_by", null, work.Actor),
            ("assignee_user_id", task.AssigneeUserId, task.AssigneeUserId ?? work.Actor), ("comment", task.Comment, comment));
    }

    /// <summary>Closes the request's other open tasks (another holder decided, or the request ended).</summary>
    private static async Task CloseOthersAsync(WorkflowWork work, Request request, Guid? keep, string reason, CancellationToken ct)
    {
        foreach (var other in (await work.TasksOfAsync(request.Instance.Id, ct).ConfigureAwait(false)).Where(t => t.Status == "pending" && t.Id != keep))
        {
            await work.ExecuteAsync("""
                update wf.workflow_tasks set status = 'skipped', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
                """, ct, other.Id, work.Now, work.Actor).ConfigureAwait(false);
            work.Change("wf.workflow_tasks", other.Id, request.Instance.CompanyId, "status", ("status", "pending", "skipped"));
            await work.HistoryAsync(request.Instance.Id, other.Id, "task_closed", work.Actor, null, new { step = other.StepKey, reason }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ends (or returns) the request, tells the module in the same transaction, and raises the event.</summary>
    private async Task CompleteAsync(WorkflowWork work, Request request, string outcome, string? comment, CancellationToken ct)
    {
        var status = outcome; // approved, rejected, returned, cancelled are also the request's statuses
        await work.ExecuteAsync("""
            update wf.workflow_instances set status = $2, completed_at = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
            """, ct, request.Instance.Id, status, outcome == WorkflowOutcomes.Returned ? null : work.Now, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_instances", request.Instance.Id, request.Instance.CompanyId, "status", ("status", request.Instance.Status, status));
        await work.HistoryAsync(request.Instance.Id, null, "completed", work.Actor, null, new { outcome }, ct).ConfigureAwait(false);

        var handler = handlers.FirstOrDefault(h => h.DocumentType == request.Type.Code)
            ?? throw new InvalidOperationException($"No workflow outcome handler is registered for document type {request.Type.Code}.");
        await handler.OnOutcomeAsync(new WorkflowOutcome(request.Instance.Id, request.Type.Code, request.Instance.DocumentId, request.Instance.CompanyId, outcome, work.Actor, null, comment, work.Now),
            work.Transaction, ct).ConfigureAwait(false);

        var people = request.Instance.SubjectUserIds.Prepend(request.Instance.RequesterUserId).Distinct().ToList();
        await work.RaiseAsync(outcome == WorkflowOutcomes.Returned ? WorkflowEvents.InstanceReturned : WorkflowEvents.InstanceCompleted, "wf.workflow_instance", request.Instance.Id,
            Payload(request, null, people, outcome), ct).ConfigureAwait(false);
    }

    private static async Task<List<Guid>> OpenTasksAsync(WorkflowWork work, Guid instanceId, CancellationToken ct) =>
        [.. (await work.TasksOfAsync(instanceId, ct).ConfigureAwait(false)).Where(t => t.Status == "pending").Select(t => t.Id)];

    // ================================================================== reading (IWorkflowInbox)

    public async Task<IReadOnlyList<InboxItem>> InboxAsync(CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var rows = new List<(Guid TaskId, string Step, Guid? Assignee, Guid? Role, DateTimeOffset Created, DateTimeOffset? Due, bool Waiting, Guid InstanceId)>();
        await using (var command = work.Command(Access.AccessSnapshot.AssignmentsCte + """
            select t.id, t.step_key, t.assignee_user_id, t.assignee_role_id, t.created_at, t.due_at, t.info_requested_at is not null, t.instance_id
            from wf.workflow_tasks t
            join wf.workflow_instances i on i.id = t.instance_id and i.status = 'running'
            where t.status = 'pending'
              and (t.assignee_user_id = $1
                   or (t.assignee_user_id is null and t.assignee_role_id is not null
                       and exists (select 1 from valid v where v.role_id = t.assignee_role_id and (v.company_id is null or v.company_id = i.company_id))
                       and i.requester_user_id <> $1 and not ($1 = any(i.subject_user_ids))))
            order by t.created_at, t.id
            limit 1000
            """, work.Actor, ApproverResolution.Today(work)))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((r.GetGuid(0), r.GetString(1), WorkflowWork.NullableGuid(r, 2), WorkflowWork.NullableGuid(r, 3), r.GetFieldValue<DateTimeOffset>(4),
                    WorkflowWork.NullableTime(r, 5), r.GetBoolean(6), r.GetGuid(7)));
            }
        }

        var items = new List<InboxItem>();
        var requests = new Dictionary<Guid, Request?>();
        foreach (var row in rows)
        {
            if (!requests.TryGetValue(row.InstanceId, out var request))
            {
                var instance = await work.InstanceAsync(row.InstanceId, lockRow: false, cancellationToken).ConfigureAwait(false);
                requests[row.InstanceId] = request = instance is null ? null : await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false);
            }

            if (request is null)
            {
                continue; // document type not registered or not licensed (ruling Q11)
            }

            items.Add(new InboxItem(row.TaskId, request.Instance.Id, request.Type.Code, request.Instance.DocumentId, request.Instance.CompanyId, Titles(request),
                Link(request), row.Step, StepName(request, row.Step), request.Instance.RequesterUserId, row.Created, row.Due, row.Assignee is null, row.Role, row.Waiting));
        }

        return items;
    }

    public async Task<WorkflowTaskDetail?> TaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        if (await work.TaskAsync(taskId, lockRow: false, cancellationToken).ConfigureAwait(false) is not { } task
            || await work.InstanceAsync(task.InstanceId, lockRow: false, cancellationToken).ConfigureAwait(false) is not { } instance
            || await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false) is not { } request
            || !await MayDecideAsync(work, request, task, work.Actor, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var history = new List<WorkflowHistoryView>();
        await using (var command = work.Command("select id, task_id, event, actor_id, acting_for_user_id, created_at, details::text from wf.workflow_history where instance_id = $1 order by created_at, id", instance.Id))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                using var details = JsonDocument.Parse(r.GetString(6));
                history.Add(new WorkflowHistoryView(r.GetGuid(0), WorkflowWork.NullableGuid(r, 1), r.GetString(2), WorkflowWork.NullableGuid(r, 3), WorkflowWork.NullableGuid(r, 4),
                    r.GetFieldValue<DateTimeOffset>(5), details.RootElement.Clone()));
            }
        }

        return new WorkflowTaskDetail(TaskView(request, task), InstanceView(request), history);
    }

    internal static WorkflowTaskView TaskViewOf(IReadOnlyDictionary<string, string> stepName, TaskRow t) =>
        new(t.Id, t.StepKey, stepName, t.Status, t.AssigneeUserId, t.AssigneeRoleId, t.ActingForUserId, t.ActedBy, t.ActedAt, t.Comment, t.DueAt, t.InfoRequestedAt, t.InfoRequestedBy, t.ClaimedAt, t.RowVersion);

    private static WorkflowTaskView TaskView(Request request, TaskRow t) => TaskViewOf(StepName(request, t.StepKey), t);

    private static WorkflowInstanceView InstanceView(Request request)
    {
        var i = request.Instance;
        using var context = JsonDocument.Parse(i.ContextJson);
        var values = context.RootElement.TryGetProperty("values", out var v) ? v.Clone() : JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        return new WorkflowInstanceView(i.Id, i.VersionId, i.DocumentType, i.DocumentId, i.CompanyId, i.Status, i.RequesterUserId, i.SubjectUserIds, i.StartedAt, i.CompletedAt,
            values, Titles(request), Link(request));
    }

    // ================================================================== helpers

    /// <summary>The request with its document type and route; null when the type is not registered or not licensed (hidden, ruling Q11).</summary>
    private async Task<Request?> LoadAsync(WorkflowWork work, InstanceRow instance, CancellationToken ct)
    {
        if (catalog.Find(instance.DocumentType) is not { } type || !await LicensedAsync(type, ct).ConfigureAwait(false))
        {
            return null;
        }

        var fields = catalog.Fields(type.Code);
        var json = await work.VersionJsonAsync(instance.VersionId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("request without version");
        return new Request(instance, type, ParseRoute(json, fields, type), fields, instance.Values());
    }

    private async Task<bool> LicensedAsync(WorkflowDocumentType type, CancellationToken ct)
    {
        if (!_licensed.TryGetValue(type.EntitlementKey, out var licensed))
        {
            _licensed[type.EntitlementKey] = licensed = await licence.IsLicensedAsync(type.EntitlementKey, ct).ConfigureAwait(false);
        }

        return licensed;
    }

    /// <summary>
    /// A stored (frozen) route version. It was checked when activated; the bulk-approval flag is an authoring check only,
    /// so a module that later stops allowing bulk approval does not break requests already running on an older version.
    /// </summary>
    private static Route ParseRoute(string json, IReadOnlyDictionary<string, RouteField> fields, WorkflowDocumentType type)
    {
        _ = type;
        using var doc = JsonDocument.Parse(json);
        var (route, problems) = RouteFormat.Parse(doc.RootElement, fields, bulkAllowed: true);
        return route ?? throw new WorkflowException("errors.workflow.route_invalid", [.. problems.Select(p => p.ToString())]);
    }

    /// <summary>
    /// The values a request keeps: only declared fields of the right type; the company field equals the request's company
    /// (filled in when absent); every subject field names one of the request's subjects (ruling Q6). At start the subject
    /// fields add to the subjects; at resubmit the subjects are fixed.
    /// </summary>
    private static Dictionary<string, JsonElement> PrepareValues(WorkflowDocumentType type, IReadOnlyDictionary<string, RouteField> fields, IReadOnlyDictionary<string, JsonElement> given,
        Guid companyId, HashSet<Guid> subjects, bool fixedSubjects)
    {
        ArgumentNullException.ThrowIfNull(given);
        if (RouteFormat.CheckValues(given, fields) is { Count: > 0 } problems)
        {
            throw new WorkflowException("errors.workflow.values_invalid", [.. problems.Select(p => p.ToString())]);
        }

        var values = given.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (values.TryGetValue(type.CompanyField, out var company) && company.ValueKind != JsonValueKind.Null)
        {
            if (Guid.Parse(company.GetString()!) != companyId)
            {
                throw new WorkflowException("errors.workflow.values_disagree");
            }
        }
        else
        {
            values[type.CompanyField] = JsonSerializer.SerializeToElement(companyId.ToString());
        }

        foreach (var field in type.SubjectFields)
        {
            if (values.TryGetValue(field, out var subject) && subject.ValueKind != JsonValueKind.Null)
            {
                var id = Guid.Parse(subject.GetString()!);
                if (fixedSubjects && !subjects.Contains(id))
                {
                    throw new WorkflowException("errors.workflow.values_disagree");
                }

                subjects.Add(id);
            }
        }

        return values;
    }

    private static string Context(IReadOnlyDictionary<string, JsonElement> values) =>
        JsonSerializer.Serialize(new { values = values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value) }, WorkflowWork.Json);

    /// <summary>One open request per document: concurrent starts of the same document wait for each other.</summary>
    private static async Task LockDocumentAsync(WorkflowWork work, string type, Guid documentId, CancellationToken ct) =>
        await work.ExecuteAsync("select pg_advisory_xact_lock(hashtext('wf.workflow_instances'), hashtext($1))", ct, type + ":" + documentId).ConfigureAwait(false);

    private WorkflowWork Work(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction { Connection: { } connection } tx)
        {
            throw new ArgumentException("The workflow engine needs the caller's open PostgreSQL transaction.", nameof(transaction));
        }

        return new WorkflowWork(connection, tx, clock.UtcNow, actor.UserId);
    }

    private static Dictionary<string, string> Titles(Request request) =>
        request.Type.Title.ToDictionary(p => p.Key, p => WorkflowCatalog.Render(p.Value, request.Instance.DocumentId, request.Values), StringComparer.Ordinal);

    private static string Link(Request request) => WorkflowCatalog.Render(request.Type.Link, request.Instance.DocumentId, request.Values);

    private static IReadOnlyDictionary<string, string> StepName(Request request, string key) =>
        request.Route.Steps.FirstOrDefault(s => s.Key == key)?.Name ?? new Dictionary<string, string>();

    private static object Payload(Request request, Guid? taskId, IReadOnlyList<Guid> recipients, string? outcome) => new
    {
        instance_id = request.Instance.Id,
        task_id = taskId,
        document_type = request.Type.Code,
        document_id = request.Instance.DocumentId,
        company_id = request.Instance.CompanyId,
        requester_user_id = request.Instance.RequesterUserId,
        recipients,
        outcome,
        link = Link(request),
    };

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
