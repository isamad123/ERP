#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// The standard attendance reports of slice S1.30b (FR-HRM-ATT-19, FR-HRM-RPT-05, FR-HRM-SSP-02, FR-HRM-GHR-06) through the real API pipeline, the real access
/// engine and a real PostgreSQL: each report returns the expected rows and totals for a small dataset processed by the real engine, a manager sees only the
/// team, an employee has no reports, another company's reader sees nothing, and no view or answer has a pay or salary field.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceReportTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Reports = "/api/hrm/attendance/reports";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Data(ReportOrg Org, DateOnly Day, Guid Manager, Guid ManagerUser, Guid OnTime, Guid OnTimeUser, Guid Late, Guid Absent, Guid Forgot, Guid Device);

    /// <summary>Three people work 09:00 to 18:00 four days ago: one on time, one late (in at 09:40), one absent; the first two report to the manager. A fourth forgot to punch out three days ago (an open exception).</summary>
    private async Task<Data> SetUpAsync()
    {
        var o = await ReportOrg.CreateAsync(_h);
        var shift = await o.ShiftAsync("09:00", "18:00");
        var day = o.Today.AddDays(-4);
        var (manager, managerUser) = await o.HireAsync("line_manager");
        var (onTime, onTimeUser) = await o.HireAsync(manager: manager);
        var (late, _) = await o.HireAsync(manager: manager);
        var (absent, _) = await o.HireAsync();
        var (forgot, _) = await o.HireAsync();
        foreach (var e in new[] { onTime, late, absent })
        {
            await o.RosterAsync(e, shift, day);
        }

        await o.RosterAsync(forgot, shift, day.AddDays(1));
        await o.PunchAsync(forgot, (ReportOrg.At(day.AddDays(1), "09:01"), "in"));
        await o.ProcessAsync(forgot, day.AddDays(1), day.AddDays(1));

        await o.PunchAsync(onTime, (ReportOrg.At(day, "09:02"), "in"), (ReportOrg.At(day, "18:03"), "out"));
        await o.PunchAsync(late, (ReportOrg.At(day, "09:40"), "in"), (ReportOrg.At(day, "18:00"), "out"));
        foreach (var e in new[] { onTime, late, absent })
        {
            await o.ProcessAsync(e, day, day);
        }

        var location = (Guid)(await _h.RowAsync("org.locations", o.Company, null, Ct))["id"]!;
        var device = (Guid)(await _h.RowAsync("hrm.attendance_devices", o.Company, new Dictionary<string, object?> { ["location_id"] = location, ["status"] = "active", ["last_error"] = "Timed out", ["company_id"] = o.Company }, Ct))["id"]!;
        return new Data(o, day, manager, managerUser, onTime, onTimeUser, late, absent, forgot, device);
    }

    private async Task<JsonElement> ReportAsync(Guid user, Data d, string code, string query = "")
    {
        var answer = await _h.GetAsync(user, $"{Reports}/{code}?companyId={d.Org.Company}{query}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.Clone();
    }

    private static List<JsonElement> Items(JsonElement report) => [.. report.GetProperty("items").EnumerateArray().Select(i => i.Clone())];

    private static string Id(JsonElement item, string name = "employmentId") => item.GetProperty(name).GetGuid().ToString();

    [Fact]
    public async Task S130b_FR_HRM_ATT_19_the_daily_muster_has_one_row_per_person_and_day_with_totals_filters_and_search()
    {
        var d = await SetUpAsync();
        var day = ReportOrg.Iso(d.Day);
        var report = await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}");
        var rows = Items(report);
        Assert.Equal(3, rows.Count);
        Assert.Equal("present", rows.Single(r => Id(r) == d.OnTime.ToString()).GetProperty("result").GetString());
        var late = rows.Single(r => Id(r) == d.Late.ToString());
        Assert.Equal("late", late.GetProperty("result").GetString());
        Assert.Equal(40, late.GetProperty("lateMinutes").GetInt32());
        Assert.Equal("absent", rows.Single(r => Id(r) == d.Absent.ToString()).GetProperty("result").GetString());
        var summary = report.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("result.present").GetInt64());
        Assert.Equal(1, summary.GetProperty("result.late").GetInt64());
        Assert.Equal(1, summary.GetProperty("result.absent").GetInt64());
        Assert.Equal(3, report.GetProperty("total").GetInt64());

        // Filters: result, person, unit, and the search text of the employee number.
        Assert.Equal([d.Absent.ToString()], Items(await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&status=absent")).Select(r => Id(r)));
        Assert.Equal([d.Late.ToString()], Items(await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&employmentId={d.Late}")).Select(r => Id(r)));
        Assert.Equal(3, Items(await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&unitId={d.Org.Place.OrgUnit}")).Count);
        Assert.Empty(Items(await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&unitId={Guid.NewGuid()}")));
        var number = await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, d.Late);
        Assert.Equal([d.Late.ToString()], Items(await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&search={number.ToLowerInvariant()}")).Select(r => Id(r)));

        // A page and its continuation.
        var first = await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&limit=2");
        Assert.Equal(2, Items(first).Count);
        Assert.True(first.GetProperty("more").GetBoolean());
        Assert.Single(Items(await ReportAsync(d.Org.Hr, d, "muster_daily", $"&from={day}&to={day}&limit=2&offset=2")));

        // A range that is the wrong way round or longer than a year is refused with the field.
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(d.Org.Hr, $"{Reports}/muster_daily?from={day}&to={ReportOrg.Iso(d.Day.AddDays(-1))}", Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(d.Org.Hr, $"{Reports}/muster_daily?from={ReportOrg.Iso(d.Day.AddDays(-400))}&to={day}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(d.Org.Hr, $"{Reports}/nothing", Ct)).Status);
    }

    [Fact]
    public async Task S130b_FR_HRM_ATT_19_the_monthly_muster_absenteeism_late_early_punch_log_device_sync_and_exception_ageing_report_the_same_dataset()
    {
        var d = await SetUpAsync();
        var from = ReportOrg.Iso(new DateOnly(d.Day.Year, d.Day.Month, 1));

        var monthly = Items(await ReportAsync(d.Org.Hr, d, "muster_monthly", $"&from={from}&to={ReportOrg.Iso(d.Day)}"));
        Assert.Equal(4, monthly.Count); // the person who forgot to punch out (three days ago) has a row too
        var onTime = monthly.Single(r => Id(r) == d.OnTime.ToString());
        Assert.Equal(1, onTime.GetProperty("presentDays").GetInt64());
        Assert.Equal(0, onTime.GetProperty("absentDays").GetInt64());
        Assert.Equal(1, monthly.Single(r => Id(r) == d.Late.ToString()).GetProperty("lateDays").GetInt64());
        Assert.Equal(1, monthly.Single(r => Id(r) == d.Absent.ToString()).GetProperty("absentDays").GetInt64());
        Assert.Equal(new DateOnly(d.Day.Year, d.Day.Month, 1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), onTime.GetProperty("month").GetString());

        var absent = Items(await ReportAsync(d.Org.Hr, d, "absenteeism", $"&from={from}&to={ReportOrg.Iso(d.Day)}"));
        Assert.Equal([d.Absent.ToString()], absent.Select(r => Id(r)));
        Assert.Equal(1, absent[0].GetProperty("scheduledDays").GetInt64());
        Assert.Equal(100.00m, absent[0].GetProperty("absencePercent").GetDecimal());

        var lateEarly = Items(await ReportAsync(d.Org.Hr, d, "late_early", $"&from={ReportOrg.Iso(d.Day)}&to={ReportOrg.Iso(d.Day)}"));
        Assert.Equal([d.Late.ToString()], lateEarly.Select(r => Id(r)));
        Assert.Equal(40, lateEarly[0].GetProperty("lateMinutes").GetInt32());

        var punches = await ReportAsync(d.Org.Hr, d, "punch_log", $"&from={ReportOrg.Iso(d.Day)}&to={ReportOrg.Iso(d.Day)}");
        Assert.Equal(4, Items(punches).Count);
        Assert.Equal(4, punches.GetProperty("summary").GetProperty("source.hr_entry").GetInt64());
        Assert.Equal(2, Items(await ReportAsync(d.Org.Hr, d, "punch_log", $"&from={ReportOrg.Iso(d.Day)}&to={ReportOrg.Iso(d.Day)}&employmentId={d.Late}")).Count);

        var devices = Items(await ReportAsync(d.Org.Hr, d, "device_sync"));
        var device = devices.Single(r => r.GetProperty("deviceId").GetGuid() == d.Device);
        Assert.Equal("Timed out", device.GetProperty("lastError").GetString());
        Assert.Equal("active", device.GetProperty("status").GetString());
        Assert.False(device.TryGetProperty("credentialsRef", out _));

        var ageing = Items(await ReportAsync(d.Org.Hr, d, "exception_ageing"));
        Assert.NotEmpty(ageing);
        Assert.All(ageing, r => Assert.Equal("open", r.GetProperty("status").GetString()));
        var forgotException = ageing.Single(r => Id(r) == d.Forgot.ToString());
        Assert.Equal("missing_out", forgotException.GetProperty("kind").GetString());
        Assert.Equal(3, forgotException.GetProperty("ageDays").GetInt32());
        Assert.Single(Items(await ReportAsync(d.Org.Hr, d, "exception_ageing", "&minAgeDays=3")));
        Assert.Empty(Items(await ReportAsync(d.Org.Hr, d, "exception_ageing", "&minAgeDays=4")));
        Assert.Single(Items(await ReportAsync(d.Org.Hr, d, "exception_ageing", "&kind=missing_out")));
    }

    [Fact]
    public async Task S130b_FR_HRM_RPT_05_FR_HRM_SSP_02_a_manager_sees_only_the_team_in_every_report_and_an_employee_may_run_none()
    {
        var d = await SetUpAsync();
        var day = ReportOrg.Iso(d.Day);
        var team = Items(await ReportAsync(d.ManagerUser, d, "muster_daily", $"&from={day}&to={day}"));
        Assert.Equal(new[] { d.Late.ToString(), d.OnTime.ToString() }.Order(StringComparer.Ordinal), team.Select(r => Id(r)).Order(StringComparer.Ordinal));
        Assert.Equal(2, Items(await ReportAsync(d.ManagerUser, d, "muster_monthly", $"&from={day}&to={day}")).Count);
        Assert.Equal(4, Items(await ReportAsync(d.ManagerUser, d, "punch_log", $"&from={day}&to={day}")).Count);
        Assert.Equal(5, Items(await ReportAsync(d.Org.Hr, d, "punch_log", $"&from={day}&to={ReportOrg.Iso(d.Day.AddDays(1))}")).Count); // HR also sees the fifth punch, of the person outside the team
        Assert.Empty(Items(await ReportAsync(d.ManagerUser, d, "absenteeism", $"&from={day}&to={day}"))); // the absent person is not on the team
        Assert.Empty(Items(await ReportAsync(d.ManagerUser, d, "exception_ageing"))); // the open exception is the forgetful person's, who is not on the team

        // The employee role has neither the catalogue nor a report; the manager's catalogue lists them.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(d.OnTimeUser, Reports, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(d.OnTimeUser, $"{Reports}/muster_daily", Ct)).Status);
        var catalogue = await _h.GetAsync(d.ManagerUser, Reports, Ct);
        Assert.Equal(7, catalogue.Json.GetProperty("reports").GetArrayLength());

        // A reader whose grant covers another company only sees nothing of this one.
        var other = await _h.CompanyAsync(Ct);
        var viewer = await _h.UserAsync(Ct);
        await _h.AssignAsync(viewer, await _h.RoleAsync(Ct, ["hrm.attendance_reports.read", "hrm.attendance_days.read", "hrm.attendance_events.read", "hrm.attendance_exceptions.read", "hrm.attendance_devices.read"], [("*", "all")]), other, _h.Today.AddDays(-1), null, Ct);
        foreach (var code in new[] { "muster_daily", "muster_monthly", "absenteeism", "late_early", "punch_log", "device_sync", "exception_ageing" })
        {
            Assert.Empty(Items(await ReportAsync(viewer, d, code, $"&from={day}&to={day}")));
        }
    }

    [Fact]
    public async Task S130b_FR_HRM_GHR_06_FR_HRM_RPT_05_no_attendance_view_and_no_report_column_has_a_pay_or_salary_field()
    {
        var d = await SetUpAsync();
        var columns = await _h.ScalarAsync<string[]>("select array_agg(table_name || '.' || column_name order by table_name, ordinal_position) from information_schema.columns where table_schema = 'hrm' and table_name like 'rpt\\_att\\_%'", Ct);
        Assert.True(columns.Length > 60, "the views exist");
        var forbidden = new[] { "salary", "pay", "wage", "amount", "rate", "ctc", "bonus", "deduction", "allowance", "gross", "net_", "compensation", "bank", "iban", "tax" };
        Assert.All(columns, c => Assert.DoesNotContain(forbidden, f => c.Split('.')[1].Contains(f, StringComparison.OrdinalIgnoreCase)));

        // The answers of every report carry only the columns of the catalogue, none of them a pay field.
        var catalogue = (await _h.GetAsync(d.Org.Hr, Reports, Ct)).Json.GetProperty("reports");
        foreach (var report in catalogue.EnumerateArray())
        {
            Assert.All(report.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!), c => Assert.DoesNotContain(forbidden, f => c.Contains(f, StringComparison.OrdinalIgnoreCase)));
        }

        // The tables the views read are read-only to the application through the views: an insert into a view is refused by the database.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync("insert into hrm.rpt_att_muster (company_id) values (gen_random_uuid())", Ct));
        Assert.Contains("view", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
