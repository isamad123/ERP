using System.Text.Json;
using Erp.CodeGen;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.UnitTests.Payroll;

/// <summary>
/// The payroll engine of slice S1.35a as pure rules: the formula language (FR-HRM-PAY-03, ruling Q1), every calculation method with a worked example
/// whose expected value is computed here independently, the pro-rata rules (FR-HRM-PAY-05), rounding (ruling Q3), the statutory plug-in point with test
/// calculators (ruling Q2, never a statutory number) and determinism from the snapshot (FR-HRM-PAY-31).
/// </summary>
public sealed class PayEngineUnitTests
{
    private static readonly DateOnly Start = new(2027, 1, 1);
    private static readonly DateOnly End = new(2027, 1, 31);
    private static readonly Guid Structure = Guid.CreateVersion7();
    private static readonly Guid Employment = Guid.CreateVersion7();
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(["BASIC", "HOUSE"], StringComparer.Ordinal);

    // ------------------------------------------------------------------ formula language

    [Theory]
    [InlineData("BASIC * 0.45")]
    [InlineData("if($worked_days >= 20, BASIC * 0.1, 0) + max(HOUSE, 1000, 2)")]
    [InlineData("round((BASIC - HOUSE) / $period_days * $paid_days, 2)")]
    [InlineData("-BASIC + --2")]
    [InlineData("if(BASIC <> HOUSE, 1, 2) + if(BASIC = 0, 3, 4) + if(BASIC < 1, 5, 6) + if(BASIC <= 1, 7, 8) + if(BASIC > 1, 9, 10)")]
    public void FR_HRM_PAY_03_the_completed_grammar_accepts_numbers_codes_inputs_operators_and_the_four_functions(string formula)
    {
        var check = new FormulaValidator().Check(formula, Known);
        Assert.True(check.IsValid, string.Join(",", check.Problems.Select(p => p.ErrorKey + ":" + p.Name)));
    }

    [Theory]
    [InlineData("BASIC +", FormulaLanguage.Syntax)]
    [InlineData("BASIC HOUSE", FormulaLanguage.Syntax)]
    [InlineData("()", FormulaLanguage.Syntax)]
    [InlineData("min(BASIC)", FormulaLanguage.Arguments)]
    [InlineData("round(BASIC, 2, 3)", FormulaLanguage.Arguments)]
    [InlineData("if(BASIC, 1, 2)", FormulaLanguage.Arguments)]
    [InlineData("if(BASIC > 1, 2)", FormulaLanguage.Arguments)]
    [InlineData("BASIC > 1", FormulaLanguage.Comparison)]
    [InlineData("min(BASIC > 1, 2)", FormulaLanguage.Comparison)]
    [InlineData("round(BASIC, 5)", FormulaLanguage.RoundPlaces)]
    [InlineData("round(BASIC, 1.5)", FormulaLanguage.RoundPlaces)]
    [InlineData("round(BASIC, HOUSE)", FormulaLanguage.RoundPlaces)]
    [InlineData("BASIC * $no_such_input", FormulaLanguage.InputUnknown)]
    [InlineData("BASIC ^ 2", FormulaValidator.Token)]
    [InlineData("loop(BASIC)", FormulaValidator.Token)]
    public void FR_HRM_PAY_03_a_formula_with_a_forbidden_construct_is_refused_when_it_is_defined(string formula, string key)
    {
        var check = new FormulaValidator().Check(formula, Known);
        Assert.Contains(check.Problems, p => p.ErrorKey == key);
    }

    [Fact]
    public void FR_HRM_PAY_03_R_282_the_catalogue_of_inputs_is_published_and_a_provider_may_add_names()
    {
        var validator = new FormulaValidator([new PayInputNameSet("loans", ["loan_installments"])]);
        Assert.Contains("worked_days", validator.KnownInputs);
        Assert.Contains("paid_days", validator.KnownInputs);
        Assert.Contains("period_days", validator.KnownInputs);
        Assert.Contains("loan_installments", validator.KnownInputs);
        Assert.True(validator.Check("BASIC - $loan_installments", Known).IsValid);
        Assert.False(new FormulaValidator().Check("BASIC - $loan_installments", Known).IsValid);
        Assert.Contains("if", ComponentRules.Reserved);
    }

