using System.Data.Common;
using System.Text;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// Administration of requests (slice S1.10 build item 9, ruling Q13; FR-ADM-USR-03, FR-XM-29). What an administrator sees
/// and changes follows <see cref="WorkflowCoverage"/>: <c>wf.workflow_instances.read</c> for the monitor (own requests for
/// a requester, the covered companies for an administrator), <c>wf.workflow_instances.cancel</c> to cancel and
/// <c>wf.workflow_tasks.reassign</c> to reassign, both for covered companies only.
/// </summary>
public sealed partial class WorkflowEngine
{
    public const int MonitorMaxItems = 500;

    public async Task<IReadOnlyList<MonitorItem>> RequestsAsync(MonitorQuery query, CancellationToken cancellationToken)
    {
        query ??= new MonitorQuery();
        string[] statuses = query.Status is { } one ? [one] : ["running", "returned"];
        if (statuses.Any(s => s is not ("running" or "returned" or "approved" or "rejected" or "cancelled" or "failed")))
        {
            throw new WorkflowException("errors.workflow.status_invalid");
        }

        var limit = Math.Clamp(query.Limit ?? 200, 1, MonitorMaxItems);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var coverage = await WorkflowCoverage.ForAsync(work, work.Actor, WorkflowPermissions.RequestsRead, cancellationToken).ConfigureAwait(false);
        var types = (await LicensedTypesAsync(cancellationToken).ConfigureAwait(false)).Select(t => t.Code).ToArray();

        var sql = new StringBuilder("""
            select i.id, i.company_id, i.version_id, i.document_type, i.document_id, i.requester_user_id, i.subject_user_ids, i.started_at, i.completed_at, i.context::text, i.status, i.row_version,
                   (select count(*)::int from wf.workflow_tasks t where t.instance_id = i.id and t.status = 'pending'),
                   exists (select 1 from wf.workflow_tasks t where t.instance_id = i.id and t.status = 'pending' and t.due_at <= $5),
                   exists (select 1 from wf.workflow_tasks t where t.instance_id = i.id and t.status = 'pending' and t.assignee_user_id is null and t.assignee_role_id is null)
            from wf.workflow_instances i
            where i.status = any($1) and i.document_type = any($2)
              and ($3 or i.company_id = any($4) or ($6 and i.requester_user_id = $7))
            """);
        var values = new List<object?> { statuses, types, coverage.AllCompanies, coverage.Companies.ToArray(), work.Now, coverage.Own, work.Actor };
        void Filter(string clause, object? value)
        {
            values.Add(value);
            sql.Append(" and ").Append(clause.Replace("@", "$" + values.Count, StringComparison.Ordinal));
        }

        if (query.DocumentType is { } type)
        {
            Filter("i.document_type = @", type);
        }

        if (query.CompanyId is { } company)
        {
            Filter("i.company_id = @", company);
        }

        if (query.After is { } after)
        {
            Filter("i.id < @", after);
        }

        if (query.Overdue is { } overdue)
        {
            sql.Append(overdue ? " and " : " and not ").Append("exists (select 1 from wf.workflow_tasks t where t.instance_id = i.id and t.status = 'pending' and t.due_at <= $5)");
        }

        if (query.CannotAssign is { } cannot)
        {
            sql.Append(cannot ? " and " : " and not ").Append("exists (select 1 from wf.workflow_tasks t where t.instance_id = i.id and t.status = 'pending' and t.assignee_user_id is null and t.assignee_role_id is null)");
        }

        values.Add(limit);
        sql.Append(" order by i.id desc limit $").Append(values.Count);

        var rows = new List<(InstanceRow Instance, int Open, bool Overdue, bool Cannot)>();
        await using (var command = work.Command(sql.ToString(), [.. values]))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((new InstanceRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetGuid(5), r.GetFieldValue<Guid[]>(6),
                    r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetString(9), r.GetString(10), r.GetInt64(11)),
                    r.GetInt32(12), r.GetBoolean(13), r.GetBoolean(14)));
            }
        }

        var items = new List<MonitorItem>();
        foreach (var row in rows)
        {
            if (await LoadAsync(work, row.Instance, cancellationToken).ConfigureAwait(false) is { } request)
            {
                var i = row.Instance;
                items.Add(new MonitorItem(i.Id, i.DocumentType, i.DocumentId, i.CompanyId, i.Status, i.RequesterUserId, i.StartedAt, i.CompletedAt, Titles(request), Link(request),
                    row.Open, row.Overdue, row.Cannot));
            }
        }

        return items;
    }

    public async Task<RequestDetail?> RequestAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        if (await work.InstanceAsync(instanceId, lockRow: false, cancellationToken).ConfigureAwait(false) is not { } instance
            || !(await WorkflowCoverage.ForAsync(work, work.Actor, WorkflowPermissions.RequestsRead, cancellationToken).ConfigureAwait(false)).Sees(instance.CompanyId, instance.RequesterUserId, work.Actor)
            || await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false) is not { } request)
        {
            return null;
        }

        var tasks = (await work.TasksOfAsync(instance.Id, cancellationToken).ConfigureAwait(false)).Select(t => TaskView(request, t)).ToList();
        return new RequestDetail(InstanceView(request), tasks, await HistoryAsync(work, instance.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// An administrator ends a running or returned request with a reason (ruling Q13): its open tasks close and the module
    /// hears <c>cancelled</c> in the same transaction. Not for one's own request (the requester withdraws it instead).
    /// </summary>
    public async Task<DecisionResult> CancelAsync(Guid instanceId, CancelRequest request, CancellationToken cancellationToken)
    {
        var reason = Trim(request?.Reason) ?? throw new WorkflowException("errors.workflow.reason_required");
        return await OnceMoreOnDeadlockAsync(() => CancelOnceAsync(instanceId, reason, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<DecisionResult> CancelOnceAsync(Guid instanceId, string reason, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        if (await work.InstanceAsync(instanceId, lockRow: true, cancellationToken).ConfigureAwait(false) is not { } instance
            || !(await WorkflowCoverage.ForAsync(work, work.Actor, WorkflowPermissions.RequestsCancel, cancellationToken).ConfigureAwait(false)).CoversCompany(instance.CompanyId)
            || await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false) is not { } req)
        {
            throw new KeyNotFoundException(RequestNotFound);
        }

        if (instance.Status is not ("running" or "returned"))
        {
            throw new WorkflowException("errors.workflow.request_not_open");
        }

        if (instance.Excludes(work.Actor))
        {
            throw new WorkflowException("errors.workflow.own_request");
        }

        await CloseOthersAsync(work, req, null, "cancelled", cancellationToken).ConfigureAwait(false);
        await work.HistoryAsync(instance.Id, null, "cancelled", work.Actor, null, new { reason }, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(work, req, WorkflowOutcomes.Cancelled, reason, null, cancellationToken).ConfigureAwait(false);
        await work.FlushAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DecisionResult(instance.Id, "cancelled", []);
    }

    /// <summary>
    /// A user's open tasks in the administrator's companies (ADM-USR-03): tasks assigned to them (including queue tasks they
    /// claimed). Tasks of document types the customer is not licensed for are counted by the FR-XM-29 query, not listed here.
    /// </summary>
    public async Task<IReadOnlyList<UserOpenTask>> OpenTasksOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var coverage = await WorkflowCoverage.ForAsync(work, work.Actor, WorkflowPermissions.TasksReassign, cancellationToken).ConfigureAwait(false);
        if (!coverage.AdministersAny || !await work.ScalarAsync<bool>("select exists (select 1 from iam.users where id = $1 and not is_system)", cancellationToken, userId).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("errors.workflow.user_not_found");
        }

        var tasks = await work.TasksAsync("""
            select t.id, t.instance_id, t.step_key, t.assignee_user_id, t.assignee_role_id, t.acting_for_user_id, t.due_at, t.acted_by, t.acted_at, t.comment, t.status,
                   t.info_requested_at, t.info_requested_by, t.claimed_at, t.created_at, t.row_version, t.escalation_level, t.escalated_at
            from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id and i.status = 'running'
            where t.status = 'pending' and t.assignee_user_id = $1 and ($2 or i.company_id = any($3))
            order by t.created_at, t.id
            """, cancellationToken, userId, coverage.AllCompanies, coverage.Companies.ToArray()).ConfigureAwait(false);

        var result = new List<UserOpenTask>();
        foreach (var task in tasks)
        {
            if (await work.InstanceAsync(task.InstanceId, lockRow: false, cancellationToken).ConfigureAwait(false) is { } instance
                && await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false) is { } request)
            {
                result.Add(new UserOpenTask(task.Id, instance.Id, instance.DocumentType, instance.DocumentId, instance.CompanyId, Titles(request), task.StepKey, task.CreatedAt, task.DueAt,
                    task.DueAt is { } due && due <= work.Now));
            }
        }

        return result;
    }

    /// <summary>Reassigns all of a user's open tasks in the administrator's companies to one person (ADM-USR-03): a result per task.</summary>
    public async Task<BulkResult> ReassignOpenTasksAsync(Guid userId, ReassignAllRequest request, CancellationToken cancellationToken)
    {
        var reason = Trim(request?.Reason) ?? throw new WorkflowException("errors.workflow.reason_required");
        var to = request!.ToUserId ?? throw new WorkflowException("errors.workflow.reassign_target_required");
        var results = new List<TaskActionResult>();
        foreach (var task in await OpenTasksOfAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            results.Add(await OneAsync(task.TaskId, () => DecideAsync(task.TaskId, (work, req, row, actingFor, ct) => ReassignCoreAsync(work, req, row, actingFor, to, reason, ct),
                cancellationToken, allowAdministrator: true), done: "reassigned").ConfigureAwait(false));
        }

        return new BulkResult(results.Count(r => r.Outcome == "reassigned"), results.Count(r => r.Outcome == "refused"), results);
    }

    /// <summary>
    /// FR-XM-29 (for final settlement, S1.25): tasks waiting for the user's decision (assigned to them) and requests raised
    /// by them or about them that have not ended. Every document type is counted, licensed or not.
    /// </summary>
    public async Task<OpenApprovals> OpenApprovalsOfAsync(Guid userId, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        NpgsqlConnection? own = null;
        try
        {
            NpgsqlConnection connection;
            NpgsqlTransaction? tx = null;
            if (transaction is NpgsqlTransaction { Connection: { } open } given)
            {
                (connection, tx) = (open, given);
            }
            else
            {
                connection = own = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
            }

            var tasks = new List<OpenApprovalTask>();
            await using (var command = IdentityDb.Command(connection, tx, """
                select t.id, i.id, i.document_type, i.document_id, i.company_id, t.step_key, t.due_at
                from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id and i.status = 'running'
                where t.status = 'pending' and t.assignee_user_id = $1 order by t.created_at, t.id
                """, userId))
            await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tasks.Add(new OpenApprovalTask(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetString(5), WorkflowWork.NullableTime(r, 6)));
                }
            }

            var requests = new List<OpenApprovalRequest>();
            await using (var command = IdentityDb.Command(connection, tx, """
                select i.id, i.document_type, i.document_id, i.company_id, i.status, i.requester_user_id
                from wf.workflow_instances i
                where i.status in ('running', 'returned') and (i.requester_user_id = $1 or $1 = any(i.subject_user_ids))
                order by i.started_at, i.id
                """, userId))
            await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    requests.Add(new OpenApprovalRequest(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5), r.GetGuid(5) == userId));
                }
            }

            return new OpenApprovals(tasks, requests);
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
