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
/// <item>Yearly grants (fixed, or tenure or grade tiers yearly) are due on the first day of the leave year or the joining date when later,
/// under the policy in force that day; the quantity is the year's quantity times the pro-rata share of the year the employment covers (its
/// last day included when known).</item>
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

        // The yearly due dates of every leave-year start month in use (the company's and any a policy gives a type).
        var startMonths = new SortedSet<int> { settingMonth };
        foreach (var month in at.Values.SelectMany(a => a.Rules.Values).Select(r => r.YearStartMonth).OfType<int>())
        {
            startMonths.Add(month);
        }

        if (await policies.AtAsync(e.Id, lastDue, ct).ConfigureAwait(false) is { } current)
        {
            foreach (var month in current.Rules.Values.Select(r => r.YearStartMonth).OfType<int>())
            {
                startMonths.Add(month);
            }
        }

        var yearDue = new SortedSet<DateOnly>();
        foreach (var sm in startMonths)
        {
            for (var ly = LeaveYears.Of(first, sm); ly.Start <= lastDue; ly = LeaveYears.For(ly.Year + 1, sm))
            {
                var due = ly.Start > span.Joining ? ly.Start : span.Joining;
                if (due >= first && due <= lastDue)
                {
                    yearDue.Add(due);
                }
            }
        }

        foreach (var d in yearDue.Where(d => !at.ContainsKey(d)))
        {
            if (await policies.AtAsync(e.Id, d, ct).ConfigureAwait(false) is { } p)
            {
                at[d] = p;
            }
        }

        var postings = new List<PlannedPosting>();
        foreach (var type in types.Where(t => t.IsActive && !t.IsDeleted))
        {
            foreach (var (d, policy) in at)
            {
                if (policy.For(type.Code) is not { } rules || policy.Resolution.Policy is not { } match)
                {
                    continue;
                }

                var rule = rules.Entitlement;
                var ly = LeaveYears.Of(d, rules.YearStartMonth ?? settingMonth);
                if (rule.IsYearly && yearDue.Contains(d) && d == (ly.Start > span.Joining ? ly.Start : span.Joining))
                {
                    var service = EntitlementRules.ServiceMonths(span.ServiceStart, d);
                    var grade = await GradeAsync(policy.Resolution.Attributes.GradeId, ct).ConfigureAwait(false);
                    if (EntitlementRules.TierQuantity(rule, service, grade) is { } full)
                    {
                        var fraction = EntitlementRules.Fraction(rule.ProRata, ly.Start, ly.End, span);
                        var q = EntitlementRules.Earned(rule, full, ly.Start, ly.End, span);
                        var leaver = span.LastDay is { } end && ly.Contains(end) && end <= today ? end : (DateOnly?)null;
                        postings.Add(new PlannedPosting(type.Id, type.Code, EntitlementRules.YearKey(ly.Year), "grant", ly.Year, d, q, match.PolicyId, match.VersionId, match.Version,
                            rule.Model, Inputs(("fullQuantity", full), ("fraction", decimal.Round(fraction, 6)), ("proRata", rule.ProRata), ("serviceMonths", service), ("grade", grade),
                                ("yearStart", ly.Start), ("yearEnd", ly.End)), leaver));
                    }
                }

                if (rule.IsMonthly && monthDue.Contains(d))
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
