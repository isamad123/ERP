using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Hrm.Contracts.Leave;

namespace Erp.UnitTests.Leave;

public sealed class LeaveReportUnitTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Erp.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void FR_HRM_LVE_10_every_leave_report_has_a_unique_code_and_an_export_type()
    {
        Assert.Equal(3, LeaveReportCodes.All.Count);
        Assert.Equal(LeaveReportCodes.All.Count, LeaveReportCodes.All.Distinct().Count());
        Assert.All(LeaveReportCodes.All, c => Assert.Equal("hrm.rpt_leave_" + c, LeaveReportCodes.ExportType(c)));
    }

    [Fact]
    public void FR_HRM_RPT_05_the_permission_is_declared_described_in_both_languages_and_held_by_managers_and_hr_but_not_employees()
    {
        var root = Root();
        var folder = Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Contracts/Leave/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "leave.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "leave.ur.json")))!;
        var key = "permissions." + LeavePermissions.ReportsRead;
        Assert.True(en.ContainsKey(key) && ur.ContainsKey(key));
        foreach (var k in new[] { "errors.hrm.leave.report.range_order", "errors.hrm.leave.report.range_too_long", "errors.hrm.leave.report.unknown" })
        {
            Assert.True(en.ContainsKey(k) && ur.ContainsKey(k) && Regex.IsMatch(ur[k], @"\p{IsArabic}", RegexOptions.None, TimeSpan.FromSeconds(5)), k);
        }

        Assert.Contains("\"" + LeavePermissions.ReportsRead + "\"", File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")), StringComparison.Ordinal);
        using var roles = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/baseline-roles.json")));
        string[] Held(string code) => [.. roles.RootElement.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
        Assert.Contains(LeavePermissions.ReportsRead, Held("line_manager"));
        Assert.Contains(LeavePermissions.ReportsRead, Held("hr_admin"));
        Assert.DoesNotContain(LeavePermissions.ReportsRead, Held("employee"));
    }

    [Fact]
    public void FR_HRM_GHR_06_the_leave_reporting_views_have_no_pay_or_salary_column()
    {
        var rules = File.ReadAllText(Path.Combine(Root(), "tools/Erp.CodeGen/rules.yaml"));
        var views = Regex.Matches(rules, @"create or replace view (hrm\.rpt_leave_\w+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.Equal(3, views.Count);
        foreach (Match view in views)
        {
            var text = rules[view.Index..rules.IndexOf(';', view.Index)];
            foreach (var forbidden in new[] { "salary", "wage", "amount", "ctc", "bonus", "allowance", "compensation", "bank", "iban", "tax" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
