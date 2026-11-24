#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Text.Json;
using Erp.CodeGen;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Infrastructure.Policies;

namespace Erp.UnitTests.Leave;

/// <summary>
/// The pure parts of the leave engine (slice S1.32a): the <c>leave</c> policy type and its checks (FR-HRM-LVE-02, -03; Build item 2), leave
/// years (ruling Q3), the entitlement arithmetic (FR-HRM-LVE-02: pro-rata, tiers, rounding, worked time; ruling Q7), the run-once source ids
/// (Build item 3), and the texts and permissions.
/// </summary>
public sealed class LeaveUnitTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static PolicyTypeCatalog Catalog() => new([new PolicyTypeSource(PolicyServices.TypeManifest)], [new LeavePolicyCheck()]);

    private static EmploymentSpan Span(string joining, string? last = null, string? service = null) =>
        new(DateOnly.Parse(joining, System.Globalization.CultureInfo.InvariantCulture), last is null ? null : DateOnly.Parse(last, System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(service ?? joining, System.Globalization.CultureInfo.InvariantCulture));

    // ------------------------------------------------------------------ policy type (FR-HRM-LVE-02, -03)

    [Fact]
    public void FR_HRM_LVE_03_the_leave_policy_type_is_registered_with_rules_per_leave_type_and_no_default_numbers()
    {
        var type = Catalog().Find("leave");
        Assert.NotNull(type);
        Assert.Equal(PolicyType.One, type.Cardinality);
        var schema = type.Schema.GetRawText();
        Assert.DoesNotContain("\"default\"", schema, StringComparison.Ordinal); // AS-4, R-002: no number is filled in
        var item = type.Schema.GetProperty("properties").GetProperty("types").GetProperty("items").GetProperty("properties");
        foreach (var rule in new[] { "entitlement", "carryForward", "encashment", "negativeLimit", "minNoticeDays", "maxConsecutiveDays", "eligibility", "sandwich", "notCombinedWith", "yearStartMonth" })
        {
            Assert.True(item.TryGetProperty(rule, out _), rule);
        }

        Assert.Equal(["fixed_yearly", "monthly_accrual", "per_days_worked", "per_hours_worked", "tenure_tiers", "grade_tiers", "manual"],
            item.GetProperty("entitlement").GetProperty("properties").GetProperty("model").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["off", "holidays", "weekly_off", "both"], item.GetProperty("sandwich").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void FR_HRM_LVE_02_every_entitlement_model_reads_and_a_complete_policy_has_no_problems()
    {
        var parameters = Json("""
            {"types": [
              {"leaveType": "ANNUAL", "entitlement": {"model": "fixed_yearly", "quantity": 12, "proRata": "days", "rounding": "nearest", "roundingStep": 0.5},
               "carryForward": {"cap": 5, "expiryMonths": 3}, "encashment": {"cap": 2, "rateBasis": "BASIC"}, "negativeLimit": 2, "minNoticeDays": 3, "maxConsecutiveDays": 10,
               "eligibility": {"genders": ["female"], "minServiceMonths": 6, "duringProbation": false}, "sandwich": "both", "notCombinedWith": ["CASUAL"], "yearStartMonth": 7},
              {"leaveType": "CASUAL", "entitlement": {"model": "monthly_accrual", "quantity": 0.5, "proRata": "months_started"}},
              {"leaveType": "WORKED", "entitlement": {"model": "per_days_worked", "quantity": 1, "per": 20}},
              {"leaveType": "HOURS", "entitlement": {"model": "per_hours_worked", "quantity": 1, "per": 40}},
              {"leaveType": "TENURE", "entitlement": {"model": "tenure_tiers", "frequency": "yearly", "proRata": "none", "tiers": [{"serviceMonthsFrom": 0, "quantity": 10}, {"serviceMonthsFrom": 60, "quantity": 15}]}},
              {"leaveType": "GRADE", "entitlement": {"model": "grade_tiers", "frequency": "monthly", "proRata": "months_completed", "tiers": [{"grade": "G1", "quantity": 1}, {"grade": "G2", "quantity": 2}]}},
              {"leaveType": "SPECIAL", "entitlement": {"model": "manual"}}
            ]}
            """);
        Assert.Empty(Catalog().Check(Catalog().Find("leave")!, parameters));
        var (rules, errors) = LeavePolicy.Parse(parameters);
        Assert.Empty(errors);
        Assert.Equal(7, rules.Count);
        var annual = rules["annual"]; // codes match whatever their case
        Assert.True(annual.Entitlement.IsYearly);
        Assert.Equal(2m, annual.NegativeAllowance);
        Assert.Equal(7, annual.YearStartMonth);
        Assert.Equal(["female"], annual.Genders);
        Assert.Equal("both", annual.Sandwich);
        Assert.True(rules["CASUAL"].Entitlement.IsMonthly);
        Assert.True(rules["WORKED"].Entitlement.IsWorked);
        Assert.True(rules["TENURE"].Entitlement.IsYearly);
        Assert.True(rules["GRADE"].Entitlement.IsMonthly);
        Assert.False(rules["SPECIAL"].Entitlement.IsYearly || rules["SPECIAL"].Entitlement.IsMonthly);
        Assert.Equal(0m, rules["SPECIAL"].NegativeAllowance); // no limit given: not below zero
    }

    [Theory]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "fixed_yearly", "proRata": "none"}}]}""", "parameters.types[0].entitlement.quantity", PolicySchema.Required)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "fixed_yearly", "quantity": 10}}]}""", "parameters.types[0].entitlement.proRata", PolicySchema.Required)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "manual", "quantity": 10}}]}""", "parameters.types[0].entitlement.quantity", LeavePolicy.NotUsed)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "per_days_worked", "quantity": 1, "per": 0}}]}""", "parameters.types[0].entitlement.per", LeavePolicy.MustBePositive)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "per_days_worked", "quantity": 1, "per": 5, "proRata": "days"}}]}""", "parameters.types[0].entitlement.proRata", LeavePolicy.NotUsed)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "tenure_tiers", "proRata": "none", "tiers": [{"serviceMonthsFrom": 0, "quantity": 1}]}}]}""", "parameters.types[0].entitlement.frequency", PolicySchema.Required)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "tenure_tiers", "frequency": "yearly", "proRata": "none", "tiers": [{"serviceMonthsFrom": 0, "quantity": 1}, {"serviceMonthsFrom": 0, "quantity": 2}]}}]}""", "parameters.types[0].entitlement.tiers[1].serviceMonthsFrom", LeavePolicy.DuplicateTier)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "grade_tiers", "frequency": "yearly", "proRata": "none", "tiers": [{"quantity": 1}]}}]}""", "parameters.types[0].entitlement.tiers[0].grade", PolicySchema.Required)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "monthly_accrual", "quantity": 1, "proRata": "none", "rounding": "up"}}]}""", "parameters.types[0].entitlement.roundingStep", PolicySchema.Required)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "manual"}}, {"leaveType": "a", "entitlement": {"model": "manual"}}]}""", "parameters.types[1].leaveType", LeavePolicy.DuplicateType)]
    [InlineData("""{"types": [{"leaveType": "A", "entitlement": {"model": "manual"}, "notCombinedWith": ["A"]}]}""", "parameters.types[0].notCombinedWith", LeavePolicy.SelfCombined)]
    public void FR_HRM_LVE_02_a_policy_that_cannot_be_applied_is_refused_with_the_place_and_the_reason(string parameters, string path, string key)
    {
        var errors = Catalog().Check(Catalog().Find("leave")!, Json(parameters));
        Assert.True(errors.TryGetValue(path, out var keys), string.Join("; ", errors.Select(e => e.Key + "=" + string.Join(",", e.Value))));
        Assert.Contains(key, keys);
    }

    [Fact]
    public void FR_HRM_LVE_02_the_schema_refuses_a_shape_before_the_leave_check_runs()
    {
        var errors = Catalog().Check(Catalog().Find("leave")!, Json("""{"types": [{"leaveType": "A", "entitlement": {"model": "weekly"}}]}"""));
        Assert.Contains(PolicySchema.NotAllowed, errors["parameters.types[0].entitlement.model"]);
        Assert.Contains(PolicySchema.Required, Catalog().Check(Catalog().Find("leave")!, Json("{}"))["parameters.types"]);
    }

    // ------------------------------------------------------------------ leave years (ruling Q3)

    [Fact]
    public void FR_HRM_LVE_02_a_leave_year_starts_in_the_company_month_and_is_numbered_by_the_year_it_starts_in()
    {
        Assert.Equal(new LeaveYearSpan(2026, new(2026, 1, 1), new(2026, 12, 31)), LeaveYears.Of(new(2026, 3, 15), 1));
        Assert.Equal(new LeaveYearSpan(2025, new(2025, 7, 1), new(2026, 6, 30)), LeaveYears.Of(new(2026, 3, 15), 7));
        Assert.Equal(new LeaveYearSpan(2026, new(2026, 7, 1), new(2027, 6, 30)), LeaveYears.Of(new(2026, 7, 1), 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeaveYears.For(2026, 13));
    }

    // ------------------------------------------------------------------ entitlement arithmetic (FR-HRM-LVE-02, ruling Q7)

    [Fact]
    public void FR_HRM_LVE_02_pro_rata_by_days_months_started_and_months_completed_for_a_mid_year_joiner_and_leaver()
    {
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 12, 31);
        var joiner = Span("2026-07-16");
        Assert.Equal(1m, EntitlementRules.Fraction("none", start, end, joiner));
        Assert.Equal(169m / 365m, EntitlementRules.Fraction("days", start, end, joiner));
        Assert.Equal(6m / 12m, EntitlementRules.Fraction("months_started", start, end, joiner));
        Assert.Equal(5m / 12m, EntitlementRules.Fraction("months_completed", start, end, joiner));
        var leaver = Span("2020-01-01", "2026-03-31");
        Assert.Equal(3m / 12m, EntitlementRules.Fraction("months_completed", start, end, leaver));
        Assert.Equal(0m, EntitlementRules.Fraction("none", start, end, Span("2027-01-01")));
        // A yearly 12 with days pro-rata, rounded to half days: 12 × 169/365 = 5.556… → 5.5
        var rule = new EntitlementRule("fixed_yearly", 12m, null, null, [], "days", "nearest", 0.5m);
        Assert.Equal(5.5m, EntitlementRules.Earned(rule, 12m, start, end, joiner));
        Assert.Equal(5.5562m, EntitlementRules.Earned(rule with { Rounding = "none", RoundingStep = null }, 12m, start, end, joiner)); // four places, no float
    }

    [Theory]
    [InlineData("up", 0.5, 2.1, 2.5)]
    [InlineData("down", 0.5, 2.4, 2.0)]
    [InlineData("nearest", 0.5, 2.25, 2.5)]
    [InlineData("nearest", 1.0, 2.5, 3.0)]
    [InlineData("none", null, 1.23456, 1.2346)]
    public void FR_HRM_LVE_02_rounding_follows_the_policy_step(string mode, double? step, double value, double expected) =>
        Assert.Equal((decimal)expected, EntitlementRules.Round((decimal)value, mode, step is null ? null : (decimal)step));

    [Fact]
    public void FR_HRM_LVE_02_tenure_and_grade_tiers_take_the_band_of_the_service_or_the_grade()
    {
        var tenure = new EntitlementRule("tenure_tiers", null, null, "yearly", [new(0, null, 10m), new(60, null, 15m), new(120, null, 20m)], "none", "none", null);
        Assert.Equal(10m, EntitlementRules.TierQuantity(tenure, 59, null));
        Assert.Equal(15m, EntitlementRules.TierQuantity(tenure, 60, null));
        Assert.Equal(20m, EntitlementRules.TierQuantity(tenure, 200, null));
        var late = tenure with { Tiers = [new(12, null, 10m)] };
        Assert.Null(EntitlementRules.TierQuantity(late, 11, null)); // below every band: nothing
        var grade = new EntitlementRule("grade_tiers", null, null, "yearly", [new(null, "G1", 12m), new(null, "G2", 18m)], "none", "none", null);
        Assert.Equal(18m, EntitlementRules.TierQuantity(grade, 0, "g2"));
        Assert.Null(EntitlementRules.TierQuantity(grade, 0, "G3"));
        Assert.Null(EntitlementRules.TierQuantity(grade, 0, null));
        Assert.Equal(59, EntitlementRules.ServiceMonths(new(2021, 3, 15), new(2026, 3, 14)));
        Assert.Equal(60, EntitlementRules.ServiceMonths(new(2021, 3, 15), new(2026, 3, 15)));
        Assert.Equal(0, EntitlementRules.ServiceMonths(new(2026, 3, 15), new(2026, 1, 1)));
    }

    [Fact]
    public void FR_HRM_LVE_02_accrual_by_time_worked_gives_the_quantity_for_every_block_worked()
    {
        var rule = new EntitlementRule("per_days_worked", 1m, 20m, null, [], "none", "down", 0.5m);
        Assert.Equal(1m, EntitlementRules.EarnedByWork(rule, 22m)); // 1.1 rounded down to half days
        Assert.Equal(0m, EntitlementRules.EarnedByWork(rule, 9m));
        var hours = new EntitlementRule("per_hours_worked", 1m, 40m, null, [], "none", "none", null);
        Assert.Equal(4.25m, EntitlementRules.EarnedByWork(hours, 170m));
    }

    [Fact]
    public void FR_HRM_LVE_10_an_automatic_posting_has_one_source_id_per_employment_type_and_period()
    {
        var e = Guid.CreateVersion7();
        var t = Guid.CreateVersion7();
        Assert.Equal(EntitlementRules.SourceId(e, t, "M2026-03"), EntitlementRules.SourceId(e, t, EntitlementRules.MonthKey(new(2026, 3, 1))));
        Assert.NotEqual(EntitlementRules.SourceId(e, t, "M2026-03"), EntitlementRules.SourceId(e, t, "M2026-04"));
        Assert.NotEqual(EntitlementRules.SourceId(e, t, "Y2026"), EntitlementRules.SourceId(e, Guid.CreateVersion7(), "Y2026"));
        Assert.Equal("Y2026/leaver", EntitlementRules.LeaverKey(2026));
    }

    // ------------------------------------------------------------------ texts and permissions

    [Fact]
    public void FR_HRM_LVE_01_the_leave_engine_declares_its_permissions_and_every_key_it_answers_with_has_an_English_and_an_Urdu_text()
    {
        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Leave/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "leave.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "leave.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(LeavePermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));
        var declared = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json"))).RootElement
            .GetProperty("permissions").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!, p => p.GetProperty("sensitive").GetBoolean());
        Assert.All(LeavePermissions.HandWritten, c => Assert.True(declared.ContainsKey(c), c));
        Assert.True(declared[LeavePermissions.LedgerPost]); // changes what an employee may take and payroll pays

        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Leave", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"((?:errors\\.)?hrm\\.leave\\.[a-z_.]+[a-z])\"").Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
        foreach (var kind in new[] { "missing", "differs", "unexpected", "below_allowed" })
        {
            Assert.True(en.ContainsKey("hrm.leave.recompute." + kind), kind);
        }

        // The parameter titles of the leave policy type are with the other policy texts (PolicyUnitTests checks every title of the manifest).
        var policies = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Policies/Localization/policies.en.json")))!;
        Assert.True(policies.ContainsKey("hrm.policy.type.leave"));
    }

    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);
}
