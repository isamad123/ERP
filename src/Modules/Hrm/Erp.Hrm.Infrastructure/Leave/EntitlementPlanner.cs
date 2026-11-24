using Erp.Hrm.Application.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// One automatic posting the policies call for (slice S1.32 Build item 3): a yearly <c>grant</c> (key <c>Y2026</c>, the whole year as known
/// today, leaver pro-rata included) or a monthly <c>accrual</c> (key <c>M2026-03</c>), with the policy version that decided it and the inputs,
/// so the recompute can explain it (FR-HRM-LVE-14). <see cref="LeaverDate"/> is the employment's last day when it ends inside this leave year
/// on or before today: the yearly grant then owes a leaver correction if it was given before the end was known.
/// </summary>
internal sealed record PlannedPosting(
    Guid LeaveTypeId, string LeaveTypeCode, string Key, string Kind, int LeaveYear, DateOnly EntryDate, decimal Quantity,
    Guid PolicyId, Guid PolicyVersionId, int PolicyVersion, string Model, IReadOnlyDictionary<string, object?> Inputs, DateOnly? LeaverDate);

/// <summary>What the policies call for over the horizon for one employment, and the start of that horizon.</summary>
internal sealed record EmploymentPlan(DateOnly Today, DateOnly HorizonFrom, IReadOnlyList<PlannedPosting> Postings)
{
    public static EmploymentPlan Empty(DateOnly today, DateOnly from) => new(today, from, []);
}

/// <summary>
/// Works out the automatic grants and accruals an employment is due (FR-HRM-LVE-02; slice S1.32 Build item 3), shared by the accrual job, which
/// posts what is missing, and the recompute, which compares (Build item 6), so both always agree. Rules:
/// <list type="bullet">
/// <item>Horizon: from the start of the previous leave year (company setting) to today; nothing before it is posted automatically. A type's
/// latest opening balance is a cut-in: postings due before its date are part of the opening and are not due again.</item>
/// <item>Yearly grants (fixed, or tenure or grade tiers yearly) are due on the first day of the first employed month of the leave year whose
/// model is yearly (the joining date when later); the quantity is the year's quantity times the share of employed months under a yearly
/// model (a mid-year change of model or policy gives each model its own months, review S1.32a finding 4) times the pro-rata share of the
/// year the employment covers (its last day included when known).</item>
/// <item>Monthly accruals (monthly, tiers monthly, per days or hours worked) are due on the month's last day (the employment's last day in its
/// final month) under the policy in force that day; worked-time accruals only once the month's attendance is locked.</item>
/// <item>Tenure counts whole months of service from the service start date; the grade is the assignment's grade on the due date.</item>
/// </list>
/// A retired leave type gets nothing new. A type the policy does not list, or a policy that cannot be applied, gets nothing (no default, AS-4).
/// </summary>
internal sealed class EntitlementPlanner(HrmDbContext db, LeavePolicyReader policies, LeaveYearSetting years, IAttendancePeriodLock locks, WorkedTimeReader worked)
{
    private readonly Dictionary<Guid, string?> grades = [];

