using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The attendance event store (slice S1.27a Build items 1 and 2; FR-HRM-ATT-01, ATT-03, DAT-01, DAT-02): the one writer of
/// <c>hrm.attendance_events</c>. Every punch is appended, never changed (the database refuses updates and deletes). <c>event_at</c> is when the
/// person punched, <c>received_at</c> is the injected server clock; a punch dated beyond <c>hrm.att.future_tolerance_minutes</c> ahead of
/// the server is refused; <c>event_date</c> is the local date in the company's time zone. A key already seen gives the same event, so a
/// device that resends a batch, or arrives hours late, causes no duplicate and no loss. A later punch of the same direction within
/// <c>hrm.att.duplicate_window_seconds</c> of a counted one is kept but marked <c>is_duplicate</c>. Corrections are new events with
/// <c>supersedes_event_id</c>. Each employment is locked while its punch is decided, so concurrent punches cannot both be counted.
/// </summary>
internal sealed class AttendanceEventStore(HrmDbContext db, IClock clock, ICurrentActor actor, AttendanceSettingReader settings, EmploymentApplier applier) : IAttendanceEventStore
{
    public const string UnknownDirection = "errors.hrm.attendance.direction_unknown";
    public const string UnknownSource = "errors.hrm.attendance.source_unknown";
    public const string KeyRequired = "errors.hrm.attendance.key_required";
    public const string KeyTooLong = "errors.hrm.attendance.key_too_long";
    public const string EmploymentUnknown = "errors.hrm.attendance.employment_unknown";
    public const string FutureTime = "errors.hrm.attendance.future_time";
    public const string SupersedesUnknown = "errors.hrm.attendance.supersedes_unknown";
    public const string IpInvalid = "errors.hrm.attendance.ip_invalid";

    public const int MaxKeyLength = 200;

    /// <summary>The employment and local date of each counted punch stored by this call (and the date of a punch a correction replaces).</summary>
    private readonly HashSet<(Guid EmploymentId, DateOnly Date)> touched = [];

