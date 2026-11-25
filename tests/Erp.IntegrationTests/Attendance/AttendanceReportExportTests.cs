#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Documents;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// Each standard attendance report as an exportable list (slice S1.30b, FR-HRM-ATT-19, FR-HRM-RPT-05, FR-ADM-DAT-04) through the real export engine and a
/// worker: the file has the report's rows, a manager's file only the team's (the job asks as the person who requested it), nothing has a pay or salary
/// column, and a person without the report permission cannot ask.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceReportExportTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Exports = "/api/itg/exports";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> ExportAsync(Guid user, ReportOrg o, string code, object query, string[] columns)
    {
        var asked = await _h.PostAsync(user, Exports, new
        {
            source = "hrm.rpt_att_" + code, query, format = "csv", companyId = o.Company, language = "en",
            columns = columns.Select(c => new { field = c, label = c }).ToArray(),
        }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        await using (var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct))
        {
            await rig.RunUntilIdleAsync(rig.Worker(extra: s => DocumentTestServices.Register(s, _h.FilesRoot)), Ct);
        }

        var view = await _h.GetAsync(user, $"{Exports}/{id}", Ct);
        Assert.Equal("done", view.Json.GetProperty("status").GetString());
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), _h.Token(user));
        using var file = await client.GetAsync(new Uri($"{Exports}/{id}/download", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        return Encoding.UTF8.GetString(await file.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task S130b_FR_HRM_ATT_19_FR_HRM_RPT_05_a_report_is_exported_as_the_person_who_asked_and_a_manager_gets_only_the_team()
    {
        var o = await ReportOrg.CreateAsync(_h);
        var shift = await o.ShiftAsync("09:00", "18:00");
        var day = o.Today.AddDays(-4);
        var (manager, managerUser) = await o.HireAsync("line_manager");
        var (onTime, _) = await o.HireAsync(manager: manager);
        var (outsider, _) = await o.HireAsync();
        foreach (var e in new[] { onTime, outsider })
        {
            await o.RosterAsync(e, shift, day);
            await o.PunchAsync(e, (ReportOrg.At(day, "09:02"), "in"), (ReportOrg.At(day, "18:00"), "out"));
            await o.ProcessAsync(e, day, day);
        }

        var catalogue = (await _h.GetAsync(o.Hr, "/api/hrm/attendance/reports", Ct)).Json.GetProperty("reports");
        var columns = catalogue.EnumerateArray().Single(r => r.GetProperty("code").GetString() == "muster_daily").GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal("hrm.rpt_att_muster_daily", catalogue.EnumerateArray().Single(r => r.GetProperty("code").GetString() == "muster_daily").GetProperty("exportType").GetString());
        var query = new { companyId = o.Company, from = ReportOrg.Iso(day), to = ReportOrg.Iso(day) };

        var all = await ExportAsync(o.Hr, o, "muster_daily", query, columns);
        var onTimeNo = await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, onTime);
        var outsiderNo = await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, outsider);
        Assert.Contains(onTimeNo, all, StringComparison.Ordinal);
        Assert.Contains(outsiderNo, all, StringComparison.Ordinal);
        Assert.Contains("present", all, StringComparison.Ordinal);
        Assert.DoesNotContain("salary", all, StringComparison.OrdinalIgnoreCase);

        var team = await ExportAsync(managerUser, o, "muster_daily", query, columns);
        Assert.Contains(onTimeNo, team, StringComparison.Ordinal);
        Assert.DoesNotContain(outsiderNo, team, StringComparison.Ordinal);

        // A person without the report permission cannot ask for the file.
        var employee = await _h.UserAsync(Ct, null, "employee");
        var refused = await _h.PostAsync(employee, Exports, new { source = "hrm.rpt_att_muster_daily", query, format = "csv", companyId = o.Company, language = "en", columns = columns.Select(c => new { field = c, label = c }).ToArray() }, Ct);
        Assert.NotEqual(HttpStatusCode.Accepted, refused.Status);
    }
}
