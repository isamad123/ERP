using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Policies;

namespace Erp.Hrm.Application.Payroll;

/// <summary>Interest methods of a loan (FR-HRM-LON-01; slice S1.41 Q2): none, flat or reducing balance, the rate an annual percentage the customer enters.</summary>
public static class LoanMethods
{
    public const string None = "none";
    public const string Flat = "flat";
    public const string Reducing = "reducing";

    public const string FixedAmount = "fixed_amount";
    public const string SalaryMultiple = "salary_multiple";
}

/// <summary>
/// The rules of one loan or advance type, read from its <c>loan</c> policy (FR-HRM-LON-01; slice S1.41 Build item 1, Q1). Every number is the customer's own entry:
/// there is no default amount, rate, tenure or share (AS-4, R-181).
/// </summary>
public sealed record LoanPolicyRules(
    int? MinServiceMonths,
    IReadOnlyList<string> EligibleGrades,
    int? MaxOpenLoans,
    string MaxAmountBasis,
    decimal? MaxAmount,
    decimal? SalaryMultiple,
    string InterestMethod,
    decimal AnnualInterestPercent,
    int MaxInstallments,
    bool RequiresGuarantor);

/// <summary>Reads and checks the parameters of the <c>loan</c> policy type.</summary>
public static class LoanPolicy
{
    public const string PolicyType = "loan";
    public const string AmountBasisMissing = "errors.hrm.loan.policy.amount_basis_missing";
    public const string RateMissing = "errors.hrm.loan.policy.rate_missing";
    public const string RateNotAllowed = "errors.hrm.loan.policy.rate_not_allowed";

    /// <summary>The rules of a valid parameter object (the policy service has already checked it against the schema and <see cref="Check"/>).</summary>
    public static LoanPolicyRules Read(JsonElement parameters)
    {
        decimal? Number(string name) => parameters.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
        int? Whole(string name) => parameters.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        var grades = parameters.TryGetProperty("eligibleGrades", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];
        return new LoanPolicyRules(
            Whole("minServiceMonths"), grades, Whole("maxOpenLoans"),
            parameters.GetProperty("maxAmountBasis").GetString()!, Number("maxAmount"), Number("salaryMultiple"),
            parameters.GetProperty("interestMethod").GetString()!, Number("annualInterestPercent") ?? 0m,
            parameters.GetProperty("maxInstallments").GetInt32(),
            parameters.TryGetProperty("requiresGuarantor", out var rg) && rg.ValueKind == JsonValueKind.True);
    }

    /// <summary>What the JSON schema cannot say: the figure the amount basis needs, and a rate only with an interest method.</summary>
    public static IReadOnlyDictionary<string, string[]> Check(JsonElement parameters)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("maxAmountBasis", out var basis) || !parameters.TryGetProperty("interestMethod", out var method))
        {
            return errors; // the schema reports the missing keys
        }

        bool Has(string name) => parameters.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number;
        if (basis.GetString() == LoanMethods.FixedAmount && !Has("maxAmount"))
        {
            errors["maxAmount"] = [AmountBasisMissing];
        }

        if (basis.GetString() == LoanMethods.SalaryMultiple && !Has("salaryMultiple"))
        {
            errors["salaryMultiple"] = [AmountBasisMissing];
        }

        if (method.GetString() != LoanMethods.None && !Has("annualInterestPercent"))
        {
            errors["annualInterestPercent"] = [RateMissing];
        }

        if (method.GetString() == LoanMethods.None && Has("annualInterestPercent"))
        {
            errors["annualInterestPercent"] = [RateNotAllowed];
        }

        return errors;
    }
}

/// <summary>The loan policy type's check beyond its schema, registered next to the others (S1.41).</summary>
public sealed class LoanPolicyCheck : IPolicyParameterCheck
{
    public string PolicyType => LoanPolicy.PolicyType;

    public IReadOnlyDictionary<string, string[]> Check(JsonElement parameters) => LoanPolicy.Check(parameters);
}

/// <summary>One installment of a schedule: principal and interest, each rounded to the currency's minor unit.</summary>
public sealed record LoanInstallmentPlan(int Seq, decimal Principal, decimal Interest)
{
    public decimal Total => Principal + Interest;
}

/// <summary>
/// The installment schedule of a loan (FR-HRM-LON-03; slice S1.41 Build item 3). Money is decimal; every installment is rounded to the currency's minor unit by the
/// payroll rounding mode (S1.35), the last installment absorbs the rounding remainder, and the principals add up to the principal exactly.
/// </summary>
public static class LoanSchedule
{
    /// <summary>How many pay periods a year a pay group's frequency makes.</summary>
    public static int PeriodsPerYear(string frequency) => frequency switch
    {
        "weekly" => 52,
        "bi_weekly" => 26,
        "semi_monthly" => 24,
        _ => 12,
    };

