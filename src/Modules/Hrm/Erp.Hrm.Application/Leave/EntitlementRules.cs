using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Erp.Hrm.Application.Leave;

/// <summary>A leave year: the year number (the calendar year it starts in) and its first and last days.</summary>
public sealed record LeaveYearSpan(int Year, DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

/// <summary>
/// Leave years (slice S1.32 ruling Q3): a leave year starts on the first day of the company's month (setting
/// <c>hrm.leave.year_start_month</c>, January by default) or of the month a policy gives one leave type, and is numbered by the calendar year
/// it starts in.
/// </summary>
public static class LeaveYears
{
    public static LeaveYearSpan For(int year, int startMonth)
    {
        if (startMonth is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(startMonth));
        }

        var start = new DateOnly(year, startMonth, 1);
        return new LeaveYearSpan(year, start, start.AddYears(1).AddDays(-1));
    }

    public static LeaveYearSpan Of(DateOnly date, int startMonth) => For(date.Month >= startMonth ? date.Year : date.Year - 1, startMonth);
}

/// <summary>The part of an employment the entitlement rules look at: the joining date, the last day (when known) and the service start.</summary>
public sealed record EmploymentSpan(DateOnly Joining, DateOnly? LastDay, DateOnly ServiceStart)
{
    public bool Covers(DateOnly date) => date >= Joining && (LastDay is not { } last || date <= last);
}

/// <summary>
/// The arithmetic of the entitlement models (FR-HRM-LVE-02; slice S1.32 Build item 3, rulings Q3 and Q7), pure so that the accrual job and
/// the recompute (FR-HRM-LVE-14) always agree. Decimal only, never floating point; a quantity is kept to four decimal places unless the policy
/// rounds it to a step.
/// </summary>
public static class EntitlementRules
{
    /// <summary>Kept places of a quantity (ruling Q7).</summary>
    public const int Places = 4;

    /// <summary>
    /// The share of a grant period (a leave year or a month, <paramref name="start"/> to <paramref name="end"/>) the employment earns under
    /// the pro-rata rule: <c>none</c> all of it when it covers any day, <c>days</c> by days covered, <c>months_started</c> by months with any
    /// day covered, <c>months_completed</c> by whole months covered.
    /// </summary>
    public static decimal Fraction(string proRata, DateOnly start, DateOnly end, EmploymentSpan employment)
    {
        ArgumentNullException.ThrowIfNull(employment);
        var from = Max(start, employment.Joining);
        var to = employment.LastDay is { } last ? Min(end, last) : end;
        if (from > to)
        {
            return 0m;
        }

        switch (proRata)
        {
            case LeaveModels.ProRataNone:
                return 1m;
            case LeaveModels.ProRataDays:
                return (decimal)(to.DayNumber - from.DayNumber + 1) / (end.DayNumber - start.DayNumber + 1);
            case LeaveModels.ProRataMonthsStarted:
            case LeaveModels.ProRataMonthsCompleted:
                var months = 0;
                var counted = 0;
                for (var m = new DateOnly(start.Year, start.Month, 1); m <= end; m = m.AddMonths(1))
                {
                    months++;
                    var mEnd = m.AddMonths(1).AddDays(-1);
                    var covered = proRata == LeaveModels.ProRataMonthsStarted ? from <= mEnd && to >= m : from <= m && to >= mEnd;
                    counted += covered ? 1 : 0;
                }

                return (decimal)counted / months;
            default:
                throw new ArgumentException("Unknown pro-rata rule: " + proRata, nameof(proRata));
        }
    }

    /// <summary>Rounds a computed quantity as the policy says (to a step: up, down or nearest, halves away from zero), then to four places.</summary>
    public static decimal Round(decimal quantity, string mode, decimal? step)
    {
        if (mode != LeaveModels.RoundNone && step is > 0m and var s)
        {
            var units = quantity / s;
            units = mode switch
            {
                LeaveModels.RoundUp => decimal.Ceiling(units),
                LeaveModels.RoundDown => decimal.Floor(units),
                _ => decimal.Round(units, 0, MidpointRounding.AwayFromZero),
            };
            quantity = units * s;
        }

        return decimal.Round(quantity, Places, MidpointRounding.ToEven);
    }

    /// <summary>Whole months of service from <paramref name="serviceStart"/> to <paramref name="at"/> (a month is complete on the same day of the month).</summary>
    public static int ServiceMonths(DateOnly serviceStart, DateOnly at)
    {
        if (at < serviceStart)
        {
            return 0;
        }

        var months = ((at.Year - serviceStart.Year) * 12) + at.Month - serviceStart.Month;
        if (serviceStart.AddMonths(months) > at)
        {
            months--;
        }

        return Math.Max(0, months);
    }

    /// <summary>
    /// The quantity of a tier for the employee: tenure tiers take the band with the most months not above the service; grade tiers the band of
    /// the grade's code. Null when no tier applies (service below every band, no grade, or a grade the policy does not list).
    /// </summary>
    public static decimal? TierQuantity(EntitlementRule rule, int serviceMonths, string? gradeCode)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Model switch
        {
            LeaveModels.TenureTiers => rule.Tiers.Where(t => t.ServiceMonthsFrom is { } f && f <= serviceMonths).OrderByDescending(t => t.ServiceMonthsFrom).Select(t => (decimal?)t.Quantity).FirstOrDefault(),
            LeaveModels.GradeTiers => gradeCode is null ? null : rule.Tiers.Where(t => string.Equals(t.Grade, gradeCode, StringComparison.OrdinalIgnoreCase)).Select(t => (decimal?)t.Quantity).FirstOrDefault(),
            _ => rule.Quantity,
        };
    }

    /// <summary>The grant or accrual of a period from a full-period quantity: the pro-rata share, then the rounding.</summary>
    public static decimal Earned(EntitlementRule rule, decimal fullQuantity, DateOnly start, DateOnly end, EmploymentSpan employment)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Round(fullQuantity * Fraction(rule.ProRata, start, end, employment), rule.Rounding, rule.RoundingStep);
    }

    /// <summary>The accrual of time worked: <c>quantity</c> for every <c>per</c> days or hours worked, then the rounding.</summary>
    public static decimal EarnedByWork(EntitlementRule rule, decimal worked)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Per is > 0m and var per && rule.Quantity is { } q ? Round(worked / per * q, rule.Rounding, rule.RoundingStep) : 0m;
    }

    /// <summary>The key of a yearly grant (<c>Y2026</c>), of its leaver correction (<c>Y2026/leaver</c>) and of a monthly accrual (<c>M2026-03</c>).</summary>
    public static string YearKey(int leaveYear) => string.Create(CultureInfo.InvariantCulture, $"Y{leaveYear}");

    public static string LeaverKey(int leaveYear) => YearKey(leaveYear) + "/leaver";

    public static string MonthKey(DateOnly month) => string.Create(CultureInfo.InvariantCulture, $"M{month:yyyy-MM}");

    /// <summary>
    /// The source id of an automatic posting: the same for the same employment, leave type and period key, so the database's unique index on
    /// (source type, source id, kind, leave type) lets each be credited once (run-once, Build item 3).
    /// </summary>
    public static Guid SourceId(Guid employmentId, Guid leaveTypeId, string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"hrm.leave_accrual|{employmentId:D}|{leaveTypeId:D}|{key}"))).AsSpan(0, 16));

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
