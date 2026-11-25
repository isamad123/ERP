using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Policies;

namespace Erp.Hrm.Application.Leave;

/// <summary>The entitlement models of a leave type (FR-HRM-LVE-02; slice S1.32 Build item 3).</summary>
public static class LeaveModels
{
    public const string FixedYearly = "fixed_yearly";
    public const string MonthlyAccrual = "monthly_accrual";
    public const string PerDaysWorked = "per_days_worked";
    public const string PerHoursWorked = "per_hours_worked";
    public const string TenureTiers = "tenure_tiers";
    public const string GradeTiers = "grade_tiers";
    public const string Manual = "manual";

    public const string Yearly = "yearly";
    public const string Monthly = "monthly";

    /// <summary>Pro-rata rules for a part of a grant period (the year or the month) the employment covers.</summary>
    public const string ProRataNone = "none";
    public const string ProRataDays = "days";
    public const string ProRataMonthsStarted = "months_started";
    public const string ProRataMonthsCompleted = "months_completed";

    public const string RoundNone = "none";
    public const string RoundUp = "up";
    public const string RoundDown = "down";
    public const string RoundNearest = "nearest";
}

/// <summary>One tier of a tenure or grade tiered entitlement: from this many months of service, or for this grade code.</summary>
public sealed record LeaveTier(int? ServiceMonthsFrom, string? Grade, decimal Quantity);

/// <summary>How a leave type is granted or accrued (FR-HRM-LVE-02). Quantities are in the leave type's unit (days, or hours for hourly types).</summary>
public sealed record EntitlementRule(
    string Model, decimal? Quantity, decimal? Per, string? Frequency, IReadOnlyList<LeaveTier> Tiers, string ProRata, string Rounding, decimal? RoundingStep)
{
    /// <summary>Credited once per leave year (a <c>grant</c> entry).</summary>
    public bool IsYearly => Model == LeaveModels.FixedYearly || (Model is LeaveModels.TenureTiers or LeaveModels.GradeTiers && Frequency == LeaveModels.Yearly);

    /// <summary>Credited once per month (an <c>accrual</c> entry).</summary>
    public bool IsMonthly => Model is LeaveModels.MonthlyAccrual or LeaveModels.PerDaysWorked or LeaveModels.PerHoursWorked
        || (Model is LeaveModels.TenureTiers or LeaveModels.GradeTiers && Frequency == LeaveModels.Monthly);

    /// <summary>Accrued from the time worked in a month (counted only once the month's attendance is locked).</summary>
    public bool IsWorked => Model is LeaveModels.PerDaysWorked or LeaveModels.PerHoursWorked;
}

/// <summary>
/// The rules of one leave type in a <c>leave</c> policy (FR-HRM-LVE-02 and -03; slice S1.32 Build item 2, ruling Q1). Every value is the
/// customer's own entry; nothing has a default number (AS-4, R-002, R-145). Carry-forward, encashment, notice, longest leave, eligibility,
/// sandwich and combining rules are read by the request and year-end engines of S1.32b; S1.32a uses the entitlement, the leave year and the
/// negative-balance limit. S1.32b adds whether HR approves after the line manager (<see cref="HrApproval"/>, a field of the approval route),
/// whether cancelling approved leave waits for approval (<see cref="CancellationApproval"/>) and how many days earned compensatory off stays
/// usable (<see cref="CompOffExpiryDays"/>; none: it does not expire). <see cref="HasCarryForward"/> and <see cref="HasEncashment"/> say whether the
/// policy sets carry-forward and encashment at all (without a cap they take the whole balance; not set, nothing is carried or paid out).
/// </summary>
public sealed record LeaveTypeRules(
    string LeaveType,
    int? YearStartMonth,
    EntitlementRule Entitlement,
    decimal? CarryForwardCap,
    int? CarryForwardExpiryMonths,
    decimal? EncashmentCap,
    string? EncashmentRateBasis,
    decimal? NegativeLimit,
    int? MinNoticeDays,
    decimal? MaxConsecutiveDays,
    IReadOnlyList<string>? Genders,
    int? MinServiceMonths,
    bool? DuringProbation,
    string Sandwich,
    IReadOnlyList<string> NotCombinedWith,
    bool HrApproval = false,
    bool CancellationApproval = false,
    int? CompOffExpiryDays = null,
    bool HasCarryForward = false,
    bool HasEncashment = false)
{
    /// <summary>The balance may fall to minus this many units; zero (no limit given) means it may not go below zero.</summary>
    public decimal NegativeAllowance => NegativeLimit ?? 0m;
}

