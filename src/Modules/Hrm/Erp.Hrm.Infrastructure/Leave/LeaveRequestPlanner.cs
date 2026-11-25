using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>What a request asks for, as the planner reads it (dates, parts, hours of the type's unit).</summary>
internal sealed record LeaveAsk(DateOnly Start, DateOnly End, string StartPart, string EndPart, decimal? Hours);

/// <summary>
/// The worked-out request (slice S1.32 Build items 7 and 8): the days and their total, the policy rules and version, the leave year, the balance
/// before and after it, the refusals (field errors, or one conflict with its details). The service saves it; the preview only shows it.
/// </summary>
internal sealed class LeavePlan
{
    public Dictionary<string, string[]> Errors { get; } = new(StringComparer.Ordinal);

    public string? Conflict { get; set; }

    public Dictionary<string, object?>? Details { get; set; }

    public LeaveCount? Count { get; set; }

    public LeaveTypeRules? Rules { get; set; }

    public Guid? PolicyVersionId { get; set; }

    public LeaveYearSpan? Year { get; set; }

    public decimal? Balance { get; set; }

    public decimal? Pending { get; set; }

    public decimal? After { get; set; }

    public decimal? Lowest { get; set; }

    public Guid? BranchId { get; set; }

    /// <summary>For a type counted in hours: the working hours of the day (the request's <c>days</c> is its share of them).</summary>
    public decimal? DayHours { get; set; }

    public bool Refused => Errors.Count > 0 || Conflict is not null;

    public void Field(string field, string key) => Errors.TryAdd(field, [key]);

    public object View(LeaveType type) => new
    {
        leaveTypeId = type.Id,
        code = type.Code,
        unit = type.Unit,
        counting = type.Counting,
        isPaid = type.IsPaid,
        days = Count?.Days.Select(d => new { date = d.Date, dayType = d.DayType, quantity = d.Quantity, kind = d.Kind, part = d.Part, reason = d.Reason }).ToList(),
        total = Count?.Total,
        leaveYear = Year?.Year,
        balance = Balance,
        pending = Pending,
        after = After,
        lowest = Lowest,
        negativeLimit = Rules?.NegativeAllowance,
        policyVersionId = PolicyVersionId,
        approval = Rules is null ? null : new { hr = Rules.HrApproval, cancellation = Rules.CancellationApproval },
    };
}

/// <summary>
/// Works out a leave request and checks it (slice S1.32 Build items 7 and 8; FR-HRM-LVE-01, -03, -04, FR-XM-26). The days come from the employee's
/// effective plan (published roster, weekly off of the shift policy, holiday calendar; S1.28b) counted by the type's rule (<see cref="LeaveDayRules"/>);
/// the checks are, in order: dates inside the employment and one leave year, a <c>leave</c> policy for the type ("no leave policy applies; ask HR"),
/// the count itself, locked attendance periods, a leave year already closed by its year-end, eligibility, notice (the employee's own request; HR
/// entering leave for someone is not held to it), the longest leave, other leave on the same days, combining with touching leave, and the balance:
/// the lowest the year's balance would reach from the first day on, with this request and the employee's other requests still waiting, and
/// expiring leave gone on its date (<see cref="LeaveLots"/>), must stay within the negative limit of the policy. A type that is not paid is not held
/// to a balance (it only counts what was taken).
/// </summary>
internal sealed class LeaveRequestPlanner(
    HrmDbContext db,
    IEffectiveShiftReader shifts,
    LeavePolicyReader policies,
    LeaveYearSetting years,
    IAttendancePeriodLock locks,
    ISettings settings)
{
    public const string AcrossYears = "errors.hrm.leave.request.across_years";
    public const string Overlap = "errors.hrm.leave.request.overlap";
    public const string YearClosed = "errors.hrm.leave.year_closed";

    public IAttendancePeriodLock Locks => locks;

    public LeavePolicyReader Policies => policies;

    /// <summary>Statuses whose days hold the calendar (the database's daily total counts the same).</summary>
    public static readonly string[] Holding = ["submitted", "approved", "cancel_requested"];

    public async Task<LeavePlan> PlanAsync(Employment e, LeaveType type, LeaveAsk ask, Guid? self, LeaveRequest? modifies, DateOnly today, bool own, CancellationToken ct)
    {
        var plan = new LeavePlan();
        if (ask.Start < e.JoiningDate)
        {
            plan.Field("startDate", LeaveLedgerService.BeforeJoining);
        }

        if (e.SeparationDate is { } last && ask.End > last)
        {
            plan.Field("endDate", LeaveLedgerService.AfterLeaving);
        }

        if (plan.Refused)
        {
            return plan;
        }

        var policy = await policies.AtAsync(e.Id, ask.Start, ct).ConfigureAwait(false);
        var rules = policy?.For(type.Code);
        if (rules is null)
        {
            plan.Conflict = LeaveLedgerService.NoPolicy;
            plan.Details = new Dictionary<string, object?> { ["reason"] = policy?.NoPolicyReason(type.Code) ?? "no_employment" };
            return plan;
        }

        plan.Rules = rules;
        plan.PolicyVersionId = policy!.Resolution.Policy?.VersionId;
        var year = await years.YearAsync(e.CompanyId, rules, ask.Start, ct).ConfigureAwait(false);
        plan.Year = year;
        if (ask.End > year.End)
        {
            plan.Field("endDate", AcrossYears);
            plan.Details = new Dictionary<string, object?> { ["yearEnd"] = year.End };
            return plan;
        }

        var hoursPerDay = await HoursPerDayAsync(e.CompanyId, ct).ConfigureAwait(false);
        var days = new List<PlanDay>();
        for (var d = ask.Start; d <= ask.End && days.Count <= LeaveDayRules.MostDays; d = d.AddDays(1))
        {
            days.Add(await PlanDayAsync(e.Id, d, hoursPerDay, ct).ConfigureAwait(false));
        }

        var count = LeaveDayRules.Count(type.Counting, type.Unit, ask.Start, ask.End, ask.StartPart, ask.EndPart, ask.Hours, rules.Sandwich, days);
        plan.Count = count;
        plan.DayHours = type.Counting == "hours" ? days[0].WorkHours : null;
        if (!count.Ok)
        {
            plan.Field(count.Field!, count.Error!);
            return plan;
        }

        var taken = count.Days.Where(d => d.Quantity > 0m).Select(d => d.Date).ToList();
        foreach (var d in taken)
        {
            if (await locks.IsLockedAsync(e.Id, d, ct).ConfigureAwait(false))
            {
                plan.Conflict = LeaveLedgerService.PeriodLocked;
                plan.Details = new Dictionary<string, object?> { ["date"] = d };
                return plan;
            }
        }

        if (await LeaveYearEnds.ClosedAsync(db, e.CompanyId, type.Id, year.Year, ct).ConfigureAwait(false))
        {
            plan.Conflict = YearClosed;
            plan.Details = new Dictionary<string, object?> { ["leaveYear"] = year.Year };
            return plan;
        }

        var person = await db.Set<Person>().AsNoTracking().Where(p => p.Id == e.PersonId).Select(p => new { p.Gender }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var probation = await db.Set<ProbationRecord>().AsNoTracking()
            .AnyAsync(p => p.EmploymentId == e.Id && (p.Status == "in_progress" || p.Status == "extended") && p.StartDate <= ask.Start && (p.ExtendedTo ?? p.EndDate) >= ask.Start, ct).ConfigureAwait(false);
        var refusal = LeaveRequestChecks.Eligibility(rules, person?.Gender, EntitlementRules.ServiceMonths(e.ServiceStartDate, ask.Start), probation)
            ?? (own ? LeaveRequestChecks.NoticeCheck(rules, today, ask.Start) : null)
            ?? LeaveRequestChecks.LongestCheck(rules, count.Total);
        if (refusal is not null)
        {
            plan.Conflict = refusal;
            plan.Details = refusal switch
            {
                LeaveRequestChecks.Notice => new Dictionary<string, object?> { ["minNoticeDays"] = rules.MinNoticeDays },
                LeaveRequestChecks.Longest => new Dictionary<string, object?> { ["maxConsecutiveDays"] = rules.MaxConsecutiveDays, ["total"] = count.Total },
                LeaveRequestChecks.Tenure => new Dictionary<string, object?> { ["minServiceMonths"] = rules.MinServiceMonths },
                _ => null,
            };
            return plan;
        }

        // Other leave on the same days (the database repeats it as a daily total; the shift's hours are checked here).
        var others = await (from d in db.Set<LeaveRequestDay>().AsNoTracking()
                            join r in db.Set<LeaveRequest>().AsNoTracking() on d.LeaveRequestId equals r.Id
                            join t in db.Set<LeaveType>().AsNoTracking() on r.LeaveTypeId equals t.Id
                            where d.EmploymentId == e.Id && d.LeaveDate >= ask.Start.AddDays(-LeaveDayRules.MostDays) && d.LeaveDate <= ask.End.AddDays(LeaveDayRules.MostDays)
                                && Holding.Contains(d.RequestStatus) && d.Quantity > 0m && (self == null || r.Id != self) && (modifies == null || r.Id != modifies.Id)
                                && (self == null || r.ModifiesRequestId != self)
                            select new { d.LeaveDate, d.Quantity, Hours = t.Counting == "hours", t.Code, r.Id })
            .ToListAsync(ct).ConfigureAwait(false);
        var inHours = type.Counting == "hours";
        foreach (var day in count.Days.Where(d => d.Quantity > 0m))
        {
            var same = others.Where(o => o.LeaveDate == day.Date).ToList();
            var otherDays = same.Where(o => !o.Hours).Sum(o => o.Quantity);
            var otherHours = same.Where(o => o.Hours).Sum(o => o.Quantity);
            var work = days.First(p => p.Date == day.Date).WorkHours;
            var full = inHours ? otherDays >= 1m || otherHours + day.Quantity > (work ?? 24m) : otherDays + day.Quantity > 1m || (otherHours > 0m && otherDays + day.Quantity >= 1m);
            if (full)
            {
                plan.Conflict = Overlap;
                plan.Details = new Dictionary<string, object?> { ["date"] = day.Date };
                return plan;
            }
        }

        // Combining: leave that touches this one, with no working day between them.
        var before = await TouchingAsync(e.Id, ask.Start, -1, hoursPerDay, ct).ConfigureAwait(false);
        var after = await TouchingAsync(e.Id, ask.End, 1, hoursPerDay, ct).ConfigureAwait(false);
        var touching = others.Where(o => !o.Hours && (o.LeaveDate >= before && o.LeaveDate < ask.Start || o.LeaveDate <= after && o.LeaveDate > ask.End)).Select(o => o.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (touching.Count > 0)
        {
            var neighbours = touching.Select(code => (code, (IReadOnlyList<string>)(policy.For(code)?.NotCombinedWith ?? []))).ToList();
            if (LeaveRequestChecks.CombinedCheck(rules, neighbours) is { } combined)
            {
                plan.Conflict = combined;
                plan.Details = new Dictionary<string, object?> { ["with"] = touching };
                return plan;
            }
        }

        await BalanceAsync(db, plan, e, type, ask.Start, count.Total, self, modifies, ct).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// The balance impact of a request already saved and waiting (slice S1.33a, the approval card): its leave year's balance at its first day, the other
    /// waiting requests, and the balance after it. Nothing is checked again (notice, overlap and the like were checked when it was saved), so an
    /// urgent request still shows its numbers; null when no leave policy covers the type on its first day.
    /// </summary>
    public async Task<LeavePlan?> ImpactAsync(Employment e, LeaveType type, LeaveRequest row, LeaveRequest? modifies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(row);
        var rules = (await policies.AtAsync(e.Id, row.StartDate, ct).ConfigureAwait(false))?.For(type.Code);
        if (rules is null)
        {
            return null;
        }

        var plan = new LeavePlan { Rules = rules, Year = await years.YearAsync(e.CompanyId, rules, row.StartDate, ct).ConfigureAwait(false) };
        await BalanceAsync(db, plan, e, type, row.StartDate, type.Counting == "hours" ? row.Hours ?? 0m : row.Days, row.Id, modifies, ct).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// The balance of the request's leave year before and after it, and the lowest it reaches from the first day on with the other waiting requests
    /// counted and expiring leave gone; refused below the policy's negative limit (paid types only).
    /// </summary>
    public static async Task BalanceAsync(HrmDbContext db, LeavePlan plan, Employment e, LeaveType type, DateOnly start, decimal total, Guid? self, LeaveRequest? modifies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(type);
        var year = plan.Year!;
        var lines = await LeaveBalances.LinesAsync(db, e.Id, type.Id, year.Year, ct).ConfigureAwait(false);
        var waiting = await db.Set<LeaveRequest>().AsNoTracking()
            .Where(r => r.EmploymentId == e.Id && r.LeaveTypeId == type.Id && r.Status == "submitted" && r.StartDate >= year.Start && r.StartDate <= year.End
                && (self == null || r.Id != self) && (modifies == null || r.Id != modifies.Id))
            .Select(r => new { r.Id, r.StartDate, r.Days, r.Hours, r.ModifiesRequestId }).ToListAsync(ct).ConfigureAwait(false);
        var inHours = type.Counting == "hours";
        var extra = new List<LotLine>();
        foreach (var w in waiting)
        {
            extra.Add(new LotLine(w.Id, w.StartDate, -(inHours ? w.Hours ?? 0m : w.Days), null, null, null, "usage"));
        }

        // A change gives back what the request it replaces took, when that is of the same type and year (its usage is reversed on approval).
        if (modifies is not null && modifies.LeaveTypeId == type.Id && modifies.Status is "approved" or "cancel_requested"
            && await LeaveUsage.FindAsync(db, modifies.Id, ct).ConfigureAwait(false) is { } usage && usage.LeaveYear == year.Year)
        {
            extra.Add(new LotLine(Guid.CreateVersion7(), usage.EntryDate < start ? usage.EntryDate : start, -usage.Quantity, null, usage.Id, null, "usage_reversal"));
        }

        var returned = extra.Where(x => x.Kind == "usage_reversal").Sum(x => x.Quantity);
        plan.Balance = LeaveLots.Project(lines.Where(l => l.Date <= start), start).Closing;
        plan.Pending = -extra.Where(x => x.Kind == "usage").Sum(x => x.Quantity);
        plan.Lowest = LeaveLots.Project([.. lines, .. extra, new LotLine(Guid.Empty, start, -total, null, null, null, "usage")], year.End).LowestFrom(start);
        plan.After = plan.Balance - plan.Pending - total + returned;
        if (type.IsPaid && plan.Lowest < -plan.Rules!.NegativeAllowance)
        {
            plan.Conflict = LeaveLedgerService.BelowAllowed;
            plan.Details = new Dictionary<string, object?> { ["lowest"] = plan.Lowest, ["allowed"] = -plan.Rules.NegativeAllowance };
        }
    }

    /// <summary>The first or last day of a stretch of off days next to <paramref name="from"/>: leave dated up to there touches the request.</summary>
    private async Task<DateOnly> TouchingAsync(Guid employmentId, DateOnly from, int step, decimal? hoursPerDay, CancellationToken ct)
    {
        var d = from.AddDays(step);
        for (var i = 0; i < 31; i++, d = d.AddDays(step))
        {
            var day = await PlanDayAsync(employmentId, d, hoursPerDay, ct).ConfigureAwait(false);
            if (day.DayType is not (PlanDayTypes.WeeklyOff or PlanDayTypes.Holiday))
            {
                return d;
            }
        }

        return d;
    }

    /// <summary>One day of the employee's plan: the kind of day and its working hours (shift time less break; else the setting).</summary>
    public async Task<PlanDay> PlanDayAsync(Guid employmentId, DateOnly date, decimal? hoursPerDay, CancellationToken ct)
    {
        var shift = await shifts.ReadAsync(employmentId, date, ct).ConfigureAwait(false);
        var dayType = shift?.DayType switch
        {
            "working" => PlanDayTypes.Working,
            "weekly_off" => PlanDayTypes.WeeklyOff,
            "holiday" => PlanDayTypes.Holiday,
            _ => PlanDayTypes.None,
        };
        decimal? hours = null;
        if (shift is { Start: { } s, End: { } en })
        {
            var minutes = (en.ToTimeSpan() - s.ToTimeSpan()).TotalMinutes;
            if (shift.CrossesMidnight || minutes <= 0)
            {
                minutes += 24 * 60;
            }

            minutes -= shift.BreakMinutes ?? 0;
            hours = minutes > 0 ? decimal.Round((decimal)minutes / 60m, 2) : null;
        }

        return new PlanDay(date, dayType, hours ?? (dayType == PlanDayTypes.None ? hoursPerDay : null));
    }

    /// <summary>The company's hours of a working day for hourly leave (setting <c>hrm.leave.hours_per_day</c>); null when not set (0).</summary>
    public async Task<decimal?> HoursPerDayAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(HrmSettingKeys.HrmLeaveHoursPerDay, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var h) && h > 0m && h <= 24m ? h : null;
    }

    /// <summary>A short text of a date for details.</summary>
    public static string Text(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
