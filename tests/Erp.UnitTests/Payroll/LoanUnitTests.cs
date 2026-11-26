using Erp.CodeGen;
using System.Text.Json;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.UnitTests.Payroll;

/// <summary>
/// The pure rules of loans and advances (slice S1.41): FR-HRM-LON-01 (the policy's parameters and checks, the eligibility reasons, the most that may be lent), FR-HRM-LON-03 (the
/// installment schedule: each method's worked example, the principals add up to the principal exactly, the last installment absorbs the rounding remainder) and the texts of every
/// key in both languages. No rate or limit is a default: every number in a test is the test's own.
/// </summary>
public sealed class LoanUnitTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<LoanInstallmentPlan> Plan(decimal principal, string method, decimal rate, int n, int perYear = 12, int decimals = 2) =>
        LoanSchedule.Build(principal, method, rate, n, perYear, decimals, MidpointRounding.AwayFromZero);

    [Theory]
    [InlineData(12_000, 12)]
    [InlineData(100_000, 7)]
    [InlineData(10, 3)]
    [InlineData(99_999.99, 11)]
    [InlineData(0.05, 2)]
    public void FR_HRM_LON_03_a_loan_without_interest_sums_to_the_principal_exactly_and_the_last_installment_absorbs_the_remainder(double principal, int n)
    {
        var p = (decimal)principal;
        var plan = Plan(p, LoanMethods.None, 0m, n);
        Assert.Equal(n, plan.Count);
        Assert.Equal(p, plan.Sum(x => x.Principal));
        Assert.All(plan, x => Assert.Equal(0m, x.Interest));
        Assert.All(plan.Take(n - 1), x => Assert.Equal(decimal.Round(p / n, 2, MidpointRounding.AwayFromZero), x.Principal));
        Assert.Equal(Enumerable.Range(1, n), plan.Select(x => x.Seq));
    }

    [Fact]
    public void FR_HRM_LON_03_a_12_installment_reducing_balance_loan_has_the_worked_example_and_sums_to_the_principal_exactly()
    {
        // 100,000 at 12 percent a year, monthly: 1 percent a month, payment 8,884.88 (the standard annuity), interest of the first month 1,000.00.
        var plan = Plan(100_000m, LoanMethods.Reducing, 12m, 12);
        Assert.Equal(12, plan.Count);
        Assert.Equal(100_000m, plan.Sum(x => x.Principal));
        Assert.Equal(1_000m, plan[0].Interest);
        Assert.Equal(7_884.88m, plan[0].Principal);
        Assert.All(plan.Take(11), x => Assert.Equal(8_884.88m, x.Total));
        // The interest follows the balance down, so it never grows.
        Assert.Equal(plan.Select(x => x.Interest).OrderByDescending(x => x), plan.Select(x => x.Interest));
        // The last installment clears what is left: its interest is a month on that balance and the whole total is within a few cents of the others.
        Assert.InRange(plan[^1].Total, 8_884.88m - 0.20m, 8_884.88m + 0.20m);
        Assert.Equal(100_000m, plan.Sum(x => x.Principal));
    }

    [Theory]
    [InlineData(2_500_000, 24, 9.5, 12)]
    [InlineData(123_456.78, 18, 7.25, 24)]
    [InlineData(5_000, 5, 20, 26)]
    [InlineData(999.99, 3, 1, 52)]
    public void FR_HRM_LON_03_reducing_balance_always_sums_to_the_principal_and_never_goes_negative(double principal, int n, double rate, int perYear)
    {
        var plan = Plan((decimal)principal, LoanMethods.Reducing, (decimal)rate, n, perYear);
        Assert.Equal((decimal)principal, plan.Sum(x => x.Principal));
        Assert.All(plan, x => Assert.True(x.Principal >= 0m && x.Interest >= 0m));
    }

    [Fact]
    public void FR_HRM_LON_03_flat_interest_is_the_rate_on_the_whole_principal_for_the_term_spread_evenly_with_the_remainder_on_the_last()
    {
        // 60,000 at 10 percent a year for 12 months: 6,000 interest, 500 a month; principal 5,000 a month.
        var plan = Plan(60_000m, LoanMethods.Flat, 10m, 12);
        Assert.Equal(60_000m, plan.Sum(x => x.Principal));
        Assert.Equal(6_000m, plan.Sum(x => x.Interest));
        Assert.All(plan, x => Assert.Equal(5_500m, x.Total));
        // An amount that does not divide: 10,000 at 9 percent for 7 months: 525 interest, 75 each; principal 1,428.57 and the last 1,428.58.
        var odd = Plan(10_000m, LoanMethods.Flat, 9m, 7);
        Assert.Equal(10_000m, odd.Sum(x => x.Principal));
        Assert.Equal(525m, odd.Sum(x => x.Interest));
        Assert.Equal(1_428.57m, odd[0].Principal);
        Assert.Equal(1_428.58m, odd[^1].Principal);
    }

    [Fact]
    public void FR_HRM_LON_03_weekly_and_semi_monthly_groups_use_their_periods_a_year()
    {
        Assert.Equal(12, LoanSchedule.PeriodsPerYear("monthly"));
        Assert.Equal(24, LoanSchedule.PeriodsPerYear("semi_monthly"));
        Assert.Equal(26, LoanSchedule.PeriodsPerYear("bi_weekly"));
        Assert.Equal(52, LoanSchedule.PeriodsPerYear("weekly"));
        // Flat interest for 26 bi-weekly installments is one year of interest.
        Assert.Equal(1_200m, Plan(10_000m, LoanMethods.Flat, 12m, 26, 26).Sum(x => x.Interest));
    }

    [Fact]
    public void FR_HRM_LON_03_a_whole_unit_currency_rounds_each_installment_to_the_unit()
    {
        var plan = Plan(1_000m, LoanMethods.None, 0m, 3, 12, 0);
        Assert.Equal([333m, 333m, 334m], plan.Select(x => x.Principal));
    }

    [Fact]
    public void FR_HRM_LON_03_the_schedule_refuses_an_empty_loan()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(0m, LoanMethods.None, 0m, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(100m, LoanMethods.None, 0m, 0));
    }

    private static LoanPolicyRules Rules(string json = """{"maxAmountBasis":"salary_multiple","salaryMultiple":3,"interestMethod":"none","maxInstallments":12}""") => LoanPolicy.Read(J(json));

    [Fact]
    public void FR_HRM_LON_01_the_policy_is_read_with_every_number_the_customers_own_and_checked_beyond_its_schema()
    {
        var all = LoanPolicy.Read(J("""{"minServiceMonths":6,"eligibleGrades":["G1","G2"],"maxOpenLoans":2,"maxAmountBasis":"fixed_amount","maxAmount":50000,"interestMethod":"reducing","annualInterestPercent":8.5,"maxInstallments":24,"requiresGuarantor":true}"""));
        Assert.Equal(6, all.MinServiceMonths);
        Assert.Equal(["G1", "G2"], all.EligibleGrades);
        Assert.Equal(2, all.MaxOpenLoans);
        Assert.Equal(50_000m, all.MaxAmount);
        Assert.Equal(8.5m, all.AnnualInterestPercent);
        Assert.True(all.RequiresGuarantor);
        var bare = Rules();
        Assert.Null(bare.MinServiceMonths);
        Assert.Empty(bare.EligibleGrades);
        Assert.False(bare.RequiresGuarantor);
        Assert.Equal(0m, bare.AnnualInterestPercent); // nothing is filled in for the customer

        Assert.Empty(LoanPolicy.Check(J("""{"maxAmountBasis":"fixed_amount","maxAmount":1,"interestMethod":"flat","annualInterestPercent":5,"maxInstallments":3}""")));
        var missing = LoanPolicy.Check(J("""{"maxAmountBasis":"fixed_amount","interestMethod":"flat","maxInstallments":3}"""));
        Assert.Equal(LoanPolicy.AmountBasisMissing, missing["maxAmount"][0]);
        Assert.Equal(LoanPolicy.RateMissing, missing["annualInterestPercent"][0]);
        Assert.Equal(LoanPolicy.AmountBasisMissing, LoanPolicy.Check(J("""{"maxAmountBasis":"salary_multiple","interestMethod":"none","maxInstallments":3}"""))["salaryMultiple"][0]);
        Assert.Equal(LoanPolicy.RateNotAllowed, LoanPolicy.Check(J("""{"maxAmountBasis":"fixed_amount","maxAmount":1,"interestMethod":"none","annualInterestPercent":5,"maxInstallments":3}"""))["annualInterestPercent"][0]);
    }

    [Fact]
    public void FR_HRM_LON_01_02_an_ineligible_applicant_gets_every_reason_and_an_eligible_one_none()
    {
        var rules = LoanPolicy.Read(J("""{"minServiceMonths":12,"eligibleGrades":["G1"],"maxOpenLoans":1,"maxAmountBasis":"salary_multiple","salaryMultiple":3,"interestMethod":"none","maxInstallments":10}"""));
        Assert.Empty(LoanEligibility.Check(rules, 24, "g1", 0, 150_000m, 10, 50_000m)); // grades compare without case; exactly the most is allowed
        var keys = LoanEligibility.Check(rules, 3, "G9", 1, 150_001m, 11, 50_000m).Select(p => p.Key).ToList();
        Assert.Equal([LoanEligibility.Service, LoanEligibility.Grade, LoanEligibility.OpenLoans, LoanEligibility.AmountAbove, LoanEligibility.InstallmentsAbove], keys);
        Assert.Equal("150000", LoanEligibility.Check(rules, 24, "G1", 0, 150_001m, 10, 50_000m).Single().Value);
        Assert.Contains(LoanEligibility.Check(rules, 24, null, 0, 1m, 1, 50_000m), p => p.Key == LoanEligibility.Grade); // no grade recorded is not an allowed grade
        // A multiple of pay needs pay: without it the application says so, never a guess.
        Assert.Equal(LoanEligibility.SalaryUnknown, LoanEligibility.Check(rules, 24, "G1", 0, 1m, 1, null).Single().Key);
        Assert.Null(LoanEligibility.MaxAmount(rules, null));
        Assert.Equal(50_000m, LoanEligibility.MaxAmount(Rules("""{"maxAmountBasis":"fixed_amount","maxAmount":50000,"interestMethod":"none","maxInstallments":5}"""), null));
    }

    [Fact]
    public void FR_HRM_LON_01_02_03_07_the_loan_permissions_events_and_texts_are_declared_and_translated()
    {
        Assert.Equal("hrm.loan.disbursed", LoanEvents.Disbursed);
        Assert.Equal("hrm.loan.installment_recovered", LoanEvents.InstallmentRecovered);
        Assert.Equal("hrm.loan_application", LoanEvents.ApplicationDocumentType);
        var root = Path.Combine(RepositoryRoot.Find(AppContext.BaseDirectory), "src/Modules/Hrm");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "Erp.Hrm.Contracts/Payroll/Localization/loans.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "Erp.Hrm.Contracts/Payroll/Localization/loans.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(LoanPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        // Every loan key the code answers with has a text, and every policy parameter has a title in both languages.
        var used = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("Generated", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.loan\\.[a-z_.]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
        foreach (var language in new[] { "en", "ur" })
        {
            var policies = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, $"Erp.Hrm.Contracts/Policies/Localization/policies.{language}.json")))!;
            var types = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Erp.Hrm.Infrastructure/Access/policy-types.json"))).RootElement.GetProperty("types").EnumerateArray();
            var loan = types.Single(t => t.GetProperty("code").GetString() == "loan");
            Assert.All(loan.GetProperty("schema").GetProperty("properties").EnumerateObject(), p => Assert.True(policies.ContainsKey(p.Value.GetProperty("title").GetString()!), p.Name));
        }
    }
}
