using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>
/// Pure rules of the attendance engine (slice S1.29a): the <c>attendance</c> policy's parameters and checks (FR-HRM-ATT-09), the 30-scenario
/// table of day results and explanations (FR-HRM-ATT-10, acceptance), repeatability, the overnight ownership of punches (ruling Q3), pairing and
/// breaks (ruling Q4), rounding (ruling Q6) and the texts of every explanation key in English and Urdu (ruling Q7).
/// </summary>
public sealed class AttendanceEngineUnitTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly DayPolicyRef Ref = new(Guid.Parse("01900000-0000-7000-8000-000000000001"), "ATT-STD", 3);
    private static readonly DayShift DayShift9To6 = new(Guid.Parse("01900000-0000-7000-8000-0000000000d1"), "DAY", "fixed", new(9, 0), new(18, 0), null, null, false, 420);
    private static readonly DayShift Night = new(Guid.Parse("01900000-0000-7000-8000-0000000000e1"), "NIGHT", "night", new(22, 0), new(6, 0), null, null, true, 390);
    private static readonly DayShift Flexible = new(Guid.Parse("01900000-0000-7000-8000-0000000000f1"), "FLEX", "flexible", new(8, 0), new(19, 0), new(10, 0), new(16, 0), false, 420);

    /// <summary>The customer's numbers of the test policy (none of them is a product default).</summary>
    private const string PolicyJson = """
        {"lateGraceMinutes":10,"earlyGraceMinutes":10,"lateHalfDayAfterMinutes":120,"earlyHalfDayAfterMinutes":120,"absentBelowMinutes":240,
         "breakMode":"fixed","breakMinutes":60,"roundingMode":"none","shiftWindowMinutes":120,"weeklyOffWork":"record","holidayWork":"record"}
        """;

    private static AttendancePolicy Policy(Action<Dictionary<string, object?>>? change = null)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(PolicyJson)!;
        change?.Invoke(values);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value)));
        return AttendancePolicy.Read(doc.RootElement) ?? throw new InvalidOperationException("test policy invalid");
    }

    private static DateTimeOffset At(string hhmm, int dayOffset = 0, int seconds = 0)
    {
        var t = TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(Day.AddDays(dayOffset).ToDateTime(t).AddSeconds(seconds), TimeSpan.FromHours(5));
    }

    private static DayPunch P(string hhmm, string direction = "in", int dayOffset = 0, int seconds = 0, bool duplicate = false, int n = 0) =>
        new(Guid.Parse($"01900000-0000-7000-8000-{(n == 0 ? Math.Abs((hhmm + direction + dayOffset + seconds).GetHashCode(StringComparison.Ordinal)) : n):D12}"), At(hhmm, dayOffset, seconds), direction, duplicate);

    private static DayPlan Working(DayShift shift) => new(DayPlan.FromRoster, DayPlan.Working, shift);

    private static DayInput Input(DayPlan plan, IEnumerable<DayPunch> punches, AttendancePolicy? policy = null, IReadOnlyList<DayLeave>? leave = null, DayDuty? duty = null) =>
        new(Day, Karachi, plan, policy ?? Policy(), Ref, leave ?? [], duty, DayRules.Counted(punches, new HashSet<Guid>()));

    /// <summary>One scenario of the acceptance table: what goes in, the result, the rule that decided and the minutes.</summary>
    public sealed record Scenario(string Name, DayInput Input, string Result, string Rule, int Worked, int Late, int Early, string[] Exceptions)
    {
        public override string ToString() => Name;
    }

    private static readonly Guid LeaveRequest = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid DutyRequest = Guid.Parse("01900000-0000-7000-8000-0000000000b1");

    public static TheoryData<Scenario> Table()
    {
        var day = Working(DayShift9To6);
        var night = Working(Night);
        Scenario S(string name, DayInput input, string result, string rule, int worked = 0, int late = 0, int early = 0, params string[] exceptions) =>
            new(name, input, result, rule, worked, late, early, exceptions);
        return new TheoryData<Scenario>
        {
            S("01 on time", Input(day, [P("09:00"), P("18:00", "out")]), "present", "present", 480),
            S("02 grace edge: 10 minutes late is within grace", Input(day, [P("09:10"), P("18:00", "out")]), "present", "present", 470),
            S("03 grace edge: 10 minutes 59 seconds counts as 10", Input(day, [P("09:10", seconds: 59), P("18:00", "out")]), "present", "present", 469),
            S("04 one minute past grace is late", Input(day, [P("09:11"), P("18:00", "out")]), "late", "late", 469, 11),
            S("05 late 22 minutes (the slice's example)", Input(day, [P("09:22"), P("18:00", "out")]), "late", "late", 458, 22),
            S("06 late two hours makes a half day", Input(day, [P("11:00"), P("18:00", "out")]), "half_day", "half_day_late", 360, 120),
            S("07 early within grace", Input(day, [P("09:00"), P("17:52", "out")]), "present", "present", 472),
            S("08 left 15 minutes early", Input(day, [P("09:00"), P("17:45", "out")]), "present", "present_left_early", 465, 0, 15),
            S("09 left 130 minutes early makes a half day", Input(day, [P("09:00"), P("15:50", "out")]), "half_day", "half_day_early", 350, 0, 130),
            S("10 fewer minutes than a full day", Input(day, [P("09:00"), P("16:50", "out")]), "half_day", "half_day_worked", 410, 0, 70),
            S("11 fewer minutes than the absence limit", Input(day, [P("09:00"), P("12:00", "out")]), "absent", "absent_worked", 120, 0, 360),
            S("12 no punch on a working day", Input(day, []), "absent", "absent_no_punch"),
            S("13 missing out punch", Input(day, [P("09:05")]), "incomplete", "missing_out", 0, 0, 0, "missing_out"),
            S("14 missing in punch", Input(day, [P("18:00", "out")]), "incomplete", "missing_in", 0, 0, 0, "missing_in"),
            S("15 duplicate punches are not counted", Input(day, [P("09:00"), P("09:00", seconds: 30, duplicate: true), P("18:00", "out")]), "present", "present", 480),
            S("16 odd count of punches", Input(day, [P("09:00"), P("13:00", "out"), P("18:00", "out")]), "present", "present", 480, 0, 0, "odd_count"),
            S("17 breaks by punches", Input(day, [P("09:00"), P("13:00", "out"), P("14:00"), P("18:00", "out")], Policy(v => { v["breakMode"] = "by_punches"; v["breakMinutes"] = null; })), "present", "present", 480),
            S("18 worked minutes rounded to the nearest 15", Input(day, [P("09:00"), P("18:07", "out")], Policy(v => { v["roundingMode"] = "nearest"; v["roundingStepMinutes"] = 15; })), "present", "present", 480),
            S("19 holiday without punches", Input(new DayPlan(DayPlan.FromRoster, DayPlan.Holiday, null), []), "holiday", "holiday"),
            S("20 holiday worked is recorded", Input(new DayPlan(DayPlan.FromRoster, DayPlan.Holiday, null), [P("10:00"), P("14:00", "out")]), "holiday", "holiday", 180),
            S("21 holiday work ignored by the policy", Input(new DayPlan(DayPlan.FromRoster, DayPlan.Holiday, null), [P("10:00"), P("14:00", "out")], Policy(v => v["holidayWork"] = "ignore")), "holiday", "holiday"),
            S("22 weekly off on the roster", Input(new DayPlan(DayPlan.FromRoster, DayPlan.WeeklyOff, null), []), "weekly_off", "weekly_off"),
            S("23 weekly off from the shift policy's pattern", Input(new DayPlan(DayPlan.FromPattern, DayPlan.WeeklyOff, null), []), "weekly_off", "weekly_off"),
            S("24 approved leave of the whole day", Input(day, [P("09:00"), P("18:00", "out")], leave: [new DayLeave(LeaveRequest, 1m, DayLeave.Full)]), "on_leave", "on_leave"),
            S("25 half-day leave in the morning, worked the afternoon", Input(day, [P("13:00"), P("18:00", "out")], leave: [new DayLeave(LeaveRequest, 0.5m, DayLeave.FirstHalf)]), "present", "present", 240),
            S("26 approved duty", Input(day, [], duty: new DayDuty(DutyRequest, "official_visit")), "on_duty", "on_duty"),
            S("27 night shift on time, out the next morning", Input(night, [P("22:00"), P("06:00", "out", 1)]), "present", "present", 420),
            S("28 night shift late", Input(night, [P("22:25"), P("06:00", "out", 1)]), "late", "late", 395, 25),
            S("29 punch far outside the shift's window", Input(day, [P("05:00"), P("09:00"), P("18:00", "out")]), "present", "present", 480, 0, 0, "outside_tolerance"),
            S("30 punches on a day with no shift", Input(DayPlan.None, [P("09:00"), P("18:00", "out")]), "incomplete", "unscheduled", 480, 0, 0, "unscheduled"),
            S("31 flexible shift: late against the core start", Input(Working(Flexible), [P("10:20"), P("19:00", "out")]), "late", "late", 460, 20),
        };
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void FR_HRM_ATT_10_each_scenario_gives_the_expected_result_and_explanation(Scenario s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var outcome = DayRules.Evaluate(s.Input);
        Assert.Equal(s.Result, outcome.Result);
        Assert.Equal(s.Rule, outcome.Explanation.Rule);
        Assert.Equal(s.Worked, outcome.WorkedMinutes);
        Assert.Equal(s.Late, outcome.LateMinutes);
        Assert.Equal(s.Early, outcome.EarlyMinutes);
        Assert.Equal(s.Exceptions.Order(StringComparer.Ordinal), outcome.Exceptions.Order(StringComparer.Ordinal));

        // The explanation names the rule and the policy version that decided, and every key it holds has a text in both languages.
        var json = outcome.Explanation.ToJson();
        Assert.Equal("ATT-STD", (string?)json["policy"]!["code"]);
        Assert.Equal(3, (int)json["policy"]!["version"]!);
        Assert.Contains(outcome.Explanation.Steps, l => l.Key == DayRules.StepPolicy);
        using var doc = JsonDocument.Parse(json.ToJsonString());
        Assert.DoesNotContain("{", DayRules.Render(doc.RootElement, Texts("en")), StringComparison.Ordinal);
        Assert.DoesNotContain("{", DayRules.Render(doc.RootElement, Texts("ur")), StringComparison.Ordinal);

        // Same input, same output (slice Goal: results are repeatable).
        var again = DayRules.Evaluate(s.Input);
        Assert.Equal(json.ToJsonString(), again.Explanation.ToJson().ToJsonString());
        Assert.Equal(outcome with { Explanation = again.Explanation, Exceptions = again.Exceptions }, again);
    }

    [Fact]
    public void FR_HRM_ATT_10_the_late_sentence_reads_as_the_slice_says_in_English_and_has_an_Urdu_text()
    {
        var outcome = DayRules.Evaluate(Input(Working(DayShift9To6), [P("09:22"), P("18:00", "out")]));
        using var doc = JsonDocument.Parse(outcome.Explanation.ToJson().ToJsonString());
        Assert.StartsWith("Late 22 minutes: shift starts 09:00, first punch 09:22, grace 10.", DayRules.Render(doc.RootElement, Texts("en")), StringComparison.Ordinal);
        Assert.Matches(@"\p{IsArabic}", DayRules.Render(doc.RootElement, Texts("ur")));
        Assert.Equal("hrm.att.explain.late", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(22, doc.RootElement.GetProperty("params").GetProperty("late").GetInt32());
    }

    [Fact]
    public void Q3_a_night_shift_owns_the_next_morning_punches_up_to_its_end_plus_the_window_and_the_next_day_starts_there()
    {
        var (from, to) = DayRules.OwnedRange(Day, Night, 120, null, 0);
        Assert.Equal(Day.ToDateTime(TimeOnly.MinValue), from);
        Assert.Equal(Day.AddDays(1).ToDateTime(new TimeOnly(8, 0)), to);
        var (nextFrom, nextTo) = DayRules.OwnedRange(Day.AddDays(1), DayShift9To6, 120, Night, 120);
        Assert.Equal(to, nextFrom); // consecutive days never share a punch
        Assert.Equal(Day.AddDays(2).ToDateTime(TimeOnly.MinValue), nextTo);
        var (s, e) = DayRules.ShiftTimes(Day, Night);
        Assert.Equal(Day.ToDateTime(new TimeOnly(22, 0)), s);
        Assert.Equal(Day.AddDays(1).ToDateTime(new TimeOnly(6, 0)), e);
    }

    [Fact]
    public void Q4_corrected_and_repeated_punches_are_not_counted_and_the_rest_keep_time_order()
    {
        var wrong = P("09:40", n: 1);
        var dup = P("09:00", seconds: 20, duplicate: true, n: 2);
        var right = P("09:00", n: 3) with { SupersedesEventId = wrong.Id };
        var outPunch = P("18:00", "out", n: 4);
        var counted = DayRules.Counted([outPunch, wrong, dup, right], new HashSet<Guid> { wrong.Id });
        Assert.Equal([right.Id, outPunch.Id], counted.Select(p => p.Id));
    }

    [Theory]
    [InlineData("none", null, 487, 487)]
    [InlineData("down", 15, 487, 480)]
    [InlineData("up", 15, 481, 495)]
    [InlineData("up", 15, 480, 480)]
    [InlineData("nearest", 15, 487, 480)]
    [InlineData("nearest", 15, 488, 495)]
    [InlineData("nearest", 10, 485, 490)]
    public void Q6_worked_minutes_are_rounded_by_the_policys_mode_and_step(string mode, int? step, int minutes, int expected)
    {
        var policy = Policy(v => { v["roundingMode"] = mode; v["roundingStepMinutes"] = step; });
        Assert.Equal(expected, policy.Round(minutes));
    }

    [Theory]
    [InlineData("""{"breakMode":"fixed"}""", "breakMinutes", AttendancePolicy.BreakMinutesRequired)]
    [InlineData("""{"breakMode":"none","breakMinutes":30}""", "breakMinutes", AttendancePolicy.BreakMinutesUnused)]
    [InlineData("""{"roundingMode":"up"}""", "roundingStepMinutes", AttendancePolicy.RoundingStepRequired)]
    [InlineData("""{"roundingMode":"none","roundingStepMinutes":5}""", "roundingStepMinutes", AttendancePolicy.RoundingStepUnused)]
    [InlineData("""{"fullDayMinutes":300,"absentBelowMinutes":400}""", "absentBelowMinutes", AttendancePolicy.AbsentAboveFullDay)]
    [InlineData("""{"lateGraceMinutes":10,"lateHalfDayAfterMinutes":10}""", "lateHalfDayAfterMinutes", AttendancePolicy.HalfDayWithinGrace)]
    [InlineData("""{"earlyGraceMinutes":15,"earlyHalfDayAfterMinutes":5}""", "earlyHalfDayAfterMinutes", AttendancePolicy.HalfDayWithinGrace)]
    public void FR_HRM_ATT_09_the_policy_check_refuses_values_that_would_be_ignored_or_out_of_order(string json, string field, string key)
    {
        using var doc = JsonDocument.Parse(json);
        var errors = AttendancePolicy.Check(doc.RootElement);
        Assert.Equal([key], errors[field]);
    }

    [Fact]
    public void FR_HRM_ATT_09_AS_4_the_attendance_type_has_every_parameter_of_the_slice_no_default_and_a_policy_missing_a_value_is_not_read_as_zero()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/policy-types.json")));
        var type = manifest.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("code").GetString() == "attendance");
        var props = type.GetProperty("schema").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["absentBelowMinutes", "breakMinutes", "breakMode", "earlyGraceMinutes", "earlyHalfDayAfterMinutes", "fullDayMinutes", "holidayWork", "lateGraceMinutes", "lateHalfDayAfterMinutes", "roundingMode", "roundingStepMinutes", "shiftWindowMinutes", "weeklyOffWork"],
            props);
        Assert.DoesNotContain("\"default\"", type.GetRawText(), StringComparison.Ordinal);
        using var partial = JsonDocument.Parse("""{"lateGraceMinutes":10}""");
        Assert.Null(AttendancePolicy.Read(partial.RootElement));
    }

    [Fact]
    public void FR_HRM_ATT_11_the_engine_raises_and_resolves_only_its_own_exception_kinds()
    {
        Assert.Equal(["missing_in", "missing_out", "odd_count", "outside_tolerance", "unscheduled"], DayResults.EngineKinds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Q7_every_explanation_and_engine_text_exists_in_English_and_Urdu()
    {
        var en = Texts("en");
        var ur = Texts("ur");
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(DayRules.Keys, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Attendance", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"((?:hrm\\.att\\.(?:skip|explain|step)|errors\\.hrm\\.attendance\\.process|errors\\.hrm\\.attendance\\.unit)[a-z_.]*)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
        Assert.All(AttendanceEnginePermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c) && ur.ContainsKey("permissions." + c), c));
        Assert.All(DayRules.Keys.Where(k => k.Contains(".explain.", StringComparison.Ordinal)), k => Assert.Matches(@"\p{IsArabic}", ur[k]));
    }

    private static Dictionary<string, string> Texts(string lang) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, $"src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization/attendance.{lang}.json")))!;

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }
}
