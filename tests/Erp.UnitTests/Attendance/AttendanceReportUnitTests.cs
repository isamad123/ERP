using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>The pure rules of the live attendance dashboard and the declarations of the reports of slice S1.30b (FR-HRM-ATT-18, ATT-19, RPT-05, GHR-06).</summary>
public sealed class AttendanceReportUnitTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 9, 0, 0, TimeSpan.FromHours(5));
    private static readonly DateTimeOffset End = Start.AddHours(9);

    private static string Classify(DateTimeOffset now, DateTimeOffset? punch = null, int? grace = 10, bool leave = false, bool duty = false, bool shift = true) =>
        DashboardRules.Classify(shift, Start, End, grace, now, punch, leave, duty);

    [Fact]
    public void FR_HRM_ATT_18_a_person_with_no_punch_is_not_due_then_not_yet_punched_after_start_plus_grace_then_absent_when_the_shift_has_ended()
    {
        Assert.Equal(DashboardStatuses.NotDue, Classify(Start.AddMinutes(-30)));
        Assert.Equal(DashboardStatuses.NotDue, Classify(Start.AddMinutes(9)));
        Assert.Equal(DashboardStatuses.NotYetPunched, Classify(Start.AddMinutes(10))); // start plus grace has passed
        Assert.Equal(DashboardStatuses.NotYetPunched, Classify(End.AddMinutes(-1)));
        Assert.Equal(DashboardStatuses.Absent, Classify(End));
        Assert.Equal(DashboardStatuses.Absent, Classify(End.AddHours(3)));
    }

    [Fact]
    public void FR_HRM_ATT_18_a_punch_makes_a_person_present_and_late_only_after_start_plus_grace_whatever_else_is_on_file()
    {
        Assert.Equal(DashboardStatuses.Present, Classify(Start.AddMinutes(30), Start.AddMinutes(-20)));
        Assert.Equal(DashboardStatuses.Present, Classify(Start.AddMinutes(30), Start.AddMinutes(10))); // exactly start plus grace is on time
        Assert.Equal(DashboardStatuses.Late, Classify(Start.AddMinutes(30), Start.AddMinutes(11)));
        Assert.Equal(DashboardStatuses.Present, Classify(End.AddHours(1), Start.AddMinutes(5), leave: true, duty: true)); // a punch beats leave and duty
        Assert.Equal(DashboardStatuses.Present, Classify(Start.AddMinutes(30), Start.AddMinutes(45), grace: null)); // no policy: present, never late
    }

    [Fact]
    public void FR_HRM_ATT_18_leave_and_duty_without_a_punch_and_a_day_with_no_shift_or_no_policy()
    {
        Assert.Equal(DashboardStatuses.OnLeave, Classify(Start.AddHours(2), leave: true));
        Assert.Equal(DashboardStatuses.OnLeave, Classify(End.AddHours(2), leave: true, duty: true)); // leave first, as the engine reads it
        Assert.Equal(DashboardStatuses.OnDuty, Classify(Start.AddHours(2), duty: true));
        Assert.Equal(DashboardStatuses.NotScheduled, Classify(Start.AddHours(2), shift: false));
        Assert.Equal(DashboardStatuses.NoPolicy, Classify(Start.AddHours(2), grace: null)); // no number has a default
        Assert.Equal(DashboardStatuses.NoPolicy, Classify(End.AddHours(2), grace: null));
        Assert.Equal(DashboardStatuses.OnLeave, Classify(Start.AddHours(2), grace: null, leave: true));
    }

    [Fact]
    public void FR_HRM_ATT_18_the_present_count_includes_the_late_people_and_every_other_status_is_itself()
    {
        Assert.Equal([DashboardStatuses.Present, DashboardStatuses.Late], DashboardRules.Expand(DashboardStatuses.Present));
        Assert.All(DashboardStatuses.All.Where(s => s != DashboardStatuses.Present), s => Assert.Equal([s], DashboardRules.Expand(s)));
        Assert.Equal(DashboardStatuses.All.Count, DashboardStatuses.All.Distinct().Count());
    }

    [Fact]
    public void FR_HRM_ATT_19_every_standard_report_has_an_export_type_and_the_codes_are_unique()
    {
        Assert.Equal(7, AttendanceReportCodes.All.Count);
        Assert.Equal(AttendanceReportCodes.All.Count, AttendanceReportCodes.All.Distinct().Count());
        Assert.All(AttendanceReportCodes.All, c => Assert.Equal("hrm.rpt_att_" + c, AttendanceReportCodes.ExportType(c)));
    }

    [Fact]
    public void FR_HRM_RPT_05_the_permissions_are_declared_described_in_both_languages_and_held_by_the_baseline_roles_that_may_see_attendance()
    {
        var root = Root();
        var folder = Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.ur.json")))!;
        Assert.All(AttendanceReportPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c) && ur.ContainsKey("permissions." + c), c));
        var declared = File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json"));
        Assert.All(AttendanceReportPermissions.HandWritten, c => Assert.Contains("\"" + c + "\"", declared, StringComparison.Ordinal));
        foreach (var key in new[] { "errors.hrm.attendance.report.range_order", "errors.hrm.attendance.report.range_too_long", "errors.hrm.attendance.report.unknown" })
        {
            Assert.True(en.ContainsKey(key) && ur.ContainsKey(key) && System.Text.RegularExpressions.Regex.IsMatch(ur[key], @"\p{IsArabic}"), key);
        }

        using var roles = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/baseline-roles.json")));
        string[] Held(string code) => [.. roles.RootElement.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
        Assert.All(AttendanceReportPermissions.HandWritten, c => Assert.Contains(c, Held("line_manager")));
        Assert.All(AttendanceReportPermissions.HandWritten, c => Assert.Contains(c, Held("hr_admin")));
        Assert.All(AttendanceReportPermissions.HandWritten, c => Assert.DoesNotContain(c, Held("employee")));
    }

    [Fact]
    public void FR_HRM_GHR_06_FR_HRM_RPT_05_the_reporting_views_in_the_design_have_no_pay_or_salary_column()
    {
        var rules = File.ReadAllText(Path.Combine(Root(), "tools/Erp.CodeGen/rules.yaml"));
        var views = System.Text.RegularExpressions.Regex.Matches(rules, @"create or replace view (hrm\.rpt_att_\w+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.Equal(7, views.Count);
        foreach (System.Text.RegularExpressions.Match view in views)
        {
            var start = view.Index;
            var end = rules.IndexOf(';', start);
            var text = rules[start..end];
            foreach (var forbidden in new[] { "salary", "pay_", "wage", "amount", "ctc", "bonus", "allowance", "compensation", "bank", "iban", "tax" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
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
