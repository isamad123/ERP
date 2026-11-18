using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Application.Jobs;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Jobs;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// The job centre (FR-KRN-JOB-03, FR-ADM-OPS-01; slice S1.11 Build item 7). Lists jobs and runs with filters, shows a run
/// with its log, queues a run now or again, cancels, changes a schedule or switches a job off (only jobs the manifest
/// marks editable) and gives the health numbers. Runs are scoped by company like other company data (<c>job.runs</c>
/// scope company): a run for a company needs a role covering that company, a run for the whole customer needs a role for
/// all companies. Runs a person asks for act with that person's rights when they start (ruling Q4).
/// </summary>
public sealed class JobCentreService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess access,
    ICurrentLicence licence,
    JobCatalog catalog,
    IAuditContext audit) : IJobCentre
{
    private const int MaxLimit = 200;

    /// <summary>A worker that started nothing for this long is not serving the customer (every-minute escalation, ruling P8).</summary>
    private static readonly TimeSpan WorkerQuiet = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<JobDefinitionView>> DefinitionsAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(JobPermissions.DefinitionsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await OpsAlerts.ReadAsync(c, """
            select d.id, d.code, d.module_id, d.schedule::text, d.is_enabled, d.priority, d.row_version,
                   (select max(r.created_at) from job.runs r where r.definition_id = d.id),
                   (select r.status from job.runs r where r.definition_id = d.id order by r.created_at desc limit 1)
            from job.definitions d order by d.code
            """, r => (Id: r.GetGuid(0), Code: r.GetString(1), Module: r.GetString(2), Schedule: r.IsDBNull(3) ? null : r.GetString(3), Enabled: r.GetBoolean(4),
                Priority: r.GetInt32(5), RowVersion: r.GetInt64(6), Last: r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7), Status: r.IsDBNull(8) ? null : r.GetString(8)), cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var calendar = await JobStore.CalendarAsync(c, null, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);
        var views = new List<JobDefinitionView>();
        foreach (var row in rows)
        {
            if (catalog.Find(row.Code) is not { } spec)
            {
                continue; // a job no installed manifest declares any more: kept in the table, not offered
            }

            JsonElement? schedule = null;
            DateTimeOffset? next = null;
            if (row.Schedule is { } text)
            {
                using var doc = JsonDocument.Parse(text);
                schedule = doc.RootElement.Clone();
                if (row.Enabled)
                {
                    try
                    {
                        next = JobSchedule.Parse(doc.RootElement).Next(now, calendar.Zone, calendar.IsWorkingDay);
                    }
                    catch (FormatException)
                    {
                        next = null;
                    }
                }
            }

            views.Add(new JobDefinitionView(row.Id, row.Code, row.Module, spec.Name, spec.Description, schedule, row.Enabled, row.Priority, spec.Manual, spec.ScheduleEditable,
                licence.Licence.IsLicensed(spec.Entitlement), [.. spec.RunsIn.Order(StringComparer.Ordinal)], next, row.Last, row.Status, row.RowVersion));
        }

        return views;
    }

    public async Task<JobDefinitionView> ChangeScheduleAsync(Guid definitionId, JobScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(JobPermissions.DefinitionsUpdate, cancellationToken).ConfigureAwait(false);
        if (!await access.CoversCompanyAsync(JobPermissions.DefinitionsUpdate, null, cancellationToken).ConfigureAwait(false))
        {
            throw new JobCentreException("errors.job.all_companies_needed", 403);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = (await ReadInTxAsync(c, tx, "select code, schedule::text, is_enabled, row_version from job.definitions where id = $1 for update",
            r => (Code: r.GetString(0), Schedule: r.IsDBNull(1) ? null : r.GetString(1), Enabled: r.GetBoolean(2), RowVersion: r.GetInt64(3)), cancellationToken, definitionId).ConfigureAwait(false)).SingleOrDefault();
        if (row.Code is null || catalog.Find(row.Code) is not { } spec)
        {
            throw new JobCentreException("errors.job.not_found", 404);
        }

        if (!spec.ScheduleEditable)
        {
            throw new JobCentreException("errors.job.schedule_not_editable", 403);
        }

        if (row.RowVersion != request.RowVersion)
        {
            throw new JobCentreException("errors.job.changed", 409);
        }

        var schedule = row.Schedule;
        if (request.Schedule is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given)
        {
            try
            {
                JobSchedule.Parse(given);
            }
            catch (FormatException ex)
            {
                throw new JobCentreException("errors.job.schedule_invalid", 400, [ex.Message]);
            }

            schedule = given.GetRawText();
        }

        var now = clock.UtcNow;
        await IdentityDb.ExecuteAsync(c, tx, """
            update job.definitions set schedule = $2, is_enabled = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
            """, cancellationToken, definitionId, schedule is null ? null : new JsonPayload(schedule), request.IsEnabled, now, actor.UserId).ConfigureAwait(false);
        var changes = new JsonArray();
        if (schedule != row.Schedule)
        {
            changes.Add(new JsonObject { ["field"] = "schedule", ["before"] = row.Schedule is null ? null : JsonNode.Parse(row.Schedule), ["after"] = schedule is null ? null : JsonNode.Parse(schedule) });
        }

        if (request.IsEnabled != row.Enabled)
        {
            changes.Add(new JsonObject { ["field"] = "is_enabled", ["before"] = row.Enabled, ["after"] = request.IsEnabled });
        }

        if (changes.Count > 0)
        {
            await AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, audit.ActorUserId, audit.ActorKind, "job.definitions", definitionId, null, "update", changes, null,
                audit.CorrelationId, audit.IpAddress, audit.Source, actor.UserId)], cancellationToken).ConfigureAwait(false);
            await JobStore.RaiseAsync(c, tx, JobEvents.ScheduleChanged, "job.definition", definitionId, now, new { code = row.Code, schedule = schedule is null ? null : JsonNode.Parse(schedule), is_enabled = request.IsEnabled, by = actor.UserId }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await DefinitionsAsync(cancellationToken).ConfigureAwait(false)).Single(d => d.Id == definitionId);
    }

    public async Task<JobRunView> RunNowAsync(Guid definitionId, RunJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(JobPermissions.RunsCreate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var code = await IdentityDb.ScalarAsync<string?>(c, null, "select code from job.definitions where id = $1", cancellationToken, definitionId).ConfigureAwait(false);
        if (code is null || catalog.Find(code) is not { } spec)
        {
            throw new JobCentreException("errors.job.not_found", 404);
        }

        var parameters = CheckParameters(spec, request.Parameters ?? []);
        return await QueueAsync(c, definitionId, spec, request.CompanyId, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRunView> RerunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await RequireAsync(JobPermissions.RunsCreate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var run = await VisibleRunAsync(c, runId, JobPermissions.RunsCreate, cancellationToken).ConfigureAwait(false) ?? throw new JobCentreException("errors.job.run_not_found", 404);
        if (run.Status is "queued" or "running")
        {
            throw new JobCentreException("errors.job.run_not_finished", 409);
        }

        var spec = catalog.Find(run.Code) ?? throw new JobCentreException("errors.job.not_found", 404);
        return await QueueAsync(c, run.DefinitionId, spec, run.CompanyId, run.Parameters?.GetRawText() ?? "{}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRunView> CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        await RequireAsync(JobPermissions.RunsUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var run = await VisibleRunAsync(c, runId, JobPermissions.RunsUpdate, cancellationToken).ConfigureAwait(false) ?? throw new JobCentreException("errors.job.run_not_found", 404);
        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // A queued run ends at once; a running run stops at its next chunk (FR-KRN-JOB-02).
        var changed = await IdentityDb.ExecuteAsync(c, tx, """
            update job.runs set
                status = case when status = 'queued' then 'cancelled' else status end,
                finished_at = case when status = 'queued' then $2 else finished_at end,
                cancel_requested_at = coalesce(cancel_requested_at, $2),
                updated_at = $2, updated_by = $3, row_version = row_version + 1
            where id = $1 and status in ('queued', 'running')
            """, cancellationToken, runId, now, actor.UserId).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new JobCentreException("errors.job.run_finished", 409);
        }

        await JobStore.LogAsync(c, tx, runId, "warning", "cancel asked by " + actor.UserId.ToString(), now, cancellationToken).ConfigureAwait(false);
        await JobStore.RaiseAsync(c, tx, JobEvents.RunCancelRequested, "job.run", runId, now, new { run_id = runId, code = run.Code, by = actor.UserId }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await VisibleRunAsync(c, runId, JobPermissions.RunsUpdate, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<JobRunView>> RunsAsync(JobRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await RequireAsync(JobPermissions.RunsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (all, companies) = await CoverageAsync(c, JobPermissions.RunsRead, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit ?? 50, 1, MaxLimit);
        return await OpsAlerts.ReadAsync(c, RunSelect + """
            where ($1::text is null or r.status = $1) and ($2::text is null or d.code = $2) and ($3::uuid is null or r.company_id = $3)
              and ($4::timestamptz is null or r.created_at >= $4) and ($5::timestamptz is null or r.created_at < $5)
              and ($6 or r.company_id = any($7))
            order by r.created_at desc, r.id desc limit $8 offset $9
            """, ReadRun, cancellationToken, query.Status, query.Code, query.CompanyId, query.From, query.To, all, companies, limit, Math.Max(0, query.Offset ?? 0)).ConfigureAwait(false);
    }

    public async Task<JobRunDetail?> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await RequireAsync(JobPermissions.RunsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await VisibleRunAsync(c, runId, JobPermissions.RunsRead, cancellationToken).ConfigureAwait(false) is not { } run)
        {
            return null;
        }

        IReadOnlyList<JobLogView> logs = await permissions.HasPermissionAsync(JobPermissions.LogsRead, cancellationToken).ConfigureAwait(false)
            ? await OpsAlerts.ReadAsync(c, "select created_at, level, message from job.run_logs where run_id = $1 order by created_at, id limit 1000",
                r => new JobLogView(r.GetFieldValue<DateTimeOffset>(0), r.GetString(1), r.GetString(2)), cancellationToken, runId).ConfigureAwait(false)
            : [];
        return new JobRunDetail(run, logs);
    }

    public async Task<JobHealthView> HealthAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(JobPermissions.RunsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var (queued, running, failed, oldest, last) = (await OpsAlerts.ReadAsync(c, """
            select count(*) filter (where status = 'queued'), count(*) filter (where status = 'running'),
                   count(*) filter (where status = 'failed' and finished_at > $1),
                   min(created_at) filter (where status = 'queued'), max(started_at)
            from job.runs
            """, r => (Q: (int)r.GetInt64(0), R: (int)r.GetInt64(1), F: (int)r.GetInt64(2), O: r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3),
                L: r.IsDBNull(4) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(4)), cancellationToken, now.AddHours(-24)).ConfigureAwait(false))[0];
        var events = new List<EventBacklogView>();
        foreach (var schema in await OpsAlerts.OutboxSchemasAsync(c, cancellationToken).ConfigureAwait(false))
        {
#pragma warning disable CA2100 // the schema name comes from the catalog and is quoted
            events.Add((await OpsAlerts.ReadAsync(c, $"""
                select count(*), count(*) filter (where attempts > 0), min(occurred_at) from {OpsAlerts.Quote(schema)}.outbox_messages where published_at is null
                """, r => new EventBacklogView(schema, (int)r.GetInt64(0), (int)r.GetInt64(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2)), cancellationToken).ConfigureAwait(false))[0]);
#pragma warning restore CA2100
        }

        return new JobHealthView(queued, running, failed, oldest, last, last is { } at && at > now - WorkerQuiet, events);
    }

    // ------------------------------------------------------------------ helpers

    private const string RunSelect = """
        select r.id, r.definition_id, d.code, r.status, r.company_id, r.progress_pct, r.attempt, r.scheduled_for, r.started_at, r.finished_at,
               r.next_attempt_at, r.cancel_requested_at, r.error, r.requested_by, r.actor_user_id, r.parameters::text, r.created_at
        from job.runs r join job.definitions d on d.id = r.definition_id

        """;

    private static JobRunView ReadRun(NpgsqlDataReader r)
    {
        JsonElement? parameters = null;
        if (!r.IsDBNull(15))
        {
            using var doc = JsonDocument.Parse(r.GetString(15));
            parameters = doc.RootElement.Clone();
        }

        return new JobRunView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetDecimal(5),
            r.GetInt32(6), Time(r, 7), Time(r, 8), Time(r, 9), Time(r, 10), Time(r, 11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetGuid(13),
            r.IsDBNull(14) ? null : r.GetGuid(14), parameters, r.GetFieldValue<DateTimeOffset>(16));
    }

    private static DateTimeOffset? Time(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    /// <summary>A run the caller may see for this permission (its company covered, or all companies for a customer-wide run); null otherwise.</summary>
    private async Task<JobRunView?> VisibleRunAsync(NpgsqlConnection c, Guid runId, string permission, CancellationToken ct)
    {
        var run = (await OpsAlerts.ReadAsync(c, RunSelect + "where r.id = $1", ReadRun, ct, runId).ConfigureAwait(false)).SingleOrDefault();
        return run is not null && await access.CoversCompanyAsync(permission, run.CompanyId, ct).ConfigureAwait(false) ? run : null;
    }

    /// <summary>Whether the caller sees every run, else the companies whose runs they see.</summary>
    private async Task<(bool All, Guid[] Companies)> CoverageAsync(NpgsqlConnection c, string permission, CancellationToken ct)
    {
        if (await access.CoversCompanyAsync(permission, null, ct).ConfigureAwait(false))
        {
            return (true, []);
        }

        var covered = new List<Guid>();
        foreach (var company in await OpsAlerts.ReadAsync(c, "select id from org.companies", r => r.GetGuid(0), ct).ConfigureAwait(false))
        {
            if (await access.CoversCompanyAsync(permission, company, ct).ConfigureAwait(false))
            {
                covered.Add(company);
            }
        }

        return (false, [.. covered]);
    }

    private async Task<JobRunView> QueueAsync(NpgsqlConnection c, Guid definitionId, JobSpec spec, Guid? companyId, string parameters, CancellationToken ct)
    {
        if (!spec.Manual)
        {
            throw new JobCentreException("errors.job.not_manual", 400);
        }

        if (!licence.Licence.IsLicensed(spec.Entitlement))
        {
            throw new JobCentreException("errors.job.not_licensed", 403);
        }

        foreach (var permission in new[] { JobPermissions.RunsCreate, spec.RequestPermission }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (!await access.CoversCompanyAsync(permission, companyId, ct).ConfigureAwait(false))
            {
                throw new JobCentreException("errors.job.no_request_permission", 403, [permission]);
            }
        }

        if (companyId is { } company && !await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from org.companies where id = $1)", ct, company).ConfigureAwait(false))
        {
            throw new JobCentreException("errors.job.company_unknown", 400);
        }

        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        await using (var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into job.runs (id, definition_id, company_id, status, priority, attempt, parameters, requested_by, actor_user_id, created_at, created_by, updated_at, updated_by, row_version)
                select $1, d.id, $3, 'queued', d.priority, 0, $4, $5, $6, $7, $5, $7, $5, 1 from job.definitions d where d.id = $2
                """, ct, id, definitionId, companyId, new JsonPayload(parameters), actor.UserId, spec.RunsAs == JobActors.Requester ? actor.UserId : null, now).ConfigureAwait(false);
            await JobStore.LogAsync(c, tx, id, "info", "queued by " + actor.UserId.ToString(), now, ct).ConfigureAwait(false);
            await JobStore.RaiseAsync(c, tx, JobEvents.RunRequested, "job.run", id, now, new { run_id = id, code = spec.Code, company_id = companyId, by = actor.UserId }, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return (await OpsAlerts.ReadAsync(c, RunSelect + "where r.id = $1", ReadRun, ct, id).ConfigureAwait(false)).Single();
    }

    /// <summary>Parameters must be declared, of their declared type, and the required ones present.</summary>
    private static string CheckParameters(JobSpec spec, Dictionary<string, JsonElement> given)
    {
        var problems = new List<string>();
        var declared = spec.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        problems.AddRange(given.Keys.Where(k => !declared.ContainsKey(k)).Select(k => "unknown: " + k));
        foreach (var p in spec.Parameters)
        {
            if (!given.TryGetValue(p.Name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (p.Required)
                {
                    problems.Add("required: " + p.Name);
                }

                continue;
            }

            var ok = p.Type switch
            {
                "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "uuid" => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
                _ => value.ValueKind == JsonValueKind.String,
            };
            if (!ok)
            {
                problems.Add($"{p.Name}: expected {p.Type}");
            }
        }

        if (problems.Count > 0)
        {
            throw new JobCentreException("errors.job.parameters_invalid", 400, problems);
        }

        return JsonSerializer.Serialize(given);
    }

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new JobCentreException("errors.access.forbidden", 403);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    private static async Task<List<T>> ReadInTxAsync<T>(NpgsqlConnection c, NpgsqlTransaction tx, string sql, Func<NpgsqlDataReader, T> read, CancellationToken ct, params object?[] values)
    {
        await using var command = IdentityDb.Command(c, tx, sql, values);
        var list = new List<T>();
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(read(r));
        }

        return list;
    }
}
