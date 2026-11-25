#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using Erp.IntegrationTests.Access;
using System.Text;
using Erp.IntegrationTests.Documents;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Leave;

/// <summary>
/// The standard leave reports (slice S1.33b Build item 9): FR-HRM-LVE-10 (balance summary by unit and leave type equals the balances the ledger gives; leave taken by
/// type and month equals the request days), FR-HRM-RPT-05 (the data scope of the caller decides the rows), FR-HRM-GHR-06 (no pay or salary column). The company's date
/// comes from the harness clock in Asia/Karachi.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class LeaveReportTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Reports = "/api/hrm/leave/reports";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private DateOnly Monday
    {
        get
        {
            var d = Today.AddDays(21);
            while (d.DayOfWeek != DayOfWeek.Monday)
            {
                d = d.AddDays(1);
            }

            if (d.AddDays(28).Year != d.Year)
            {
                d = new DateOnly(d.Year + 1, 1, 8);
                while (d.DayOfWeek != DayOfWeek.Monday)
                {
                    d = d.AddDays(1);
                }
            }

            return d;
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Org(Guid Company, Guid Admin, Guid Type, string Code, Guid ManagerUser, Guid Manager, (Guid Employment, Guid User)[] Team, Guid Outsider);

    /// <summary>A manager with two direct reports and one person outside the team, one leave type with a manual leave policy and 10 days granted to each of the three.</summary>
    private async Task<Org> OrgAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var place = await _h.PlaceAsync(company, Ct);
        var admin = await _h.UserAsync(Ct, null, "hr_admin");
        var joined = Monday.AddYears(-2);
        async Task<Guid> HireAsync(Guid? user, Guid? manager)
        {
            var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
            var answer = await _h.PostAsync(admin, "/api/hrm/employments", EmploymentTestSupport.HireBody(company, place, person: person, joining: Iso(joined), manager: manager), Ct);
            Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
            var id = answer.Json.GetProperty("id").GetGuid();
            if (user is { } u)
            {
                await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, u);
            }

            return id;
        }

        var managerUser = await _h.UserAsync(Ct, null, "line_manager");
        var manager = await HireAsync(managerUser, null);
        var team = new List<(Guid, Guid)>();
        for (var i = 0; i < 2; i++)
        {
            var user = await _h.UserAsync(Ct, null, "employee");
            team.Add((await HireAsync(user, manager), user));
        }

        var outsider = await HireAsync(null, null);
        var type = await _h.PostAsync(admin, "/api/hrm/leave-types", new
        {
            companyId = company, code = "RPT" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), nameI18n = new { en = "Report leave", ur = "رپورٹ رخصت" }, isPaid = true,
            counting = "working_days", unit = "day", requiresDocument = false, isCompOff = false, isUnpaidDeduction = false,
        }, Ct);
        Assert.True(type.Status == HttpStatusCode.Created, type.Body);
        var typeId = type.Json.GetProperty("id").GetGuid();
        var code = type.Json.GetProperty("code").GetString()!;
        var from = new DateOnly(Today.Year - 3, 1, 1);
        var (leave, _) = await _h.ActivePolicyAsync(admin, company, "leave", new { types = new[] { new { leaveType = code, entitlement = new { model = "manual" } } } }, from, Ct);
        var set = await _h.ActivateSetAsync(admin, company, "leave", from, Ct, (leave, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        foreach (var employment in team.Select(t => t.Item1).Append(outsider))
        {
            var grant = await _h.PostAsync(admin, $"/api/hrm/employments/{employment}/leave-ledger", new { leaveTypeId = typeId, kind = "grant", quantity = 10m, entryDate = Iso(new DateOnly(Monday.Year, 1, 1)), reason = "Granted for the test" }, Ct);
            Assert.True(grant.Status == HttpStatusCode.Created, grant.Body);
        }

        return new Org(company, admin, typeId, code, managerUser, manager, [.. team], outsider);
    }

    private async Task<Guid> RequestAsync(Org o, int who, DateOnly start, DateOnly end, bool approve)
    {
        var asked = await _h.PostAsync(o.Team[who].User, "/api/hrm/leave-requests", new { leaveTypeId = o.Type, startDate = Iso(start), endDate = Iso(end) }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        if (approve)
        {
            var instance = await _h.ScalarAsync<Guid>("select workflow_instance_id from hrm.leave_requests where id = $1", Ct, id);
            var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at desc limit 1", Ct, instance);
            var done = await _h.PostAsync(o.ManagerUser, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
            Assert.True(done.Status == HttpStatusCode.OK, done.Body);
        }

        return id;
    }

    [Fact]
    public async Task FR_HRM_LVE_10_the_balance_summary_equals_the_balances_of_the_ledger_and_groups_by_unit_and_type()
    {
        var o = await OrgAsync();
        await RequestAsync(o, 0, Monday, Monday.AddDays(1), approve: true); // two days used
        var answer = await _h.GetAsync(o.Admin, $"{Reports}/balance_summary?companyId={o.Company}&leaveYear={Monday.Year}&leaveTypeId={o.Type}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        var rows = answer.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        var first = rows.Single(r => r.GetProperty("employmentId").GetGuid() == o.Team[0].Employment);
        Assert.Equal(10m, first.GetProperty("added").GetDecimal());
        Assert.Equal(2m, first.GetProperty("used").GetDecimal());
        Assert.Equal(8m, first.GetProperty("balance").GetDecimal());
        Assert.Equal(first.GetProperty("added").GetDecimal() - first.GetProperty("used").GetDecimal() + first.GetProperty("adjusted").GetDecimal() - first.GetProperty("removed").GetDecimal(), first.GetProperty("balance").GetDecimal());
        var ledger = await _h.GetAsync(o.Admin, $"/api/hrm/employments/{o.Team[0].Employment}/leave-balances?asOf={Iso(Monday.AddDays(3))}&leaveTypeId={o.Type}", Ct);
        Assert.Equal(first.GetProperty("balance").GetDecimal(), ledger.Json.GetProperty("balances")[0].GetProperty("balance").GetDecimal());
        var groups = answer.Json.GetProperty("summary").GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(3, groups.Sum(g => g.GetProperty("people").GetInt32()));
        Assert.Equal(28m, groups.Sum(g => g.GetProperty("balance").GetDecimal()));
        Assert.All(groups, g => Assert.Equal("day", g.GetProperty("unit").GetString()));
    }

    [Fact]
    public async Task FR_HRM_LVE_10_leave_taken_by_type_and_month_equals_the_request_days_and_pending_requests_list_the_waiting_ones()
    {
        var o = await OrgAsync();
        var approved = await RequestAsync(o, 0, Monday, Monday.AddDays(1), approve: true);
        var waiting = await RequestAsync(o, 1, Monday.AddDays(2), Monday.AddDays(2), approve: false);
        var month = new DateOnly(Monday.Year, Monday.Month, 1);

        var taken = await _h.GetAsync(o.Admin, $"{Reports}/leave_taken?companyId={o.Company}&from={Iso(month)}&to={Iso(month)}&leaveTypeId={o.Type}", Ct);
        Assert.True(taken.Status == HttpStatusCode.OK, taken.Body);
        var inMonth = await _h.ScalarAsync<decimal>("select coalesce(sum(quantity), 0) from hrm.leave_request_days where leave_request_id = $1 and date_trunc('month', leave_date) = $2::date", Ct, approved, month);
        Assert.Equal(inMonth, taken.Json.GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("taken").GetDecimal()));
        Assert.Equal(inMonth, taken.Json.GetProperty("summary").GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("taken").GetDecimal())); // waiting leave is not taken

        var pending = await _h.GetAsync(o.Admin, $"{Reports}/pending_requests?companyId={o.Company}&leaveTypeId={o.Type}", Ct);
        var ids = pending.Json.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("requestId").GetGuid()).ToList();
        Assert.Equal([waiting], ids);
        Assert.Equal("submitted", pending.Json.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(1, pending.Json.GetProperty("summary").GetProperty("groups")[0].GetProperty("requests").GetInt32());
        var none = await _h.GetAsync(o.Admin, $"{Reports}/pending_requests?companyId={o.Company}&status=cancel_requested", Ct);
        Assert.Empty(none.Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task FR_HRM_RPT_05_a_manager_sees_only_the_team_and_a_person_without_the_permission_nothing_and_the_range_is_checked()
    {
        var o = await OrgAsync();
        await RequestAsync(o, 0, Monday, Monday, approve: false);
        var team = await _h.GetAsync(o.ManagerUser, $"{Reports}/balance_summary?companyId={o.Company}&leaveYear={Monday.Year}&leaveTypeId={o.Type}", Ct);
        Assert.True(team.Status == HttpStatusCode.OK, team.Body);
        var seen = team.Json.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("employmentId").GetGuid()).Order().ToList();
        Assert.Equal(o.Team.Select(t => t.Employment).Order().ToList(), seen);
        Assert.DoesNotContain(o.Outsider, seen);
        var pending = await _h.GetAsync(o.ManagerUser, $"{Reports}/pending_requests?companyId={o.Company}", Ct);
        Assert.Single(pending.Json.GetProperty("items").EnumerateArray());

        var employee = await _h.GetAsync(o.Team[0].User, $"{Reports}/balance_summary?companyId={o.Company}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, employee.Status);
        var unknown = await _h.GetAsync(o.Admin, $"{Reports}/nothing", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        var order = await _h.GetAsync(o.Admin, $"{Reports}/leave_taken?companyId={o.Company}&from=2025-05-01&to=2025-04-01", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, order.Status);
        Assert.Contains("errors.hrm.leave.report.range_order", order.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_GHR_06_FR_HRM_LVE_10_the_catalogue_names_every_report_with_its_export_type_and_no_column_has_pay()
    {
        var o = await OrgAsync();
        var catalogue = (await _h.GetAsync(o.Admin, Reports, Ct)).Json.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(["balance_summary", "leave_taken", "pending_requests"], catalogue.Select(r => r.GetProperty("code").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(catalogue, r =>
        {
            Assert.StartsWith("hrm.rpt_leave_", r.GetProperty("exportType").GetString(), StringComparison.Ordinal);
            Assert.All(r.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!), c =>
                Assert.DoesNotMatch("(?i)salary|wage|amount|ctc|bonus|allowance|compensation|bank|iban|tax", c));
        });
    }

    [Fact]
    public async Task FR_HRM_LVE_10_FR_HRM_RPT_05_a_report_is_exported_as_the_person_who_asked_and_a_manager_gets_only_the_team()
    {
        var o = await OrgAsync();
        var catalogue = (await _h.GetAsync(o.Admin, Reports, Ct)).Json.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "balance_summary");
        var columns = catalogue.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var query = new { companyId = o.Company, leaveYear = Monday.Year, leaveTypeId = o.Type };
        async Task<string> ExportAsync(Guid user)
        {
            var asked = await _h.PostAsync(user, "/api/itg/exports", new
            {
                source = "hrm.rpt_leave_balance_summary", query, format = "csv", companyId = o.Company, language = "en",
                columns = columns.Select(c => new { field = c, label = c }).ToArray(),
            }, Ct);
            Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
            var id = asked.Json.GetProperty("id").GetGuid();
            await using (var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct))
            {
                await rig.RunUntilIdleAsync(rig.Worker(extra: s => DocumentTestServices.Register(s, _h.FilesRoot)), Ct);
            }

            using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), _h.Token(user));
            using var file = await client.GetAsync(new Uri($"/api/itg/exports/{id}/download", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            return Encoding.UTF8.GetString(await file.Content.ReadAsByteArrayAsync(Ct));
        }

        await _h.AssignAsync(o.Admin, await _h.RoleAsync(Ct, ["itg.exports.run"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        await _h.AssignAsync(o.ManagerUser, await _h.RoleAsync(Ct, ["itg.exports.run"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        var team = (Guid)await _h.ScalarAsync<Guid>("select employment_id from (select $1::uuid as employment_id) x", Ct, o.Team[0].Employment);
        var teamNo = await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, team);
        var outsiderNo = await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, o.Outsider);
        var all = await ExportAsync(o.Admin);
        Assert.Contains(teamNo, all, StringComparison.Ordinal);
        Assert.Contains(outsiderNo, all, StringComparison.Ordinal);
        Assert.DoesNotContain("salary", all, StringComparison.OrdinalIgnoreCase);
        var mine = await ExportAsync(o.ManagerUser);
        Assert.Contains(teamNo, mine, StringComparison.Ordinal);
        Assert.DoesNotContain(outsiderNo, mine, StringComparison.Ordinal);
    }
}