    [Fact]
    public void FR_HRM_PAY_03_evaluation_is_decimal_rounds_each_step_to_four_places_and_takes_only_the_branch_of_if_it_needs()
    {
        decimal Eval(string f, decimal basic, decimal worked, MidpointRounding mode = MidpointRounding.AwayFromZero)
        {
            var (tree, problems) = FormulaLanguage.Parse(f);
            Assert.True(tree is not null, string.Join(",", problems.Select(p => p.ErrorKey)));
            return FormulaLanguage.Evaluate(tree, c => c == "BASIC" ? basic : 0m, n => n == "worked_days" ? worked : 0m, mode);
        }

        Assert.Equal(decimal.Round(100m / 3m, 4, MidpointRounding.AwayFromZero), Eval("BASIC / 3", 100m, 0m)); // 33.3333
        Assert.Equal(33.33m, Eval("round(BASIC / 3, 2)", 100m, 0m));
        Assert.Equal(2.35m, Eval("round(2.345, 2)", 0m, 0m));
        Assert.Equal(2.34m, Eval("round(2.345, 2)", 0m, 0m, MidpointRounding.ToEven));
        Assert.Equal(0m, Eval("if($worked_days > 0, BASIC / $worked_days, 0)", 100m, 0m)); // the division is not reached
        Assert.Equal(-7m, Eval("min(-BASIC, 3) + 3", 10m, 0m));
        var (zero, _) = FormulaLanguage.Parse("BASIC / $worked_days");
        var e = Assert.Throws<FormulaEvaluationException>(() => FormulaLanguage.Evaluate(zero!, _ => 1m, _ => 0m, MidpointRounding.AwayFromZero));
        Assert.Equal(FormulaLanguage.DivisionByZero, e.ErrorKey);
    }

    // ------------------------------------------------------------------ methods (FR-HRM-PAY-03), one worked example each

    [Fact]
    public void FR_HRM_PAY_03_every_method_is_worked_out_as_an_independent_calculation_gives_it()
    {
        var b = new Snap();
        var basic = b.Component("BASIC", "earning", 1, "fixed", amount: 50_000m);
        var house = b.Component("HOUSE", "earning", 2, "percent_of", percent: 45m, bases: [basic]);
        b.Component("CONV", "earning", 3, "formula", formula: "round(BASIC * 0.1 + $overtime_hours * 10, 0)");
        b.Component("MEAL", "earning", 4, "per_day", amount: 300m);
        b.Component("HOURLY", "earning", 5, "per_hour", amount: 250m);
        b.Component("ATTEND", "earning", 6, "attendance_linked", amount: 3_100m);
        var levy = b.Component("BANDED", "deduction", 7, "table_lookup", table: "test.bands");
        var bonus = b.Component("BONUS", "earning", 8, "manual");
        b.Component("EMPLOYER", "employer_contribution", 9, "fixed", amount: 2_000m);
        b.Component("CANTEEN", "deduction", 10, "fixed", amount: 1_000m);
        b.Component("TRAVEL", "reimbursement", 11, "fixed", amount: 1_500m);
        b.Table("test.bands", (null, 100_000m, 0m, 0.01m), (100_000m, null, 1_000m, 0.02m));
        b.Named("worked_days", 20m).Named("worked_hours", 160m).Named("overtime_hours", 12.5m);
        b.Amount(bonus, 5_000m);
        b.NonWorking(Weekends());
        var calc = PayEngine.Calculate(b.Build(), []);
        Assert.True(calc.Complete, string.Join(",", calc.Problems.Select(p => p.Key + ":" + p.Subject)));

        // Expected values, each from its own arithmetic.
        var expectedBasic = 50_000m;
        var expectedHouse = expectedBasic * 45m / 100m; // 22,500
        var expectedConv = Math.Round((expectedBasic * 0.1m) + (12.5m * 10m), 0, MidpointRounding.AwayFromZero); // 5,125
        var expectedMeal = 300m * 31m; // per day times paid days (31 employed, no unpaid leave)
        var expectedHourly = 250m * 160m;
        var workingDays = 31 - Weekends().Count; // 21 in January 2027
        var expectedAttend = Math.Round(3_100m * 20m / workingDays, 2, MidpointRounding.AwayFromZero);
        var earningsBeforeBand = expectedBasic + expectedHouse + expectedConv + expectedMeal + expectedHourly + Math.Round(3_100m * 20m / workingDays, 4, MidpointRounding.AwayFromZero);
        var expectedBand = Math.Round(1_000m + (0.02m * (earningsBeforeBand - 100_000m)), 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedBasic, Line(calc, "BASIC"));
        Assert.Equal(expectedHouse, Line(calc, "HOUSE"));
        Assert.Equal(expectedConv, Line(calc, "CONV"));
        Assert.Equal(expectedMeal, Line(calc, "MEAL"));
        Assert.Equal(expectedHourly, Line(calc, "HOURLY"));
        Assert.Equal(expectedAttend, Line(calc, "ATTEND"));
        Assert.Equal(expectedBand, Line(calc, "BANDED"));
        Assert.Equal(5_000m, Line(calc, "BONUS"));
        var gross = expectedBasic + expectedHouse + expectedConv + expectedMeal + expectedHourly + expectedAttend + 5_000m;
        Assert.Equal(gross, calc.Gross);
        Assert.Equal(expectedBand + 1_000m, calc.Deductions);
        Assert.Equal(1_500m, calc.Reimbursements);
        Assert.Equal(gross + 1_500m - expectedBand - 1_000m, calc.Net);
        Assert.Equal(2_000m, calc.EmployerCost);
        Assert.Equal(20m, calc.WorkedDays);
        Assert.Equal(31m, calc.PaidDays);
        Assert.Equal(300m, calc.Lines.Single(l => l.Code == "MEAL").Rate);
        Assert.Equal(31m, calc.Lines.Single(l => l.Code == "MEAL").Units);
        Assert.Contains(calc.Trace, t => t.Component == "BANDED" && t.Text.Contains("test.bands", StringComparison.Ordinal));
        Assert.Equal(PayEngine.Version, calc.EngineVersion);
        _ = house;
        _ = levy;
    }

