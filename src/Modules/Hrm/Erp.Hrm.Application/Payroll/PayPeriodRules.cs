namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// The calendar rules of a pay group (slice S1.34a, FR-HRM-PAY-01, slice S1.34 Q6). Pure: no database, no clock, no statutory value.
/// <list type="bullet">
/// <item>monthly: one period per calendar month from the tax-year start month (12 periods);</item>
/// <item>semi-monthly: 1st to 15th and 16th to the month's end (24 periods);</item>
/// <item>weekly and bi-weekly: seven or fourteen days from the first chosen weekday on or after the tax year's start, or the day after
/// the group's last period when that period reaches into the tax year; periods start inside the tax year and may end after it.</item>
/// </list>
/// The cut-off is <c>cutOffDays</c> before the period's last day and the payment <c>paymentDays</c> after it (0 when not set).
/// </summary>
public static class PayPeriodRules
{
    public static readonly string[] Frequencies = ["monthly", "semi_monthly", "bi_weekly", "weekly"];

    public static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>The first day of the tax year that starts in <paramref name="taxYear"/>.</summary>
    public static DateOnly TaxYearStart(int taxYear, int startMonth) => new(taxYear, startMonth, 1);

    /// <summary>The last day of that tax year.</summary>
    public static DateOnly TaxYearEnd(int taxYear, int startMonth) => TaxYearStart(taxYear, startMonth).AddYears(1).AddDays(-1);

    /// <summary>
    /// The periods of a tax year. <paramref name="previousEnd"/> is the last day of the group's latest existing period, if any;
    /// <paramref name="weekStartsOn"/> is needed for weekly and bi-weekly groups without a previous period reaching into the year.
    /// Returns an empty list when a weekly group needs a weekday and none was given.
    /// </summary>
    public static IReadOnlyList<GeneratedPeriod> Generate(string frequency, int taxYear, int startMonth, int? cutOffDays, int? paymentDays,
        DayOfWeek? weekStartsOn, DateOnly? previousEnd)
    {
        var begin = TaxYearStart(taxYear, startMonth);
        var last = TaxYearEnd(taxYear, startMonth);
        var spans = new List<(DateOnly Start, DateOnly End)>();
        switch (frequency)
        {
            case "monthly":
                for (var m = 0; m < 12; m++)
                {
                    var first = begin.AddMonths(m);
                    spans.Add((first, first.AddMonths(1).AddDays(-1)));
                }

                break;
            case "semi_monthly":
                for (var m = 0; m < 12; m++)
                {
                    var first = begin.AddMonths(m);
                    spans.Add((first, first.AddDays(14)));
                    spans.Add((first.AddDays(15), first.AddMonths(1).AddDays(-1)));
                }

                break;
            case "bi_weekly" or "weekly":
                var length = frequency == "weekly" ? 7 : 14;
                DateOnly start;
                if (previousEnd is { } end && end >= begin)
                {
                    start = end.AddDays(1);
                }
                else if (weekStartsOn is { } weekday)
                {
                    start = begin;
                    while (start.DayOfWeek != weekday)
                    {
                        start = start.AddDays(1);
                    }
                }
                else
                {
                    return [];
                }

                for (; start <= last; start = start.AddDays(length))
                {
                    spans.Add((start, start.AddDays(length - 1)));
                }

                break;
            default:
                return [];
        }

        return [.. spans.Select((s, i) => new GeneratedPeriod(i + 1, s.Start, s.End, s.End.AddDays(-(cutOffDays ?? 0)), s.End.AddDays(paymentDays ?? 0)))];
    }

    /// <summary>The weekday named in a request (monday to sunday), or null.</summary>
    public static DayOfWeek? ParseWeekday(string? name) =>
        name is null ? null : Array.IndexOf(Weekdays, name.Trim().ToLowerInvariant()) is var i and >= 0 ? (DayOfWeek)i : null;

    /// <summary>True when a period's cut-off date is on or after its first day and its payment date on or after its cut-off date.</summary>
    public static bool DatesInOrder(DateOnly start, DateOnly cutOff, DateOnly payment) => cutOff >= start && payment >= cutOff;
}
