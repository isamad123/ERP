using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>What one escalation run did for one customer.</summary>
public sealed record EscalationRun(int DueDatesSet, int Escalated, int NotPossible);

/// <summary>
/// Escalation (FR-XM-32, FR-HRM-NTF-11; slice S1.10 ruling Q9, S1.10b rulings P3 and P6). Run every minute by the worker
/// host (and by the operator command <c>workflow escalate</c>) until S1.11 moves it into jobs. Acts as the system user.
/// <list type="bullet">
/// <item>Open tasks without a time limit (opened before S1.10b) get one, counted from when they arrived.</item>
/// <item>A task past its time limit becomes <c>escalated</c> once (it can no longer be decided) and the next level gets a
/// new task with a fresh limit: the assignee's line manager (<see cref="IApproverDirectory"/>), else the step's escalation
/// user or role. The requester and the people the request is about are never chosen. A question still waiting moves to
/// the new task (ruling P6). Event <c>kernel.workflow.task_escalated</c>.</item>
/// <item>After <see cref="MaxEscalationLevels"/> levels, or when nobody is above, the task stays open, is marked as
/// handled (<c>escalated_at</c>) so it is not tried again, and is listed as overdue for administrators.</item>
/// <item>Nothing is ever approved or rejected automatically.</item>
/// </list>
/// Each task is handled in its own transaction, locking the request and then the task as decisions do, so a decision and
/// an escalation of the same task run one after the other. Tasks of document types that are not registered or not
/// licensed are left alone.
/// </summary>
public sealed partial class WorkflowEngine
{
    public const int MaxEscalationLevels = 3;

    private const int EscalationPage = 200;

