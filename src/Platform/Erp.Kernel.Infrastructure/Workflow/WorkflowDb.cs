using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>A request row (<c>wf.workflow_instances</c>).</summary>
internal sealed record InstanceRow(
    Guid Id,
    Guid CompanyId,
    Guid VersionId,
    string DocumentType,
    Guid DocumentId,
    Guid RequesterUserId,
    Guid[] SubjectUserIds,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string ContextJson,
    string Status,
    long RowVersion)
{
    /// <summary>The frozen values (<c>context.values</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement> Values()
    {
        using var doc = JsonDocument.Parse(ContextJson);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in values.EnumerateObject())
            {
                result[p.Name] = p.Value.Clone();
            }
        }

        return result;
    }

    /// <summary>The requester and the people the document is about: never approvers of this request (ruling Q6).</summary>
    public bool Excludes(Guid userId) => userId == RequesterUserId || SubjectUserIds.Contains(userId);
}

/// <summary>A task row (<c>wf.workflow_tasks</c>).</summary>
internal sealed record TaskRow(
    Guid Id,
    Guid InstanceId,
    string StepKey,
    Guid? AssigneeUserId,
    Guid? AssigneeRoleId,
    Guid? ActingForUserId,
    DateTimeOffset? DueAt,
    Guid? ActedBy,
    DateTimeOffset? ActedAt,
    string? Comment,
    string Status,
    DateTimeOffset? InfoRequestedAt,
    Guid? InfoRequestedBy,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset CreatedAt,
    long RowVersion);

/// <summary>
/// One unit of workflow work on one transaction: SQL, the decision history (append-only), events in
/// <c>wf.outbox_messages</c> and the change-log entries of <c>wf.*</c> rows, written before commit (S1.07: the engine
/// writes with SQL, so it records its own change-log entries, as numbering does).
/// </summary>
internal sealed class WorkflowWork(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset now, Guid actor)
{
    private readonly List<ChangeEntry> _changes = [];

    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public DateTimeOffset Now { get; } = now;

    public Guid Actor { get; } = actor;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<int> ExecuteAsync(string sql, CancellationToken ct, params object?[] values) =>
        IdentityDb.ExecuteAsync(Connection, Transaction, sql, ct, values);

