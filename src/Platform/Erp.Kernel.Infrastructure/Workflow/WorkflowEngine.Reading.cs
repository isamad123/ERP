using System.Text;
using System.Text.Json;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// The reading side of the engine (slice S1.10 build item 8, rulings Q8, Q12; FR-KRN-WFL-08, FR-HRM-SSP-02): the inbox
/// with its filters and counts, "my requests" and task details. Only document types the customer is licensed for are
/// shown (ruling Q11).
/// </summary>
public sealed partial class WorkflowEngine
{
    public const int InboxMaxItems = 1000;
    public const int MyRequestsMaxItems = 200;

    /// <summary>The caller's open tasks: assigned to them, delegated to them now, and unclaimed queue tasks of roles they hold.</summary>
    private const string InboxRows = """
        select t.id, t.step_key, t.assignee_user_id, t.assignee_role_id, t.created_at, t.due_at, t.info_requested_at is not null, t.instance_id, t.escalation_level,
               i.document_type
        from wf.workflow_tasks t
        join wf.workflow_instances i on i.id = t.instance_id and i.status = 'running'
        where t.status = 'pending' and i.document_type = any($3)
          and i.requester_user_id <> $1 and not ($1 = any(i.subject_user_ids))
          and (t.assignee_user_id = $1
               or (t.assignee_user_id is null and t.assignee_role_id is not null
                   and exists (select 1 from valid v where v.role_id = t.assignee_role_id and (v.company_id is null or v.company_id = i.company_id)))
               or (t.assignee_user_id is not null and @delegated))
        """;

    public async Task<IReadOnlyList<InboxItem>> InboxAsync(InboxQuery query, CancellationToken cancellationToken)
    {
        query ??= new InboxQuery();
        if (query.Sort is not (null or "received" or "due"))
        {
            throw new WorkflowException("errors.workflow.sort_invalid");
        }

        var limit = Math.Clamp(query.Limit ?? InboxMaxItems, 1, InboxMaxItems);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var types = (await LicensedTypesAsync(cancellationToken).ConfigureAwait(false))
            .Where(t => (query.Module is null || t.Module == query.Module) && (query.DocumentType is null || t.Code == query.DocumentType))
            .Select(t => t.Code).ToArray();

        var sql = new StringBuilder(Access.AccessSnapshot.AssignmentsCte).Append(InboxRows.Replace("@delegated", DelegationCovers("t.assignee_user_id", "$1", "$4", "i.document_type", "i.company_id"), StringComparison.Ordinal));
        var values = new List<object?> { work.Actor, ApproverResolution.Today(work), types, work.Now };
        void Filter(string clause, object? value)
        {
            values.Add(value);
            sql.Append(" and ").Append(clause.Replace("@", "$" + values.Count, StringComparison.Ordinal));
        }

        if (query.CompanyId is { } company)
        {
            Filter("i.company_id = @", company);
        }

        if (query.RequesterUserId is { } requester)
        {
            Filter("i.requester_user_id = @", requester);
        }

        if (query.Step is { } stepKey)
        {
            Filter("t.step_key = @", stepKey);
        }

        if (query.Waiting is { } waiting)
        {
            sql.Append(waiting ? " and t.info_requested_at is not null" : " and t.info_requested_at is null");
        }

        if (query.Overdue is { } overdue)
        {
            sql.Append(overdue ? " and t.due_at <= $4" : " and (t.due_at is null or t.due_at > $4)");
        }

        if (query.ReceivedFrom is { } receivedFrom)
        {
            Filter("t.created_at >= @", receivedFrom);
        }

        if (query.ReceivedTo is { } receivedTo)
        {
            Filter("t.created_at < @", receivedTo);
        }

        sql.Append(query.Sort == "due" ? " order by t.due_at nulls last, t.created_at, t.id" : " order by t.created_at, t.id");
        values.Add(limit);
        sql.Append(" limit $").Append(values.Count);

        var rows = new List<(Guid TaskId, string Step, Guid? Assignee, Guid? Role, DateTimeOffset Created, DateTimeOffset? Due, bool Waiting, Guid InstanceId, int Level)>();
        await using (var command = work.Command(sql.ToString(), [.. values]))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((r.GetGuid(0), r.GetString(1), WorkflowWork.NullableGuid(r, 2), WorkflowWork.NullableGuid(r, 3), r.GetFieldValue<DateTimeOffset>(4),
                    WorkflowWork.NullableTime(r, 5), r.GetBoolean(6), r.GetGuid(7), r.GetInt32(8)));
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

            var step = request.Route.Steps.FirstOrDefault(s => s.Key == row.Step);
            var actingFor = row.Assignee is { } assignee && assignee != work.Actor ? assignee : (Guid?)null;
            items.Add(new InboxItem(row.TaskId, request.Instance.Id, request.Type.Code, request.Instance.DocumentId, request.Instance.CompanyId, Titles(request),
                Link(request), row.Step, StepName(request, row.Step), request.Instance.RequesterUserId, row.Created, row.Due, row.Assignee is null, row.Role, row.Waiting,
                request.Type.Module, actingFor, row.Due is { } due && due <= work.Now, row.Level, request.Type.BulkApproval && step?.BulkApproval == true));
        }