    [Fact]
    public void FR_HRM_PAY_03_a_table_with_keyed_rows_or_without_a_row_for_the_earnings_and_a_units_input_without_a_rate_make_the_employee_incomplete()
    {
        var b = new Snap();
        b.Component("BASIC", "earning", 1, "fixed", amount: 10_000m);
        b.Component("BANDED", "deduction", 2, "table_lookup", table: "test.keyed");
        b.Table("test.keyed", (null, null, 5m, null));
        b.KeyRows();
        var calc = PayEngine.Calculate(b.Build(), []);
        Assert.Contains(calc.Problems, p => p.Key == PayEngine.TableNeedsCalculator && p.Subject == "BANDED");

        var c = new Snap();
        c.Component("BASIC", "earning", 1, "fixed", amount: 10_000m);
        c.Component("BANDED", "deduction", 2, "table_lookup", table: "test.high");
        c.Table("test.high", (50_000m, null, 5m, null));
        var bonus = c.Component("BONUS", "earning", 3, "manual");
        c.Units(bonus, 2m);
        var other = PayEngine.Calculate(c.Build(), []);
        Assert.Contains(other.Problems, p => p.Key == PayEngine.TableRowMissing);
        Assert.Contains(other.Problems, p => p.Key == PayEngine.InputRateMissing && p.Subject == "BONUS");
        Assert.False(other.Complete);
    }

    [Fact]
    public void FR_HRM_PAY_07_units_inputs_are_paid_at_the_components_rate_and_amount_inputs_add_to_the_component()
    {
        var b = new Snap();
        var basic = b.Component("BASIC", "earning", 1, "fixed", amount: 40_000m);
        var shift = b.Component("SHIFT", "earning", 2, "manual", amount: 1_250m);
        b.Units(shift, 3m);
        b.Amount(basic, 1_000m); // a one-time adjustment of the basic line
        var calc = PayEngine.Calculate(b.Build(), []);
        Assert.True(calc.Complete);
        Assert.Equal(3m * 1_250m, Line(calc, "SHIFT"));
        Assert.Equal(41_000m, Line(calc, "BASIC"));
    }

    // ------------------------------------------------------------------ pro-rata (FR-HRM-PAY-05)

    [Fact]
    public void FR_HRM_PAY_05_a_joiner_is_paid_by_each_rule_from_the_day_they_join()
    {
        var joined = new DateOnly(2027, 1, 16); // 16 days of 31; 15 days missing
        decimal Pay(string rule, int? divisor = null, string? count = null)
        {
            var b = new Snap().Employed(joined, End);
            b.Compensation(joined, null);
            b.Component("BASIC", "earning", 1, "fixed", amount: 50_000m, rule: rule, divisor: divisor, count: count);
            b.NonWorking(Weekends());
            var calc = PayEngine.Calculate(b.Build(), []);
            Assert.True(calc.Complete, string.Join(",", calc.Problems.Select(p => p.Key)));
            return Line(calc, "BASIC");
        }

        var weekends = Weekends();
        var workingInPeriod = 31 - weekends.Count;
        var workingMissing = Enumerable.Range(1, 15).Select(d => new DateOnly(2027, 1, d)).Count(d => !weekends.Contains(d));
        Assert.Equal(Math.Round(50_000m * 16m / 31m, 2, MidpointRounding.AwayFromZero), Pay("calendar_days"));
        Assert.Equal(Math.Round(50_000m * (workingInPeriod - workingMissing) / workingInPeriod, 2, MidpointRounding.AwayFromZero), Pay("working_days"));
        // The fixed divisors pay the days employed (R-175 ruling): 16 days at 50,000 / 30; the working days employed at 50,000 / 26, at most 26.
        Assert.Equal(Math.Round(50_000m * 16m / 30m, 2, MidpointRounding.AwayFromZero), Pay("fixed_30"));
        Assert.Equal(Math.Round(50_000m * Math.Min(26, workingInPeriod - workingMissing) / 26m, 2, MidpointRounding.AwayFromZero), Pay("custom", 26, "working_days"));
        Assert.Equal(50_000m, Pay("none"));
    }

