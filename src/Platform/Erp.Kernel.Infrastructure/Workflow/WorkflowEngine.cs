using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Generated;
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
/// <item>Delegation (slice S1.10b, ruling Q8): a delegate decides a task assigned to the delegator with the delegator's
/// right to decide; the task, the history and the outcome record "acted by the delegate, for the delegator".</item>
/// <item>Every task gets a time limit in working days (ruling Q9); escalation is <see cref="EscalateAsync"/>.</item>
/// </list>
/// The engine is split over partial files: this one (module side, routing, decisions), <c>WorkflowEngine.Reading.cs</c>
/// (inbox, counts, my requests, task details), <c>WorkflowEngine.Escalation.cs</c> and <c>WorkflowEngine.Monitor.cs</c>
/// (administration and the FR-XM-29 query).
/// </summary>
public sealed partial class WorkflowEngine(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    ILicensedFeatures licence,
    WorkflowCatalog catalog,
    IApproverDirectory directory,
    IEnumerable<IWorkflowOutcomeHandler> handlers)
    : IWorkflowService, IWorkflowInbox, IWorkflowMonitor, IWorkflowQueries
{
    public const string TaskNotFound = "errors.workflow.task_not_found";
    public const string RequestNotFound = "errors.workflow.request_not_found";
    public const string NotRequester = "errors.workflow.not_requester";

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

        // Ruling Q7: the requester corrects and resubmits. Anyone else could write the values and then approve them
        // (FR-XM-32; review S1.10a finding 3, CR-059).
        if (instance.RequesterUserId != work.Actor)
        {
            throw new WorkflowException(NotRequester);
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

            var up = (await DecidersAsync(work, request, new ApproverTarget(await _people.ManagersOfAsync(work, target.People.Where(request.Instance.Excludes), ct).ConfigureAwait(false), null), ct)
                .ConfigureAwait(false)).People.Where(p => !request.Instance.Excludes(p)).ToList();
            if (up.Count > 0)
            {
                return await AssignAsync(work, request, step, null, up, "moved_up", ct).ConfigureAwait(false);
            }

            if (via != "fallback" && step.Fallback is not null)
            {
                var fallback = await DecidersAsync(work, request, await _people.ResolveAsync(work, step.Fallback, request.Instance, request.Type, request.Values, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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

    /// <summary>
    /// The people a step names who may decide the request (its approver, else its fallback). Someone without the right to
    /// decide the request's company counts as not found, so the fallback, moving up and "cannot assign" apply instead of a
    /// task nobody can decide (review S1.10a finding 2, CR-059).
    /// </summary>
    private async Task<(ApproverTarget Target, string Via)> TargetAsync(WorkflowWork work, Request request, RouteStep step, CancellationToken ct)
    {
        var target = await DecidersAsync(work, request, await _people.ResolveAsync(work, step.Approver, request.Instance, request.Type, request.Values, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (target.IsEmpty && step.Fallback is not null)
        {
            return (await DecidersAsync(work, request, await _people.ResolveAsync(work, step.Fallback, request.Instance, request.Type, request.Values, ct).ConfigureAwait(false), ct).ConfigureAwait(false), "fallback");
        }

        return (target, "approver");
    }

    /// <summary>The people of a target who hold the right to decide the request (<see cref="CanDecideAsync"/>); none: nobody.</summary>
    private static async Task<ApproverTarget> DecidersAsync(WorkflowWork work, Request request, ApproverTarget target, CancellationToken ct)
    {
        var people = new List<Guid>();
        foreach (var person in target.People)
        {
            if (await CanDecideAsync(work, request, person, ct).ConfigureAwait(false))
            {
                people.Add(person);
            }
        }

        return people.Count == 0 ? ApproverTarget.Nobody : new ApproverTarget(people, target.RoleId);
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
    private static async Task<List<Guid>> AssignAsync(WorkflowWork work, Request request, RouteStep step, Guid? roleId, List<Guid> people, string via, CancellationToken ct,
        int level = 0, TaskRow? from = null)
    {
        if (roleId is { } role)
        {
            return [await InsertTaskAsync(work, request, step.Key, null, role, people, via, ct, level, from).ConfigureAwait(false)];
        }

        var ids = new List<Guid>();
        foreach (var person in people)
        {
            ids.Add(await InsertTaskAsync(work, request, step.Key, person, null, [person], via, ct, level, from).ConfigureAwait(false));
        }

        return ids;
    }

    /// <summary>
    /// Opens a task with its time limit (ruling Q9): the step's working days, else the company's default. A task opened in
    /// place of <paramref name="from"/> (reassignment, escalation) keeps a question still waiting for the requester (ruling P6).
    /// </summary>
    private static async Task<Guid> InsertTaskAsync(WorkflowWork work, Request request, string stepKey, Guid? userId, Guid? roleId, IReadOnlyList<Guid> recipients, string via,
        CancellationToken ct, int level = 0, TaskRow? from = null)
    {
        var id = work.NewId();
        var due = await DueAtAsync(work, request, stepKey, ct).ConfigureAwait(false);
        var (askedAt, askedBy) = from is { InfoRequestedAt: { } at } ? (at, from.InfoRequestedBy) : ((DateTimeOffset?)null, (Guid?)null);
        await work.ExecuteAsync("""
            insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, assignee_role_id, status, due_at, escalation_level, info_requested_at, info_requested_by,
                created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, 'pending', $6, $7, $8, $9, $10, $11, $10, $11, 1)
            """, ct, id, request.Instance.Id, stepKey, userId, roleId, due, level, askedAt, askedBy, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", id, request.Instance.CompanyId, "create",
            ("instance_id", null, request.Instance.Id), ("step_key", null, stepKey), ("assignee_user_id", null, userId), ("assignee_role_id", null, roleId), ("status", null, "pending"),
            ("due_at", null, due), ("escalation_level", null, level));
        await work.HistoryAsync(request.Instance.Id, id, "task_assigned", work.Actor, null, new { step = stepKey, assignee_user_id = userId, assignee_role_id = roleId, via, due_at = due, escalation_level = level }, ct).ConfigureAwait(false);
        await work.RaiseAsync(WorkflowEvents.TaskAssigned, "wf.workflow_task", id, Payload(request, id, recipients, null), ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Nobody can decide the step: the task waits unassigned for an administrator (ruling Q6), never approved.</summary>
    private static async Task<Guid> CannotAssignAsync(WorkflowWork work, Request request, RouteStep step, string reason, CancellationToken ct)
    {
        var id = work.NewId();
        var due = await DueAtAsync(work, request, step.Key, ct).ConfigureAwait(false);
        await work.ExecuteAsync("""
            insert into wf.workflow_tasks (id, instance_id, step_key, status, due_at, escalation_level, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, 'pending', $4, 0, $5, $6, $5, $6, 1)
            """, ct, id, request.Instance.Id, step.Key, due, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", id, request.Instance.CompanyId, "create", ("instance_id", null, request.Instance.Id), ("step_key", null, step.Key), ("status", null, "pending"), ("due_at", null, due));
        await work.HistoryAsync(request.Instance.Id, id, "cannot_assign", work.Actor, null, new { step = step.Key, reason }, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>When a task opened now on the step becomes late (ruling Q9, S1.10b ruling P3).</summary>
    private static async Task<DateTimeOffset> DueAtAsync(WorkflowWork work, Request request, string stepKey, CancellationToken ct, DateTimeOffset? arrived = null)
    {
        var calendar = await WorkflowTime.CalendarAsync(work, request.Instance.CompanyId, ct).ConfigureAwait(false);
        var step = request.Route.Steps.FirstOrDefault(s => s.Key == stepKey);
        return calendar.DueAt(arrived ?? work.Now, step?.TimeLimitWorkingDays);
    }

    // ================================================================== decisions (IWorkflowInbox)

    public Task<DecisionResult> ApproveAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken) =>
        ApproveCoreAsync(taskId, Trim(request?.Comment), bulk: false, cancellationToken);

    private Task<DecisionResult> ApproveCoreAsync(Guid taskId, string? comment, bool bulk, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, actingFor, ct) =>
        {
            await ActOnTaskAsync(work, req, task, "approved", comment, actingFor, ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "approved", work.Actor, actingFor, new { step = task.StepKey, comment, bulk }, ct).ConfigureAwait(false);
            await CloseOthersAsync(work, req, task.Id, "decided_by_other", ct).ConfigureAwait(false);
            var index = req.Route.Steps.ToList().FindIndex(s => s.Key == task.StepKey);
            var opened = await AdvanceAsync(work, req, index + 1, ct).ConfigureAwait(false);
            if (opened.Count == 0)
            {
                await CompleteAsync(work, req, WorkflowOutcomes.Approved, comment, actingFor, ct).ConfigureAwait(false);
                return new DecisionResult(req.Instance.Id, "approved", []);
            }

            return new DecisionResult(req.Instance.Id, "running", opened);
        }, cancellationToken, requireBulk: bulk);

    public Task<DecisionResult> RejectAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, actingFor, ct) =>
        {
            var reason = Trim(request?.Comment) ?? throw new WorkflowException("errors.workflow.reason_required");
            await ActOnTaskAsync(work, req, task, "rejected", reason, actingFor, ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "rejected", work.Actor, actingFor, new { step = task.StepKey, reason }, ct).ConfigureAwait(false);
            await CloseOthersAsync(work, req, task.Id, "decided_by_other", ct).ConfigureAwait(false);
            await CompleteAsync(work, req, WorkflowOutcomes.Rejected, reason, actingFor, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "rejected", []);
        }, cancellationToken);

    public Task<DecisionResult> ReturnAsync(Guid taskId, DecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, actingFor, ct) =>
        {
            var reason = Trim(request?.Comment) ?? throw new WorkflowException("errors.workflow.reason_required");
            await ActOnTaskAsync(work, req, task, "changes_requested", reason, actingFor, ct).ConfigureAwait(false);
            await work.HistoryAsync(req.Instance.Id, task.Id, "returned", work.Actor, actingFor, new { step = task.StepKey, reason }, ct).ConfigureAwait(false);
            await CloseOthersAsync(work, req, task.Id, "decided_by_other", ct).ConfigureAwait(false);
            await CompleteAsync(work, req, WorkflowOutcomes.Returned, reason, actingFor, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "returned", []);
        }, cancellationToken);

    /// <summary>
    /// Passes the task to another eligible person (ruling Q7): by its assignee (or their delegate), or by an administrator
    /// holding <c>wf.workflow_tasks.reassign</c> for the request's company (ruling Q13; also a task nobody could be found for).
    /// </summary>
    public Task<DecisionResult> ReassignAsync(Guid taskId, ReassignRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, (work, req, task, actingFor, ct) => ReassignCoreAsync(work, req, task, actingFor, request?.ToUserId, Trim(request?.Reason), ct), cancellationToken, allowAdministrator: true);

    private static async Task<DecisionResult> ReassignCoreAsync(WorkflowWork work, Request req, TaskRow task, Guid? actingFor, Guid? toUserId, string? reason, CancellationToken ct)
    {
        if (reason is null)
        {
            throw new WorkflowException("errors.workflow.reason_required");
        }

        var to = toUserId ?? throw new WorkflowException("errors.workflow.reassign_target_required");
        if (to == work.Actor || to == task.AssigneeUserId)
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

        // Several people may hold tasks of one step (ruling R1): never a second open task of the step for the same person
        // (S1.10b review finding 5; escalation already avoids it).
        if ((await work.TasksOfAsync(req.Instance.Id, ct).ConfigureAwait(false)).Any(t => t.Status == "pending" && t.StepKey == task.StepKey && t.Id != task.Id && t.AssigneeUserId == to))
        {
            throw new WorkflowException("errors.workflow.reassign_target_has_task");
        }

        await ActOnTaskAsync(work, req, task, "reassigned", reason, actingFor, ct, claim: false).ConfigureAwait(false);
        await work.HistoryAsync(req.Instance.Id, task.Id, "reassigned", work.Actor, actingFor, new { step = task.StepKey, to_user_id = to, reason }, ct).ConfigureAwait(false);
        var opened = await InsertTaskAsync(work, req, task.StepKey, to, null, [to], "reassigned", ct, task.EscalationLevel, task).ConfigureAwait(false);
        return new DecisionResult(req.Instance.Id, "running", [.. (await OpenTasksAsync(work, req.Instance.Id, ct).ConfigureAwait(false)).Where(id => id != task.Id).Append(opened).Distinct()]);
    }

    public Task<DecisionResult> AskAsync(Guid taskId, AskRequest request, CancellationToken cancellationToken) =>
        DecideAsync(taskId, async (work, req, task, actingFor, ct) =>
        {
            var question = Trim(request?.Question) ?? throw new WorkflowException("errors.workflow.question_required");
            if (task.InfoRequestedAt is not null)
            {
                throw new WorkflowException("errors.workflow.info_pending");
            }

            var asked = await work.ScalarAsync<DateTime>("""
                update wf.workflow_tasks set assignee_user_id = coalesce(assignee_user_id, $2), claimed_at = case when assignee_user_id is null then $3 else claimed_at end,
                    info_requested_at = $3, info_requested_by = $2, updated_at = $3, updated_by = $2, row_version = row_version + 1
                where id = $1
                returning info_requested_at
                """, ct, task.Id, work.Actor, work.Now).ConfigureAwait(false);
            var askedAt = new DateTimeOffset(DateTime.SpecifyKind(asked, DateTimeKind.Utc));
            work.Change("wf.workflow_tasks", task.Id, req.Instance.CompanyId, "update", ("info_requested_at", null, work.Now), ("info_requested_by", null, work.Actor),
                ("assignee_user_id", task.AssigneeUserId, task.AssigneeUserId ?? work.Actor));
            // asked_at is the task's stored info_requested_at, so the question is found again for a task that inherited it (P6; review finding 8).
            await work.HistoryAsync(req.Instance.Id, task.Id, "info_requested", work.Actor, actingFor, new { step = task.StepKey, question, asked_at = askedAt }, ct).ConfigureAwait(false);
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
            await CompleteAsync(work, req, WorkflowOutcomes.Cancelled, reason, null, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "cancelled", []);
        }, cancellationToken);

    /// <summary>
    /// Takes a role-queue task (KRN-WFL-02; slice S1.10 ruling Q4): an eligible holder of the role who may decide the request
    /// becomes its assignee. The request, then the task, are locked, so of two holders claiming at once one wins and the
    /// other is told it was taken (409).
    /// </summary>
    public Task<DecisionResult> ClaimAsync(Guid taskId, CancellationToken cancellationToken) =>
        OnTaskAsync(taskId, async (work, req, task, ct) =>
        {
            if (task.AssigneeRoleId is null || req.Instance.Excludes(work.Actor)
                || !await MayTakeFromQueueAsync(work, req, task, task.AssigneeRoleId.Value, work.Actor, ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException(TaskNotFound);
            }

            if (task.Status != "pending")
            {
                throw new WorkflowException("errors.workflow.task_not_pending");
            }

            if (task.AssigneeUserId is not null)
            {
                throw new WorkflowException("errors.workflow.task_claimed");
            }

            RunningOnly(req);
            if (!await CanDecideAsync(work, req, work.Actor, ct).ConfigureAwait(false))
            {
                throw new WorkflowException("errors.workflow.no_decide_permission");
            }

            await work.ExecuteAsync("""
                update wf.workflow_tasks set assignee_user_id = $2, claimed_at = $3, updated_at = $3, updated_by = $2, row_version = row_version + 1 where id = $1
                """, ct, task.Id, work.Actor, work.Now).ConfigureAwait(false);
            work.Change("wf.workflow_tasks", task.Id, req.Instance.CompanyId, "update", ("assignee_user_id", null, work.Actor), ("claimed_at", null, work.Now));
            await work.HistoryAsync(req.Instance.Id, task.Id, "claimed", work.Actor, null, new { step = task.StepKey, role_id = task.AssigneeRoleId }, ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "running", await OpenTasksAsync(work, req.Instance.Id, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Gives a claimed queue task back to the role's queue: only the claimer, and not while their question waits.</summary>
    public Task<DecisionResult> ReleaseAsync(Guid taskId, CancellationToken cancellationToken) =>
        OnTaskAsync(taskId, async (work, req, task, ct) =>
        {
            if (task.AssigneeRoleId is null || task.ClaimedAt is null || task.AssigneeUserId != work.Actor)
            {
                throw new KeyNotFoundException(TaskNotFound);
            }

            if (task.Status != "pending")
            {
                throw new WorkflowException("errors.workflow.task_not_pending");
            }

            RunningOnly(req);
            if (task.InfoRequestedAt is not null)
            {
                throw new WorkflowException("errors.workflow.info_pending");
            }

            await work.ExecuteAsync("""
                update wf.workflow_tasks set assignee_user_id = null, claimed_at = null, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
                """, ct, task.Id, work.Now, work.Actor).ConfigureAwait(false);
            work.Change("wf.workflow_tasks", task.Id, req.Instance.CompanyId, "update", ("assignee_user_id", work.Actor, null), ("claimed_at", task.ClaimedAt, null));
            await work.HistoryAsync(req.Instance.Id, task.Id, "released", work.Actor, null, new { step = task.StepKey, role_id = task.AssigneeRoleId }, ct).ConfigureAwait(false);
            var holders = await ApproverResolution.RoleHoldersAsync(work, task.AssigneeRoleId.Value, req.Instance.CompanyId, ApproverResolution.Today(work), ct).ConfigureAwait(false);
            await work.RaiseAsync(WorkflowEvents.TaskAssigned, "wf.workflow_task", task.Id, Payload(req, task.Id, [.. holders.Where(p => !req.Instance.Excludes(p))], null), ct).ConfigureAwait(false);
            return new DecisionResult(req.Instance.Id, "running", await OpenTasksAsync(work, req.Instance.Id, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// Approves several tasks (KRN-WFL-08; ruling Q12): at most <c>wf.bulk_approval.max_items</c>; each task is checked and
    /// decided in its own transaction exactly as a single approval, and only where the document type and the step allow
    /// bulk approval. One result per task.
    /// </summary>
    public async Task<BulkResult> BulkApproveAsync(BulkApproveRequest request, CancellationToken cancellationToken)
    {
        var ids = (request?.TaskIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new WorkflowException("errors.workflow.bulk_empty");
        }

        if (ids.Count > await IntSettingAsync(KernelSettingKeys.WfBulkApprovalMaxItems, 100, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowException("errors.workflow.bulk_too_many");
        }

        var comment = Trim(request!.Comment);
        var results = new List<TaskActionResult>();
        foreach (var id in ids)
        {
            results.Add(await OneAsync(id, () => ApproveCoreAsync(id, comment, bulk: true, cancellationToken)).ConfigureAwait(false));
        }

        return new BulkResult(results.Count(r => r.Outcome == "approved"), results.Count(r => r.Outcome == "refused"), results);
    }

    /// <summary>One task of a bulk action: its result, or the refusal's key (the other tasks go on).</summary>
    private static async Task<TaskActionResult> OneAsync(Guid taskId, Func<Task<DecisionResult>> action, string done = "approved")
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            return new TaskActionResult(taskId, done, result.Status, null);
        }
        catch (WorkflowException ex)
        {
            return new TaskActionResult(taskId, "refused", null, ex.ErrorKey);
        }
        catch (KeyNotFoundException ex) when (ex.Message.StartsWith("errors.", StringComparison.Ordinal))
        {
            return new TaskActionResult(taskId, "refused", null, ex.Message);
        }
    }

    /// <summary>A customer-wide whole-number setting, read on its own connection.</summary>
    private async Task<int> IntSettingAsync(string key, int fallback, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        var resolved = await Settings.SettingsStore.ResolveAsync(c, null, [key], null, null, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct).ConfigureAwait(false);
        return resolved.TryGetValue(key, out var v) && v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var n) ? n : fallback;
    }

    /// <summary>
    /// One decision on one task, in its own transaction: the request is locked, then the task. The caller must be the
    /// task's assignee, an active delegate of the assignee (deciding with the assignee's rights, ruling Q8), or, for a queue
    /// task nobody has claimed, an eligible holder of its role (who then claims it); never the requester or a subject; and
    /// must hold the document type's decide permission for the request's company (for a delegate: the delegator must).
    /// With <paramref name="allowAdministrator"/>, an administrator holding <c>wf.workflow_tasks.reassign</c> for the
    /// request's company may act too (reassignment, ruling Q13). With <paramref name="requireBulk"/>, the document type and
    /// the step must allow bulk approval.
    /// </summary>
    private async Task<DecisionResult> DecideAsync(Guid taskId, Func<WorkflowWork, Request, TaskRow, Guid?, CancellationToken, Task<DecisionResult>> action, CancellationToken ct,
        bool allowAdministrator = false, bool requireBulk = false) =>
        await OnTaskAsync(taskId, async (work, request, task, c) =>
        {
            var capacity = await CapacityAsync(work, request, task, work.Actor, c).ConfigureAwait(false);
            var administrator = false;
            if (capacity is null && allowAdministrator && !request.Instance.Excludes(work.Actor)
                && (await WorkflowCoverage.ForAsync(work, work.Actor, WorkflowPermissions.TasksReassign, c).ConfigureAwait(false)).CoversCompany(request.Instance.CompanyId))
            {
                administrator = true;
            }
            else if (capacity is null)
            {
                // A holder of the queue who lost the race to another holder is told it was taken (409), not "not found".
                if (task.AssigneeRoleId is not null && task.ClaimedAt is not null && !request.Instance.Excludes(work.Actor)
                    && await MayTakeFromQueueAsync(work, request, task, task.AssigneeRoleId.Value, work.Actor, c).ConfigureAwait(false))
                {
                    throw new WorkflowException(task.Status == "pending" ? "errors.workflow.task_claimed" : "errors.workflow.task_not_pending");
                }

                throw new KeyNotFoundException(TaskNotFound);
            }

            if (task.Status != "pending")
            {
                throw new WorkflowException("errors.workflow.task_not_pending");
            }

            RunningOnly(request);
            if (requireBulk && !(request.Type.BulkApproval && request.Route.Steps.FirstOrDefault(s => s.Key == task.StepKey)?.BulkApproval == true))
            {
                throw new WorkflowException("errors.workflow.bulk_not_allowed");
            }

            if (!administrator && !await CanDecideAsync(work, request, capacity!.ActingFor ?? work.Actor, c).ConfigureAwait(false))
            {
                throw new WorkflowException("errors.workflow.no_decide_permission");
            }

            return await action(work, request, task, capacity?.ActingFor, c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    /// <summary>Loads and locks a task's request (first) and the task (second) in a new transaction, runs the action and commits.</summary>
    private async Task<DecisionResult> OnTaskAsync(Guid taskId, Func<WorkflowWork, Request, TaskRow, CancellationToken, Task<DecisionResult>> action, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var instanceId = await work.ScalarAsync<Guid?>("select instance_id from wf.workflow_tasks where id = $1", ct, taskId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(TaskNotFound);
        var instance = await work.InstanceAsync(instanceId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(TaskNotFound);
        var task = await work.TaskAsync(taskId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(TaskNotFound);
        var request = await LoadAsync(work, instance, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(TaskNotFound);
        var result = await action(work, request, task, ct).ConfigureAwait(false);
        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private static void RunningOnly(Request request)
    {
        if (request.Instance.Status != "running")
        {
            throw new WorkflowException("errors.workflow.request_not_running");
        }
    }

    /// <summary>An action of the requester on their own request (answer a question, withdraw).</summary>
    private Task<DecisionResult> RequesterActionAsync(Guid instanceId, Func<WorkflowWork, Request, CancellationToken, Task<DecisionResult>> action, CancellationToken ct) =>
        OnceMoreOnDeadlockAsync(() => RequesterActionOnceAsync(instanceId, action, ct));

    /// <summary>
    /// Runs an engine-owned unit of work, and once more when PostgreSQL ended it as a deadlock victim (SQLSTATE 40P01). Withdraw
    /// and cancel lock the request and then (in the module's outcome handler) the document, while a module's resubmit locks
    /// its document and then the request; the engine's side retries so the person does not see a server error (S1.10b review
    /// finding 6). The first attempt rolled back completely, so the second starts from the committed state.
    /// </summary>
    internal static async Task<T> OnceMoreOnDeadlockAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            return await action().ConfigureAwait(false);
        }
    }

    private async Task<DecisionResult> RequesterActionOnceAsync(Guid instanceId, Func<WorkflowWork, Request, CancellationToken, Task<DecisionResult>> action, CancellationToken ct)
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

    /// <summary>In what capacity a user may act on a task; <see cref="ActingFor"/> is the delegator for a delegate.</summary>
    private sealed record Capacity(Guid? ActingFor);

    /// <summary>
    /// Whether, and for whom, the user may act on the task: its assignee; an active delegate of its assignee for this
    /// request's document type and company (ruling Q8; tasks the assignee holds personally, ruling P2); or, for a queue
    /// task nobody has claimed, an eligible holder of its role. Never the requester or a subject (FR-XM-32).
    /// </summary>
    private static async Task<Capacity?> CapacityAsync(WorkflowWork work, Request request, TaskRow task, Guid userId, CancellationToken ct)
    {
        if (request.Instance.Excludes(userId))
        {
            return null;
        }

        if (task.AssigneeUserId is { } assignee)
        {
            if (assignee == userId)
            {
                return new Capacity(null);
            }

            return await DelegatedAsync(work, assignee, userId, request.Instance.DocumentType, request.Instance.CompanyId, ct).ConfigureAwait(false) ? new Capacity(assignee) : null;
        }

        return task.AssigneeRoleId is { } role && await MayTakeFromQueueAsync(work, request, task, role, userId, ct).ConfigureAwait(false)
            ? new Capacity(null)
            : null;
    }

    /// <summary>
    /// Whether the user may take a role-queue task: an eligible holder of its role, and not a person whose own task on this
    /// step of the request was escalated (S1.10b review finding 3: "the original assignee can no longer decide it").
    /// </summary>
    private static async Task<bool> MayTakeFromQueueAsync(WorkflowWork work, Request request, TaskRow task, Guid role, Guid userId, CancellationToken ct) =>
        (await ApproverResolution.RoleHoldersAsync(work, role, request.Instance.CompanyId, ApproverResolution.Today(work), ct).ConfigureAwait(false)).Contains(userId)
        && await work.ScalarAsync<bool>("select " + NotEscalatedFrom("$1", "$2", "$3"), ct, userId, task.InstanceId, task.StepKey).ConfigureAwait(false);

    /// <summary>
    /// The SQL test "the user's own task on this step of this request was not escalated" (S1.10b review finding 3), for
    /// role-queue tasks. A person an administrator reassigns the task to by name is not affected.
    /// </summary>
    internal static string NotEscalatedFrom(string user, string instance, string step) => $"""
        not exists (select 1 from wf.workflow_tasks et
                    where et.instance_id = {instance} and et.step_key = {step} and et.status = 'escalated' and et.assignee_user_id = {user})
        """;

    /// <summary>
    /// The SQL test "an active delegation from <c>{from}</c> to <c>{to}</c> covers the request now" (ruling Q8): status active,
    /// now within its time, the request's document type and company inside its lists (an empty list: all). Only the
    /// delegator's own tasks are matched, so a delegation is never passed on. It applies only while the delegator is an
    /// active user: a disabled or archived user's rights are not used by anyone (S1.10b review finding 1); their open tasks
    /// are reassigned by an administrator (FR-ADM-USR-03) or move up when late.
    /// </summary>
    internal static string DelegationCovers(string from, string to, string now, string documentType, string company) => $"""
        exists (select 1 from iam.delegations dl
                join iam.users du on du.id = dl.from_user_id and du.status = 'active' and not du.is_deleted
                where dl.from_user_id = {from} and dl.to_user_id = {to} and dl.status = 'active'
                  and dl.valid_from <= {now} and dl.valid_to > {now}
                  and (jsonb_array_length(coalesce(dl.scope -> 'document_types', '[]'::jsonb)) = 0 or (dl.scope -> 'document_types') ? {documentType})
                  and (jsonb_array_length(coalesce(dl.scope -> 'company_ids', '[]'::jsonb)) = 0 or (dl.scope -> 'company_ids') ? ({company})::text))
        """;

    private static Task<bool> DelegatedAsync(WorkflowWork work, Guid from, Guid to, string documentType, Guid companyId, CancellationToken ct) =>
        work.ScalarAsync<bool>("select " + DelegationCovers("$1", "$2", "$3", "$4", "$5"), ct, from, to, work.Now, documentType, companyId);

    /// <summary>The right to decide (ruling Q14): <c>wf.workflow_tasks.update</c> and the document type's own decide permission, for the request's company.</summary>
    private static async Task<bool> CanDecideAsync(WorkflowWork work, Request request, Guid userId, CancellationToken ct) =>
        await ApproverResolution.HoldsAsync(work, userId, WorkflowPermissions.TasksDecide, request.Instance.CompanyId, ct).ConfigureAwait(false)
        && (request.Type.DecidePermission is not { } own || await ApproverResolution.HoldsAsync(work, userId, own, request.Instance.CompanyId, ct).ConfigureAwait(false));

    /// <summary>
    /// Records the caller's action on the task, and for whom (a delegate's delegator). Deciding an unclaimed queue task
    /// claims it in the same statement; a reassignment by an administrator does not.
    /// </summary>
    private static async Task ActOnTaskAsync(WorkflowWork work, Request request, TaskRow task, string status, string? comment, Guid? actingFor, CancellationToken ct, bool claim = true)
    {
        await work.ExecuteAsync("""
            update wf.workflow_tasks set assignee_user_id = case when $7 then coalesce(assignee_user_id, $2) else assignee_user_id end,
                claimed_at = case when $7 and assignee_user_id is null and assignee_role_id is not null then $3 else claimed_at end,
                status = $4, acted_by = $2, acted_at = $3, comment = $5, acting_for_user_id = $6, updated_at = $3, updated_by = $2, row_version = row_version + 1
            where id = $1
            """, ct, task.Id, work.Actor, work.Now, status, comment, actingFor, claim).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", task.Id, request.Instance.CompanyId, "status", ("status", task.Status, status), ("acted_by", null, work.Actor), ("acting_for_user_id", null, actingFor),
            ("assignee_user_id", task.AssigneeUserId, claim ? task.AssigneeUserId ?? work.Actor : task.AssigneeUserId), ("comment", task.Comment, comment));
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
    private async Task CompleteAsync(WorkflowWork work, Request request, string outcome, string? comment, Guid? actingFor, CancellationToken ct)
    {
        var status = outcome; // approved, rejected, returned, cancelled are also the request's statuses
        await work.ExecuteAsync("""
            update wf.workflow_instances set status = $2, completed_at = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
            """, ct, request.Instance.Id, status, outcome == WorkflowOutcomes.Returned ? null : work.Now, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_instances", request.Instance.Id, request.Instance.CompanyId, "status", ("status", request.Instance.Status, status));
        await work.HistoryAsync(request.Instance.Id, null, "completed", work.Actor, actingFor, new { outcome }, ct).ConfigureAwait(false);

        var handler = handlers.FirstOrDefault(h => h.DocumentType == request.Type.Code)
            ?? throw new InvalidOperationException($"No workflow outcome handler is registered for document type {request.Type.Code}.");
        await handler.OnOutcomeAsync(new WorkflowOutcome(request.Instance.Id, request.Type.Code, request.Instance.DocumentId, request.Instance.CompanyId, outcome, work.Actor, actingFor, comment, work.Now),
            work.Transaction, ct).ConfigureAwait(false);

        var people = request.Instance.SubjectUserIds.Prepend(request.Instance.RequesterUserId).Distinct().ToList();
        await work.RaiseAsync(outcome == WorkflowOutcomes.Returned ? WorkflowEvents.InstanceReturned : WorkflowEvents.InstanceCompleted, "wf.workflow_instance", request.Instance.Id,
            Payload(request, null, people, outcome), ct).ConfigureAwait(false);
    }

    private static async Task<List<Guid>> OpenTasksAsync(WorkflowWork work, Guid instanceId, CancellationToken ct) =>
        [.. (await work.TasksOfAsync(instanceId, ct).ConfigureAwait(false)).Where(t => t.Status == "pending").Select(t => t.Id)];

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