        return items;
    }

    /// <summary>
    /// Counts per document type for menus and badges (ruling Q12), and the pending maker-checker requests of other people
    /// when the caller may decide changes (<c>iam.maker_checker.approve</c>; decided on the S1.06 endpoints).
    /// </summary>
    public async Task<InboxCounts> CountsAsync(CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var licensed = await LicensedTypesAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, (int Count, int Overdue)>(StringComparer.Ordinal);
        await using (var command = work.Command(Access.AccessSnapshot.AssignmentsCte + "select q.document_type, count(*)::int, count(*) filter (where q.due_at <= $4)::int from ("
            + InboxRows.Replace("@delegated", DelegationCovers("t.assignee_user_id", "$1", "$4", "i.document_type", "i.company_id"), StringComparison.Ordinal)
            + ") q(id, step_key, assignee_user_id, assignee_role_id, created_at, due_at, waiting, instance_id, escalation_level, document_type) group by q.document_type",
            work.Actor, ApproverResolution.Today(work), licensed.Select(t => t.Code).ToArray(), work.Now))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                counts[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
            }
        }

        var types = licensed.Where(t => counts.ContainsKey(t.Code))
            .Select(t => new InboxTypeCount(t.Code, t.Module, t.Name, counts[t.Code].Count, counts[t.Code].Overdue))
            .OrderBy(t => t.DocumentType, StringComparer.Ordinal).ToList();

        var changes = 0;
        if (await ApproverResolution.HoldsAnyAsync(work, work.Actor, AccessControlPermissions.ApproveChanges, cancellationToken).ConfigureAwait(false))
        {
            // Pending requests older than iam.maker_checker.expiry_days can no longer be decided (they expire at the next decision).
            var days = await Access.AccessDb.SettingIntAsync(work.Connection, work.Transaction, Identity.IdentitySettings.MakerCheckerExpiryDays, 7, cancellationToken).ConfigureAwait(false);
            changes = await work.ScalarAsync<int>("""
                select count(*)::int from iam.maker_checker_requests where status = 'pending' and requested_by <> $1 and created_at > $2
                """, cancellationToken, work.Actor, work.Now.AddDays(-days)).ConfigureAwait(false);
        }

        return new InboxCounts(types.Sum(t => t.Count), types.Sum(t => t.Overdue), types, changes);
    }

    /// <summary>The caller's own requests, newest first (ruling Q12): status, the tasks now open and questions waiting for them.</summary>
    public async Task<IReadOnlyList<MyRequestView>> MyRequestsAsync(MyRequestsQuery query, CancellationToken cancellationToken)
    {
        query ??= new MyRequestsQuery();
        string[] statuses = query.Status is { } one ? [one] : ["running", "returned"];
        if (statuses.Any(s => s is not ("running" or "returned" or "approved" or "rejected" or "cancelled")))
        {
            throw new WorkflowException("errors.workflow.status_invalid");
        }

        var limit = Math.Clamp(query.Limit ?? MyRequestsMaxItems, 1, MyRequestsMaxItems);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var instances = await work.InstancesAsync(
            "select id, company_id, version_id, document_type, document_id, requester_user_id, subject_user_ids, started_at, completed_at, context::text, status, row_version "
            + "from wf.workflow_instances where requester_user_id = $1 and status = any($2) order by started_at desc, id desc limit $3",
            cancellationToken, work.Actor, statuses, limit).ConfigureAwait(false);

        var result = new List<MyRequestView>();
        foreach (var instance in instances)
        {
            if (await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false) is not { } request)
            {
                continue; // not licensed (ruling Q11)
            }

            var open = (await work.TasksOfAsync(instance.Id, cancellationToken).ConfigureAwait(false)).Where(t => t.Status == "pending").ToList();
            var questions = new List<WaitingQuestion>();
            foreach (var task in open.Where(t => t.InfoRequestedAt is not null))
            {
                questions.Add(new WaitingQuestion(task.Id, task.InfoRequestedBy, task.InfoRequestedAt!.Value, await LastQuestionAsync(work, instance.Id, cancellationToken).ConfigureAwait(false)));
            }

            result.Add(new MyRequestView(instance.Id, instance.DocumentType, instance.DocumentId, instance.CompanyId, Titles(request), Link(request), instance.Status,
                instance.StartedAt, instance.CompletedAt,
                [.. open.Select(t => new MyRequestTask(t.Id, t.StepKey, StepName(request, t.StepKey), t.AssigneeUserId, t.AssigneeRoleId, t.DueAt, t.DueAt is { } d && d <= work.Now))],
                questions));
        }

        return result;
    }

    /// <summary>The text of the last question asked on a request (a question moves with the request, ruling P6).</summary>
    private static async Task<string?> LastQuestionAsync(WorkflowWork work, Guid instanceId, CancellationToken ct) =>
        await work.ScalarAsync<string>("""
            select details ->> 'question' from wf.workflow_history where instance_id = $1 and event = 'info_requested' order by created_at desc, id desc limit 1
            """, ct, instanceId).ConfigureAwait(false);

    public async Task<WorkflowTaskDetail?> TaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        if (await work.TaskAsync(taskId, lockRow: false, cancellationToken).ConfigureAwait(false) is not { } task
            || await work.InstanceAsync(task.InstanceId, lockRow: false, cancellationToken).ConfigureAwait(false) is not { } instance
            || await LoadAsync(work, instance, cancellationToken).ConfigureAwait(false) is not { } request
            || await CapacityAsync(work, request, task, work.Actor, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return new WorkflowTaskDetail(TaskView(request, task), InstanceView(request), await HistoryAsync(work, instance.Id, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<List<WorkflowHistoryView>> HistoryAsync(WorkflowWork work, Guid instanceId, CancellationToken ct)
    {
        var history = new List<WorkflowHistoryView>();
        await using var command = work.Command("select id, task_id, event, actor_id, acting_for_user_id, created_at, details::text from wf.workflow_history where instance_id = $1 order by created_at, id", instanceId);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            using var details = JsonDocument.Parse(r.GetString(6));
            history.Add(new WorkflowHistoryView(r.GetGuid(0), WorkflowWork.NullableGuid(r, 1), r.GetString(2), WorkflowWork.NullableGuid(r, 3), WorkflowWork.NullableGuid(r, 4),
                r.GetFieldValue<DateTimeOffset>(5), details.RootElement.Clone()));
        }

        return history;
    }

    /// <summary>The registered document types the customer is licensed for.</summary>
    private async Task<List<WorkflowDocumentType>> LicensedTypesAsync(CancellationToken ct)
    {
        var result = new List<WorkflowDocumentType>();
        foreach (var type in catalog.All)
        {
            if (await LicensedAsync(type, ct).ConfigureAwait(false))
            {
                result.Add(type);
            }
        }

        return result;
    }

    internal static WorkflowTaskView TaskViewOf(IReadOnlyDictionary<string, string> stepName, TaskRow t) =>
        new(t.Id, t.StepKey, stepName, t.Status, t.AssigneeUserId, t.AssigneeRoleId, t.ActingForUserId, t.ActedBy, t.ActedAt, t.Comment, t.DueAt, t.InfoRequestedAt, t.InfoRequestedBy, t.ClaimedAt, t.RowVersion,
            t.EscalationLevel, t.EscalatedAt);

    private static WorkflowTaskView TaskView(Request request, TaskRow t) => TaskViewOf(StepName(request, t.StepKey), t);

    private static WorkflowInstanceView InstanceView(Request request)
    {
        var i = request.Instance;
        using var context = JsonDocument.Parse(i.ContextJson);
        var values = context.RootElement.TryGetProperty("values", out var v) ? v.Clone() : JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        return new WorkflowInstanceView(i.Id, i.VersionId, i.DocumentType, i.DocumentId, i.CompanyId, i.Status, i.RequesterUserId, i.SubjectUserIds, i.StartedAt, i.CompletedAt,
            values, Titles(request), Link(request));
    }
}
