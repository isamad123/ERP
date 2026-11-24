using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>Pure rules of shifts, weekly-off patterns and roster checks (slice S1.28a).</summary>
public sealed partial class ShiftUnitTests
{
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

    private static TimeOnly T(string text) => TimeOnly.ParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static ShiftSpec Spec(string type, string? start, string? end, bool crosses = false, int full = 480, string? coreStart = null, string? coreEnd = null, params ShiftSegment[] segments) =>
        new(type, start is null ? null : T(start), end is null ? null : T(end), coreStart is null ? null : T(coreStart), coreEnd is null ? null : T(coreEnd), crosses, segments, full);

    [Fact]
    public void FR_HRM_ATT_06_each_of_the_six_shift_types_is_accepted_with_its_own_times()
    {
        Assert.Empty(ShiftRules.Validate(Spec("fixed", "09:00", "17:00")));
        Assert.Empty(ShiftRules.Validate(Spec("flexible", "07:00", "19:00", false, 480, "10:00", "15:00")));
        Assert.Empty(ShiftRules.Validate(Spec("rotating", "06:00", "14:00")));
        Assert.Empty(ShiftRules.Validate(Spec("split", "08:00", "20:00", false, 480, null, null, new ShiftSegment(T("08:00"), T("12:00"), "work"), new ShiftSegment(T("16:00"), T("20:00"), "work"))));
        Assert.Empty(ShiftRules.Validate(Spec("night", "22:00", "06:00", true)));
        Assert.Empty(ShiftRules.Validate(Spec("continuous", "08:00", "08:00", true, 1440)));
    }

    [Fact]
    public void FR_HRM_ATT_06_wrong_times_are_refused_with_the_field_and_a_key()
    {
        Assert.Contains(ShiftRules.TimesRequired, ShiftRules.Validate(Spec("fixed", null, null))["startTime"]);
        Assert.Contains(ShiftRules.EndNotAfterStart, ShiftRules.Validate(Spec("fixed", "17:00", "09:00"))["endTime"]);
        Assert.Contains(ShiftRules.MidnightMismatch, ShiftRules.Validate(Spec("night", "06:00", "22:00", true))["crossesMidnight"]);
        Assert.Contains(ShiftRules.CoreRequired, ShiftRules.Validate(Spec("flexible", "07:00", "19:00"))["coreStart"]);
        Assert.Contains(ShiftRules.CoreOutside, ShiftRules.Validate(Spec("flexible", "07:00", "19:00", false, 480, "06:00", "15:00"))["coreStart"]);
        Assert.Contains(ShiftRules.SegmentsRequired, ShiftRules.Validate(Spec("split", "08:00", "20:00"))["segments"]);
        Assert.Contains(ShiftRules.ContinuousLength, ShiftRules.Validate(Spec("continuous", "08:00", "16:00", true, 1440))["endTime"]);
        Assert.Contains(ShiftRules.FullDayRange, ShiftRules.Validate(Spec("fixed", "09:00", "17:00", false, 600))["minFullDayMinutes"]);
        Assert.Contains(ShiftRules.TypeInvalid, ShiftRules.Validate(Spec("weekend", "09:00", "17:00"))["shiftType"]);
    }

