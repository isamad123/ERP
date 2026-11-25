using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>The pure rules of the attendance requests of slice S1.30a (FR-HRM-ATT-11, ATT-14): fields, order of times, days a duty covers, texts.</summary>
public sealed class AttendanceRequestUnitTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private static readonly string[] Kinds = ["on_duty", "official_visit", "work_from_home", "field_duty"];
    private static readonly DateOnly Monday = new(2026, 3, 2);

    private static DateTimeOffset At(DateOnly day, string hhmm) =>
        new(day.ToDateTime(TimeOnly.ParseExact(hhmm, "HH:mm", System.Globalization.CultureInfo.InvariantCulture)), TimeSpan.FromHours(5));

    [Fact]
    public void FR_HRM_ATT_11_a_correction_needs_a_reason_and_a_time_in_order_on_or_next_to_its_day()
    {
        Assert.Empty(AttendanceRequestRules.CheckRegularization(Monday, Monday.AddDays(3), null, At(Monday, "18:00"), "Forgot", Karachi));
        Assert.Empty(AttendanceRequestRules.CheckRegularization(Monday, Monday.AddDays(3), At(Monday, "22:00"), At(Monday.AddDays(1), "06:00"), "Night shift", Karachi));
        Assert.Equal(["requestedIn"], AttendanceRequestRules.CheckRegularization(Monday, Monday, null, null, "x", Karachi).Keys);
        Assert.Equal(["requestedOut"], AttendanceRequestRules.CheckRegularization(Monday, Monday, At(Monday, "18:00"), At(Monday, "09:00"), "x", Karachi).Keys);
        Assert.Equal(["requestedOut"], AttendanceRequestRules.CheckRegularization(Monday, Monday.AddDays(9), null, At(Monday.AddDays(2), "09:00"), "x", Karachi).Keys);
        Assert.Equal(["workDate"], AttendanceRequestRules.CheckRegularization(Monday.AddDays(1), Monday, null, At(Monday.AddDays(1), "09:00"), "x", Karachi).Keys);
        Assert.Equal(["reason"], AttendanceRequestRules.CheckRegularization(Monday, Monday, null, At(Monday, "18:00"), "  ", Karachi).Keys);
        Assert.Equal(AttendanceRequestRules.TooLong, AttendanceRequestRules.CheckRegularization(Monday, Monday, null, At(Monday, "18:00"), new string('x', 501), Karachi)["reason"][0]);
    }

    [Fact]
    public void FR_HRM_ATT_14_a_duty_needs_a_known_kind_days_in_order_and_a_purpose()
    {
        Assert.Empty(AttendanceRequestRules.CheckDuty("field_duty", Monday, Monday.AddDays(2), null, null, "Survey"));
        Assert.Equal(["endDate", "kind", "purpose"], AttendanceRequestRules.CheckDuty("holiday", Monday, Monday.AddDays(-1), null, null, null).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["endTime"], AttendanceRequestRules.CheckDuty("on_duty", Monday, Monday, new TimeOnly(15, 0), new TimeOnly(9, 0), "x").Keys);
        Assert.Equal(["endDate", "kind", "purpose", "startDate"], AttendanceRequestRules.CheckDuty(null, null, null, null, null, null).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FR_HRM_ATT_14_a_duty_without_times_covers_whole_days_in_the_companys_zone_and_names_its_first_and_last_day()
    {
        var (start, end) = AttendanceRequestRules.DutyInstants(Monday, Monday.AddDays(2), null, null, Karachi);
        Assert.Equal(At(Monday, "00:00"), start);
        Assert.Equal(At(Monday.AddDays(3), "00:00"), end);
        Assert.Equal((Monday, Monday.AddDays(2)), AttendanceRequestRules.DutyDays(start, end, Karachi)); // midnight ends the day before
        var (s2, e2) = AttendanceRequestRules.DutyInstants(Monday, Monday, new TimeOnly(9, 0), new TimeOnly(13, 0), Karachi);
        Assert.Equal((Monday, Monday), AttendanceRequestRules.DutyDays(s2, e2, Karachi));
        Assert.Equal("13:00", AttendanceRequestRules.LocalTime(e2, Karachi));
    }

    [Fact]
    public void FR_HRM_ATT_14_the_policy_schema_names_a_flag_per_duty_kind_and_the_defaults_are_yes_except_work_from_home()
    {
        Assert.Equal(["field_duty", "official_visit", "on_duty", "work_from_home"], AttendancePolicy.DutyKinds.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([true, true, false, true], Kinds.Select(k => AttendancePolicy.DutyKinds[k]));
        var schema = File.ReadAllText(Path.Combine(Root(), "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/policy-types.json"));
        Assert.All(AttendancePolicy.DutyKinds.Keys, k => Assert.Contains("\"" + AttendancePolicy.DutyFlagName(k) + "\"", schema, StringComparison.Ordinal));
        using var doc = JsonDocument.Parse("""{"lateGraceMinutes":0,"earlyGraceMinutes":0,"absentBelowMinutes":0,"breakMode":"none","roundingMode":"none","shiftWindowMinutes":0,"weeklyOffWork":"ignore","holidayWork":"ignore","workFromHomeCountsAsPresent":true,"onDutyCountsAsPresent":false}""");
        var policy = AttendancePolicy.Read(doc.RootElement)!;
        Assert.True(policy.CountsAsPresent("work_from_home"));
        Assert.False(policy.CountsAsPresent("on_duty"));
        Assert.True(policy.CountsAsPresent("field_duty"));
    }

    [Fact]
    public void FR_HRM_ATT_11_every_text_of_the_requests_is_in_english_and_urdu_and_the_declared_permissions_are_described()
    {
        var root = Root();
        var folder = Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.ur.json")))!;
        var used = Directory.GetFiles(Path.Combine(root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/Generated/", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.attendance\\.request\\.[a-z_]+)\"").Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k) && System.Text.RegularExpressions.Regex.IsMatch(ur[k], @"\p{IsArabic}"), k));
        Assert.All(AttendanceRequestPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c) && ur.ContainsKey("permissions." + c), c));
        var declared = File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json"));
        Assert.All(AttendanceRequestPermissions.HandWritten, c => Assert.Contains("\"" + c + "\"", declared, StringComparison.Ordinal));
        var workflow = File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/workflow.json"));
        Assert.Contains("\"" + AttendanceRequestEvents.RegularizationDocumentType + "\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"" + AttendanceRequestEvents.DutyDocumentType + "\"", workflow, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