/// <summary>
/// Reads the parameters of a <c>leave</c> policy version (slice S1.32 Build item 2, ruling Q1, CR-086 S1.32a detail): one policy holds the
/// rules of each leave type it covers, keyed by the leave type's code. The schema in <c>policy-types.json</c> checks the shape; this checks
/// what a schema cannot say (the values a model needs, tiers that repeat, values a model does not use), so a policy that cannot be applied
/// is refused when it is saved and, should one be stored anyway, answered as "no policy" for that type with the problem.
/// </summary>
public static class LeavePolicy
{
    public const string PolicyType = "leave";

    public const string NotUsed = "errors.hrm.leave.policy.not_used";
    public const string DuplicateType = "errors.hrm.leave.policy.duplicate_type";
    public const string DuplicateTier = "errors.hrm.leave.policy.duplicate_tier";
    public const string MustBePositive = "errors.hrm.leave.policy.positive";
    public const string SelfCombined = "errors.hrm.leave.policy.self_combined";

    /// <summary>The rules by leave type code (case-insensitive) and the problems found, path to error keys (paths as <see cref="PolicySchema"/>).</summary>
    public static (IReadOnlyDictionary<string, LeaveTypeRules> Rules, IReadOnlyDictionary<string, string[]> Errors) Parse(JsonElement parameters)
    {
        var rules = new Dictionary<string, LeaveTypeRules>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Fail(string path, string key)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            if (!list.Contains(key, StringComparer.Ordinal))
            {
                list.Add(key);
            }
        }

        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
        {
            Fail("parameters.types", PolicySchema.Required);
            return (rules, Done(errors));
        }

