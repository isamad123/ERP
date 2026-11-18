using System.Text.Json;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Jobs;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>A run taken by this worker: what the runner needs to execute it.</summary>
internal sealed record ClaimedRun(Guid Id, Guid DefinitionId, string Code, Guid? CompanyId, string? Parameters, string? Checkpoint, int Attempt, DateTimeOffset? ScheduledFor, Guid? ActorUserId, Guid? RequestedBy, bool TakenOver);

/// <summary>The customer's calendar as schedules use it (slice S1.11 ruling Q3, P5).</summary>
internal sealed record JobCalendar(TimeZoneInfo Zone, IReadOnlySet<DayOfWeek> OffDays, IReadOnlySet<DateOnly> Holidays)
{
    public bool IsWorkingDay(DateOnly day) => !(OffDays.Count < 7 && OffDays.Contains(day.DayOfWeek)) && !Holidays.Contains(day);
}

/// <summary>
/// The SQL of the jobs framework on the customer database (slice S1.11 Build item 2). Every statement is parameterized.
/// Times come from the injected clock of the caller.
/// </summary>
internal static class JobStore
{
    private static readonly string[] CalendarKeys = [KernelSettingKeys.OrgTimeZone, KernelSettingKeys.OrgWeeklyOffDays];

    /// <summary>The customer-level calendar, or a company's calendar with its holidays (ruling P5).</summary>
    public static async Task<JobCalendar> CalendarAsync(NpgsqlConnection c, Guid? companyId, DateOnly today, CancellationToken ct)
    {
        var settings = await SettingsStore.ResolveAsync(c, null, CalendarKeys, companyId, null, today, ct).ConfigureAwait(false);
        var zone = WorkingCalendar.Zone(settings.TryGetValue(KernelSettingKeys.OrgTimeZone, out var z) && z.Value.ValueKind == JsonValueKind.String ? z.Value.GetString() : "Asia/Karachi");
        var offDays = settings.TryGetValue(KernelSettingKeys.OrgWeeklyOffDays, out var o) && o.Value.ValueKind == JsonValueKind.Array
            ? WorkingCalendar.ParseOffDays(o.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!))
            : [DayOfWeek.Saturday, DayOfWeek.Sunday];
        var holidays = new HashSet<DateOnly>();
        if (companyId is { } company)
        {
            await using var command = IdentityDb.Command(c, null, """
                select h.holiday_date from org.companies co
                join ref.holidays h on h.calendar_id = co.holiday_calendar_id
                where co.id = $1 and h.kind = any($2) and h.holiday_date between $3 and $4
                """, company, WorkingCalendar.CountedHolidayKinds.ToArray(), today.AddDays(-800), today.AddDays(800));
            await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                holidays.Add(r.GetFieldValue<DateOnly>(0));
            }
        }

        return new JobCalendar(zone, offDays, holidays);
    }

    /// <summary>Enabled scheduled definitions: id, code, schedule.</summary>
    public static async Task<List<(Guid Id, string Code, string Schedule, int Priority, DateTimeOffset CreatedAt)>> ScheduledAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, null, "select id, code, schedule::text, priority, created_at from job.definitions where is_enabled and schedule is not null order by code");
        var list = new List<(Guid, string, string, int, DateTimeOffset)>();
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }

    /// <summary>The latest due slot of each schedule, given the calendar of its company (or of the customer).</summary>
    public static DateTimeOffset? LatestSlot(string scheduleJson, DateTimeOffset now, Func<Guid?, JobCalendar> calendar)
    {
        using var doc = JsonDocument.Parse(scheduleJson);
        var schedule = JobSchedule.Parse(doc.RootElement);
        var cal = calendar(schedule.CalendarCompanyId);
        return schedule.Latest(now, cal.Zone, cal.IsWorkingDay);
    }

    /// <summary>
    /// Queues the run of one schedule slot once (unique slot, FR-KRN-JOB-05): a second worker planning the same slot
    /// changes nothing. No new slot is queued while a run of the job still waits to start (ruling P7).
    /// </summary>
    public static Task<int> PlanSlotAsync(NpgsqlConnection c, Guid definitionId, int priority, DateTimeOffset slot, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, null, """
            insert into job.runs (id, definition_id, status, priority, attempt, scheduled_for, parameters, created_at, created_by, updated_at, updated_by, row_version)
            select $1, $2, 'queued', $3, 0, $4, '{}'::jsonb, $5, $6, $5, $6, 1
            where not exists (select 1 from job.runs w where w.definition_id = $2 and w.status = 'queued')
            on conflict (definition_id, scheduled_for) where scheduled_for is not null do nothing
            """, ct, Guid.CreateVersion7(now), definitionId, priority, slot, now, BaselineSeed.SystemUserId);

    /// <summary>
    /// Takes the next run this worker may start (FR-KRN-JOB-04, FR-KRN-JOB-05): a queued run whose wait is over, or a
    /// running run whose worker stopped renewing its lease; highest priority first, then the oldest; never more running
    /// runs of a job than its concurrency limit. Claims in one customer database are serialized by a transaction lock,
    /// so two workers cannot both pass the concurrency check.
    /// </summary>
    public static async Task<ClaimedRun?> ClaimAsync(NpgsqlConnection c, IReadOnlyCollection<string> codes, string worker, DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            return null;
        }

        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('job.claim'))", ct).ConfigureAwait(false);
        ClaimedRun? claimed = null;
        await using (var command = IdentityDb.Command(c, tx, """
            with candidate as (
                select r.id, r.status = 'running' as taken_over from job.runs r
                join job.definitions d on d.id = r.definition_id
                where d.code = any($1)
                  and ((r.status = 'queued' and (r.next_attempt_at is null or r.next_attempt_at <= $2))
                       or (r.status = 'running' and r.lease_expires_at < $2))
                  and (select count(*) from job.runs x where x.definition_id = r.definition_id and x.status = 'running'
                       and x.lease_expires_at >= $2 and x.id <> r.id) < d.concurrency_limit
                order by r.priority desc, r.created_at, r.id
                limit 1
                for update of r skip locked
            )
            update job.runs r set status = 'running', lease_owner = $3, lease_expires_at = $4, started_at = coalesce(r.started_at, $2),
                attempt = r.attempt + 1, next_attempt_at = null, updated_at = $2, updated_by = $5, row_version = r.row_version + 1
            from candidate, job.definitions d
            where r.id = candidate.id and d.id = r.definition_id
            returning r.id, r.definition_id, d.code, r.company_id, r.parameters::text, r.checkpoint::text, r.attempt, r.scheduled_for, r.actor_user_id, r.requested_by, candidate.taken_over
            """, codes.ToArray(), now, worker, now + lease, BaselineSeed.SystemUserId))
        await using (var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                claimed = new ClaimedRun(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
                    r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetBoolean(10));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>Renews this worker's lease; false when the run is no longer this worker's (taken over, cancelled, finished).</summary>
    public static async Task<bool> RenewAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid run, string worker, DateTimeOffset now, TimeSpan lease, CancellationToken ct) =>
        await IdentityDb.ExecuteAsync(c, tx, """
            update job.runs set lease_expires_at = $4, updated_at = $3
            where id = $1 and lease_owner = $2 and status = 'running'
            """, ct, run, worker, now, now + lease).ConfigureAwait(false) == 1;

    /// <summary>
    /// The chunk guard (FR-KRN-JOB-02): locks this worker's run row; false when the lease was lost. Cancel asked: true
    /// with Cancelled set.
    /// </summary>
    public static async Task<(bool Held, bool Cancelled)> HoldAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid run, string worker, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, "select cancel_requested_at is not null from job.runs where id = $1 and lease_owner = $2 and status = 'running' for update", run, worker);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is bool cancelled ? (true, cancelled) : (false, false);
    }

    public static Task<int> SaveCheckpointAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid run, string? checkpoint, decimal? progress, DateTimeOffset now, TimeSpan lease, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            update job.runs set checkpoint = coalesce($2, checkpoint), progress_pct = coalesce($3, progress_pct), lease_expires_at = $5, updated_at = $4
            where id = $1
            """, ct, run, checkpoint is null ? null : new JsonPayload(checkpoint), progress, now, now + lease);

    public static Task LogAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid run, string level, string message, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            insert into job.run_logs (id, run_id, level, message, created_at, created_by) values ($1, $2, $3, $4, $5, $6)
            """, ct, Guid.CreateVersion7(now), run, level, message.Length > 4000 ? message[..4000] : message, now, BaselineSeed.SystemUserId);

    /// <summary>Ends a run this worker holds: succeeded or cancelled.</summary>
    public static Task<int> FinishAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid run, string worker, string status, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            update job.runs set status = $3, finished_at = $4, lease_owner = null, lease_expires_at = null,
                progress_pct = case when $3 = 'succeeded' then 100 else progress_pct end, updated_at = $4, updated_by = $5, row_version = row_version + 1
            where id = $1 and lease_owner = $2 and status = 'running'
            """, ct, run, worker, status, now, BaselineSeed.SystemUserId);

    /// <summary>A failed attempt with attempts left: queued again, waiting until <paramref name="retryAt"/> (CR-046 item 1, P1).</summary>
    public static Task<int> RetryLaterAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid run, string worker, string error, DateTimeOffset retryAt, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            update job.runs set status = 'queued', next_attempt_at = $4, error = $3, lease_owner = null, lease_expires_at = null,
                updated_at = $5, updated_by = $6, row_version = row_version + 1
            where id = $1 and (lease_owner = $2 or $2::text is null) and status = 'running'
            """, ct, run, worker, error, retryAt, now, BaselineSeed.SystemUserId);

    /// <summary>A run that failed after its last attempt; the failure event and its alert go out in the same transaction (FR-KRN-JOB-06).</summary>
    public static async Task<bool> FailAsync(NpgsqlConnection c, Guid run, string? worker, string code, Guid? owner, string error, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var changed = await IdentityDb.ExecuteAsync(c, tx, """
            update job.runs set status = 'failed', finished_at = $4, error = $3, lease_owner = null, lease_expires_at = null,
                updated_at = $4, updated_by = $5, row_version = row_version + 1
            where id = $1 and (lease_owner = $2 or $2::text is null) and status in ('running', 'queued')
            """, ct, run, worker, error, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
        if (changed == 1)
        {
            await RaiseAsync(c, tx, JobEvents.RunFailed, "job.run", run, now, new { run_id = run, code, error }, ct).ConfigureAwait(false);
            await OpsAlerts.RaiseAsync(c, tx, AlertKinds.JobFailed, $"{AlertKinds.JobFailed}:{run}", OpsAlerts.Error, new { run_id = run, code, error }, owner, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return changed == 1;
    }

    /// <summary>An event in <c>job.outbox_messages</c>, written in the caller's transaction.</summary>
    public static Task RaiseAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string eventType, string aggregateType, Guid aggregateId, DateTimeOffset now, object payload, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            insert into job.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, $4, $5, $6, '{}'::jsonb, null)
            """, ct, Guid.CreateVersion7(now), now, eventType, aggregateType, aggregateId, new JsonPayload(JsonSerializer.Serialize(payload)));

    public static Task<Guid?> OwnerOfAsync(NpgsqlConnection c, Guid definitionId, CancellationToken ct) =>
        IdentityDb.ScalarAsync<Guid?>(c, null, "select owner_user_id from job.definitions where id = $1", ct, definitionId);
}
