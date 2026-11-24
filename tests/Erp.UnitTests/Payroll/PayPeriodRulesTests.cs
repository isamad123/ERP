using Erp.CodeGen;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.UnitTests.Payroll;

/// <summary>The pure calendar rules of a pay group (slice S1.34a): FR-HRM-PAY-01 periods by frequency, FR-HRM-PAY-38 cut-off and payment dates.</summary>
public sealed class PayPeriodRulesTests
{
    [Fact]
    public void FR_HRM_PAY_01_a_monthly_tax_year_has_twelve_calendar_months_from_its_start_month()
    {
        var periods = PayPeriodRules.Generate("monthly", 2026, 7, 3, 5, null, null);
        Assert.Equal(12, periods.Count);
        Assert.Equal(new GeneratedPeriod(1, new(2026, 7, 1), new(2026, 7, 31), new(2026, 7, 28), new(2026, 8, 5)), periods[0]);
        Assert.Equal(new DateOnly(2027, 2, 28), periods[7].End); // February of a common year
        Assert.Equal(new DateOnly(2027, 6, 30), periods[11].End);
        Assert.Equal(Enumerable.Range(1, 12), periods.Select(p => p.PeriodNo));
    }

    [Fact]
    public void FR_HRM_PAY_01_the_periods_of_a_year_are_gap_free_and_never_overlap_in_every_frequency()
    {
        foreach (var (frequency, weekday) in new (string, DayOfWeek?)[] { ("monthly", null), ("semi_monthly", null), ("bi_weekly", DayOfWeek.Friday), ("weekly", DayOfWeek.Monday) })
        {
            var periods = PayPeriodRules.Generate(frequency, 2028, 1, null, null, weekday, null);
            Assert.NotEmpty(periods);
            for (var i = 1; i < periods.Count; i++)
            {
                Assert.Equal(periods[i - 1].End.AddDays(1), periods[i].Start);
            }

            Assert.All(periods, p => Assert.True(p.Start <= p.CutOff && p.CutOff <= p.Payment));
        }
    }

    [Fact]
    public void FR_HRM_PAY_01_a_semi_monthly_year_has_24_periods_the_first_to_the_15th_and_the_16th_to_the_months_end()
    {
        var periods = PayPeriodRules.Generate("semi_monthly", 2028, 1, 2, 0, null, null);
        Assert.Equal(24, periods.Count);
        Assert.Equal((new DateOnly(2028, 1, 1), new DateOnly(2028, 1, 15)), (periods[0].Start, periods[0].End));
        Assert.Equal((new DateOnly(2028, 1, 16), new DateOnly(2028, 1, 31)), (periods[1].Start, periods[1].End));
        Assert.Equal(new DateOnly(2028, 2, 29), periods[3].End); // leap year
        Assert.Equal(new DateOnly(2028, 1, 13), periods[0].CutOff);
        Assert.Equal(periods[0].End, periods[0].Payment);
    }

    [Fact]
    public void FR_HRM_PAY_01_weekly_periods_start_on_the_chosen_weekday_and_a_later_year_continues_from_the_last_period()
    {
        var first = PayPeriodRules.Generate("weekly", 2026, 1, 1, 2, DayOfWeek.Monday, null);
        Assert.Equal(new DateOnly(2026, 1, 5), first[0].Start);
        Assert.Equal(new DateOnly(2026, 1, 11), first[0].End);
        Assert.Equal(new DateOnly(2026, 1, 10), first[0].CutOff);
        Assert.Equal(new DateOnly(2026, 1, 13), first[0].Payment);
        Assert.All(first, p => Assert.Equal(DayOfWeek.Monday, p.Start.DayOfWeek));
        Assert.True(first[^1].End >= new DateOnly(2026, 12, 31));

        // The next year ignores the weekday sent and starts the day after the last period.
        var next = PayPeriodRules.Generate("weekly", 2027, 1, 1, 2, DayOfWeek.Friday, first[^1].End);
        Assert.Equal(first[^1].End.AddDays(1), next[0].Start);
        Assert.Equal(1, next[0].PeriodNo);

        var biweekly = PayPeriodRules.Generate("bi_weekly", 2026, 1, 0, 0, DayOfWeek.Friday, null);
        Assert.Equal(new DateOnly(2026, 1, 2), biweekly[0].Start);
        Assert.Equal(new DateOnly(2026, 1, 15), biweekly[0].End);
        Assert.Equal(26, biweekly.Count);
    }

    [Fact]
    public void FR_HRM_PAY_01_weekly_periods_without_a_weekday_or_a_previous_period_are_not_guessed_and_unknown_frequencies_make_none()
    {
        Assert.Empty(PayPeriodRules.Generate("weekly", 2026, 1, null, null, null, null));
        Assert.Empty(PayPeriodRules.Generate("bi_weekly", 2026, 1, null, null, null, new DateOnly(2025, 6, 30))); // an old period does not reach into the year
        Assert.Empty(PayPeriodRules.Generate("daily", 2026, 1, null, null, DayOfWeek.Monday, null));
    }

    [Theory]
    [InlineData("monday", DayOfWeek.Monday)]
    [InlineData(" Sunday ", DayOfWeek.Sunday)]
    [InlineData("SATURDAY", DayOfWeek.Saturday)]
    public void FR_HRM_PAY_01_a_weekday_is_read_by_name(string name, DayOfWeek expected) => Assert.Equal(expected, PayPeriodRules.ParseWeekday(name));

    [Fact]
    public void FR_HRM_PAY_01_an_unknown_weekday_is_not_read_and_the_tax_year_spans_twelve_months()
    {
        Assert.Null(PayPeriodRules.ParseWeekday("someday"));
        Assert.Null(PayPeriodRules.ParseWeekday(null));
        Assert.Equal(new DateOnly(2027, 6, 30), PayPeriodRules.TaxYearEnd(2026, 7));
        Assert.Equal(new DateOnly(2026, 12, 31), PayPeriodRules.TaxYearEnd(2026, 1));
    }

    [Fact]
    public void FR_HRM_PAY_38_dates_of_a_period_must_be_in_order()
    {
        var start = new DateOnly(2026, 7, 1);
        Assert.True(PayPeriodRules.DatesInOrder(start, start, start));
        Assert.False(PayPeriodRules.DatesInOrder(start, start.AddDays(-1), start.AddDays(5)));
        Assert.False(PayPeriodRules.DatesInOrder(start, start.AddDays(10), start.AddDays(9)));
    }

    [Fact]
    public void FR_HRM_PAY_38_the_pay_set_up_declares_its_event_and_hand_written_permissions_and_each_has_an_English_and_an_Urdu_text()
    {
        Assert.Equal("hrm.pay.cutoff_due", PayEvents.CutoffDue);
        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Payroll/Localization");
        var en = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "pay.en.json")))!;
        var ur = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "pay.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(PayPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        // Every pay key the service code answers with, and every readiness label, has a text.
        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Payroll", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.pay\\.[a-z_]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
        var registry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/pay-readiness.json")));
        Assert.All(registry.RootElement.GetProperty("items").EnumerateArray(), i => Assert.True(en.ContainsKey(i.GetProperty("labelKey").GetString()!), i.ToString()));
    }

    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);
}