        var i = 0;
        foreach (var t in types.EnumerateArray())
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"parameters.types[{i++}]");
            if (t.ValueKind != JsonValueKind.Object)
            {
                Fail(path, PolicySchema.WrongType);
                continue;
            }

            var code = Text(t, "leaveType")?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                Fail(path + ".leaveType", PolicySchema.Required);
                continue;
            }

            var entitlement = Entitlement(t, path + ".entitlement", Fail);
            var notCombined = t.TryGetProperty("notCombinedWith", out var nc) && nc.ValueKind == JsonValueKind.Array
                ? nc.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (notCombined.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                Fail(path + ".notCombinedWith", SelfCombined);
            }

            var carry = t.TryGetProperty("carryForward", out var cf) && cf.ValueKind == JsonValueKind.Object ? cf : default;
            var encash = t.TryGetProperty("encashment", out var en) && en.ValueKind == JsonValueKind.Object ? en : default;
            var eligibility = t.TryGetProperty("eligibility", out var el) && el.ValueKind == JsonValueKind.Object ? el : default;
            var genders = eligibility.ValueKind == JsonValueKind.Object && eligibility.TryGetProperty("genders", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Distinct(StringComparer.Ordinal).ToList()
                : null;
            var typeRules = new LeaveTypeRules(
                code,
                Int(t, "yearStartMonth"),
                entitlement,
                Dec(carry, "cap"),
                Int(carry, "expiryMonths"),
                Dec(encash, "cap"),
                Text(encash, "rateBasis"),
                Dec(t, "negativeLimit"),
                Int(t, "minNoticeDays"),
                Dec(t, "maxConsecutiveDays"),
                genders,
                Int(eligibility, "minServiceMonths"),
                eligibility.ValueKind == JsonValueKind.Object && eligibility.TryGetProperty("duringProbation", out var dp) && dp.ValueKind is JsonValueKind.True or JsonValueKind.False ? dp.GetBoolean() : null,
                Text(t, "sandwich") ?? "off",
                notCombined,
                Bool(t, "hrApproval") ?? false,
                Bool(t, "cancellationApproval") ?? false,
                Int(t, "compOffExpiryDays"),
                carry.ValueKind == JsonValueKind.Object,
                encash.ValueKind == JsonValueKind.Object);
            if (!rules.TryAdd(code, typeRules))
            {
                Fail(path + ".leaveType", DuplicateType);
            }
        }

        return (rules, Done(errors));
    }

    /// <summary>The problems of a policy's parameters beyond its schema (the policy framework's type check for <c>leave</c>).</summary>
    public static IReadOnlyDictionary<string, string[]> Check(JsonElement parameters) => Parse(parameters).Errors;

    private static EntitlementRule Entitlement(JsonElement type, string path, Action<string, string> fail)
    {
        if (!type.TryGetProperty("entitlement", out var e) || e.ValueKind != JsonValueKind.Object)
        {
            fail(path, PolicySchema.Required);
            return new EntitlementRule(LeaveModels.Manual, null, null, null, [], LeaveModels.ProRataNone, LeaveModels.RoundNone, null);
        }

        var model = Text(e, "model") ?? LeaveModels.Manual;
        var quantity = Dec(e, "quantity");
        var per = Dec(e, "per");
        var frequency = Text(e, "frequency");
        var proRata = Text(e, "proRata");
        var rounding = Text(e, "rounding") ?? LeaveModels.RoundNone;
        var step = Dec(e, "roundingStep");
        var tiers = new List<LeaveTier>();
        if (e.TryGetProperty("tiers", out var ts) && ts.ValueKind == JsonValueKind.Array)
        {
            foreach (var tier in ts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                tiers.Add(new LeaveTier(Int(tier, "serviceMonthsFrom"), Text(tier, "grade")?.Trim(), Dec(tier, "quantity") ?? 0m));
            }
        }

        var tiered = model is LeaveModels.TenureTiers or LeaveModels.GradeTiers;
        var worked = model is LeaveModels.PerDaysWorked or LeaveModels.PerHoursWorked;
        var automatic = model != LeaveModels.Manual;

        // What each model needs, and what it does not use (a value that would be ignored is refused, so nobody believes it applies).
        Need(quantity is not null, model is LeaveModels.FixedYearly or LeaveModels.MonthlyAccrual || worked, "quantity");
        Need(per is not null, worked, "per");
        Need(frequency is not null, tiered, "frequency");
        Need(tiers.Count > 0, tiered, "tiers");
        Need(proRata is not null, automatic && !worked, "proRata");
        Need(step is not null, rounding != LeaveModels.RoundNone, "roundingStep");
        if (per is <= 0m)
        {
            fail(path + ".per", MustBePositive);
        }

        if (step is <= 0m)
        {
            fail(path + ".roundingStep", MustBePositive);
        }

        if (!automatic && rounding != LeaveModels.RoundNone)
        {
            fail(path + ".rounding", NotUsed);
        }

        for (var i = 0; i < tiers.Count; i++)
        {
            var tierPath = string.Create(CultureInfo.InvariantCulture, $"{path}.tiers[{i}]");
            if (model == LeaveModels.TenureTiers)
            {
                if (tiers[i].ServiceMonthsFrom is null)
                {
                    fail(tierPath + ".serviceMonthsFrom", PolicySchema.Required);
                }

                if (tiers[i].Grade is not null)
                {
                    fail(tierPath + ".grade", NotUsed);
                }

                if (tiers[i].ServiceMonthsFrom is { } from && tiers.Take(i).Any(x => x.ServiceMonthsFrom == from))
                {
                    fail(tierPath + ".serviceMonthsFrom", DuplicateTier);
                }
            }
            else if (model == LeaveModels.GradeTiers)
            {
                if (string.IsNullOrEmpty(tiers[i].Grade))
                {
                    fail(tierPath + ".grade", PolicySchema.Required);
                }

                if (tiers[i].ServiceMonthsFrom is not null)
                {
                    fail(tierPath + ".serviceMonthsFrom", NotUsed);
                }

                if (tiers[i].Grade is { Length: > 0 } grade && tiers.Take(i).Any(x => string.Equals(x.Grade, grade, StringComparison.OrdinalIgnoreCase)))
                {
                    fail(tierPath + ".grade", DuplicateTier);
                }
            }
        }

        return new EntitlementRule(model, quantity, per, frequency, tiers, proRata ?? LeaveModels.ProRataNone, rounding, step);

        void Need(bool present, bool needed, string name)
        {
            if (needed && !present)
            {
                fail(path + "." + name, PolicySchema.Required);
            }
            else if (!needed && present)
            {
                fail(path + "." + name, NotUsed);
            }
        }
    }

    private static Dictionary<string, string[]> Done(Dictionary<string, List<string>> errors) =>
        errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
}

/// <summary>The policy framework's check of a <c>leave</c> policy beyond its schema (registered with the type catalogue).</summary>
public sealed class LeavePolicyCheck : IPolicyParameterCheck
{
    public string PolicyType => LeavePolicy.PolicyType;

    public IReadOnlyDictionary<string, string[]> Check(JsonElement parameters) => LeavePolicy.Check(parameters);
}
