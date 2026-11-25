using System.Text.Json;
using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Contracts.Expenses;

namespace Erp.UnitTests.Expenses;

/// <summary>
/// The rules of expense claims that need no database (slice S1.42): FR-HRM-EXP-01 (limits by grade, per-diem and mileage rates read from the policy; no number
/// is invented), FR-HRM-EXP-02 (conversion at the customer's rate, checks of a line), FR-HRM-EXP-03 (each policy flag has a failing example), the finance
/// step by threshold (Q5) and the approver's decision on a line (FR-XM-32 is proved against the engine in the integration tests).
/// </summary>
public sealed class ExpenseUnitTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static JsonElement Policy(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ExpenseLineFacts Line(ExpenseRule? rule = null, bool requiresReceipt = false, bool hasReceipt = true, bool duplicate = false, decimal amount = 100m, decimal? quantity = null, DateOnly? date = null) =>
        new(requiresReceipt, hasReceipt, duplicate, date ?? Today, amount, quantity, rule);

    [Fact]
    public void FR_HRM_EXP_01_the_rules_of_the_policy_are_read_and_the_grade_rule_wins_over_the_any_grade_rule()
    {
        var rules = ExpenseRules.Rules(Policy("""
            {"rules":[
              {"categoryCode":"TRAVEL","basis":"per_line","amount":500},
              {"categoryCode":"TRAVEL","gradeCode":"G5","basis":"per_line","amount":900},
              {"categoryCode":"PERDIEM","gradeCode":"G5","basis":"per_unit","amount":45.5},
              {"categoryCode":"BAD","basis":"per_line"}]}
            """));
        Assert.Equal(3, rules.Count); // the rule without an amount is skipped
        Assert.Equal(900m, ExpenseRules.RuleFor(rules, "travel", "g5")!.Amount); // codes compare without case
        Assert.Equal(500m, ExpenseRules.RuleFor(rules, "TRAVEL", "G1")!.Amount);
        Assert.Equal(500m, ExpenseRules.RuleFor(rules, "TRAVEL", null)!.Amount);
        Assert.Null(ExpenseRules.RuleFor(rules, "PERDIEM", "G1")); // a per-diem rate written for one grade does not apply to another
        Assert.Null(ExpenseRules.RuleFor(rules, "MEALS", "G5")); // no rule: no limit, never a default
        Assert.Empty(ExpenseRules.Rules(Policy("{}")));
    }

    [Fact]
    public void FR_HRM_EXP_03_over_limit_is_flagged_by_a_line_limit_or_by_the_rate_times_the_quantity()
    {
        var perLine = new ExpenseRule("TRAVEL", null, ExpenseRule.PerLine, 500m);
        Assert.Empty(ExpenseRules.Flags(Line(perLine, amount: 500m), Today, 0)); // exactly the limit is within it
        Assert.Equal([ExpenseFlags.OverLimit], ExpenseRules.Flags(Line(perLine, amount: 500.01m), Today, 0));
        var perDay = new ExpenseRule("PERDIEM", "G5", ExpenseRule.PerUnit, 45m);
        Assert.Empty(ExpenseRules.Flags(Line(perDay, amount: 135m, quantity: 3m), Today, 0));
        Assert.Equal([ExpenseFlags.OverLimit], ExpenseRules.Flags(Line(perDay, amount: 136m, quantity: 3m), Today, 0));
        Assert.Equal([ExpenseFlags.OverLimit], ExpenseRules.Flags(Line(perDay, amount: 10m, quantity: null), Today, 0)); // a rate cannot be met without a quantity
        Assert.Empty(ExpenseRules.Flags(Line(null, amount: 9_999_999m), Today, 0)); // no policy rule, no limit
    }

    [Fact]
    public void FR_HRM_EXP_03_a_duplicate_a_missing_receipt_and_a_date_outside_the_window_each_raise_their_own_flag_in_a_fixed_order()
    {
        Assert.Equal([ExpenseFlags.Duplicate], ExpenseRules.Flags(Line(duplicate: true), Today, 0));
        Assert.Equal([ExpenseFlags.MissingReceipt], ExpenseRules.Flags(Line(requiresReceipt: true, hasReceipt: false), Today, 0));
        Assert.Empty(ExpenseRules.Flags(Line(requiresReceipt: false, hasReceipt: false), Today, 0));
        Assert.Empty(ExpenseRules.Flags(Line(date: Today.AddDays(-400)), Today, 0)); // window 0 means none
        Assert.Empty(ExpenseRules.Flags(Line(date: Today.AddDays(-30)), Today, 30)); // exactly the window is inside it
        Assert.Equal([ExpenseFlags.OutOfPolicy], ExpenseRules.Flags(Line(date: Today.AddDays(-31)), Today, 30));
        Assert.Equal([ExpenseFlags.OutOfPolicy], ExpenseRules.Flags(Line(date: Today.AddDays(1)), Today, 0)); // dated in the future
        var all = ExpenseRules.Flags(Line(new ExpenseRule("T", null, ExpenseRule.PerLine, 1m), requiresReceipt: true, hasReceipt: false, duplicate: true, date: Today.AddDays(-99)), Today, 10);
        Assert.Equal([ExpenseFlags.OverLimit, ExpenseFlags.Duplicate, ExpenseFlags.MissingReceipt, ExpenseFlags.OutOfPolicy], all);
    }

    [Fact]
    public void FR_HRM_EXP_02_a_foreign_amount_is_converted_at_the_rate_and_rounded_to_the_currency_minor_unit_half_away_from_zero()
    {
        Assert.Equal(27_950.00m, ExpenseRules.Convert(100m, 279.5m, 2));
        Assert.Equal(2.35m, ExpenseRules.Convert(1m, 2.345m, 2)); // half away from zero
        Assert.Equal(2m, ExpenseRules.Convert(1m, 1.5m, 0));
        Assert.Equal(100m, ExpenseRules.Convert(100m, 1m, 2));
    }

    [Fact]
    public void FR_HRM_EXP_02_the_checks_of_a_line_name_every_missing_or_wrong_field_and_a_per_diem_or_mileage_line_needs_a_quantity()
    {
        var errors = new Dictionary<string, string[]>();
        ExpenseRules.CheckLine(0, new ExpenseLineInput(null, null, null, null, null, null, null, null, null, null), null, errors);
        Assert.Equal(["lines[0].amount", "lines[0].categoryId", "lines[0].currencyCode", "lines[0].expenseDate"], errors.Keys.Order(StringComparer.Ordinal));

        errors.Clear();
        var ok = new ExpenseLineInput(null, Guid.NewGuid(), Today, 10m, "PKR", null, null, null, "P1", "CC1");
        ExpenseRules.CheckLine(1, ok, "general", errors);
        Assert.Empty(errors);
        ExpenseRules.CheckLine(2, ok, "mileage", errors);
        Assert.Equal([ExpenseRules.QuantityRequired], errors["lines[2].quantity"]);
        errors.Clear();
        ExpenseRules.CheckLine(3, ok with { Amount = 0m, CurrencyCode = "pk", Quantity = 0m, Project = new string('x', 41) }, "per_diem", errors);
        Assert.Equal([ExpenseRules.AmountPositive], errors["lines[3].amount"]);
        Assert.Equal([ExpenseRules.CurrencyInvalid], errors["lines[3].currencyCode"]);
        Assert.Equal([ExpenseRules.QuantityPositive], errors["lines[3].quantity"]);
        Assert.Equal([ExpenseRules.TextTooLong], errors["lines[3].project"]);
    }

    [Fact]
    public void FR_HRM_EXP_03_the_finance_step_is_added_only_when_the_total_reaches_a_set_threshold()
    {
        Assert.False(ExpenseRules.NeedsFinance(1_000_000m, 0m)); // 0 means no finance step
        Assert.False(ExpenseRules.NeedsFinance(999.99m, 1000m));
        Assert.True(ExpenseRules.NeedsFinance(1000m, 1000m));
    }

    [Fact]
    public void FR_HRM_EXP_03_an_approver_may_accept_from_nothing_to_the_claimed_amount_and_less_than_claimed_needs_a_reason()
    {
        Assert.Null(ExpenseRules.CheckDecision(100m, 100m, null));
        Assert.Null(ExpenseRules.CheckDecision(100m, 60m, "too much for a taxi"));
        Assert.Null(ExpenseRules.CheckDecision(100m, 0m, "not a business expense"));
        Assert.Equal("errors.hrm.expense.reason_required", ExpenseRules.CheckDecision(100m, 60m, "  "));
        Assert.Equal("errors.hrm.expense.approved_out_of_range", ExpenseRules.CheckDecision(100m, 100.01m, "x"));
        Assert.Equal("errors.hrm.expense.approved_out_of_range", ExpenseRules.CheckDecision(100m, -1m, "x"));
        Assert.Equal(ExpenseRules.Required, ExpenseRules.CheckDecision(100m, null, null));
    }

    [Fact]
    public void FR_HRM_EXP_01_the_hand_written_permissions_are_in_the_manifest_and_every_text_of_the_slice_exists_in_both_languages()
    {
        var root = RepoRoot();
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var codes = manifest.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToList();
        Assert.All(ExpensePermissions.HandWritten, c => Assert.Contains(c, codes));
        var folder = Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Contracts/Expenses/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "expenses.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "expenses.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ExpensePermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c)));
        Assert.All(ExpenseFlags.All, f => Assert.True(en.ContainsKey("hrm.expense.flag." + f)));
        Assert.All(en.Values.Concat(ur.Values), v => Assert.False(string.IsNullOrWhiteSpace(v)));
        // Every error key the slice's code can answer has a text (the keys are constants of the rules and of the services).
        var keys = Directory.EnumerateFiles(Path.Combine(root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Expense", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.expense\\.[a-z_]+)\"").Select(m => m.Groups[1].Value))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(en.ContainsKey(k), k));
    }

    [Fact]
    public void FR_HRM_EXP_01_the_expense_policy_type_is_registered_with_the_rules_schema_and_no_number_filled_in()
    {
        var root = RepoRoot();
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/policy-types.json")));
        var type = doc.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("code").GetString() == "expense");
        var item = type.GetProperty("schema").GetProperty("properties").GetProperty("rules").GetProperty("items");
        Assert.Equal(["categoryCode", "basis", "amount"], item.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("default", type.GetRawText(), StringComparison.Ordinal); // no figure is shipped (AS-4, Q2)
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Erp.sln not found");
    }
}
