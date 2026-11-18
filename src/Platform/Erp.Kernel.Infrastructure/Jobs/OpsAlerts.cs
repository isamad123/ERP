using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Jobs;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// Operational alerts (FR-KRN-JOB-06; slice S1.11 Build item 6, rulings Q8, Q9; risks R-012, R-014). Each alert is a
/// <c>kernel.alert.raised</c> event in <c>job.outbox_messages</c>, sent by the notifications of part S1.11b to Tenant
/// Admins (in-app and e-mail, mandatory), the job's owner and, for unusual access, Auditors. The same alert key is raised
/// at most once an hour, so a lasting problem does not flood anyone.
/// </summary>
public static class OpsAlerts
{
    public const string Critical = "critical";
    public const string Error = "error";
    public const string Warning = "warning";

    /// <summary>A scheduled run not started this long after its time is overdue (ruling Q8).</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromMinutes(15);

    /// <summary>Events waiting longer than this are a backlog (ruling Q8).</summary>
    public static readonly TimeSpan BacklogAfter = TimeSpan.FromMinutes(5);

    /// <summary>A consumer still failing an event this long after it was written raises its own alert (ruling Q7).</summary>
    public static readonly TimeSpan ConsumerFailingAfter = TimeSpan.FromHours(1);

    /// <summary>A lease expired this long ago means its worker stopped (another worker takes over within a poll).</summary>
    public static readonly TimeSpan WorkerStoppedAfter = TimeSpan.FromMinutes(1);

    /// <summary>Critical audit events that alert (S1.07 handoff, R-012, R-014).</summary>
    public static IReadOnlyList<string> CriticalAuditEvents { get; } = ["aud.tamper_attempt", "aud.chain_broken", "aud.anchor_copy_mismatch", "aud.clock_ahead"];

    private static readonly TimeSpan Repeat = TimeSpan.FromHours(1);

    /// <summary>Who an alert of this kind goes to, besides the job's owner (rulings Q8, Q9).</summary>
    public static IReadOnlyList<string> AudienceOf(string kind) => kind == AlertKinds.UnusualAccess ? ["tenant_admin", "auditor"] : ["tenant_admin"];

    /// <summary>Raises an alert unless the same key was raised within the last hour; true when raised.</summary>
    public static async Task<bool> RaiseAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string kind, string key, string severity, object details, Guid? owner, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var aggregate = KeyId(key);
        if (await IdentityDb.ScalarAsync<bool>(c, tx, """
            select exists (select 1 from job.outbox_messages where event_type = $1 and aggregate_id = $2 and occurred_at > $3)
            """, ct, JobEvents.AlertRaised, aggregate, now - Repeat).ConfigureAwait(false))
        {
            return false;
        }

        await JobStore.RaiseAsync(c, tx, JobEvents.AlertRaised, "kernel.alert", aggregate, now, new
        {
            kind,
            key,
            severity,
            details,
            owner_user_id = owner,
            audience = AudienceOf(kind),
            mandatory = true,
        }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>A stable id for an alert key (the outbox aggregate id), so "raised in the last hour" is one indexed look-up.</summary>
    public static Guid KeyId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    /// <summary>What one check found: the alerts raised (kinds).</summary>
    public sealed record CheckResult(IReadOnlyList<string> Raised, DateTimeOffset AuditSeenUntil);

    /// <summary>
    /// The checks of the <c>kernel.ops_alerts</c> job: stuck runs and stopped workers, overdue schedules, event backlog
    /// and failing consumers, critical audit events since <paramref name="auditSince"/>, and unusual access (ruling P4).
    /// </summary>
    public static async Task<CheckResult> CheckAsync(NpgsqlConnection c, DateTimeOffset now, DateTimeOffset auditSince, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var raised = new List<string>();
        async Task Raise(string kind, string key, string severity, object details, Guid? owner)
        {
            if (await RaiseAsync(c, null, kind, key, severity, details, owner, now, ct).ConfigureAwait(false))
            {
                raised.Add(kind);
            }
        }

        // Runs past their time limit, and runs whose worker stopped renewing the lease.
        foreach (var (run, code, owner, leaseExpired) in await ReadAsync(c, """
            select r.id, d.code, d.owner_user_id, r.lease_expires_at < $1 - $2 from job.runs r join job.definitions d on d.id = r.definition_id
            where r.status = 'running'
              and (r.lease_expires_at < $1 - $2 or r.started_at + make_interval(secs => d.timeout_seconds * greatest(r.attempt, 1)) + $2 < $1)
            """, r => (r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), r.GetBoolean(3)), ct, now, WorkerStoppedAfter).ConfigureAwait(false))
        {
            var kind = leaseExpired ? AlertKinds.WorkerStopped : AlertKinds.JobStuck;
            await Raise(kind, $"{kind}:{run}", Error, new { run_id = run, code }, owner).ConfigureAwait(false);
        }

        // Scheduled runs not started 15 minutes after their time.
        foreach (var (run, code, owner, slot) in await ReadAsync(c, """
            select r.id, d.code, d.owner_user_id, r.scheduled_for from job.runs r join job.definitions d on d.id = r.definition_id
            where r.status = 'queued' and r.scheduled_for < $1 - $2
            """, r => (r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3)), ct, now, OverdueAfter).ConfigureAwait(false))
        {
            await Raise(AlertKinds.JobOverdue, $"{AlertKinds.JobOverdue}:{run}", Warning, new { run_id = run, code, scheduled_for = slot }, owner).ConfigureAwait(false);
        }

