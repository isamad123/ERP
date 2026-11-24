using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The approved leave days of an employment from <c>hrm.leave_request_days</c> of approved requests (contract <see cref="IApprovedLeaveDays"/>,
/// slice S1.29a). The first and last day of a request carry its start and end part (a half day); the days between are whole. S1.32b writes the
/// requests and their days; until then nothing is approved and the reader answers nothing.
/// </summary>
internal sealed class ApprovedLeaveDaysReader(HrmDbContext db) : IApprovedLeaveDays
{
    public async Task<IReadOnlyList<ApprovedLeaveDay>> ForAsync(Guid employmentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var rows = await (from d in db.Set<LeaveRequestDay>().AsNoTracking()
                          join r in db.Set<LeaveRequest>().AsNoTracking() on d.LeaveRequestId equals r.Id
                          join t in db.Set<LeaveType>().AsNoTracking() on r.LeaveTypeId equals t.Id
                          where d.EmploymentId == employmentId && d.LeaveDate >= firstDay && d.LeaveDate <= lastDay && r.Status == "approved" && d.Quantity > 0m
                          select new { d.LeaveDate, d.LeaveRequestId, d.Quantity, r.StartDate, r.EndDate, r.StartPart, r.EndPart, Hours = t.Counting == "hours" })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A type counted in hours stores hours (data design check "<= 1 day (or shift hours for hourly leave)"): minutes, not a share of the day.
        return [.. rows.Select(r => r.Hours
                ? new ApprovedLeaveDay(r.LeaveDate, r.LeaveRequestId, 0m, DayLeave.Full, (int)Math.Round(r.Quantity * 60m, MidpointRounding.AwayFromZero))
                : new ApprovedLeaveDay(r.LeaveDate, r.LeaveRequestId, Math.Min(r.Quantity, 1m),
                    r.LeaveDate == r.StartDate && r.StartPart != DayLeave.Full ? r.StartPart : r.LeaveDate == r.EndDate && r.EndPart != DayLeave.Full ? r.EndPart : DayLeave.Full))
            .OrderBy(r => r.Date).ThenBy(r => r.LeaveRequestId)];
    }
}

/// <summary>
/// The approved duty days of an employment from <c>hrm.duty_requests</c> (contract <see cref="IApprovedDutyDays"/>, slice S1.29a): every local
/// date of the company that the request's time covers. S1.30 writes the requests; until then nothing is approved.
/// </summary>
internal sealed class ApprovedDutyDaysReader(HrmDbContext db, EmploymentApplier applier) : IApprovedDutyDays
{
    public async Task<IReadOnlyList<ApprovedDutyDay>> ForAsync(Guid employmentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var e = await db.Set<Employment>().AsNoTracking().Where(x => x.Id == employmentId).Select(x => new { x.CompanyId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return [];
        }

        var zone = await applier.ZoneAsync(e.CompanyId, cancellationToken).ConfigureAwait(false);
        var earliest = new DateTimeOffset(firstDay.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var latest = new DateTimeOffset(lastDay.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rows = await db.Set<DutyRequest>().AsNoTracking()
            .Where(r => r.EmploymentId == employmentId && r.Status == "approved" && r.StartAt < latest && r.EndAt > earliest)
            .OrderBy(r => r.StartAt).ThenBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var days = new List<ApprovedDutyDay>();
        foreach (var r in rows)
        {
            for (var d = firstDay; d <= lastDay; d = d.AddDays(1))
            {
                var dayStart = Instant(d, zone);
                var dayEnd = Instant(d.AddDays(1), zone);
                if (r.StartAt < dayEnd && r.EndAt > dayStart && !days.Exists(x => x.Date == d))
                {
                    days.Add(new ApprovedDutyDay(d, r.Id, r.Kind, r.StartAt, r.EndAt));
                }
            }
        }

        return days;
    }

    private static DateTimeOffset Instant(DateOnly d, TimeZoneInfo zone)
    {
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

/// <summary>
/// Re-queues days through the outbox (contract <see cref="IAttendanceReprocessing"/>, slice S1.29 Build item 6). The event is added to the
/// caller's context and saved with the caller's change.
/// </summary>
internal sealed class AttendanceReprocessing(HrmDbContext db, Erp.BuildingBlocks.Domain.Time.IClock clock, Erp.BuildingBlocks.Application.Security.ICurrentActor actor) : IAttendanceReprocessing
{
    public void Requeue(Guid employmentId, DateOnly firstDay, DateOnly lastDay, string reason) => Add(db, employmentId, firstDay, lastDay, reason, clock.UtcNow, actor.UserId);

    public static void Add(HrmDbContext db, Guid employmentId, DateOnly firstDay, DateOnly lastDay, string reason, DateTimeOffset now, Guid actor) =>
        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = AttendanceEngineEvents.DaysRequeued,
            AggregateType = AttendanceEngineEvents.AggregateType,
            AggregateId = employmentId,
            Payload = JsonSerializer.SerializeToDocument(new { employmentId, from = firstDay, to = lastDay, reason }, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor }, JsonSerializerOptions.Web),
        });
}

/// <summary>
/// The <c>attendance</c> policy of an employment on a date (slice S1.29 Build item 1, ruling Q2): the attendance policy the day's shift names
/// (<c>hrm.shifts.policy_id</c>, when it is an attendance policy with a version in force), otherwise the one <see cref="IPolicyResolver"/> finds.
/// No policy is an answer: <see cref="AttendancePolicyAnswer.SkipKey"/> says why, and the day is not processed (never a default).
/// </summary>
internal sealed class AttendancePolicyReader(HrmDbContext db, IPolicyResolver resolver)
{
    public const string NoPolicy = "hrm.att.skip.no_policy";
    public const string PolicyInvalid = "hrm.att.skip.policy_invalid";

    private readonly Dictionary<(Guid, DateOnly, Guid?), AttendancePolicyAnswer> cache = [];

    public async Task<AttendancePolicyAnswer> AtAsync(Guid employmentId, DateOnly date, Guid? shiftPolicyId, CancellationToken ct)
    {
        if (cache.TryGetValue((employmentId, date, shiftPolicyId), out var known))
        {
            return known;
        }

        AttendancePolicyAnswer answer;
        var named = shiftPolicyId is { } policyId
            ? await (from v in db.Set<PolicyVersion>().AsNoTracking()
                     join p in db.Set<Policy>().AsNoTracking() on v.PolicyId equals p.Id
                     where p.Id == policyId && p.PolicyType == AttendancePolicy.PolicyType && !p.IsDeleted && v.Status != "draft" && v.ValidFrom <= date && (v.ValidTo == null || v.ValidTo > date)
                     orderby v.ValidFrom descending
                     select new { v.Id, p.Code, v.Version, v.Parameters }).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        if (named is not null)
        {
            answer = Answer(named.Parameters.RootElement, new DayPolicyRef(named.Id, named.Code, named.Version));
        }
        else
        {
            var resolution = await resolver.ResolveAsync(employmentId, AttendancePolicy.PolicyType, date, ct).ConfigureAwait(false);
            answer = resolution?.Policy is { } match ? Answer(match.Parameters, new DayPolicyRef(match.VersionId, match.PolicyCode, match.Version)) : new AttendancePolicyAnswer(null, null, NoPolicy);
        }

        cache[(employmentId, date, shiftPolicyId)] = answer;
        return answer;
    }

    private static AttendancePolicyAnswer Answer(JsonElement parameters, DayPolicyRef reference) =>
        AttendancePolicy.Read(parameters) is { } policy ? new AttendancePolicyAnswer(policy, reference, null) : new AttendancePolicyAnswer(null, reference, PolicyInvalid);
}

/// <summary>The policy of a day, or the reason (a localization key) there is none.</summary>
internal sealed record AttendancePolicyAnswer(AttendancePolicy? Policy, DayPolicyRef? Reference, string? SkipKey);

/// <summary>
/// The attendance period of a day (<c>hrm.attendance_days.period_id</c>, "derived from pay group and date on insert"): the employment's pay
/// group on the date (its assignment), the pay period of that group covering the date (S1.34a's calendar), and the attendance period of that pay
/// period, opened here the first time a day of it is processed (status open, the pay period's dates). S1.31 locks it. Null when the employment
/// has no pay group or no pay period covers the date: the result cannot be stored and the day is reported as not processed.
/// </summary>
internal sealed class AttendancePeriodLocator(HrmDbContext db)
{
    public const string NoPayPeriod = "hrm.att.skip.no_pay_period";

    private readonly Dictionary<Guid, Guid> byPayPeriod = [];

    public async Task<Guid?> PeriodAsync(Guid employmentId, Guid companyId, DateOnly date, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var group = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employmentId && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date))
            .OrderByDescending(a => a.ValidFrom).Select(a => a.PayGroupId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (group is not { } payGroup)
        {
            return null;
        }

        var payPeriod = await db.Set<PayPeriod>().AsNoTracking().Where(p => p.PayGroupId == payGroup && p.StartDate <= date && p.EndDate >= date)
            .Select(p => new { p.Id, p.StartDate, p.EndDate }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (payPeriod is null)
        {
            return null;
        }

        if (byPayPeriod.TryGetValue(payPeriod.Id, out var known))
        {
            return known;
        }

        // An existing period is read without a lock; only opening one takes the pay period's lock (one writer until the transaction ends, so two
        // processors never open it twice; review S1.29a finding 7: no lock held across employments that a waiting processor might hold). Saved
        // through the context so the change log records it.
        var period = await db.Set<AttendancePeriod>().AsNoTracking().Where(p => p.PayPeriodId == payPeriod.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (period is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance_period:" + payPeriod.Id.ToString("D")}, 0))", ct).ConfigureAwait(false);
            period = await db.Set<AttendancePeriod>().AsNoTracking().Where(p => p.PayPeriodId == payPeriod.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        if (period is null)
        {
            var row = new AttendancePeriod
            {
                Id = Guid.CreateVersion7(now), CompanyId = companyId, PayGroupId = payGroup, PayPeriodId = payPeriod.Id, PeriodStart = payPeriod.StartDate, PeriodEnd = payPeriod.EndDate,
                Status = "open", CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor,
            };
            db.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.Entry(row).State = EntityState.Detached;
            period = row.Id;
        }

        byPayPeriod[payPeriod.Id] = period.Value;
        return period;
    }
}