    public static IReadOnlyList<LoanInstallmentPlan> Build(decimal principal, string method, decimal annualPercent, int installments, int periodsPerYear, int decimals, MidpointRounding mode)
    {
        if (principal <= 0m || installments < 1 || periodsPerYear < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(principal), "A loan has a principal above zero and at least one installment.");
        }

        decimal Round(decimal v) => PayRounding.Line(v, decimals, mode);
        var rate = annualPercent / 100m / periodsPerYear;
        var plan = new List<LoanInstallmentPlan>(installments);
        if (method == LoanMethods.Reducing && rate > 0m)
        {
            var growth = 1m;
            for (var i = 0; i < installments; i++)
            {
                growth *= 1m + rate;
            }

            var payment = Round(principal * rate * growth / (growth - 1m));
            var balance = principal;
            for (var i = 1; i <= installments; i++)
            {
                var interest = Round(balance * rate);
                var part = i == installments ? balance : Math.Min(balance, Math.Max(0m, payment - interest));
                plan.Add(new LoanInstallmentPlan(i, part, interest));
                balance -= part;
            }

            return plan;
        }

        var interestTotal = method == LoanMethods.Flat ? Round(principal * (annualPercent / 100m) * installments / periodsPerYear) : 0m;
        var principalEach = Round(principal / installments);
        var interestEach = Round(interestTotal / installments);
        decimal principalLeft = principal, interestLeft = interestTotal;
        for (var i = 1; i <= installments; i++)
        {
            var last = i == installments;
            var p = last ? principalLeft : principalEach;
            var interest = last ? interestLeft : interestEach;
            plan.Add(new LoanInstallmentPlan(i, p, interest));
            principalLeft -= p;
            interestLeft -= interest;
        }

        return plan;
    }
}

/// <summary>What an applicant has to be told, as localization keys with the value that matters (FR-HRM-LON-01, LON-02: an ineligible applicant gets the reason).</summary>
public sealed record LoanProblem(string Key, string? Value = null);

/// <summary>The checks of an application against the loan type's rules (pure; the service reads the facts).</summary>
public static class LoanEligibility
{
    public const string Service = "errors.hrm.loan.not_eligible.service";
    public const string Grade = "errors.hrm.loan.not_eligible.grade";
    public const string OpenLoans = "errors.hrm.loan.not_eligible.open_loans";
    public const string AmountAbove = "errors.hrm.loan.amount_above_max";
    public const string SalaryUnknown = "errors.hrm.loan.salary_unknown";
    public const string InstallmentsAbove = "errors.hrm.loan.installments_above_max";
    public const string GuarantorRequired = "errors.hrm.loan.guarantor_required";
    public const string GuarantorSelf = "errors.hrm.loan.guarantor_self";
    public const string GuarantorNoSignIn = "errors.hrm.loan.guarantor_no_sign_in";

    /// <summary>The largest principal the type allows this employee, or null when it cannot be worked out (no salary recorded for a salary multiple).</summary>
    public static decimal? MaxAmount(LoanPolicyRules rules, decimal? grossPay) =>
        rules.MaxAmountBasis == LoanMethods.FixedAmount ? rules.MaxAmount : grossPay is { } pay && rules.SalaryMultiple is { } multiple ? decimal.Round(pay * multiple, 4, MidpointRounding.AwayFromZero) : null;

    public static IReadOnlyList<LoanProblem> Check(LoanPolicyRules rules, int serviceMonths, string? gradeCode, int openLoans, decimal principal, int installments, decimal? grossPay)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var problems = new List<LoanProblem>();
        if (rules.MinServiceMonths is { } min && serviceMonths < min)
        {
            problems.Add(new LoanProblem(Service, min.ToString(CultureInfo.InvariantCulture)));
        }

        if (rules.EligibleGrades.Count > 0 && (gradeCode is null || !rules.EligibleGrades.Contains(gradeCode, StringComparer.OrdinalIgnoreCase)))
        {
            problems.Add(new LoanProblem(Grade, string.Join(", ", rules.EligibleGrades)));
        }

        if (rules.MaxOpenLoans is { } most && openLoans >= most)
        {
            problems.Add(new LoanProblem(OpenLoans, most.ToString(CultureInfo.InvariantCulture)));
        }

        if (MaxAmount(rules, grossPay) is { } max)
        {
            if (principal > max)
            {
                problems.Add(new LoanProblem(AmountAbove, max.ToString("0.####", CultureInfo.InvariantCulture)));
            }
        }
        else
        {
            problems.Add(new LoanProblem(SalaryUnknown));
        }

        if (installments > rules.MaxInstallments)
        {
            problems.Add(new LoanProblem(InstallmentsAbove, rules.MaxInstallments.ToString(CultureInfo.InvariantCulture)));
        }

        return problems;
    }
}
