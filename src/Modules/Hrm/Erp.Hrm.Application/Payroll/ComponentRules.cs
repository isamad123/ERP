using System.Text.Json;
using System.Text.RegularExpressions;

namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// The pure rules of pay components, structures and compensation (slice S1.34b; FR-HRM-PAY-02 to -06): which method needs what,
/// the pro-rata rule and its parameters, flag codes, assign rules and line shapes. No statutory value appears here (AS-4); the
/// flag codes, table codes and tax-treatment labels are the pack's or the customer's.
/// </summary>
public static partial class ComponentRules
{
    public const string CodeInvalid = "errors.hrm.pay.component_code";
    public const string KindInvalid = "errors.hrm.pay.component_kind";
    public const string MethodInvalid = "errors.hrm.pay.method";
    public const string MethodNeedsFormula = "errors.hrm.pay.method_needs_formula";
    public const string MethodNeedsBases = "errors.hrm.pay.method_needs_bases";
    public const string MethodNeedsTable = "errors.hrm.pay.method_needs_table";
    public const string MethodNoExtras = "errors.hrm.pay.method_no_extras";
    public const string ProRataInvalid = "errors.hrm.pay.pro_rata";
    public const string ProRataParamsInvalid = "errors.hrm.pay.pro_rata_params";
    public const string FlagCodeInvalid = "errors.hrm.pay.flag_code";
    public const string CodeLabelInvalid = "errors.hrm.pay.code_label";
    public const string AssignRuleInvalid = "errors.hrm.pay.assign_rule";
    public const string LineNeedsValue = "errors.hrm.pay.line_needs_value";
    public const string LineNegative = "errors.hrm.pay.line_negative";

    public static readonly string[] Kinds = ["earning", "deduction", "employer_contribution", "reimbursement", "informational"];

    public static readonly string[] Methods = ["fixed", "percent_of", "formula", "table_lookup", "per_day", "per_hour", "attendance_linked", "manual"];

    public static readonly string[] ProRataRules = ["none", "calendar_days", "working_days", "fixed_30", "custom"];

    public static readonly string[] DayCounts = ["calendar_days", "working_days"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex ComponentCodePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FlagCodePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,39}$")]
    private static partial Regex LabelPattern();

    /// <summary>A component code is an identifier, so a formula can name it.</summary>
    public static bool IsComponentCode(string? code) => code is not null && ComponentCodePattern().IsMatch(code);

    public static bool IsFlagCode(string? code) => code is not null && FlagCodePattern().IsMatch(code);

    /// <summary>A pack or customer code (a statutory table, a tax-treatment label): letters, digits, dot, dash, underscore.</summary>
    public static bool IsLabel(string? code) => code is not null && LabelPattern().IsMatch(code);

    /// <summary>
    /// What a method needs (FR-HRM-PAY-03): a formula for <c>formula</c>, at least one base component for <c>percent_of</c>, a
    /// statutory table code for <c>table_lookup</c>; the others take none of them. Returns field to error key.
    /// </summary>
    public static Dictionary<string, string> CheckMethod(string? method, string? formula, IReadOnlyCollection<Guid>? bases, string? tableCode)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (method is null || !Methods.Contains(method, StringComparer.Ordinal))
        {
            errors["method"] = MethodInvalid;
            return errors;
        }

        var hasFormula = !string.IsNullOrWhiteSpace(formula);
        var hasBases = bases is { Count: > 0 };
        var hasTable = !string.IsNullOrWhiteSpace(tableCode);
        switch (method)
        {
            case "formula":
                if (!hasFormula)
                {
                    errors["formula"] = MethodNeedsFormula;
                }

                break;
            case "percent_of":
                if (!hasBases)
                {
                    errors["baseComponentIds"] = MethodNeedsBases;
                }

                break;
            case "table_lookup":
                if (!hasTable)
                {
                    errors["statutoryTableCode"] = MethodNeedsTable;
                }

                break;
        }

        if (method != "formula" && hasFormula)
        {
            errors["formula"] = MethodNoExtras;
        }