    public async Task<IReadOnlyList<AttendanceEventOutcome>> AppendAsync(IReadOnlyList<AttendanceEventInput> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var results = new List<AttendanceEventOutcome>(events.Count);
        if (events.Count == 0)
        {
            return results;
        }

        var now = clock.UtcNow;
        var ids = events.Select(e => e.EmploymentId).Distinct().ToList();
        var companies = await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.CompanyId, cancellationToken).ConfigureAwait(false);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            results.Clear();
            touched.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var input in events)
            {
                results.Add(await AppendOneAsync(input, companies, now, cancellationToken).ConfigureAwait(false));
            }

            await RequeueLateAsync(now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return results;
    }

    private async Task<AttendanceEventOutcome> AppendOneAsync(AttendanceEventInput input, Dictionary<Guid, Guid> companies, DateTimeOffset now, CancellationToken ct)
    {
        var key = input.IdempotencyKey;
        AttendanceEventOutcome Refuse(string error) => new(key ?? string.Empty, AttendanceEventOutcome.Refused, null, false, error);
        if (string.IsNullOrWhiteSpace(key))
        {
            return Refuse(KeyRequired);
        }

        if (key.Length > MaxKeyLength)
        {
            return Refuse(KeyTooLong);
        }

        if (!AttendanceDirections.All.Contains(input.Direction, StringComparer.Ordinal))
        {
            return Refuse(UnknownDirection);
        }

        if (!AttendanceSources.All.Contains(input.Source, StringComparer.Ordinal))
        {
            return Refuse(UnknownSource);
        }

        IPAddress? ip = null;
        if (input.IpAddress is not null && !IPAddress.TryParse(input.IpAddress, out ip))
        {
            return Refuse(IpInvalid);
        }

        if (!companies.TryGetValue(input.EmploymentId, out var companyId))
        {
            return Refuse(EmploymentUnknown);
        }

        var tolerance = await settings.FutureToleranceAsync(companyId, ct).ConfigureAwait(false);
        if (input.EventAt > now.AddMinutes(tolerance))
        {
            return Refuse(FutureTime);
        }

        // One punch of an employment is decided at a time; the lock is released at commit.
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance:" + input.EmploymentId}, 0))", ct).ConfigureAwait(false);

        var seen = await db.Set<AttendanceEvent>().AsNoTracking().Where(e => e.CompanyId == companyId && e.IdempotencyKey == key)
            .Select(e => new { e.Id, e.IsDuplicate }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (seen is not null)
        {
            return new(key, AttendanceEventOutcome.AlreadyReceived, seen.Id, seen.IsDuplicate, null);
        }

        if (input.SupersedesEventId is { } superseded
            && !await db.Set<AttendanceEvent>().AsNoTracking().AnyAsync(e => e.Id == superseded && e.EmploymentId == input.EmploymentId, ct).ConfigureAwait(false))
        {
            return Refuse(SupersedesUnknown);
        }

        var eventAt = input.EventAt.ToUniversalTime();
        var eventDate = await applier.TodayAsync(companyId, eventAt, ct).ConfigureAwait(false);
        var window = await settings.DuplicateWindowAsync(companyId, ct).ConfigureAwait(false);
        var duplicate = input.SupersedesEventId is null && await IsDuplicateAsync(input, eventAt, eventDate, window, ct).ConfigureAwait(false);
        var id = Guid.CreateVersion7(now);
        var created = await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into hrm.attendance_events (id, company_id, employment_id, event_date, event_at, direction, source, device_id, received_at,
                idempotency_key, ip_address, is_duplicate, supersedes_event_id, created_at, created_by)
            values ({id}, {companyId}, {input.EmploymentId}, {eventDate}, {eventAt}, {input.Direction}, {input.Source}, {input.DeviceId}, {now},
                {key}, {ip}, {duplicate}, {input.SupersedesEventId}, {now}, {actor.UserId})
            on conflict (company_id, idempotency_key, event_date) do nothing
            """, ct).ConfigureAwait(false);
        if (created == 0)
        {
            // The same key arrived for another day of the same company a moment ago; answer with the stored punch.
            var other = await db.Set<AttendanceEvent>().AsNoTracking().Where(e => e.CompanyId == companyId && e.IdempotencyKey == key).Select(e => new { e.Id, e.IsDuplicate }).FirstAsync(ct).ConfigureAwait(false);
            return new(key, AttendanceEventOutcome.AlreadyReceived, other.Id, other.IsDuplicate, null);
        }

        if (!duplicate)
        {
            touched.Add((input.EmploymentId, eventDate));
            if (input.SupersedesEventId is { } replaced)
            {
                var replacedDate = await db.Set<AttendanceEvent>().AsNoTracking().Where(e => e.Id == replaced).Select(e => e.EventDate).FirstAsync(ct).ConfigureAwait(false);
                touched.Add((input.EmploymentId, replacedDate));
            }
        }

        return new(key, AttendanceEventOutcome.Stored, id, duplicate, null);
    }

    /// <summary>
    /// A punch that arrives for a day already processed re-queues that day and the day before (whose night shift may own it), and only those
    /// (slice S1.29 Build item 6): an outbox event in this transaction, handled by the engine's consumer. Days not processed yet need nothing.
    /// </summary>
    private async Task RequeueLateAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (employmentId, date) in touched.OrderBy(t => t.EmploymentId).ThenBy(t => t.Date))
        {
            var from = date.AddDays(-1);
            if (await db.Set<AttendanceDay>().AsNoTracking().AnyAsync(d => d.EmploymentId == employmentId && d.WorkDate >= from && d.WorkDate <= date && d.SupersededAt == null, ct).ConfigureAwait(false))
            {
                AttendanceReprocessing.Add(db, employmentId, from, date, "late_punch", now, actor.UserId);
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>A counted punch of the same employment and direction inside the window, on either side (R-154: a late earlier punch is marked, the stored later one stays counted).</summary>
    private async Task<bool> IsDuplicateAsync(AttendanceEventInput input, DateTimeOffset eventAt, DateOnly eventDate, int windowSeconds, CancellationToken ct)
    {
        if (windowSeconds <= 0)
        {
            return false;
        }

        var from = eventAt.AddSeconds(-windowSeconds);
        var to = eventAt.AddSeconds(windowSeconds);
        var (dayFrom, dayTo) = (eventDate.AddDays(-1), eventDate.AddDays(1));
        return await db.Set<AttendanceEvent>().AsNoTracking().AnyAsync(e => e.EmploymentId == input.EmploymentId && e.EventDate >= dayFrom && e.EventDate <= dayTo
            && e.Direction == input.Direction && !e.IsDuplicate && e.EventAt >= from && e.EventAt <= to, ct).ConfigureAwait(false);
    }
}