    /// <summary>
    /// The 30-day rule pays the days employed (coordinator's ruling under CR-083 settling R-175, CR-093 S1.35b detail a): amount / 30 x min(30, employed
    /// calendar days), a whole period employed paying the full amount, each unpaid leave day costing amount / 30. Expected values worked out here: a 30,000
    /// salary in January (31 days) and February (28 days).
    /// </summary>
    [Theory]
    [InlineData(2027, 1, 2, 31, 0, 30_000)]          // joined on the 2nd of a 31-day month: 30 days employed, capped at 30
    [InlineData(2027, 1, 31, 31, 0, 1_000)]          // joined on the 31st: one day, 30,000 / 30
    [InlineData(2027, 1, 1, 10, 0, 10_000)]          // left on the 10th: ten days
    [InlineData(2027, 1, 1, 31, 0, 30_000)]          // the whole month
    [InlineData(2027, 1, 1, 31, 2, 28_000)]          // the whole month with two days of unpaid leave
    [InlineData(2027, 2, 1, 28, 0, 30_000)]          // the whole of February
    [InlineData(2027, 2, 15, 28, 0, 14_000)]         // joined on 15 February: 14 days
    [InlineData(2027, 2, 28, 28, 0, 1_000)]          // joined on 28 February: one day
    public void FR_HRM_PAY_05_R_175_the_30_day_rule_pays_the_days_employed_capped_at_30_and_a_whole_month_in_full(int year, int month, int from, int to, int unpaidDays, int expected)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var b = new Snap(start, end).Employed(new DateOnly(year, month, from), new DateOnly(year, month, to));
        b.Compensation(new DateOnly(year, month, from), null);
        b.Component("BASIC", "earning", 1, "fixed", amount: 30_000m, rule: "fixed_30");
        for (var d = 0; d < unpaidDays; d++)
        {
            b.Unpaid(new DateOnly(year, month, 10 + d), 1m);
        }