    public async Task<EscalationRun> EscalateAsync(CancellationToken cancellationToken)
    {
        var dueSet = 0;
        var escalated = 0;
        var notPossible = 0;
        var now = clock.UtcNow;

        // 1. Time limits for open tasks that have none.
        foreach (var taskId in await PagedTaskIdsAsync("""
            select t.id from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id and i.status = 'running'
            where t.status = 'pending' and t.due_at is null and t.id > $1 order by t.id limit $2
            """, now, cancellationToken).ConfigureAwait(false))
        {
            if (await SystemTaskAsync(taskId, now, async (work, request, task, ct) =>
                {
                    if (task.Status != "pending" || task.DueAt is not null)
                    {
                        return false;
                    }

                    var due = await DueAtAsync(work, request, task.StepKey, ct, task.CreatedAt).ConfigureAwait(false);
                    await work.ExecuteAsync("update wf.workflow_tasks set due_at = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", ct,
                        task.Id, due, work.Now, work.Actor).ConfigureAwait(false);
                    work.Change("wf.workflow_tasks", task.Id, request.Instance.CompanyId, "update", ("due_at", null, due));
                    return true;
                }, cancellationToken).ConfigureAwait(false))
            {
                dueSet++;
            }
        }

        // 2. Tasks past their time limit.
        foreach (var taskId in await PagedTaskIdsAsync("""
            select t.id from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id and i.status = 'running'
            where t.status = 'pending' and t.due_at <= $3 and t.escalated_at is null and t.id > $1 order by t.id limit $2
            """, now, cancellationToken).ConfigureAwait(false))
        {
            bool? moved = null;
            await SystemTaskAsync(taskId, now, async (work, request, task, ct) =>
            {
                if (task.Status != "pending" || task.EscalatedAt is not null || task.DueAt is not { } due || due > work.Now)
                {
                    return false;
                }

                moved = await EscalateTaskAsync(work, request, task, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            if (moved == true)
            {
                escalated++;
            }
            else if (moved == false)
            {
                notPossible++;
            }
        }

        return new EscalationRun(dueSet, escalated, notPossible);
    }

    /// <summary>Moves one late task up a level; false when that is not possible (the task stays open and overdue).</summary>
    private async Task<bool> EscalateTaskAsync(WorkflowWork work, Request request, TaskRow task, CancellationToken ct)
    {
        var step = request.Route.Steps.FirstOrDefault(s => s.Key == task.StepKey);
        if (step is null || task.EscalationLevel >= MaxEscalationLevels)
        {
            await NotPossibleAsync(work, request, task, step is null ? "step_unknown" : "max_level", ct).ConfigureAwait(false);
            return false;
        }

        // The next level: the assignee's line manager, else the step's escalation user or role (ruling Q9).
        Guid? roleId = null;
        var via = "manager";
        var people = task.AssigneeUserId is { } assignee
            ? (await _people.ManagersOfAsync(work, [assignee], ct).ConfigureAwait(false)).Where(p => !request.Instance.Excludes(p) && p != assignee).ToList()
            : [];
        if (people.Count == 0 && step.Escalation is { } spec)
        {
            var target = await _people.ResolveAsync(work, spec, request.Instance, request.Values, ct).ConfigureAwait(false);
            people = [.. target.People.Where(p => !request.Instance.Excludes(p) && p != task.AssigneeUserId)];
            roleId = target.RoleId;
            via = "escalation";
        }

        if (people.Count == 0)
        {
            await NotPossibleAsync(work, request, task, "nobody_above", ct).ConfigureAwait(false);
            return false;
        }

        // Somebody of the next level may already hold a task of this step (several holders, ruling R1): no second task.
        var open = (await work.TasksOfAsync(request.Instance.Id, ct).ConfigureAwait(false)).Where(t => t.Status == "pending" && t.StepKey == task.StepKey && t.Id != task.Id).ToList();
        if (roleId is { } role && open.Any(t => t.AssigneeRoleId == role && t.AssigneeUserId is null))
        {
            people = [];
        }

        people = [.. people.Where(p => !open.Any(t => t.AssigneeUserId == p))];

        await work.ExecuteAsync("""
            update wf.workflow_tasks set status = 'escalated', escalated_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
            """, ct, task.Id, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", task.Id, request.Instance.CompanyId, "status", ("status", "pending", "escalated"), ("escalated_at", null, work.Now));
        await work.HistoryAsync(request.Instance.Id, task.Id, "escalated", work.Actor, null, new
        {
            step = task.StepKey,
            from_level = task.EscalationLevel,
            to_level = task.EscalationLevel + 1,
            to_user_ids = roleId is null ? people : [],
            to_role_id = roleId,
            via,
        }, ct).ConfigureAwait(false);

        var opened = people.Count == 0 ? [] : await AssignAsync(work, request, step, roleId, people, "escalated", ct, task.EscalationLevel + 1, task).ConfigureAwait(false);
        var recipients = people.Concat(task.AssigneeUserId is { } a ? [a] : []).Distinct().ToList();
        await work.RaiseAsync(WorkflowEvents.TaskEscalated, "wf.workflow_task", task.Id, new
        {
            instance_id = request.Instance.Id,
            task_id = task.Id,
            new_task_ids = opened,
            document_type = request.Type.Code,
            document_id = request.Instance.DocumentId,
            company_id = request.Instance.CompanyId,
            requester_user_id = request.Instance.RequesterUserId,
            recipients,
            escalation_level = task.EscalationLevel + 1,
            outcome = (string?)null,
            link = Link(request),
        }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Nobody above, or the last level reached: the task stays open and overdue, handled once (never auto-approved).</summary>
    private static async Task NotPossibleAsync(WorkflowWork work, Request request, TaskRow task, string reason, CancellationToken ct)
    {
        await work.ExecuteAsync("update wf.workflow_tasks set escalated_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", ct,
            task.Id, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_tasks", task.Id, request.Instance.CompanyId, "update", ("escalated_at", null, work.Now));
        await work.HistoryAsync(request.Instance.Id, task.Id, "escalation_not_possible", work.Actor, null, new { step = task.StepKey, level = task.EscalationLevel, reason }, ct).ConfigureAwait(false);
    }

    /// <summary>All task ids a query finds, read in pages by id ($1 last id, $2 page size, $3 now).</summary>
    private async Task<List<Guid>> PagedTaskIdsAsync(string sql, DateTimeOffset now, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        var last = Guid.Empty;
        while (true)
        {
            await using var command = IdentityDb.Command(c, null, sql, last, EscalationPage, now);
            var page = await Access.AccessDb.IdsAsync(command, ct).ConfigureAwait(false);
            ids.AddRange(page);
            if (page.Count < EscalationPage)
            {
                return ids;
            }

            last = page[^1];
        }
    }

    /// <summary>One task in its own transaction as the system user: request locked, then task; false when nothing was done.</summary>
    private async Task<bool> SystemTaskAsync(Guid taskId, DateTimeOffset now, Func<WorkflowWork, Request, TaskRow, CancellationToken, Task<bool>> action, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, now, BaselineSeed.SystemUserId);
        var instanceId = await work.ScalarAsync<Guid?>("select instance_id from wf.workflow_tasks where id = $1", ct, taskId).ConfigureAwait(false);
        if (instanceId is null
            || await work.InstanceAsync(instanceId.Value, lockRow: true, ct).ConfigureAwait(false) is not { Status: "running" } instance
            || await work.TaskAsync(taskId, lockRow: true, ct).ConfigureAwait(false) is not { } task
            || await LoadAsync(work, instance, ct).ConfigureAwait(false) is not { } request)
        {
            return false;
        }

        if (!await action(work, request, task, ct).ConfigureAwait(false))
        {
            return false;
        }

        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