    public async Task<EmploymentPlan> PlanAsync(Employment e, IReadOnlyList<LeaveType> types, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(types);
        var settingMonth = await years.StartMonthAsync(e.CompanyId, ct).ConfigureAwait(false);
        var horizon = LeaveYears.Of(today, settingMonth).Start.AddYears(-1);
        if (e.Status == "cancelled" || types.Count == 0)
        {
            return EmploymentPlan.Empty(today, horizon);
        }

        var span = new EmploymentSpan(e.JoiningDate, e.SeparationDate, e.ServiceStartDate);
        var lastDue = span.LastDay is { } l && l < today ? l : today;
        var first = span.Joining > horizon ? span.Joining : horizon;
        if (first > lastDue)
        {
            return EmploymentPlan.Empty(today, horizon);
        }

        // The month due dates: each month's last day, or the employment's last day in its final month.
        var monthDue = new SortedSet<DateOnly>();
        for (var m = new DateOnly(first.Year, first.Month, 1); m <= lastDue; m = m.AddMonths(1))
        {
            var end = m.AddMonths(1).AddDays(-1);
            var due = span.LastDay is { } last && last < end ? last : end;
            if (due >= first && due <= today)
            {
                monthDue.Add(due);
            }
        }

        var at = new SortedDictionary<DateOnly, LeavePolicyAt>();
        foreach (var d in monthDue)
        {
            if (await policies.AtAsync(e.Id, d, ct).ConfigureAwait(false) is { } p)
            {
                at[d] = p;
            }
        }

        var current = await policies.AtAsync(e.Id, lastDue, ct).ConfigureAwait(false);
        var postings = new List<PlannedPosting>();
        foreach (var type in types.Where(t => t.IsActive && !t.IsDeleted))
        {
            // Yearly grants, one per leave year: under the model in force at each month's end (review S1.32a finding 4), the year's quantity
            // times the share of the employed months whose model is yearly, times the pro-rata share of the year the employment covers. A
            // policy that starts, or turns monthly, in the middle of a year so gives the yearly grant only for its own months.
            var sm = current?.For(type.Code)?.YearStartMonth ?? settingMonth;
            for (var ly = LeaveYears.Of(first, sm); ly.Start <= lastDue; ly = LeaveYears.For(ly.Year + 1, sm))
            {
                if (await YearlyAsync(e, type, ly, span, first, lastDue, today, ct).ConfigureAwait(false) is { } grant)
                {
                    postings.Add(grant);
                }
            }

            foreach (var (d, policy) in at)
            {
                if (policy.For(type.Code) is not { } rules || policy.Resolution.Policy is not { } match)
                {
                    continue;
                }

                var rule = rules.Entitlement;
                var ly = LeaveYears.Of(d, rules.YearStartMonth ?? settingMonth);
                if (rule.IsMonthly)
                {
                    var mStart = new DateOnly(d.Year, d.Month, 1);
                    var mEnd = mStart.AddMonths(1).AddDays(-1);
                    if (rule.IsWorked)
                    {
                        // Worked time is final only once the month's attendance is locked (FR-XM-26); until then nothing is due.
                        if (!await locks.IsLockedAsync(e.Id, mStart > span.Joining ? mStart : span.Joining, ct).ConfigureAwait(false)
                            || !await locks.IsLockedAsync(e.Id, d, ct).ConfigureAwait(false))
                        {
                            continue;
                        }

                        var time = await worked.WorkedAsync(e.Id, mStart > span.Joining ? mStart : span.Joining, d, rule.Model == LeaveModels.PerHoursWorked, ct).ConfigureAwait(false);
                        postings.Add(new PlannedPosting(type.Id, type.Code, EntitlementRules.MonthKey(mStart), "accrual", ly.Year, d, EntitlementRules.EarnedByWork(rule, time),
                            match.PolicyId, match.VersionId, match.Version, rule.Model, Inputs(("worked", time), ("per", rule.Per), ("quantity", rule.Quantity)), null));
                        continue;
                    }

                    var service = EntitlementRules.ServiceMonths(span.ServiceStart, d);
                    var grade = await GradeAsync(policy.Resolution.Attributes.GradeId, ct).ConfigureAwait(false);
                    if (EntitlementRules.TierQuantity(rule, service, grade) is { } full)
                    {
                        var fraction = EntitlementRules.Fraction(rule.ProRata, mStart, mEnd, span);
                        postings.Add(new PlannedPosting(type.Id, type.Code, EntitlementRules.MonthKey(mStart), "accrual", ly.Year, d, EntitlementRules.Earned(rule, full, mStart, mEnd, span),
                            match.PolicyId, match.VersionId, match.Version, rule.Model, Inputs(("fullQuantity", full), ("fraction", decimal.Round(fraction, 6)), ("proRata", rule.ProRata),
                                ("serviceMonths", service), ("grade", grade)), null));
                    }
                }
            }
        }

        // An opening balance includes everything due before its date (the cut-in at go-live or at a rehire's start).
        var openings = await db.Set<LeaveLedger>().AsNoTracking()
            .Where(x => x.EmploymentId == e.Id && x.Kind == "opening" && x.ReversesEntryId == null
                && !db.Set<LeaveLedger>().Any(r => r.ReversesEntryId == x.Id))
            .GroupBy(x => x.LeaveTypeId).Select(g => new { g.Key, Date = g.Max(x => x.EntryDate) })
            .ToDictionaryAsync(x => x.Key, x => x.Date, ct).ConfigureAwait(false);
        return new EmploymentPlan(today, horizon,
            [.. postings.Where(p => p.Quantity != 0m && !(openings.TryGetValue(p.LeaveTypeId, out var o) && p.EntryDate < o)).OrderBy(p => p.EntryDate).ThenBy(p => p.LeaveTypeCode, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The yearly grant of one leave year, or null: due on the first day of the first employed month whose model is yearly (the joining date
    /// when later), under that month's policy; the quantity is the year's (tier by service on the due date, grade of that month) times the
    /// share of employed months under a yearly model, times the pro-rata share of the year (the employment's last day included when known).
    /// </summary>
    private async Task<PlannedPosting?> YearlyAsync(Employment e, LeaveType type, LeaveYearSpan ly, EmploymentSpan span, DateOnly first, DateOnly lastDue, DateOnly today, CancellationToken ct)
    {
        var employed = 0;
        var yearly = new List<(DateOnly Month, LeaveTypeRules Rules, LeavePolicyAt Policy)>();
        for (var m = ly.Start; m <= ly.End; m = m.AddMonths(1))
        {
            var mEnd = m.AddMonths(1).AddDays(-1);
            if (mEnd < span.Joining || (span.LastDay is { } last && m > last))
            {
                continue; // a month without the employment counts for neither model (pro-rata takes care of it)
            }

            employed++;
            var probe = span.LastDay is { } end && end < mEnd ? end : mEnd;
            if (await policies.AtAsync(e.Id, probe, ct).ConfigureAwait(false) is { } p && p.For(type.Code) is { Entitlement.IsYearly: true } r && p.Resolution.Policy is not null)
            {
                yearly.Add((m, r, p));
            }
        }

        if (yearly.Count == 0)
        {
            return null;
        }

        var (month, rules, policy) = yearly[0];
        var due = month > span.Joining ? month : span.Joining;
        if (due < first || due > lastDue)
        {
            return null;
        }

        var rule = rules.Entitlement;
        var match = policy.Resolution.Policy!;
        var service = EntitlementRules.ServiceMonths(span.ServiceStart, due);
        var grade = await GradeAsync(policy.Resolution.Attributes.GradeId, ct).ConfigureAwait(false);
        if (EntitlementRules.TierQuantity(rule, service, grade) is not { } full)
        {
            return null;
        }

        var share = (decimal)yearly.Count / employed;
        var fraction = EntitlementRules.Fraction(rule.ProRata, ly.Start, ly.End, span);
        var q = EntitlementRules.Round(full * share * fraction, rule.Rounding, rule.RoundingStep);
        var leaver = span.LastDay is { } leaving && ly.Contains(leaving) && leaving <= today ? leaving : (DateOnly?)null;
        return new PlannedPosting(type.Id, type.Code, EntitlementRules.YearKey(ly.Year), "grant", ly.Year, due, q, match.PolicyId, match.VersionId, match.Version,
            rule.Model, Inputs(("fullQuantity", full), ("yearlyMonths", yearly.Count), ("employedMonths", employed), ("fraction", decimal.Round(fraction, 6)), ("proRata", rule.ProRata),
                ("serviceMonths", service), ("grade", grade), ("yearStart", ly.Start), ("yearEnd", ly.End)), leaver);
    }

    private async Task<string?> GradeAsync(Guid? gradeId, CancellationToken ct)
    {
        if (gradeId is not { } id)
        {
            return null;
        }

        if (!grades.TryGetValue(id, out var code))
        {
            code = await db.Set<Grade>().AsNoTracking().Where(g => g.Id == id).Select(g => g.Code).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            grades[id] = code;
        }

        return code;
    }

    private static Dictionary<string, object?> Inputs(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value is DateOnly d ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : v.Value, StringComparer.Ordinal);
}
