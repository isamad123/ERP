using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Contracts.Expenses;

namespace Erp.Hrm.Application.Expenses;

/// <summary>One rule of the <c>expense</c> policy (slice S1.42, FR-HRM-EXP-01): a category, optionally one grade, and a limit per line or a rate per unit (per-diem day, mileage kilometre).</summary>
public sealed record ExpenseRule(string CategoryCode, string? GradeCode, string Basis, decimal Amount)
{
    public const string PerLine = "per_line";

    public const string PerUnit = "per_unit";
}

/// <summary>What the pure rules need to know about one line (all amounts in the claim currency).</summary>
public sealed record ExpenseLineFacts(bool RequiresReceipt, bool HasReceipt, bool Duplicate, DateOnly ExpenseDate, decimal Amount, decimal? Quantity, ExpenseRule? Rule);

/// <summary>
/// The rules of expense claims that need no database (slice S1.42; FR-HRM-EXP-01, EXP-02, EXP-03): reading the policy, the policy flags of a line, the
/// checks of a line's input and the conversion of a foreign amount. Every answer is a localization key. No limit, rate or window is invented here: with
/// no rule the line is not over any limit and with no window it is never out of period (AS-4).
/// </summary>
public static class ExpenseRules
{
    public const string Required = "errors.data.required";
    public const string AmountPositive = "errors.hrm.expense.amount_positive";
    public const string CurrencyInvalid = "errors.hrm.expense.currency_invalid";
    public const string QuantityRequired = "errors.hrm.expense.quantity_required";
    public const string QuantityPositive = "errors.hrm.expense.quantity_positive";
    public const string TextTooLong = "errors.hrm.expense.text_too_long";
    public const string ReimburseViaInvalid = "errors.hrm.expense.reimburse_via_invalid";
    public const string TooManyLines = "errors.hrm.expense.too_many_lines";
    public const string DateRequired = "errors.hrm.expense.date_required";

    public const int MostLines = 100;
    public const int CodeLength = 40;
    public const int DescriptionLength = 500;

    /// <summary>The rules of a policy version's parameters (the JSON schema has been checked when the policy was saved); a rule that cannot be read is skipped.</summary>
    public static IReadOnlyList<ExpenseRule> Rules(JsonElement parameters)
    {
        var list = new List<ExpenseRule>();
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in rules.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("categoryCode", out var c) && c.ValueKind == JsonValueKind.String
                && r.TryGetProperty("basis", out var b) && b.ValueKind == JsonValueKind.String && r.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var amount))
            {
                var grade = r.TryGetProperty("gradeCode", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                list.Add(new ExpenseRule(c.GetString()!, string.IsNullOrWhiteSpace(grade) ? null : grade, b.GetString()!, amount));
            }
        }

        return list;
    }

    /// <summary>The rule that applies to a category and a grade: the one for that grade, else the one for any grade; null when the policy says nothing (no limit).</summary>
    public static ExpenseRule? RuleFor(IReadOnlyList<ExpenseRule> rules, string categoryCode, string? gradeCode)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var ofCategory = rules.Where(r => string.Equals(r.CategoryCode, categoryCode, StringComparison.OrdinalIgnoreCase)).ToList();
        return (gradeCode is null ? null : ofCategory.FirstOrDefault(r => string.Equals(r.GradeCode, gradeCode, StringComparison.OrdinalIgnoreCase))) ?? ofCategory.FirstOrDefault(r => r.GradeCode is null);
    }

    /// <summary>
    /// The policy flags of a line, in a fixed order (FR-HRM-EXP-03). Over the limit: more than the line limit, or more than the rate times the quantity (a
    /// per-unit rule with no quantity cannot be met). Missing receipt: the category asks for one and the line has none. Out of policy: dated after the
    /// day the claim is made, or older than the window (a window of 0 means none).
    /// </summary>
    public static IReadOnlyList<string> Flags(ExpenseLineFacts line, DateOnly today, int windowDays)
    {
        ArgumentNullException.ThrowIfNull(line);
        var flags = new List<string>(4);
        if (line.Rule is { } rule)
        {
            var limit = rule.Basis == ExpenseRule.PerUnit ? (line.Quantity is { } q ? rule.Amount * q : (decimal?)null) : rule.Amount;
            if (limit is null || line.Amount > limit)
            {
                flags.Add(ExpenseFlags.OverLimit);
            }
        }

        if (line.Duplicate)
        {
            flags.Add(ExpenseFlags.Duplicate);
        }

        if (line.RequiresReceipt && !line.HasReceipt)
        {
            flags.Add(ExpenseFlags.MissingReceipt);
        }

        if (line.ExpenseDate > today || (windowDays > 0 && today.DayNumber - line.ExpenseDate.DayNumber > windowDays))
        {
            flags.Add(ExpenseFlags.OutOfPolicy);
        }

        return flags;
    }

    /// <summary>The checks of one line's input that need no database; keys are per field of <c>lines[n]</c>.</summary>
    public static void CheckLine(int index, ExpenseLineInput line, string? categoryKind, IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(errors);
        var at = $"lines[{index.ToString(CultureInfo.InvariantCulture)}].";
        if (line.CategoryId is null)
        {
            errors[at + "categoryId"] = [Required];
        }

        if (line.ExpenseDate is null)
        {
            errors[at + "expenseDate"] = [DateRequired];
        }

        if (line.Amount is not { } amount)
        {
            errors[at + "amount"] = [Required];
        }
        else if (amount <= 0)
        {
            errors[at + "amount"] = [AmountPositive];
        }

        if (line.CurrencyCode is not { Length: 3 } code || code.Any(ch => ch is < 'A' or > 'Z'))
        {
            errors[at + "currencyCode"] = [line.CurrencyCode is null ? Required : CurrencyInvalid];
        }

        if (categoryKind is "per_diem" or "mileage" && line.Quantity is null)
        {
            errors[at + "quantity"] = [QuantityRequired];
        }
        else if (line.Quantity is <= 0)
        {
            errors[at + "quantity"] = [QuantityPositive];
        }

        if ((line.Description?.Length ?? 0) > DescriptionLength)
        {
            errors[at + "description"] = [TextTooLong];
        }

        if ((line.Project?.Length ?? 0) > CodeLength)
        {
            errors[at + "project"] = [TextTooLong];
        }

        if ((line.CostCentre?.Length ?? 0) > CodeLength)
        {
            errors[at + "costCentre"] = [TextTooLong];
        }
    }

    /// <summary>A foreign amount in the claim currency: rounded to the currency's minor unit, half away from zero (the same convention as payroll's default).</summary>
    public static decimal Convert(decimal amount, decimal rate, int decimals) =>
        decimal.Round(amount * rate, Math.Clamp(decimals, 0, 4), MidpointRounding.AwayFromZero);

    /// <summary>The finance step is added when the total reaches the threshold; a threshold of 0 means no finance step (Q5).</summary>
    public static bool NeedsFinance(decimal total, decimal threshold) => threshold > 0 && total >= threshold;

    /// <summary>The amount an approver may accept for a line: from 0 to the claimed amount; less than claimed needs a reason.</summary>
    public static string? CheckDecision(decimal claimed, decimal? approved, string? reason)
    {
        if (approved is null)
        {
            return Required;
        }

        if (approved < 0 || approved > claimed)
        {
            return "errors.hrm.expense.approved_out_of_range";
        }

        return approved < claimed && string.IsNullOrWhiteSpace(reason) ? "errors.hrm.expense.reason_required" : null;
    }
}