        var calc = PayEngine.Calculate(b.Build(), []);
        Assert.True(calc.Complete, string.Join(",", calc.Problems.Select(p => p.Key)));
        var employed = to - from + 1;
        var whole = employed == end.Day;
        Assert.Equal(30_000m / 30m * ((whole ? 30 : Math.Min(30, employed)) - unpaidDays), Line(calc, "BASIC"));
        Assert.Equal((decimal)expected, Line(calc, "BASIC"));
    }

    /// <summary>
    /// The calendar-day and working-day rules need no change for the ruling: their divisor is the period's own count of days, so paying the days employed
    /// and deducting the days missing give the same amount (a joiner on the 31st of January is paid 1/31 by the calendar rule).
    /// </summary>
    [Fact]
    public void FR_HRM_PAY_05_R_175_the_calendar_and_working_day_rules_pay_the_days_employed_over_the_periods_own_days()
    {
        var b = new Snap().Employed(new DateOnly(2027, 1, 31), End);
        b.Compensation(new DateOnly(2027, 1, 31), null);
        b.Component("BASIC", "earning", 1, "fixed", amount: 31_000m, rule: "calendar_days");
        Assert.Equal(1_000m, Line(PayEngine.Calculate(b.Build(), []), "BASIC"));

        // A mid-month revision under the 30-day rule over a whole month: each part by its days of the month, the total still exact for one amount.
        var r = new Snap();
        var basic = r.Component("BASIC", "earning", 1, "fixed", amount: 31_000m, rule: "fixed_30");
        r.Revision(new DateOnly(2027, 1, 16), (basic, 31_000m));
        Assert.Equal(31_000m, Line(PayEngine.Calculate(r.Build(), []), "BASIC"));

        // A revision from 30,000 to 31,000 on the 16th: 15 days at the old amount and 16 at the new one, each over the month's 31 days.
        var raised = new Snap();
        var pay = raised.Component("BASIC", "earning", 1, "fixed", amount: 30_000m, rule: "fixed_30");
        raised.Revision(new DateOnly(2027, 1, 16), (pay, 31_000m));
        Assert.Equal(Math.Round((30_000m * 15m / 31m) + (31_000m * 16m / 31m), 2, MidpointRounding.AwayFromZero), Line(PayEngine.Calculate(raised.Build(), []), "BASIC"));
    }

    [Fact]
    public void FR_HRM_PAY_05_a_leaver_unpaid_leave_and_a_mid_month_revision_are_prorated_and_a_whole_month_is_exact()
    {
        // A whole month at every rule is the full amount, to the paisa.
        foreach (var rule in new[] { "calendar_days", "working_days", "fixed_30", "none" })
        {
            var whole = new Snap();
            whole.Component("BASIC", "earning", 1, "fixed", amount: 33_333.33m, rule: rule);
            whole.NonWorking(Weekends());
            Assert.Equal(33_333.33m, Line(PayEngine.Calculate(whole.Build(), []), "BASIC"));
        }

        // A leaver whose last day is the 10th.
        var leaver = new Snap().Employed(Start, new DateOnly(2027, 1, 10));
        leaver.Component("BASIC", "earning", 1, "fixed", amount: 62_000m, rule: "calendar_days");
        Assert.Equal(62_000m * 10m / 31m, Line(PayEngine.Calculate(leaver.Build(), []), "BASIC"));

        // Two days and a half of unpaid leave on the calendar rule.
        var unpaid = new Snap();
        unpaid.Component("BASIC", "earning", 1, "fixed", amount: 31_000m, rule: "calendar_days");
        unpaid.Unpaid(new DateOnly(2027, 1, 5), 1m).Unpaid(new DateOnly(2027, 1, 6), 1m).Unpaid(new DateOnly(2027, 1, 7), 0.5m);
        var unpaidCalc = PayEngine.Calculate(unpaid.Build(), []);
        Assert.Equal(31_000m * (31m - 2.5m) / 31m, Line(unpaidCalc, "BASIC"));
        Assert.Equal(28.5m, unpaidCalc.PaidDays);

        // A revision from the 16th: 40,000 for 15 days, 50,000 for 16 days; a component without pro-rata takes the later amount.
        var revised = new Snap();
        var basic = revised.Component("BASIC", "earning", 1, "fixed", amount: 40_000m, rule: "calendar_days");
        var fixedAllowance = revised.Component("FLAT", "earning", 2, "fixed", amount: 1_000m, rule: "none");
        revised.Revision(new DateOnly(2027, 1, 16), (basic, 50_000m), (fixedAllowance, 2_000m));
        var revisedCalc = PayEngine.Calculate(revised.Build(), []);
        Assert.Equal(Math.Round((40_000m * 15m / 31m) + (50_000m * 16m / 31m), 2, MidpointRounding.AwayFromZero), Line(revisedCalc, "BASIC"));
        Assert.Equal(2_000m, Line(revisedCalc, "FLAT"));
    }

    [Fact]
    public void FR_HRM_PAY_05_review_1_a_per_day_component_is_never_prorated_twice_and_a_mid_month_rate_change_pays_each_day_at_its_own_rate()
    {
        // A joiner on 16 January on 300 a day: 16 paid days, nothing cut again.
        var joiner = new Snap().Employed(new DateOnly(2027, 1, 16), End);
        joiner.Compensation(new DateOnly(2027, 1, 16), null);
        joiner.Component("DAILY", "earning", 1, "per_day", amount: 300m);
        Assert.Equal(300m * 16m, Line(PayEngine.Calculate(joiner.Build(), []), "DAILY"));

        // The rate rises from 300 to 350 on 16 January, with a day of unpaid leave on 20 January: 15 days at 300 and 15 at 350.
        var raised = new Snap();
        var daily = raised.Component("DAILY", "earning", 1, "per_day", amount: 300m);
        raised.Revision(new DateOnly(2027, 1, 16), (daily, 350m));
        raised.Unpaid(new DateOnly(2027, 1, 20), 1m);
        Assert.Equal((15m * 300m) + (15m * 350m), Line(PayEngine.Calculate(raised.Build(), []), "DAILY"));

        // A version that still carries a pro-rata rule (saved before the rule was refused) makes the employee incomplete, never paid twice short.
        var old = new Snap();
        old.Component("DAILY", "earning", 1, "per_day", amount: 300m, rule: "calendar_days");
        var refused = PayEngine.Calculate(old.Build(), []);
        Assert.Contains(refused.Problems, p => p.Key == PayEngine.ProRataNotApplicable && p.Subject == "DAILY");

        // The component service refuses the combination when it is defined.
        Assert.Equal(ComponentRules.ProRataNotApplicable, ComponentRules.ProRataProblem("per_hour", "working_days", null));
        Assert.Equal(ComponentRules.ProRataNotApplicable, ComponentRules.ProRataProblem("attendance_linked", "fixed_30", null));
        Assert.Equal(ComponentRules.ProRataNotApplicable, ComponentRules.ProRataProblem("formula", "calendar_days", "BASIC / $period_days * $paid_days"));
        Assert.Null(ComponentRules.ProRataProblem("formula", "calendar_days", "BASIC * 0.1 + $period_days"));
        Assert.Null(ComponentRules.ProRataProblem("per_day", "none", null));
        Assert.Null(ComponentRules.ProRataProblem("fixed", "calendar_days", null));
        Assert.Equal(PayInputNames.BuiltIn.Except([PayInputNames.PeriodDays, PayInputNames.WorkingDays]).Order(), ComponentRules.CountingInputs.Order());
    }

    [Fact]
    public void FR_HRM_PAY_07_review_3_a_dated_quantity_counts_only_on_the_days_the_employee_is_in_the_runs_pay_group()
    {
        // In this run's pay group from 15 January (moved in from another group): overtime of the 10th belongs to the other group's run.
        var moved = new Snap().Employed(new DateOnly(2027, 1, 15), End);
        moved.Compensation(new DateOnly(2026, 1, 1), null);
        moved.Component("OT", "earning", 1, "formula", formula: "$overtime_hours * 100");
        moved.Dated(PayInputNames.OvertimeHours, 4m, new DateOnly(2027, 1, 10)).Dated(PayInputNames.OvertimeHours, 3m, new DateOnly(2027, 1, 20));
        moved.Dated(PayInputNames.WorkedDays, 1m, new DateOnly(2027, 1, 10)).Dated(PayInputNames.WorkedDays, 1m, new DateOnly(2027, 1, 20));
        var calc = PayEngine.Calculate(moved.Build(), []);
        Assert.Equal(3m * 100m, Line(calc, "OT"));
        Assert.Equal(1m, calc.WorkedDays);
    }

    [Fact]
    public void Review_12_an_amount_too_large_for_decimal_arithmetic_makes_the_employee_incomplete_not_the_batch_fail()
    {
        var b = new Snap();
        var basic = b.Component("BASIC", "earning", 1, "fixed", amount: 79_000_000_000_000_000_000_000_000_000m);
        b.Component("HOUSE", "earning", 2, "percent_of", percent: 900m, bases: [basic]);
        var calc = PayEngine.Calculate(b.Build(), []);
        Assert.False(calc.Complete);
        Assert.Contains(calc.Problems, p => p.Key == FormulaLanguage.Overflow);
    }

    // ------------------------------------------------------------------ rounding (ruling Q3)

    [Fact]
    public void Q3_lines_round_to_the_currencys_minor_unit_by_the_runs_mode()
    {
        decimal Pay(string mode)
        {
            var b = new Snap { Mode = mode };
            b.Component("BASIC", "earning", 1, "fixed", amount: 100.125m);
            return Line(PayEngine.Calculate(b.Build(), []), "BASIC");
        }

        Assert.Equal(100.13m, Pay(PayRounding.HalfAwayFromZero));
        Assert.Equal(100.12m, Pay(PayRounding.HalfEven));
    }

    // ------------------------------------------------------------------ statutory plug-in point (ruling Q2)

    [Fact]
    public void Q2_a_statutory_component_without_its_calculator_makes_the_employee_incomplete_never_zero()
    {
        var b = new Snap();
        b.Component("BASIC", "earning", 1, "fixed", amount: 50_000m, flags: new() { ["test_base"] = true });
        b.Component("LEVY", "deduction", 2, "manual", flags: new() { ["statutory"] = true, ["test_levy"] = true });
        var none = PayEngine.Calculate(b.Build(), []);
        Assert.False(none.Complete);
        Assert.Contains(none.Problems, p => p.Key == PayEngine.StatutoryCalculatorMissing && p.Subject == "LEVY");
        Assert.DoesNotContain(none.Lines, l => l.Code == "LEVY");

        var ambiguous = PayEngine.Calculate(b.Build(), [new TestLevy(), new TestLevy()]);
        Assert.Contains(ambiguous.Problems, p => p.Key == PayEngine.StatutoryCalculatorAmbiguous);

        // A test calculator (not a statutory rule: a made-up two per cent of the lines flagged test_base, from its frozen data).
        var snapshot = b.Build() with { Statutory = new Dictionary<string, JsonElement> { ["test_levy"] = JsonSerializer.SerializeToElement(new { percent = 2m }) } };
        var levied = PayEngine.Calculate(snapshot, [new TestLevy()]);
        Assert.True(levied.Complete, string.Join(",", levied.Problems.Select(p => p.Key)));
        Assert.Equal(50_000m * 2m / 100m, Line(levied, "LEVY"));
        Assert.True(levied.Lines.Single(l => l.Code == "LEVY").Statutory);
        Assert.Equal(50_000m - 1_000m, levied.Net);
        Assert.Equal(123m, levied.Facts["tax_ytd"]);
        Assert.Contains(levied.Trace, t => t.Step == "statutory:test_levy");

        var refused = PayEngine.Calculate(b.Build(), [new TestLevy()]); // no frozen data: the calculator answers a problem
        Assert.Contains(refused.Problems, p => p.Key == TestLevy.NoData);
    }

    [Fact]
    public void Q2_a_formula_using_a_statutory_component_and_a_one_time_input_on_one_are_refused()
    {
        var b = new Snap();
        b.Component("BASIC", "earning", 1, "fixed", amount: 50_000m);
        var levy = b.Component("LEVY", "deduction", 2, "manual", flags: new() { ["statutory"] = true });
        b.Component("AFTER", "earning", 3, "formula", formula: "LEVY * 2");
        b.Amount(levy, 10m);
        var calc = PayEngine.Calculate(b.Build(), [new TestLevy()]);
        Assert.Contains(calc.Problems, p => p.Key == PayEngine.UsesStatutory && p.Subject == "AFTER");
        Assert.Contains(calc.Problems, p => p.Key == PayEngine.InputOnStatutory && p.Subject == "LEVY");
    }

    [Fact]
    public void Q7_a_salary_in_another_currency_without_a_rate_makes_the_employee_incomplete()
    {
        var b = new Snap { Currency = "USD" };
        b.Component("BASIC", "earning", 1, "fixed", amount: 1_000m);
        Assert.Contains(PayEngine.Calculate(b.Build(), []).Problems, p => p.Key == PayEngine.CurrencyRateMissing);
    }

    // ------------------------------------------------------------------ determinism (FR-HRM-PAY-31)

    [Fact]
    public void FR_HRM_PAY_31_the_same_snapshot_gives_the_same_result_and_its_stored_form_and_hash_round_trip()
    {
        var b = new Snap();
        var basic = b.Component("BASIC", "earning", 1, "fixed", amount: 45_678.91m, rule: "working_days");
        b.Component("HOUSE", "earning", 2, "percent_of", percent: 40m, bases: [basic]);
        b.Component("CONV", "earning", 3, "formula", formula: "round(BASIC / $period_days * $paid_days * 0.05, 2)");
        b.NonWorking(Weekends()).Unpaid(new DateOnly(2027, 1, 12), 1m).Named("worked_days", 19m);
        var snapshot = b.Build();
        var canonical = PaySnapshotCodec.Canonical(snapshot);
        var stored = PaySnapshotCodec.Pack(canonical);
        var unpacked = PaySnapshotCodec.Unpack(stored);
        Assert.Equal(canonical, unpacked);
        Assert.Equal(canonical, PaySnapshotCodec.Canonical(PaySnapshotCodec.Read(unpacked)));
        Assert.True(stored.RootElement.GetProperty("data").GetString()!.Length < canonical.Length, "the stored form is compressed");

        var first = PayEngine.Calculate(snapshot, []);
        var again = PayEngine.Calculate(PaySnapshotCodec.Read(unpacked), []);
        Assert.Equal(first.Lines, again.Lines);
        Assert.Equal((first.Gross, first.Net, first.Deductions, first.EmployerCost), (again.Gross, again.Net, again.Deductions, again.EmployerCost));
        Assert.Equal(first.Trace, again.Trace);

        // One input changed: another hash and another result.
        var changed = snapshot with { Inputs = [.. snapshot.Inputs.Where(i => i.Name != PayInputNames.UnpaidLeaveDays)] };
        Assert.NotEqual(PaySnapshotCodec.Hash(canonical), PaySnapshotCodec.Hash(PaySnapshotCodec.Canonical(changed)));
        Assert.NotEqual(first.Net, PayEngine.Calculate(changed, []).Net);
    }

    [Fact]
    public void FR_HRM_PAY_31_every_problem_key_and_input_name_has_an_English_and_an_Urdu_text()
    {
        var folder = Path.Combine(RepositoryRoot.Find(AppContext.BaseDirectory), "src/Modules/Hrm/Erp.Hrm.Contracts/Payroll/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "pay.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "pay.ur.json")))!;
        string[] keys =
        [
            .. PayEngine.ProblemKeys, .. PayInputNames.BuiltIn.Select(n => "hrm.pay.input." + n), .. PayRunPermissions.HandWritten.Select(p => "permissions." + p),
            FormulaLanguage.Syntax, FormulaLanguage.InputUnknown, FormulaLanguage.Arguments, FormulaLanguage.Comparison, FormulaLanguage.RoundPlaces,
            FormulaLanguage.DivisionByZero, FormulaLanguage.Overflow,
        ];
        Assert.All(keys, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
        var used = Directory.GetFiles(Path.Combine(RepositoryRoot.Find(AppContext.BaseDirectory), "src/Modules/Hrm/Erp.Hrm.Infrastructure/Payroll/Engine"), "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.pay\\.[a-z_.]+)\"").Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
    }

    // ------------------------------------------------------------------ helpers

    private static decimal Line(PayCalculation calc, string code) =>
        calc.Lines.SingleOrDefault(l => l.Code == code)?.Amount ?? throw new InvalidOperationException($"no line {code}: {string.Join(",", calc.Problems.Select(p => p.Key + ":" + p.Subject))}");

    private static List<DateOnly> Weekends() =>
        [.. Enumerable.Range(0, 31).Select(i => Start.AddDays(i)).Where(d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)];

    /// <summary>A test-only calculator: a percentage from its frozen data of the lines flagged test_base. Not a statutory rule of any country.</summary>
    private sealed class TestLevy : IStatutoryCalculator
    {
        public const string NoData = "errors.hrm.pay.test.no_data";

        public string Code => "test_levy";

        public bool AppliesTo(StatutoryComponent component) => component.Flags.GetValueOrDefault("statutory");

        public Task<IReadOnlyDictionary<Guid, JsonElement>> FreezeAsync(StatutoryFreezeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, JsonElement>>(new Dictionary<Guid, JsonElement>());

        public StatutoryOutcome Calculate(StatutoryContext context)
        {
            if (context.Frozen is not { } frozen)
            {
                return new StatutoryOutcome(null, NoData, ["no frozen data"]);
            }

            var basis = context.Lines.Where(l => l.Flags.GetValueOrDefault("test_base")).Sum(l => l.Amount);
            var amount = basis * frozen.GetProperty("percent").GetDecimal() / 100m;
            return new StatutoryOutcome(amount, null, [$"{basis} x {frozen.GetProperty("percent").GetDecimal()}%"], new Dictionary<string, decimal> { ["tax_ytd"] = 123m });
        }
    }

    /// <summary>Builds a snapshot by hand: one company, January 2027 in PKR (two places), one employee employed all month on one structure.</summary>
    private sealed class Snap
    {
        private readonly List<SnapshotComponent> _components = [];
        private readonly List<SnapshotStructureRow> _rows = [];
        private readonly List<SnapshotCompensationLine> _lines = [];
        private readonly List<SnapshotTable> _tables = [];
        private readonly List<PayInputItem> _inputs = [];
        private readonly List<DateOnly> _nonWorking = [];
        private readonly List<(DateOnly From, (Guid Component, decimal Amount)[] Lines)> _revisions = [];
        private readonly DateOnly _start;
        private readonly DateOnly _end;
        private DateRange _employed;
        private (DateOnly From, DateOnly? To) _comp = (new DateOnly(2026, 1, 1), null);
        private bool _keyed;

        public Snap()
            : this(Start, End)
        {
        }

        /// <summary>A snapshot of another monthly period (S1.35b: February for the 30-day rule).</summary>
        public Snap(DateOnly start, DateOnly end)
        {
            _start = start;
            _end = end;
            _employed = new DateRange(start, end);
        }

        public string Mode { get; init; } = PayRounding.HalfAwayFromZero;

        public string Currency { get; init; } = "PKR";

        public Snap Employed(DateOnly from, DateOnly to)
        {
            _employed = new DateRange(from, to);
            return this;
        }

        public void Compensation(DateOnly from, DateOnly? to) => _comp = (from, to);

        public Guid Component(string code, string kind, int order, string method, decimal? amount = null, decimal? percent = null, Guid[]? bases = null,
            string? formula = null, string? table = null, string rule = "none", int? divisor = null, string? count = null, Dictionary<string, bool>? flags = null)
        {
            var id = Guid.CreateVersion7();
            _components.Add(new SnapshotComponent(id, code, kind, order,
                [new SnapshotVersion(Guid.CreateVersion7(), new DateOnly(2026, 1, 1), null, method, formula, bases ?? [], table, rule, divisor, count, flags ?? new Dictionary<string, bool>(), "regular")]));
            _rows.Add(new SnapshotStructureRow(Guid.CreateVersion7(), Structure, id, order, null, null, false, "PKR", new DateOnly(2026, 1, 1), null));
            _lines.Add(new SnapshotCompensationLine(id, amount, percent, null));
            return id;
        }

        public void Table(string code, params (decimal? Lower, decimal? Upper, decimal? Fixed, decimal? Rate)[] rows) =>
            _tables.Add(new SnapshotTable(code, Guid.CreateVersion7(), new DateOnly(2026, 1, 1), "slab",
                [.. rows.Select((r, i) => new SnapshotTableRow(i + 1, r.Lower, r.Upper, r.Fixed, r.Rate, _keyed ? "K" : null))]));

        public void KeyRows()
        {
            _keyed = true;
            var t = _tables[^1];
            _tables[^1] = t with { Rows = [.. t.Rows.Select(r => r with { Key = "K" })] };
        }

        public Snap Named(string name, decimal value)
        {
            _inputs.Add(new PayInputItem(Employment, "test", "test", null, name, value));
            return this;
        }

        public Snap Unpaid(DateOnly day, decimal days)
        {
            _inputs.Add(new PayInputItem(Employment, "attendance", "hrm.leave_requests", Guid.CreateVersion7(), PayInputNames.UnpaidLeaveDays, days, day));
            return this;
        }

        public Snap Dated(string name, decimal value, DateOnly day)
        {
            _inputs.Add(new PayInputItem(Employment, "test", "test", Guid.CreateVersion7(), name, value, day));
            return this;
        }

        public void Amount(Guid component, decimal amount) => _inputs.Add(new PayInputItem(Employment, "pay_inputs", "manual", Guid.CreateVersion7(), ComponentId: component, Amount: amount));

        public void Units(Guid component, decimal units) => _inputs.Add(new PayInputItem(Employment, "pay_inputs", "manual", Guid.CreateVersion7(), ComponentId: component, Units: units));

        public Snap NonWorking(IEnumerable<DateOnly> days)
        {
            _nonWorking.AddRange(days);
            return this;
        }

        public void Revision(DateOnly from, params (Guid Component, decimal Amount)[] lines) => _revisions.Add((from, lines));

        public PaySnapshot Build()
        {
            var compensation = new List<SnapshotCompensation>();
            var firstTo = _revisions.Count > 0 ? _revisions[0].From : _comp.To;
            compensation.Add(new SnapshotCompensation(Guid.CreateVersion7(), Structure, _comp.From, firstTo, Currency, [.. _lines]));
            foreach (var (from, lines) in _revisions)
            {
                compensation.Add(new SnapshotCompensation(Guid.CreateVersion7(), Structure, from, null, Currency,
                    [.. _lines.Select(l => lines.FirstOrDefault(x => x.Component == l.ComponentId) is { Component: var c } x && c != Guid.Empty ? l with { Amount = x.Amount } : l)]));
            }

            return new PaySnapshot(
                PayEngine.Version,
                new SnapshotRun(Guid.CreateVersion7(), "regular", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2026, 7, _start, _end, "PKR", 2, Mode),
                new SnapshotEmployment(Employment, "permanent", _employed.From, null, [_employed]),
                [],
                new SnapshotDays(true, [.. _nonWorking.Distinct().Order()]),
                compensation,
                [.. _rows],
                [.. _components],
                [.. _tables],
                [.. _inputs],
                [],
                new Dictionary<string, JsonElement>());
        }
    }
}