    [Fact]
    public void FR_HRM_ATT_06_segments_must_lie_inside_the_shift_in_order_and_without_overlap()
    {
        var overlap = Spec("split", "08:00", "20:00", false, 480, null, null, new ShiftSegment(T("08:00"), T("13:00"), "work"), new ShiftSegment(T("12:00"), T("20:00"), "work"));
        Assert.Contains(ShiftRules.SegmentsInvalid, ShiftRules.Validate(overlap)["segments"]);
        var outside = Spec("fixed", "09:00", "17:00", false, 480, null, null, new ShiftSegment(T("16:00"), T("18:00"), "break"));
        Assert.Contains(ShiftRules.SegmentsInvalid, ShiftRules.Validate(outside)["segments"]);
        var withBreak = Spec("fixed", "09:00", "17:00", false, 420, null, null, new ShiftSegment(T("13:00"), T("14:00"), "break"));
        Assert.Empty(ShiftRules.Validate(withBreak));
        var nightBreak = Spec("night", "22:00", "06:00", true, 420, null, null, new ShiftSegment(T("01:00"), T("02:00"), "break"));
        Assert.Empty(ShiftRules.Validate(nightBreak));
        Assert.Equal(480, ShiftRules.SpanMinutes(T("22:00"), T("06:00"), true));
        Assert.Null(ShiftRules.ParseSegments(JsonDocument.Parse("""[{"start":"9am","end":"13:00"}]""").RootElement));
        Assert.Single(ShiftRules.ParseSegments(JsonDocument.Parse("""[{"start":"09:00","end":"13:00"}]""").RootElement)!);
    }

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FR_HRM_ATT_06_a_fixed_pattern_gives_the_same_weekdays_every_week()
    {
        var (pattern, error) = WeeklyOffPattern.Parse(Params("""{"weeklyOffKind":"fixed","offDays":[6,7]}"""));
        Assert.Null(error);
        var off = pattern!.OffDates(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)).ToList();
        Assert.Equal([new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 7), new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 14)], off);
    }

    [Fact]
    public void FR_HRM_ATT_06_a_rotating_pattern_counts_across_a_month_boundary()
    {
        // A 6-day cycle starting on 27 February 2026 whose 5th and 6th days are off: off on 3 and 4 March, 9 and 10 March, 15 and 16 March.
        var (pattern, error) = WeeklyOffPattern.Parse(Params("""{"weeklyOffKind":"rotating","cycleDays":6,"offDays":[5,6],"anchorDate":"2026-02-27"}"""));
        Assert.Null(error);
        var off = pattern!.OffDates(new DateOnly(2026, 2, 27), new DateOnly(2026, 3, 16)).ToList();
        Assert.Equal(
            [new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 16)],
            off);
        Assert.True(pattern.IsOff(new DateOnly(2026, 2, 26) .AddDays(-1))); // before the anchor the cycle runs backwards: 25 February is position 5
        Assert.False(pattern.IsOff(new DateOnly(2026, 2, 27)));
    }

    [Fact]
    public void FR_HRM_ATT_06_alternating_weeks_swap_the_off_days_each_week()
    {
        // Week of 2 March 2026 (Monday): Friday off; the next week: Friday and Saturday off.
        var (pattern, error) = WeeklyOffPattern.Parse(Params("""{"weeklyOffKind":"alternating_weeks","offDays":[5],"altOffDays":[5,6],"anchorDate":"2026-03-04"}"""));
        Assert.Null(error);
        var off = pattern!.OffDates(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 22)).ToList();
        Assert.Equal([new DateOnly(2026, 3, 6), new DateOnly(2026, 3, 13), new DateOnly(2026, 3, 14), new DateOnly(2026, 3, 20)], off);
    }

    [Theory]
    [InlineData("""{"weeklyOffKind":"monthly"}""", WeeklyOffPattern.KindInvalid)]
    [InlineData("""{"weeklyOffKind":"fixed","offDays":[8]}""", WeeklyOffPattern.DaysInvalid)]
    [InlineData("""{"weeklyOffKind":"fixed","offDays":[6,6]}""", WeeklyOffPattern.DaysInvalid)]
    [InlineData("""{"weeklyOffKind":"rotating","cycleDays":1,"offDays":[1],"anchorDate":"2026-01-01"}""", WeeklyOffPattern.CycleInvalid)]
    [InlineData("""{"weeklyOffKind":"rotating","cycleDays":4,"offDays":[5],"anchorDate":"2026-01-01"}""", WeeklyOffPattern.DaysInvalid)]
    [InlineData("""{"weeklyOffKind":"rotating","cycleDays":4,"offDays":[1]}""", WeeklyOffPattern.AnchorRequired)]
    [InlineData("""{"weeklyOffKind":"alternating_weeks","offDays":[6]}""", WeeklyOffPattern.AnchorRequired)]
    public void FR_HRM_ATT_06_an_incomplete_pattern_is_refused_with_a_key(string json, string key)
    {
        var (pattern, error) = WeeklyOffPattern.Parse(Params(json));
        Assert.Null(pattern);
        Assert.Equal(key, error);
    }

    private static readonly Guid Ali = Guid.NewGuid();
    private static readonly Guid Day = Guid.NewGuid();
    private static readonly Guid Night = Guid.NewGuid();
    private static readonly Dictionary<Guid, ShiftTimes> Shifts = new()
    {
        [Day] = new(T("08:00"), T("16:00"), false),
        [Night] = new(T("22:00"), T("06:00"), true),
    };

    private static IReadOnlyList<RosterCheckMessage> Run(IReadOnlyCollection<RosterDay> days, IReadOnlyCollection<OtherRosterDay>? others = null, int? minimumRest = null, EmploymentSpan? span = null, Func<Guid, DateOnly, bool>? rest = null) =>
        RosterChecks.Run(days, others ?? [], Shifts, (_, _) => minimumRest,
            new Dictionary<Guid, EmploymentSpan> { [Ali] = span ?? new EmploymentSpan(new DateOnly(2020, 1, 1), null) }, rest ?? ((_, _) => false));

    [Fact]
    public void FR_HRM_ATT_07_two_shifts_on_one_day_are_an_error()
    {
        var date = new DateOnly(2026, 3, 2);
        var other = Guid.NewGuid();
        var found = Run([new RosterDay(Ali, date, "working", Day, null)], [new OtherRosterDay(other, Ali, date, "working", Night)]);
        var message = Assert.Single(found);
        Assert.Equal((RosterChecks.DoubleAssignment, RosterChecks.Error), (message.Code, message.Severity));
        Assert.Empty(Run([new RosterDay(Ali, date, "working", Day, null)], [new OtherRosterDay(other, Ali, date, "weekly_off", null)]));
    }

    [Fact]
    public void FR_HRM_ATT_07_rest_below_the_policy_minimum_is_reported_only_when_a_minimum_is_set()
    {
        var d1 = new DateOnly(2026, 3, 2);
        var days = new[] { new RosterDay(Ali, d1, "working", Night, null), new RosterDay(Ali, d1.AddDays(1), "working", Day, null) };
        // Night ends at 06:00 on the 3rd and the day shift starts at 08:00 the same day: 120 minutes of rest.
        Assert.Empty(Run(days));
        Assert.Empty(Run(days, minimumRest: 120));
        var short_ = Assert.Single(Run(days, minimumRest: 660));
        Assert.Equal((RosterChecks.ShortRest, RosterChecks.Warning), (short_.Code, short_.Severity));
        Assert.Equal(120, short_.Detail!["restMinutes"]);
        Assert.Equal(16 * 60, RosterChecks.RestMinutes(Shifts[Day], Shifts[Day]));
    }

    [Fact]
    public void FR_HRM_ATT_07_a_day_outside_the_employment_is_an_error_and_work_on_a_rest_day_needs_a_reason()
    {
        var date = new DateOnly(2026, 3, 2);
        var outside = Run([new RosterDay(Ali, date, "working", Day, null)], span: new EmploymentSpan(new DateOnly(2026, 3, 5), null));
        Assert.Equal(RosterChecks.OutsideEmployment, Assert.Single(outside).Code);
        var withoutReason = Run([new RosterDay(Ali, date, "working", Day, null)], rest: (_, _) => true);
        Assert.Equal((RosterChecks.RestDayWork, RosterChecks.Warning), (Assert.Single(withoutReason).Code, withoutReason[0].Severity));
        Assert.Empty(Run([new RosterDay(Ali, date, "working", Day, "Plant shutdown cover")], rest: (_, _) => true));
    }

    [Fact]
    public void FR_HRM_ATT_06_the_shift_and_roster_permissions_and_every_text_exist_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var declared = manifest.RootElement.GetProperty("permissions").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!, p => p.GetProperty("sensitive").GetBoolean(), StringComparer.Ordinal);
        Assert.All(ShiftPermissions.HandWritten, c => Assert.True(declared.ContainsKey(c), c));
        Assert.True(declared[ShiftPermissions.RostersAmend]);

        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "shifts.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "shifts.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(ShiftPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        var sources = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("Generated", StringComparison.Ordinal));
        var used = sources.SelectMany(f => ShiftKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
        Assert.All(new[] { RosterChecks.DoubleAssignment, RosterChecks.ShortRest, RosterChecks.OutsideEmployment, RosterChecks.RestDayWork }, c => Assert.True(en.ContainsKey("hrm.roster.check." + c), c));
    }

    [GeneratedRegex("\"(errors\\.hrm\\.(?:shift|shift_pattern|roster)\\.[a-z_]+)\"")]
    private static partial Regex ShiftKey();
}
