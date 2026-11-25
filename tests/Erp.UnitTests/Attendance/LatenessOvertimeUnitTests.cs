using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>
/// Pure rules of slice S1.29b: the late-coming and early-leaving rules of the <c>attendance</c> policy and the monthly count (FR-HRM-ATT-13: the fourth
/// late of a month beyond three allowed is penalised; half days and absences are their own consequence), overtime by day type with the minimum, the time
/// before the shift and the explanation (FR-HRM-ATT-12), caps enforced on approval, comp-off days (FR-HRM-LVE-08), the policy's checks without any
/// default number (AS-4, R-002) and the texts in English and Urdu.
/// </summary>
public sealed class LatenessOvertimeUnitTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly DayPolicyRef Ref = new(Guid.Parse("01900000-0000-7000-8000-000000000001"), "ATT-STD", 3);
    private static readonly DayShift Shift9To6 = new(Guid.Parse("01900000-0000-7000-8000-0000000000d1"), "DAY", "fixed", new(9, 0), new(18, 0), null, null, false, 420);

    private const string BaseJson = """
        {"lateGraceMinutes":10,"earlyGraceMinutes":10,"absentBelowMinutes":240,"breakMode":"fixed","breakMinutes":60,"roundingMode":"none",
         "shiftWindowMinutes":240,"weeklyOffWork":"record","holidayWork":"record"}
        """;

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>The base policy with extra properties (a JSON object body without braces).</summary>
    private static JsonElement With(string extra) => Json(BaseJson.TrimEnd().TrimEnd('}') + "," + extra + "}");

    private static AttendancePolicy Policy(string extra) => AttendancePolicy.Read(With(extra)) ?? throw new InvalidOperationException("test policy invalid");

    private const string Overtime = """
        "overtime":{"working":{"multiplier":1.5,"approval":"post","dailyCapMinutes":120,"monthlyCapMinutes":600,"minimumMinutes":30,"countBeforeShift":false,"compensation":"pay"},
                    "weeklyOff":{"multiplier":2,"approval":"pre","compensation":"comp_off","compOffDayMinutes":480},
                    "holiday":{"multiplier":2.5,"approval":"post","compensation":"comp_off","compOffDayMinutes":480}}
        """;

    private static DateTimeOffset At(string hhmm, int dayOffset = 0) =>
        new(Day.AddDays(dayOffset).ToDateTime(TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture)), TimeSpan.FromHours(5));

    private static DayPunch P(string hhmm, string direction = "in") =>
        new(Guid.Parse($"01900000-0000-7000-8000-{Math.Abs((hhmm + direction).GetHashCode(StringComparison.Ordinal)):D12}"), At(hhmm), direction);

    private static DayInput Input(DayPlan plan, AttendancePolicy policy, params DayPunch[] punches) =>
        new(Day, Karachi, plan, policy, Ref, [], null, DayRules.Counted(punches, new HashSet<Guid>()));

    private static readonly DayPlan Working = new(DayPlan.FromRoster, DayPlan.Working, Shift9To6);
    private static readonly DayPlan WeeklyOff = new(DayPlan.FromRoster, DayPlan.WeeklyOff, null);
    private static readonly DayPlan Holiday = new(DayPlan.FromRoster, DayPlan.Holiday, null);

    // ------------------------------------------------------------------ overtime (FR-HRM-ATT-12)

    public sealed record OvertimeCase(string Name, DayPlan Plan, string[] Punches, int Overtime, string? Step, string Extra = Overtime, bool IgnoreHoliday = false)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<OvertimeCase> OvertimeTable() => new()
    {
        new OvertimeCase("01 no overtime rule: none", Working, ["09:00", "20:00"], 0, null, "\"earlyHalfDayAfterMinutes\":200"),
        new OvertimeCase("02 left on time: none", Working, ["09:00", "18:00"], 0, null),
        new OvertimeCase("03 two hours after the shift", Working, ["09:00", "20:00"], 120, LatenessAndOvertime.StepOvertimeWorking),
        new OvertimeCase("04 twenty minutes after the shift is below the minimum of thirty", Working, ["09:00", "18:20"], 0, LatenessAndOvertime.StepOvertimeBelowMinimum),
        new OvertimeCase("05 an hour early does not count when the rule says so", Working, ["08:00", "18:45"], 45, LatenessAndOvertime.StepOvertimeWorking),
        new OvertimeCase("06 an hour early counts when the rule says so", Working, ["08:00", "18:45"], 105, LatenessAndOvertime.StepOvertimeWorking,
            Overtime.Replace("\"countBeforeShift\":false", "\"countBeforeShift\":true", StringComparison.Ordinal)),
        new OvertimeCase("07 late and stayed late: overtime after the shift still counts", Working, ["09:20", "19:00"], 60, LatenessAndOvertime.StepOvertimeWorking),
        new OvertimeCase("08 a half day has no overtime", Working, ["13:00", "20:00"], 0, null),
        new OvertimeCase("09 a weekly off worked: all the work", WeeklyOff, ["10:00", "15:00"], 240, LatenessAndOvertime.StepOvertimeRest),
        new OvertimeCase("10 a holiday worked: all the work", Holiday, ["10:00", "18:00"], 420, LatenessAndOvertime.StepOvertimeRest),
        new OvertimeCase("11 a holiday whose work the policy ignores: none", Holiday, ["10:00", "18:00"], 0, null, IgnoreHoliday: true),
    };

    [Theory]
    [MemberData(nameof(OvertimeTable))]
    public void FR_HRM_ATT_12_overtime_by_day_type_from_the_policy(OvertimeCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var json = BaseJson.TrimEnd().TrimEnd('}') + "," + c.Extra + "}";
        if (c.IgnoreHoliday)
        {
            json = json.Replace("\"holidayWork\":\"record\"", "\"holidayWork\":\"ignore\"", StringComparison.Ordinal);
        }

        var policy = AttendancePolicy.Read(Json(json)) ?? throw new InvalidOperationException("policy");
        var punches = c.Punches.Select((t, i) => P(t, i == 0 ? "in" : "out")).ToArray();
        var outcome = DayRules.Evaluate(Input(c.Plan, policy, punches));
        Assert.Equal(c.Overtime, outcome.OvertimeMinutes);
        var keys = outcome.Explanation.Steps.Select(s => s.Key).ToList();
        if (c.Step is null)
        {
            Assert.DoesNotContain(keys, k => k.Contains("overtime", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(c.Step, keys);
            Assert.Equal(DayRules.StepPolicy, keys[^1]); // the policy step still closes the explanation
        }

        // The same input twice gives the same result (slice Goal).
        Assert.Equal(outcome.OvertimeMinutes, DayRules.Evaluate(Input(c.Plan, policy, punches)).OvertimeMinutes);
    }

    [Fact]
    public void FR_HRM_ATT_12_caps_are_enforced_on_approval_daily_then_monthly()
    {
        var rule = Policy(Overtime).OvertimeFor(DayPlan.Working)!;
        Assert.Equal(new OvertimeApproval(90, null), LatenessAndOvertime.Approvable(90, null, rule, 0));
        Assert.Equal(new OvertimeApproval(120, LatenessAndOvertime.CappedDaily), LatenessAndOvertime.Approvable(200, null, rule, 0));
        Assert.Equal(new OvertimeApproval(40, LatenessAndOvertime.CappedMonthly), LatenessAndOvertime.Approvable(90, null, rule, 560));
        Assert.Equal(0, LatenessAndOvertime.Approvable(90, null, rule, 600).Minutes); // nothing left: the approval is refused
        // Pre-approval: the minutes asked for, not those worked (none yet).
        Assert.Equal(new OvertimeApproval(60, null), LatenessAndOvertime.Approvable(0, 60, rule, 0));
        // No cap given: the policy has none (statutory limits are the customer's to enter, R-002).
        var open = Policy(Overtime.Replace("\"dailyCapMinutes\":120,\"monthlyCapMinutes\":600,", string.Empty, StringComparison.Ordinal)).OvertimeFor(DayPlan.Working)!;
        Assert.Equal(new OvertimeApproval(500, null), LatenessAndOvertime.Approvable(500, null, open, 10_000));
    }

    [Fact]
    public void FR_HRM_LVE_08_comp_off_days_are_the_approved_minutes_over_the_policys_day()
    {
        var rule = Policy(Overtime).OvertimeFor(DayPlan.Holiday)!;
        Assert.Equal(OvertimeRule.CompOff, rule.Compensation);
        Assert.Equal(1m, LatenessAndOvertime.CompOffDays(480, rule));
        Assert.Equal(0.5m, LatenessAndOvertime.CompOffDays(240, rule));
        Assert.Equal(0.4375m, LatenessAndOvertime.CompOffDays(210, rule));
    }

    // ------------------------------------------------------------------ lateness (FR-HRM-ATT-13)

    private static readonly PenaltyRule LeaveAfterThree = new(3, PenaltyRule.Leave, "CL", 1m);

    private static PenaltyDay D(int day, string result, int late = 0, int early = 0, PenaltyRule? lateRule = null, PenaltyRule? earlyRule = null) =>
        new(new DateOnly(2026, 3, day), Guid.CreateVersion7(), Guid.Empty, Guid.Empty, result, late, early, lateRule ?? LeaveAfterThree, earlyRule);

    [Fact]
    public void FR_HRM_ATT_13_the_fourth_late_of_a_month_beyond_three_allowed_is_penalised_and_each_later_one()
    {
        var days = new[] { D(2, "late", 15), D(3, "present"), D(4, "late", 20), D(5, "late", 12), D(6, "late", 30), D(9, "late", 11) };
        var plan = LatenessAndOvertime.Plan(days.Reverse()); // order of the input does not matter
        Assert.Equal([new DateOnly(2026, 3, 6), new DateOnly(2026, 3, 9)], plan.Select(p => p.Day.Date));
        Assert.Equal([4, 5], plan.Select(p => p.Occurrence));
        Assert.All(plan, p => Assert.Equal((LatenessAndOvertime.Late, 3, PenaltyRule.Leave), (p.Kind, p.Allowed, p.Rule.Action)));
        Assert.Equal(30, plan[0].Minutes);
    }

    [Fact]
    public void FR_HRM_ATT_13_half_days_and_absences_are_their_own_consequence_and_early_leaving_counts_on_its_own()
    {
        var early = new PenaltyRule(0, PenaltyRule.Warning, null, null);
        var days = new[]
        {
            D(2, "half_day", 130), D(3, "absent", 40), D(4, "late", 15, 20, earlyRule: early), D(5, "present", 0, 25, earlyRule: early), D(6, "half_day", 0, 150, earlyRule: early),
        };
        var plan = LatenessAndOvertime.Plan(days);
        Assert.DoesNotContain(plan, p => p.Kind == LatenessAndOvertime.Late); // one late day only, three allowed
        Assert.Equal([(new DateOnly(2026, 3, 4), 1), (new DateOnly(2026, 3, 5), 2)], plan.Where(p => p.Kind == LatenessAndOvertime.Early).Select(p => (p.Day.Date, p.Occurrence)));
    }

    [Fact]
    public void FR_HRM_ATT_13_each_day_keeps_the_rule_of_its_own_policy_version_and_no_rule_means_no_penalty()
    {
        var days = new[] { D(2, "late", 15), D(3, "late", 15), D(4, "late", 15), D(5, "late", 15, lateRule: null) with { Late = null }, D(6, "late", 15, lateRule: new PenaltyRule(3, PenaltyRule.PayDeduction, null, null)) };
        var plan = LatenessAndOvertime.Plan(days);
        var only = Assert.Single(plan);
        Assert.Equal((new DateOnly(2026, 3, 6), 5, PenaltyRule.PayDeduction), (only.Day.Date, only.Occurrence, only.Rule.Action));
        var explained = LatenessAndOvertime.Explain(only);
        Assert.Equal(LatenessAndOvertime.PenaltyPay, explained.Key);
        Assert.Equal(DayRules.ValuePrefix + "late", explained.Params["kind"]);
    }

    [Fact]
    public void FR_HRM_ATT_13_the_month_is_the_calendar_month_of_the_day()
    {
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), LatenessAndOvertime.Month(new DateOnly(2026, 2, 17)));
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), LatenessAndOvertime.Month(new DateOnly(2028, 2, 29)));
    }

    // ------------------------------------------------------------------ the policy (AS-4)

    [Fact]
    public void FR_HRM_ATT_12_13_AS_4_the_rules_are_read_from_the_policy_and_checked_without_defaults()
    {
        var policy = Policy(Overtime + ",\"latePenalty\":{\"allowedPerMonth\":3,\"action\":\"leave\",\"leaveTypeCode\":\"CL\",\"leaveUnits\":0.5},\"earlyPenalty\":{\"allowedPerMonth\":2,\"action\":\"warning\"}");
        Assert.Equal(new PenaltyRule(3, PenaltyRule.Leave, "CL", 0.5m), policy.LatePenalty);
        Assert.Equal(new PenaltyRule(2, PenaltyRule.Warning, null, null), policy.EarlyPenalty);
        Assert.Equal(1.5m, policy.OvertimeFor(DayPlan.Working)!.Multiplier);
        Assert.Equal(OvertimeRule.Pre, policy.OvertimeFor(DayPlan.WeeklyOff)!.Approval);
        Assert.Null(Policy("\"earlyHalfDayAfterMinutes\":200").OvertimeFor(DayPlan.Working)); // no rule, no overtime: nothing is assumed
        Assert.Null(Policy("\"earlyHalfDayAfterMinutes\":200").LatePenalty);

        Assert.Equal([AttendancePolicy.LeaveTypeRequired], AttendancePolicy.Check(With("\"latePenalty\":{\"allowedPerMonth\":3,\"action\":\"leave\",\"leaveUnits\":1}"))["latePenalty.leaveTypeCode"]);
        Assert.Equal([AttendancePolicy.LeaveUnitsRequired], AttendancePolicy.Check(With("\"earlyPenalty\":{\"allowedPerMonth\":3,\"action\":\"leave\",\"leaveTypeCode\":\"CL\"}"))["earlyPenalty.leaveUnits"]);
        Assert.Equal([AttendancePolicy.PenaltyLeaveUnused], AttendancePolicy.Check(With("\"latePenalty\":{\"allowedPerMonth\":3,\"action\":\"warning\",\"leaveUnits\":1}"))["latePenalty.action"]);
        Assert.Equal([AttendancePolicy.CompOffMinutesRequired], AttendancePolicy.Check(With("\"overtime\":{\"holiday\":{\"multiplier\":2,\"approval\":\"post\",\"compensation\":\"comp_off\"}}"))["overtime.holiday.compOffDayMinutes"]);
        Assert.Equal([AttendancePolicy.CompOffUnused], AttendancePolicy.Check(With("\"overtime\":{\"holiday\":{\"multiplier\":2,\"approval\":\"post\",\"compensation\":\"pay\",\"compOffDayMinutes\":480}}"))["overtime.holiday.compensation"]);
        Assert.Equal([AttendancePolicy.MonthlyBelowDaily], AttendancePolicy.Check(With("\"overtime\":{\"working\":{\"multiplier\":1.5,\"approval\":\"post\",\"compensation\":\"pay\",\"countBeforeShift\":false,\"dailyCapMinutes\":120,\"monthlyCapMinutes\":60}}"))["overtime.working.monthlyCapMinutes"]);
        Assert.Equal([AttendancePolicy.CountBeforeShiftRequired], AttendancePolicy.Check(With("\"overtime\":{\"working\":{\"multiplier\":1.5,\"approval\":\"post\",\"compensation\":\"pay\"}}"))["overtime.working.countBeforeShift"]);
        Assert.Empty(AttendancePolicy.Check(With(Overtime)));
        // A policy failing its check is never read (so a broken version never gives zero caps or zero allowances).
        Assert.Null(AttendancePolicy.Read(With("\"overtime\":{\"working\":{\"multiplier\":1.5,\"approval\":\"post\",\"compensation\":\"pay\"}}")));
    }

    [Fact]
    public void AS_4_R_002_no_statutory_working_time_number_or_rate_is_in_the_manifest()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/policy-types.json")));
        var type = manifest.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("code").GetString() == "attendance");
        var schema = type.GetProperty("schema").GetProperty("properties");
        Assert.DoesNotContain("\"default\"", type.GetRawText(), StringComparison.Ordinal);
        foreach (var dayType in new[] { "working", "weeklyOff", "holiday" })
        {
            var rule = schema.GetProperty("overtime").GetProperty("properties").GetProperty(dayType);
            Assert.Equal(["approval", "compensation", "multiplier"], rule.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order(StringComparer.Ordinal));
        }

        Assert.Equal(["action", "allowedPerMonth"], schema.GetProperty("latePenalty").GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Q7_every_lateness_and_overtime_text_exists_in_English_and_Urdu()
    {
        var en = Texts("Attendance/Localization/attendance", "en");
        var ur = Texts("Attendance/Localization/attendance", "ur");
        Assert.All(LatenessAndOvertime.Keys.Concat(["hrm.att.value.late", "hrm.att.value.early"]), k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
        Assert.All(LatenessAndOvertime.Keys, k => Assert.Matches(@"\p{IsArabic}", ur[k]));
        Assert.All(AttendanceConsequencePermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c) && ur.ContainsKey("permissions." + c), c));
        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Attendance", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.attendance\\.(?:overtime|penalty)\\.[a-z_]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));

        var pen = Texts("Policies/Localization/policies", "en");
        var pur = Texts("Policies/Localization/policies", "ur");
        Assert.All(AttendancePolicy.ErrorKeys, k => Assert.True(pen.ContainsKey(k) && pur.ContainsKey(k), k));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/policy-types.json")));
        var titles = System.Text.RegularExpressions.Regex.Matches(manifest.RootElement.GetRawText(), "\"(hrm\\.policy\\.param\\.attendance\\.[a-z_.]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.All(titles, k => Assert.True(pen.ContainsKey(k) && pur.ContainsKey(k), k));
    }

    private static Dictionary<string, string> Texts(string file, string lang) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, $"src/Modules/Hrm/Erp.Hrm.Contracts/{file}.{lang}.json")))!;

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Erp.sln not found");
        }
    }
}