        if (method != "percent_of" && hasBases)
        {
            errors["baseComponentIds"] = MethodNoExtras;
        }

        if (method != "table_lookup" && hasTable)
        {
            errors["statutoryTableCode"] = MethodNoExtras;
        }

        return errors;
    }

    /// <summary>
    /// The pro-rata rule and its parameters (FR-HRM-PAY-05): <c>custom</c> needs <c>{"divisor": 1..366}</c> and may name which days
    /// count (<c>"count": "calendar_days"|"working_days"</c>); the other rules take no parameters. Returns the normalised parameters.
    /// </summary>
    public static (JsonElement? Params, string? Error) CheckProRata(string? rule, JsonElement? parameters)
    {
        if (rule is null || !ProRataRules.Contains(rule, StringComparer.Ordinal))
        {
            return (null, ProRataInvalid);
        }

        var given = parameters is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } p ? p : (JsonElement?)null;
        if (rule != "custom")
        {
            return given is null ? (null, null) : (null, ProRataParamsInvalid);
        }

        if (given is not { ValueKind: JsonValueKind.Object } obj)
        {
            return (null, ProRataParamsInvalid);
        }

        var allowed = new[] { "divisor", "count" };
        if (obj.EnumerateObject().Any(x => !allowed.Contains(x.Name, StringComparer.Ordinal)))
        {
            return (null, ProRataParamsInvalid);
        }

        if (!obj.TryGetProperty("divisor", out var divisor) || divisor.ValueKind != JsonValueKind.Number || !divisor.TryGetInt32(out var d) || d is < 1 or > 366)
        {
            return (null, ProRataParamsInvalid);
        }

        var count = "calendar_days";
        if (obj.TryGetProperty("count", out var c))
        {
            if (c.ValueKind != JsonValueKind.String || !DayCounts.Contains(c.GetString(), StringComparer.Ordinal))
            {
                return (null, ProRataParamsInvalid);
            }

            count = c.GetString()!;
        }

        return (JsonSerializer.SerializeToElement(new { divisor = d, count }), null);
    }

    /// <summary>
    /// The default assignment rule of a structure (FR-HRM-PAY-04): <c>by</c> is <c>grade</c> (with <c>gradeIds</c>),
    /// <c>employee_type</c> (with <c>employeeTypes</c>) or <c>individual</c> (nothing else). Returns the grade ids and employee
    /// types it names so the service can check they exist.
    /// </summary>
    public static (IReadOnlyList<Guid> Grades, IReadOnlyList<string> Types, string? Error) CheckAssignRule(JsonElement? rule)
    {
        if (rule is not { ValueKind: JsonValueKind.Object } obj)
        {
            return ([], [], AssignRuleInvalid);
        }

        var by = obj.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var names = obj.EnumerateObject().Select(x => x.Name).ToList();
        switch (by)
        {
            case "individual" when names.Count == 1:
                return ([], [], null);
            case "grade" when names.All(n => n is "by" or "gradeIds") && obj.TryGetProperty("gradeIds", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0:
                var ids = new List<Guid>();
                foreach (var item in g.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
                    {
                        return ([], [], AssignRuleInvalid);
                    }

                    ids.Add(id);
                }

                return (ids.Distinct().ToList(), [], null);
            case "employee_type" when names.All(n => n is "by" or "employeeTypes") && obj.TryGetProperty("employeeTypes", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0:
                var types = t.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList();
                return types.Any(x => string.IsNullOrWhiteSpace(x)) ? ([], [], AssignRuleInvalid) : ([], types.Select(x => x!).Distinct(StringComparer.Ordinal).ToList(), null);
            default:
                return ([], [], AssignRuleInvalid);
        }
    }

    /// <summary>A compensation line needs an amount, a percentage or units, none negative; amounts are kept to four decimals (money is exact).</summary>
    public static string? CheckLine(decimal? amount, decimal? percent, decimal? units)
    {
        if (amount is null && percent is null && units is null)
        {
            return LineNeedsValue;
        }

        return amount is < 0 || percent is < 0 || units is < 0 ? LineNegative : null;
    }

    public static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven);
}