        // Enabled schedules whose latest slot (15 minutes old or more) has no run at all: nobody is planning them.
        await CheckUnplannedAsync(c, now, Raise, ct).ConfigureAwait(false);

        // Events waiting in any outbox.
        foreach (var schema in await OutboxSchemasAsync(c, ct).ConfigureAwait(false))
        {
#pragma warning disable CA2100 // the schema name comes from the catalog and is quoted
            var (oldest, failing) = (await ReadAsync(c, $"""
                select min(occurred_at), min(occurred_at) filter (where attempts > 0) from {Quote(schema)}.outbox_messages where published_at is null
                """, r => (r.IsDBNull(0) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(0), r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1)), ct).ConfigureAwait(false))[0];
#pragma warning restore CA2100
            if (oldest is { } o && o < now - BacklogAfter)
            {
                await Raise(AlertKinds.EventBacklog, $"{AlertKinds.EventBacklog}:{schema}", Warning, new { schema, oldest_event_at = o }, null).ConfigureAwait(false);
            }

            if (failing is { } f && f < now - ConsumerFailingAfter)
            {
                await Raise(AlertKinds.EventConsumerFailing, $"{AlertKinds.EventConsumerFailing}:{schema}", Error, new { schema, oldest_failing_event_at = f }, null).ConfigureAwait(false);
            }
        }

        // Critical audit events since the last check (R-012, R-014).
        var seenUntil = auditSince;
        foreach (var (id, at, type, severity) in await ReadAsync(c, """
            select id, occurred_at, event_type, severity from aud.security_events
            where occurred_at > $1 and occurred_at <= $2 and (event_type = any($3) or severity = 'critical')
            order by occurred_at
            """, r => (r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3)), ct, auditSince, now, CriticalAuditEvents.ToArray()).ConfigureAwait(false))
        {
            var kind = type == "aud.clock_ahead" ? AlertKinds.ClockAhead : AlertKinds.AuditCritical;
            await Raise(kind, $"{kind}:{id}", Critical, new { security_event_id = id, event_type = type, severity, occurred_at = at }, null).ConfigureAwait(false);
            seenUntil = at > seenUntil ? at : seenUntil;
        }

        seenUntil = now > seenUntil ? now : seenUntil;

        // Unusual access (HRM-PRV-05, ruling Q9, P4): more than the setting's sensitive records of one user in the last hour.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var settings = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.AudAlertsSensitivePeoplePerHour], null, null, today, ct).ConfigureAwait(false);
        var limit = settings.TryGetValue(KernelSettingKeys.AudAlertsSensitivePeoplePerHour, out var s) && s.Value.ValueKind == JsonValueKind.Number && s.Value.TryGetInt32(out var n) && n >= 1 ? n : 50;
        foreach (var (user, records) in await ReadAsync(c, """
            select actor_user_id,
                   count(distinct (entity_type, entity_id)) filter (where action = 'view' and entity_id is not null)
                   + coalesce(sum(coalesce(row_count, 1)) filter (where action in ('export', 'print') or entity_id is null), 0)
            from aud.access_log where occurred_at > $1 and occurred_at <= $2
            group by actor_user_id
            """, r => (r.GetGuid(0), r.GetInt64(1)), ct, now - TimeSpan.FromHours(1), now).ConfigureAwait(false))
        {
            if (records > limit)
            {
                var hour = now.UtcDateTime.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);
                await Raise(AlertKinds.UnusualAccess, $"{AlertKinds.UnusualAccess}:{user}:{hour}", Critical, new { user_id = user, records, limit, window_minutes = 60 }, null).ConfigureAwait(false);
            }
        }

        return new CheckResult(raised, seenUntil);
    }

    private static async Task CheckUnplannedAsync(NpgsqlConnection c, DateTimeOffset now, Func<string, string, string, object, Guid?, Task> raise, CancellationToken ct)
    {
        var definitions = await ReadAsync(c, """
            select d.id, d.code, d.owner_user_id, d.schedule::text, d.created_at,
                   (select max(r.scheduled_for) from job.runs r where r.definition_id = d.id),
                   exists (select 1 from job.runs r where r.definition_id = d.id and r.status in ('queued', 'running'))
            from job.definitions d where d.is_enabled and d.schedule is not null
            """, r => (Id: r.GetGuid(0), Code: r.GetString(1), Owner: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), Schedule: r.GetString(3),
                Created: r.GetFieldValue<DateTimeOffset>(4), Last: r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(5), Open: r.GetBoolean(6)), ct).ConfigureAwait(false);
        if (definitions.Count == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var calendars = new Dictionary<Guid, JobCalendar>();
        var customer = await JobStore.CalendarAsync(c, null, today, ct).ConfigureAwait(false);
        foreach (var d in definitions.Where(d => !d.Open))
        {
            JobSchedule schedule;
            try
            {
                using var doc = JsonDocument.Parse(d.Schedule);
                schedule = JobSchedule.Parse(doc.RootElement);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
                continue;
            }

            var calendar = customer;
            if (schedule.CalendarCompanyId is { } company)
            {
                calendar = calendars.TryGetValue(company, out var cal) ? cal : calendars[company] = await JobStore.CalendarAsync(c, company, today, ct).ConfigureAwait(false);
            }

            if (schedule.Latest(now - OverdueAfter, calendar.Zone, calendar.IsWorkingDay) is { } slot && slot >= d.Created && (d.Last is null || d.Last < slot))
            {
                await raise(AlertKinds.JobOverdue, $"{AlertKinds.JobOverdue}:{d.Id}:{slot:O}", Warning, new { code = d.Code, scheduled_for = slot }, d.Owner).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The schemas of the customer database that have an outbox (every module schema, spec 03 section 7.5) on which the
    /// application login holds <paramref name="privilege"/> (SELECT to read, UPDATE to dispatch, DELETE to purge). The audit
    /// schema's outbox is read-only for the application (spec 03 section 3.7), so nothing is dispatched or purged there.
    /// </summary>
    public static async Task<List<string>> OutboxSchemasAsync(NpgsqlConnection c, CancellationToken ct, string privilege = "SELECT") =>
        await ReadAsync(c, """
            select n.nspname from pg_class o join pg_namespace n on n.oid = o.relnamespace
            join pg_class i on i.relnamespace = n.oid and i.relname = 'inbox_messages' and i.relkind in ('r', 'p')
            where o.relname = 'outbox_messages' and o.relkind in ('r', 'p') and n.nspname not like 'pg\_%' and n.nspname <> 'information_schema'
              and has_table_privilege(o.oid, $1)
              and has_table_privilege(i.oid, case when $1 = 'SELECT' then 'SELECT' else 'INSERT' end)
            order by n.nspname
            """, r => r.GetString(0), ct, privilege).ConfigureAwait(false);

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static async Task<List<T>> ReadAsync<T>(NpgsqlConnection c, string sql, Func<NpgsqlDataReader, T> read, CancellationToken ct, params object?[] values)
    {
        await using var command = IdentityDb.Command(c, null, sql, values);
        var list = new List<T>();
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(read(r));
        }

        return list;
    }
}
