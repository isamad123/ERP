using System.Text.Json;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Infrastructure.Policies;

namespace Erp.UnitTests.Payroll;

/// <summary>The pure rules of pay components, formulas, pro-rata, structures and bonus definitions (slice S1.34b): FR-HRM-PAY-02, -03, -05, -08.</summary>
public sealed class PayComponentRulesTests
{
    private static readonly HashSet<string> Known = new(["BASIC", "HOUSE", "Medical_1"], StringComparer.Ordinal);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("BASIC * 0.45")]
    [InlineData("round(min(BASIC * 0.5, $cap), 0) + HOUSE")]
    [InlineData("(BASIC + Medical_1) / $days_in_period")]
    [InlineData("max(BASIC, 1000)")]
    public void FR_HRM_PAY_03_a_formula_over_known_components_inputs_and_the_three_functions_is_valid(string formula)
    {
        var check = new FormulaValidator().Check(formula, Known);
        Assert.True(check.IsValid, string.Join(",", check.Problems.Select(p => p.ErrorKey)));
    }

    [Theory]
    [InlineData(null, FormulaValidator.Empty)]
    [InlineData("   ", FormulaValidator.Empty)]
    [InlineData("BASIC * Nothing", FormulaValidator.UnknownName)]
    [InlineData("BASIC ^ 2", FormulaValidator.Token)]
    [InlineData("sqrt(BASIC)", FormulaValidator.Token)]
    [InlineData("min(BASIC, 2", FormulaValidator.Brackets)]
    [InlineData("BASIC) + (HOUSE", FormulaValidator.Brackets)]
    public void FR_HRM_PAY_03_a_formula_with_an_unknown_name_a_bad_token_or_unbalanced_brackets_is_refused(string? formula, string key)
    {
        var check = new FormulaValidator().Check(formula, Known);
        Assert.Contains(check.Problems, p => p.ErrorKey == key);
    }

    [Fact]
    public void FR_HRM_PAY_03_the_names_a_formula_uses_are_reported_and_nothing_is_evaluated()
    {
        var check = new FormulaValidator().Check("BASIC * 2 + $cap + BASIC", Known);
        Assert.Equal(["BASIC"], check.Components);
        Assert.Equal(["$cap"], check.Inputs);
    }

    [Fact]
    public void FR_HRM_PAY_03_a_cycle_is_found_and_named_and_a_chain_is_not_a_cycle()
    {
        var validator = new FormulaValidator();
        IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);
        Assert.Null(validator.FindCycle(new Dictionary<string, IReadOnlySet<string>> { ["A"] = Set("B"), ["B"] = Set("C"), ["C"] = Set() }));
        var loop = validator.FindCycle(new Dictionary<string, IReadOnlySet<string>> { ["A"] = Set("B"), ["B"] = Set("C"), ["C"] = Set("A") });
        Assert.Equal(["A", "B", "C", "A"], loop);
        Assert.Equal(["X", "X"], validator.FindCycle(new Dictionary<string, IReadOnlySet<string>> { ["X"] = Set("X") }));
    }

    [Theory]
    [InlineData("fixed", null, 0, null, null)]
    [InlineData("manual", null, 0, null, null)]
    [InlineData("per_day", null, 0, null, null)]
    [InlineData("attendance_linked", null, 0, null, null)]
    [InlineData("formula", "BASIC * 2", 0, null, null)]
    [InlineData("percent_of", null, 1, null, null)]
    [InlineData("table_lookup", null, 0, "pk.table", null)]
    [InlineData("formula", null, 0, null, "formula")]
    [InlineData("percent_of", null, 0, null, "baseComponentIds")]
    [InlineData("table_lookup", null, 0, null, "statutoryTableCode")]
    [InlineData("fixed", "1", 0, null, "formula")]
    [InlineData("fixed", null, 1, null, "baseComponentIds")]
    [InlineData("formula", "1", 0, "pk.table", "statutoryTableCode")]
    [InlineData("magic", null, 0, null, "method")]
    public void FR_HRM_PAY_03_each_method_needs_what_it_needs_and_takes_nothing_else(string method, string? formula, int bases, string? table, string? failingField)
    {
        var errors = ComponentRules.CheckMethod(method, formula, [.. Enumerable.Range(0, bases).Select(_ => Guid.NewGuid())], table);
        if (failingField is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Contains(failingField, errors.Keys);
        }
    }

    [Theory]
    [InlineData("none", null, null)]
    [InlineData("calendar_days", null, null)]
    [InlineData("working_days", null, null)]
    [InlineData("fixed_30", null, null)]
    [InlineData("custom", """{"divisor":26}""", null)]
    [InlineData("custom", """{"divisor":26,"count":"working_days"}""", null)]
    [InlineData("custom", null, ComponentRules.ProRataParamsInvalid)]
    [InlineData("custom", """{"divisor":0}""", ComponentRules.ProRataParamsInvalid)]
    [InlineData("custom", """{"divisor":367}""", ComponentRules.ProRataParamsInvalid)]
    [InlineData("custom", """{"divisor":26,"count":"lunar_days"}""", ComponentRules.ProRataParamsInvalid)]
    [InlineData("custom", """{"divisor":26,"extra":1}""", ComponentRules.ProRataParamsInvalid)]
    [InlineData("fixed_30", """{"divisor":30}""", ComponentRules.ProRataParamsInvalid)]
    [InlineData("daily", null, ComponentRules.ProRataInvalid)]
    public void FR_HRM_PAY_05_the_pro_rata_rule_and_the_parameters_of_a_custom_rule_are_checked(string rule, string? parameters, string? error)
    {
        var (normalised, problem) = ComponentRules.CheckProRata(rule, parameters is null ? null : J(parameters));
        Assert.Equal(error, problem);
        if (error is null && rule == "custom")
        {
            Assert.Equal(26, normalised!.Value.GetProperty("divisor").GetInt32());
            Assert.True(normalised.Value.TryGetProperty("count", out _)); // the day count is always stored, so a reader never guesses
        }
    }

    [Theory]
    [InlineData("BASIC", true)]
    [InlineData("Medical_1", true)]
    [InlineData("1BASIC", false)]
    [InlineData("BASIC-1", false)]
    [InlineData("BA SIC", false)]
    [InlineData("", false)]
    public void FR_HRM_PAY_02_a_component_code_is_an_identifier_so_a_formula_can_name_it(string code, bool valid) =>
        Assert.Equal(valid, ComponentRules.IsComponentCode(code));

    [Theory]
    [InlineData("taxable", true)]
    [InlineData("eobi_base", true)]
    [InlineData("Taxable", false)]
    [InlineData("1x", false)]
    [InlineData("a b", false)]
    public void FR_HRM_PAY_02_flag_codes_are_lowercase_codes_the_pack_declares_and_no_value_is_coded(string code, bool valid) =>
        Assert.Equal(valid, ComponentRules.IsFlagCode(code));

    [Theory]
    [InlineData("""{"by":"individual"}""", true)]
    [InlineData("""{"by":"employee_type","employeeTypes":["permanent"]}""", true)]
    [InlineData("""{"by":"grade","gradeIds":["0194e1a0-0000-7000-8000-000000000001"]}""", true)]
    [InlineData("""{"by":"grade"}""", false)]
    [InlineData("""{"by":"grade","gradeIds":[]}""", false)]
    [InlineData("""{"by":"grade","gradeIds":["nope"]}""", false)]
    [InlineData("""{"by":"individual","gradeIds":[]}""", false)]
    [InlineData("""{"by":"department"}""", false)]
    [InlineData("""[1]""", false)]
    public void FR_HRM_PAY_04_the_assign_rule_is_by_grade_employee_type_or_individual(string rule, bool valid) =>
        Assert.Equal(valid, ComponentRules.CheckAssignRule(J(rule)).Error is null);

    [Theory]
    [InlineData(1000, null, null, null)]
    [InlineData(null, 45, null, null)]
    [InlineData(null, null, 26, null)]
    [InlineData(null, null, null, ComponentRules.LineNeedsValue)]
    [InlineData(-1, null, null, ComponentRules.LineNegative)]
    [InlineData(null, -5, null, ComponentRules.LineNegative)]
    public void FR_HRM_CMP_03_a_line_needs_an_amount_a_percentage_or_units_and_none_is_negative(int? amount, int? percent, int? units, string? error) =>
        Assert.Equal(error, ComponentRules.CheckLine(amount, percent, units));

    [Fact]
    public void FR_HRM_PAY_02_money_is_kept_exact_to_four_decimals()
    {
        Assert.Equal(100000.5000m, ComponentRules.Money(100000.5m));
        Assert.Equal(0.1234m, ComponentRules.Money(0.12345m)); // half to even at the fourth decimal
        Assert.Equal(0.1236m, ComponentRules.Money(0.12355m));
    }

    [Theory]
    [InlineData("""{"bonusKind":"eid","componentCode":"EID_BONUS","basis":"percent_of_component","percent":100,"basisComponentCode":"BASIC","minServiceMonths":6,"taxTreatment":"irregular","runKind":"off_cycle"}""", null, null)]
    [InlineData("""{"bonusKind":"ad_hoc","componentCode":"BONUS","basis":"fixed_amount","amount":5000.5,"runKind":"regular"}""", null, null)]
    [InlineData("""{"bonusKind":"annual","componentCode":"BONUS","basis":"multiple_of_component","multiple":1.5,"basisComponentCode":"BASIC","runKind":"regular"}""", null, null)]
    [InlineData("""{"bonusKind":"christmas","componentCode":"BONUS","basis":"fixed_amount","runKind":"regular"}""", "parameters.bonusKind", PolicySchema.NotAllowed)]
    [InlineData("""{"bonusKind":"eid","basis":"fixed_amount","runKind":"regular"}""", "parameters.componentCode", PolicySchema.Required)]
    [InlineData("""{"bonusKind":"eid","componentCode":"B","basis":"fixed_amount"}""", "parameters.runKind", PolicySchema.Required)]
    [InlineData("""{"bonusKind":"eid","componentCode":"B","basis":"fixed_amount","amount":-1,"runKind":"regular"}""", "parameters.amount", PolicySchema.BelowMinimum)]
    [InlineData("""{"bonusKind":"eid","componentCode":"B","basis":"fixed_amount","runKind":"weekly"}""", "parameters.runKind", PolicySchema.NotAllowed)]
    public void FR_HRM_PAY_08_a_bonus_is_a_policy_with_its_kind_basis_eligibility_tax_label_and_run_kind(string parameters, string? path, string? key)
    {
        var catalog = new PolicyTypeCatalog([new PolicyTypeSource(PolicyServices.TypeManifest)]);
        var bonus = catalog.Find("bonus")!;
        Assert.True(bonus.IsMany); // an Eid bonus and an annual bonus can both apply
        var errors = PolicySchema.Check(bonus.Schema, J(parameters));
        if (path is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Contains(key!, errors[path]);
        }
    }
}