    public Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params object?[] values) =>
        IdentityDb.ScalarAsync<T>(Connection, Transaction, sql, ct, values);

    public NpgsqlCommand Command(string sql, params object?[] values) => IdentityDb.Command(Connection, Transaction, sql, values);

    public Guid NewId() => Guid.CreateVersion7(Now);

    // ------------------------------------------------------------------ rows

    private const string InstanceColumns = "id, company_id, version_id, document_type, document_id, requester_user_id, subject_user_ids, started_at, completed_at, context::text, status, row_version";

    private const string TaskColumns = "id, instance_id, step_key, assignee_user_id, assignee_role_id, acting_for_user_id, due_at, acted_by, acted_at, comment, status, info_requested_at, info_requested_by, claimed_at, created_at, row_version";

    public async Task<InstanceRow?> InstanceAsync(Guid id, bool lockRow, CancellationToken ct)
    {
        var rows = await InstancesAsync($"select {InstanceColumns} from wf.workflow_instances where id = $1" + (lockRow ? " for update" : string.Empty), ct, id).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The open (running or returned) request of a document, locked.</summary>
    public async Task<InstanceRow?> OpenInstanceAsync(string documentType, Guid documentId, CancellationToken ct)
    {
        var rows = await InstancesAsync($"select {InstanceColumns} from wf.workflow_instances where document_type = $1 and document_id = $2 and status in ('running', 'returned') order by started_at desc limit 1 for update", ct, documentType, documentId).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<List<InstanceRow>> InstancesAsync(string sql, CancellationToken ct, params object?[] values)
    {
        var list = new List<InstanceRow>();
        await using var command = Command(sql, values);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new InstanceRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetGuid(5), r.GetFieldValue<Guid[]>(6),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetString(9), r.GetString(10), r.GetInt64(11)));
        }

        return list;
    }

    public async Task<TaskRow?> TaskAsync(Guid id, bool lockRow, CancellationToken ct)
    {
        var rows = await TasksAsync($"select {TaskColumns} from wf.workflow_tasks where id = $1" + (lockRow ? " for update" : string.Empty), ct, id).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<List<TaskRow>> TasksOfAsync(Guid instanceId, CancellationToken ct) =>
        TasksAsync($"select {TaskColumns} from wf.workflow_tasks where instance_id = $1 order by created_at, id", ct, instanceId);

    public async Task<List<TaskRow>> TasksAsync(string sql, CancellationToken ct, params object?[] values)
    {
        var list = new List<TaskRow>();
        await using var command = Command(sql, values);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadTask(r));
        }

        return list;
    }

    private static TaskRow ReadTask(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), NullableGuid(r, 3), NullableGuid(r, 4), NullableGuid(r, 5), NullableTime(r, 6), NullableGuid(r, 7), NullableTime(r, 8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), NullableTime(r, 11), NullableGuid(r, 12), NullableTime(r, 13), r.GetFieldValue<DateTimeOffset>(14), r.GetInt64(15));

    public static Guid? NullableGuid(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static DateTimeOffset? NullableTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    /// <summary>The route version of a request: its stored definition JSON.</summary>
    public Task<string?> VersionJsonAsync(Guid versionId, CancellationToken ct) =>
        ScalarAsync<string>("select definition::text from wf.workflow_versions where id = $1", ct, versionId);

    // ------------------------------------------------------------------ history, events, change log

    /// <summary>
    /// Appends one row to the decision history (KRN-WFL-07: who, for whom, when, and why). The history is read in time
    /// order, so each row of a request is at least one microsecond after the request's previous row: rows written in the
    /// same instant (one decision assigning the next step, or a clock that has not moved) keep the order they happened in.
    /// The request is locked by the caller, so no other writer interleaves.
    /// </summary>
    public async Task HistoryAsync(Guid instanceId, Guid? taskId, string eventCode, Guid? actorId, Guid? actingFor, object details, CancellationToken ct)
    {
        if (!_lastHistory.TryGetValue(instanceId, out var last))
        {
            await using var command = Command("select max(created_at) from wf.workflow_history where instance_id = $1", instanceId);
            await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            last = await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0) ? r.GetFieldValue<DateTimeOffset>(0) : DateTimeOffset.MinValue;
        }

        var now = Microseconds(Now);
        var at = now > last ? now : last.AddTicks(TicksPerMicrosecond);
        _lastHistory[instanceId] = at;
        await ExecuteAsync("""
            insert into wf.workflow_history (id, instance_id, task_id, event, actor_id, acting_for_user_id, details, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, ct, Guid.CreateVersion7(at), instanceId, taskId, eventCode, actorId, actingFor, new JsonPayload(JsonSerializer.Serialize(details, Json)), at, Actor).ConfigureAwait(false);
    }

    /// <summary>An event for other modules and notifications (ruling Q15), in the same transaction (outbox); events of one unit keep their order.</summary>
    public Task RaiseAsync(string eventType, string aggregateType, Guid aggregateId, object payload, CancellationToken ct)
    {
        var at = Microseconds(Now).AddTicks(TicksPerMicrosecond * _events++);
        return ExecuteAsync("""
            insert into wf.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, $4, $5, $6, $7, null)
            """, ct, Guid.CreateVersion7(at), at, eventType, aggregateType, aggregateId, new JsonPayload(JsonSerializer.Serialize(payload, Json)), new JsonPayload(JsonSerializer.Serialize(new { actor = Actor }, Json)));
    }

    private const long TicksPerMicrosecond = 10;

    private readonly Dictionary<Guid, DateTimeOffset> _lastHistory = [];

    private int _events;

    /// <summary>The time as PostgreSQL stores it (whole microseconds).</summary>
    private static DateTimeOffset Microseconds(DateTimeOffset time) => new(time.Ticks - (time.Ticks % TicksPerMicrosecond), time.Offset);

    /// <summary>Records a change-log entry for a <c>wf.*</c> row; written by <see cref="FlushAsync"/>.</summary>
    public void Change(string table, Guid id, Guid? companyId, string action, params (string Field, object? Before, object? After)[] fields)
    {
        var changes = new JsonArray();
        foreach (var (field, before, after) in fields)
        {
            changes.Add(new JsonObject
            {
                ["field"] = field,
                ["before"] = before is null ? null : JsonSerializer.SerializeToNode(before, Json),
                ["after"] = after is null ? null : JsonSerializer.SerializeToNode(after, Json),
            });
        }

        var request = AuditAmbient.Current;
        var system = Actor == BaselineSeed.SystemUserId;
        _changes.Add(new ChangeEntry(NewId(), Now, system ? null : Actor, system ? ActorKinds.System : ActorKinds.User, table, id, companyId, action, changes, null,
            request?.CorrelationId, request?.IpAddress, system && request is null ? AuditSources.System : AuditAmbient.Source, Actor));
    }

    /// <summary>Writes the collected change-log entries; call once, last, before commit.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await AuditDb.AppendChangesAsync(Connection, Transaction, _changes, ct).ConfigureAwait(false);
        _changes.Clear();
    }
}
