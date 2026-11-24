using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>Building the engine on a given context (a job chunk's or the dispatcher's transaction) with the services of the scope.</summary>
internal static class AttendanceEngine
{
    /// <summary>The most days one request, run or re-queue covers.</summary>
    public const int MostDays = 62;

    public static AttendanceDayProcessor Processor(HrmDbContext db, IServiceProvider services)
    {
        var resolver = PolicyResolver.For(db, services);
        var effective = new EffectiveShiftReader(db, resolver, services.GetRequiredService<IHolidayCalendars>(), new AttendanceSettingReader(services.GetRequiredService<ISettings>()));
        return new AttendanceDayProcessor(db, resolver, effective, services.GetRequiredService<IApprovedLeaveDays>(), services.GetRequiredService<IApprovedDutyDays>(),
            services.GetRequiredService<IAttendancePeriodLock>(), new EmploymentApplier(db, services.GetRequiredService<ISettings>(), resolver));
    }

    public static async Task<HrmDbContext> OnAsync(IServiceProvider services, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }

    /// <summary>Adds the runs to counts by status, and by reason for skipped days (only counts are kept, so memory stays small).</summary>
    public static void Count(Dictionary<string, int> counts, IEnumerable<DayRun> runs)
    {
        foreach (var r in runs)
        {
            var key = r.Status == DayRun.Skipped ? DayRun.Skipped + ":" + r.Reason : r.Status;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>A short count for logs: "written 3, unchanged 10, skipped 2 (hrm.att.skip.no_policy 2)".</summary>
    public static string Summary(IReadOnlyDictionary<string, int> counts)
    {
        var skipped = counts.Where(c => c.Key.StartsWith(DayRun.Skipped + ":", StringComparison.Ordinal)).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"written {counts.GetValueOrDefault(DayRun.Written)}, unchanged {counts.GetValueOrDefault(DayRun.Unchanged)}, skipped {skipped.Sum(c => c.Value)} ({string.Join(", ", skipped.Select(c => c.Key[(DayRun.Skipped.Length + 1)..] + " " + c.Value.ToString(CultureInfo.InvariantCulture)))})");
    }
}

/// <summary>
/// The job <c>hrm.attendance_process</c> (slice S1.29 Build item 5, ruling Q5): every night (02:00 company time, editable in the job centre) the
/// last two days of every current employment (a day not yet ended is left for the next night), and on demand a range of dates (parameters <c>from</c>, <c>to</c>, at most 62 days, never after
/// today) for the company of the run, a unit (<c>org_unit_id</c>, with its sub-units unless <c>sub_units</c> is false) or one employment
/// (<c>employment_id</c>). Employments are walked in id order in chunks; each chunk is one transaction with the checkpoint, so a stopped run
/// continues after the last employment it finished, and the engine writes a day only when its result changed: a repeated or resumed run adds
/// no row (run-once per employment and date). Days in a locked period are skipped (FR-XM-26). The job actor writes.
/// </summary>
internal sealed class AttendanceProcessJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.attendance_process";

    private const int Chunk = 50;

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var services = job.Services;
        var actor = services.GetRequiredService<ICurrentActor>().UserId;
        var p = job.Parameters;
        var from = Date(p, "from");
        var to = Date(p, "to");
        var only = Uuid(p, "employment_id");
        var unit = Uuid(p, "org_unit_id");
        var subUnits = !(p.ValueKind == JsonValueKind.Object && p.TryGetProperty("sub_units", out var s) && s.ValueKind == JsonValueKind.False);
        if ((from is null) != (to is null))
        {
            throw new JobFailedException("errors.hrm.attendance.process_range_required");
        }

        if (from is { } f && to is { } t && (t < f || t.DayNumber - f.DayNumber + 1 > AttendanceEngine.MostDays))
        {
            throw new JobFailedException(t < f ? "errors.hrm.attendance.process_range_order" : "errors.hrm.attendance.process_range_too_long");
        }

        IReadOnlySet<Guid>? units = null;
        if (unit is { } root)
        {
            units = subUnits
                ? (await services.GetRequiredService<IOrgStructure>().SubtreesAsync([root], to ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), cancellationToken).ConfigureAwait(false)).GetValueOrDefault(root) ?? new HashSet<Guid> { root }
                : new HashSet<Guid> { root };
        }

        var now = clock.UtcNow;
        Guid? after = job.Checkpoint is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("after", out var a) && Guid.TryParse(a.GetString(), out var last) ? last : null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var employments = 0;
        while (true)
        {
            var read = 0;
            var cursor = after;
            var checkpoint = new Leave.WalkCheckpoint { After = cursor?.ToString("D", CultureInfo.InvariantCulture) };
            var chunkRuns = new List<DayRun>();
            var chunkEmployments = 0;
            await job.CommitChunkAsync(async (connection, transaction, ct) =>
            {
                chunkRuns.Clear();
                chunkEmployments = 0;
                await using var db = await AttendanceEngine.OnAsync(services, connection, transaction, ct).ConfigureAwait(false);
                var processor = AttendanceEngine.Processor(db, services);
                var applier = new EmploymentApplier(db, services.GetRequiredService<ISettings>(), PolicyResolver.For(db, services));
                var oldest = (from ?? DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2)).AddDays(-1);
                var query = db.Set<Employment>().AsNoTracking().Where(e => e.Status != "cancelled" && (e.SeparationDate == null || e.SeparationDate >= oldest));
                if (job.CompanyId is { } company)
                {
                    query = query.Where(e => e.CompanyId == company);
                }

                if (only is { } single)
                {
                    query = query.Where(e => e.Id == single);
                }

                if (units is not null)
                {
                    var unitIds = units.ToList();
                    var lastDay = to ?? DateOnly.FromDateTime(now.UtcDateTime);
                    var firstDay = from ?? lastDay.AddDays(-2);
                    query = query.Where(e => db.Set<EmploymentAssignment>().Any(x => x.EmploymentId == e.Id && unitIds.Contains(x.OrgUnitId) && x.ValidFrom <= lastDay && (x.ValidTo == null || x.ValidTo > firstDay)));
                }

                if (cursor is { } fromId)
                {
                    query = query.Where(e => e.Id.CompareTo(fromId) > 0);
                }

                var page = await query.OrderBy(e => e.Id).Take(Chunk).ToListAsync(ct).ConfigureAwait(false);
                read = page.Count;
                var todays = new Dictionary<Guid, DateOnly>();
                foreach (var e in page)
                {
                    after = e.Id;
                    checkpoint.After = e.Id.ToString("D", CultureInfo.InvariantCulture);
                    if (!todays.TryGetValue(e.CompanyId, out var today))
                    {
                        todays[e.CompanyId] = today = await applier.TodayAsync(e.CompanyId, now, ct).ConfigureAwait(false);
                    }

                    // The nightly run (and an on-demand run without dates) works out the last two days, so a day whose shift and window end after
                    // the run (a night shift) is stored the night after, and a missed night is caught up once (review S1.29a finding 2); a day
                    // already stored and unchanged costs a comparison and writes nothing. A range never reaches past today.
                    var first = from ?? today.AddDays(-2);
                    var lastDate = to ?? today.AddDays(-1);
                    if (lastDate > today)
                    {
                        lastDate = today;
                    }

                    if (first > lastDate)
                    {
                        continue;
                    }

                    chunkRuns.AddRange((await processor.ProcessAsync(e, first, lastDate, now, actor, save: true, ct).ConfigureAwait(false)).Select(r => r with { Outcome = null }));
                    chunkEmployments++;
                }
            }, checkpoint, null, cancellationToken).ConfigureAwait(false);
            AttendanceEngine.Count(counts, chunkRuns);
            employments += chunkEmployments;
            if (read < Chunk)
            {
                break;
            }
        }

        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{employments} employments: {AttendanceEngine.Summary(counts)}."), cancellationToken).ConfigureAwait(false);
    }

    private static DateOnly? Date(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static Guid? Uuid(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;
}

/// <summary>
/// Re-queued days (slice S1.29 Build item 6): on <c>hrm.attendance.days_requeued</c> the engine works out the named days of the employment again in
/// the dispatcher's transaction (once per event; a failure is retried, never dropped). Only those days; never after the company's today, at
/// most 62 days, and a locked day is left as it is (FR-XM-26).
/// </summary>
public sealed class AttendanceReprocessConsumer(IServiceProvider services, IClock clock) : IEventConsumer
{
    public string Name => "hrm.attendance_reprocess";

    public bool Accepts(string eventType) => eventType == AttendanceEngineEvents.DaysRequeued;

    public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var p = message.Payload;
        var employmentId = p.GetProperty("employmentId").GetGuid();
        var from = DateOnly.Parse(p.GetProperty("from").GetString()!, CultureInfo.InvariantCulture);
        var to = DateOnly.Parse(p.GetProperty("to").GetString()!, CultureInfo.InvariantCulture);
        var actor = message.Headers.ValueKind == JsonValueKind.Object && message.Headers.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.String && Guid.TryParse(a.GetString(), out var g) ? g : Guid.Empty;
        await using var db = await AttendanceEngine.OnAsync(services, connection, transaction, cancellationToken).ConfigureAwait(false);
        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var today = await new EmploymentApplier(db, services.GetRequiredService<ISettings>(), PolicyResolver.For(db, services)).TodayAsync(e.CompanyId, now, cancellationToken).ConfigureAwait(false);
        if (to > today)
        {
            to = today;
        }

        if (to.DayNumber - from.DayNumber + 1 > AttendanceEngine.MostDays)
        {
            from = to.AddDays(1 - AttendanceEngine.MostDays);
        }

        if (from > to)
        {
            return;
        }

        await AttendanceEngine.Processor(db, services).ProcessAsync(e, from, to, now, actor, save: true, cancellationToken).ConfigureAwait(false);
    }
}
