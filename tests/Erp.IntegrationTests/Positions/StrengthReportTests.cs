#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Positions;

/// <summary>
/// The reports, chart and unit coordination of slice S1.22b through the real API pipeline, the real access engine and a real
/// PostgreSQL: the strength report and its drill and export (FR-HRM-POS-03, GHR-03), the org chart by position with vacant boxes,
/// holders' names, as-of and future dates, filters and exports (FR-HRM-POS-04), and what a unit move or deactivation does to positions
/// and people (FR-HRM-POS-08).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class StrengthReportTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private readonly List<Guid> _companies = [];
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        _h.Clock.Reset();
        if (_companies.Count > 0)
        {
            await _h.ExecAsync("""
                update hrm.outbox_messages set published_at = now()
                where published_at is null and aggregate_id in (select id from hrm.employments where company_id = any($1))
                """, CancellationToken.None, _companies.ToArray());
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync(string model)
    {
        var company = await _h.CompanyAsync(Ct);
        _companies.Add(company);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var set = await _h.PutAsync(admin, "/api/cfg/settings/hrm.org.model", new { layer = "company", scopeId = company, value = model, reason = "test" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        var place = await _h.PlaceAsync(company, Ct);
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        return (company, place);
    }

    private async Task<Guid> PersonAsync(string first, string last)
    {
        var id = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        await _h.ExecAsync("update hrm.persons set first_name = $2, last_name = $3, name_local = $4 where id = $1", Ct, id, first, last, first + " (ur)");
        return id;
    }

    private async Task<Guid> HireAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, string first, Guid? position = null, Guid? grade = null, Guid? unit = null, Guid? manager = null, string? joining = null)
    {
        var made = await _h.PostAsync(user, Employments, new
        {
            personId = await PersonAsync(first, "Tester"),
            companyId = company,
            employeeType = "permanent",
            joiningDate = joining ?? Iso(Today.AddDays(-200)),
            assignment = new { orgUnitId = unit ?? place.OrgUnit, branchId = place.Branch, title = "Officer " + first, positionId = position, gradeId = grade, managerEmploymentId = manager },
        }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return made.Json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> PositionAsync(Guid user, Guid company, Guid unit, string code, decimal fte = 1m, string? from = null, Guid? reportsTo = null, Guid? grade = null)
    {
        var made = await _h.PostAsync(user, "/api/hrm/positions", new { companyId = company, positionCode = code, title = "Title " + code, orgUnitId = unit, budgetedFte = fte, validFrom = from ?? Iso(Today.AddDays(-365)), reportsToPositionId = reportsTo, gradeId = grade }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return made.Json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> GradeAsync(Guid user, Guid company, string code)
    {
        var made = await _h.PostAsync(user, "/api/hrm/grades", new { companyId = company, code, nameI18n = new { en = code }, rank = 1, defaults = new { leaveEntitlementKey = "annual" } }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return made.Json.GetProperty("id").GetGuid();
    }

    private static JsonElement Row(JsonElement report, string unitCode) =>
        report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("unitCode").GetString() == unitCode);

    private async Task<string> UnitCodeAsync(Guid unit) => await _h.ScalarAsync<string>("select code from org.org_units where id = $1", Ct, unit);

    // ------------------------------------------------------------------ strength report

    [Fact]
    public async Task FR_HRM_POS_03_GHR_03_the_strength_report_counts_sanctioned_filled_vacant_over_planned_and_frozen_as_of_a_date_and_drills_into_the_positions()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        var code = await UnitCodeAsync(place.OrgUnit);
        var grade = await GradeAsync(hr, company, "G7");
        var full = await PositionAsync(hr, company, place.OrgUnit, "RP-FULL", 1m, grade: grade);
        var half = await PositionAsync(hr, company, place.OrgUnit, "RP-HALF", 2m, grade: grade);
        var cut = await PositionAsync(hr, company, place.OrgUnit, "RP-CUT", 1m, grade: grade);
        var frozen = await PositionAsync(hr, company, place.OrgUnit, "RP-FROZEN", 1m, grade: grade);
        await PositionAsync(hr, company, place.OrgUnit, "RP-FUTURE", 3m, from: Iso(Today.AddDays(30)), grade: grade);
        await HireAsync(hr, company, place, "Fiona", full, grade);
        await HireAsync(hr, company, place, "Hamza", half, grade);
        await HireAsync(hr, company, place, "Chloe", cut, grade);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, frozen);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(hr, $"/api/hrm/positions/{frozen}/changes", new { effectiveDate = Iso(Today), status = "frozen", rowVersion = version }, Ct)).Status);
        var cutVersion = await _h.ScalarAsync<long>("select row_version from hrm.positions where id = $1", Ct, cut);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(hr, $"/api/hrm/positions/{cut}/changes", new { effectiveDate = Iso(Today.AddDays(-1)), budgetedFte = 0.5m, rowVersion = cutVersion }, Ct)).Status);

        // Today: 1 + 2 + 0.5 + 1 (frozen) = 4.5 sanctioned; filled 1 + 1 + 1; vacant: half has one place, frozen none; over: the cut position by 0.5; the future position is only planned.
        var report = await _h.GetAsync(hr, $"/api/hrm/strength-report?companyId={company}", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        Assert.Equal("position", report.Json.GetProperty("model").GetString());
        var row = Row(report.Json, code);
        Assert.Equal(4.5m, row.GetProperty("sanctioned").GetDecimal());
        Assert.Equal(3m, row.GetProperty("filled").GetDecimal());
        Assert.Equal(1m, row.GetProperty("vacant").GetDecimal());
        Assert.Equal(0.5m, row.GetProperty("over").GetDecimal());
        Assert.Equal(1m, row.GetProperty("frozen").GetDecimal());
        Assert.Equal(3m, row.GetProperty("planned").GetDecimal());
        Assert.Equal("G7", row.GetProperty("gradeCode").GetString());
        Assert.Equal(4.5m, report.Json.GetProperty("totals").GetProperty("sanctioned").GetDecimal());

        // The report for the day the future position starts counts its budget then (3 more sanctioned, 3 vacant) and no longer lists it as planned.
        var later = await _h.GetAsync(hr, $"/api/hrm/strength-report?companyId={company}&asOf={Iso(Today.AddDays(30))}", Ct);
        var laterRow = Row(later.Json, code);
        Assert.Equal(7.5m, laterRow.GetProperty("sanctioned").GetDecimal());
        Assert.Equal(4m, laterRow.GetProperty("vacant").GetDecimal());
        Assert.Equal(0m, laterRow.GetProperty("planned").GetDecimal());

        // Before the people joined everything is vacant and filled is nil; grouping by nothing but the unit merges the grades; an unknown dimension is refused.
        var early = await _h.GetAsync(hr, $"/api/hrm/strength-report?companyId={company}&asOf={Iso(Today.AddDays(-300))}&groupBy=unit", Ct);
        Assert.Equal(0m, Row(early.Json, code).GetProperty("filled").GetDecimal());
        Assert.Equal(5m, Row(early.Json, code).GetProperty("vacant").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(hr, $"/api/hrm/strength-report?companyId={company}&groupBy=colour", Ct)).Status);

        // The drill names the positions of a row, their status and holders.
        var drill = await _h.GetAsync(hr, $"/api/hrm/strength-report/drill?companyId={company}&unitId={place.OrgUnit}&gradeId={grade}", Ct);
        Assert.True(drill.Status == HttpStatusCode.OK, drill.Body);
        var positions = drill.Json.GetProperty("positions").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!, p => p);
        Assert.Equal("filled", positions["RP-FULL"].GetProperty("status").GetString());
        Assert.Equal("open", positions["RP-HALF"].GetProperty("status").GetString());
        Assert.Equal("frozen", positions["RP-FROZEN"].GetProperty("status").GetString());
        Assert.Equal("planned", positions["RP-FUTURE"].GetProperty("status").GetString());
        Assert.Equal(0.5m, positions["RP-CUT"].GetProperty("overFte").GetDecimal());
        Assert.Contains("Fiona", positions["RP-FULL"].GetProperty("holders")[0].GetProperty("name").GetString());

        // The export is a CSV with the same figures; codes that look like formulas are neutralised.
        var export = await _h.GetAsync(hr, $"/api/hrm/strength-report/export?companyId={company}", Ct);
        Assert.Equal(HttpStatusCode.OK, export.Status);
        Assert.StartsWith("unit_code,grade_code,location_code,sanctioned,filled,vacant,over,planned,frozen", export.Body, StringComparison.Ordinal);
        Assert.Contains($"{code},G7,,4.5,3,1,0.5,3,1", export.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_POS_03_in_a_headcount_company_the_report_compares_the_sanctioned_count_with_the_people_of_the_unit_and_grade_and_every_read_needs_the_permission()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("headcount");
        var code = await UnitCodeAsync(place.OrgUnit);
        var analyst = await GradeAsync(hr, company, "ANALYST");
        var budget = await _h.PostAsync(hr, "/api/hrm/headcount-budgets", new { companyId = company, orgUnitId = place.OrgUnit, gradeId = analyst, sanctionedCount = 3, validFrom = Iso(Today.AddDays(-400)) }, Ct);
        Assert.True(budget.Status == HttpStatusCode.Created, budget.Body);
        await HireAsync(hr, company, place, "Amna", grade: analyst);
        await HireAsync(hr, company, place, "Bilal", grade: analyst);

        var report = await _h.GetAsync(hr, $"/api/hrm/strength-report?companyId={company}", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        Assert.Equal("headcount", report.Json.GetProperty("model").GetString());
        var row = Row(report.Json, code);
        Assert.Equal(3m, row.GetProperty("sanctioned").GetDecimal());
        Assert.Equal(2m, row.GetProperty("filled").GetDecimal());
        Assert.Equal(1m, row.GetProperty("vacant").GetDecimal());
        Assert.Equal(0m, row.GetProperty("over").GetDecimal());

        var drill = await _h.GetAsync(hr, $"/api/hrm/strength-report/drill?companyId={company}&unitId={place.OrgUnit}&gradeId={analyst}", Ct);
        Assert.Equal(2, drill.Json.GetProperty("people").GetArrayLength());
        Assert.Contains(drill.Json.GetProperty("people").EnumerateArray(), p => p.GetProperty("name").GetString()!.StartsWith("Amna", StringComparison.Ordinal));

        // Somebody with no HR read permission is refused; a company that does not exist is not found; a missing company is a clear request error.
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"/api/hrm/strength-report?companyId={company}", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"/api/hrm/org-chart?companyId={company}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(hr, $"/api/hrm/strength-report?companyId={Guid.NewGuid()}", Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(hr, "/api/hrm/strength-report", Ct)).Status);
    }

    // ------------------------------------------------------------------ org chart

    [Fact]
    public async Task FR_HRM_POS_04_the_chart_shows_reporting_lines_of_positions_with_holders_and_vacant_boxes_as_of_any_date_filters_and_exports()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("position");
        var boss = await PositionAsync(hr, company, place.OrgUnit, "CH-BOSS");
        var lead = await PositionAsync(hr, company, place.OrgUnit, "CH-LEAD", reportsTo: boss);
        var clerk = await PositionAsync(hr, company, place.OrgUnit, "CH-CLERK", 2m, reportsTo: lead);
        await PositionAsync(hr, company, place.OrgUnit, "CH-NEW", 1m, from: Iso(Today.AddDays(60)), reportsTo: boss);
        var joined = Iso(Today.AddDays(-20));
        await HireAsync(hr, company, place, "Bashir", boss);
        await HireAsync(hr, company, place, "Clara", clerk, joining: joined);
        _ = lead;

        var chart = await _h.GetAsync(hr, $"/api/hrm/org-chart?companyId={company}", Ct);
        Assert.True(chart.Status == HttpStatusCode.OK, chart.Body);
        var nodes = chart.Json.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("code").GetString()!, n => n);
        Assert.Equal(3, nodes.Count); // the future position is not on the chart yet
        Assert.Equal(JsonValueKind.Null, nodes["CH-BOSS"].GetProperty("parentId").ValueKind);
        Assert.Equal("ch-boss", nodes["CH-LEAD"].GetProperty("parentId").GetString());
        Assert.Equal("ch-lead", nodes["CH-CLERK"].GetProperty("parentId").GetString());
        Assert.False(nodes["CH-BOSS"].GetProperty("vacant").GetBoolean());
        Assert.Contains("Bashir", nodes["CH-BOSS"].GetProperty("holders")[0].GetProperty("name").GetString());
        Assert.True(nodes["CH-LEAD"].GetProperty("vacant").GetBoolean()); // nobody holds the lead position
        Assert.True(nodes["CH-CLERK"].GetProperty("vacant").GetBoolean()); // budget 2, one holder
        Assert.Equal(JsonValueKind.Null, nodes["CH-BOSS"].GetProperty("holders")[0].GetProperty("photoFileId").ValueKind);

        // A future date shows the new position under the boss; a date before the second person joined shows the clerk box with no holder.
        var ahead = await _h.GetAsync(hr, $"/api/hrm/org-chart?companyId={company}&asOf={Iso(Today.AddDays(61))}", Ct);
        Assert.Equal(4, ahead.Json.GetProperty("nodes").GetArrayLength());
        var before = await _h.GetAsync(hr, $"/api/hrm/org-chart?companyId={company}&asOf={Iso(Today.AddDays(-30))}", Ct);
        Assert.Empty(before.Json.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("code").GetString() == "CH-CLERK").GetProperty("holders").EnumerateArray());

        // Filtering by a unit that has none of them leaves an empty chart; filtering by their unit keeps all three and their lines.
        var none = await _h.GetAsync(hr, $"/api/hrm/org-chart?companyId={company}&unitId={Guid.NewGuid()}", Ct);
        Assert.Equal(0, none.Json.GetProperty("nodes").GetArrayLength());
        var same = await _h.GetAsync(hr, $"/api/hrm/org-chart?companyId={company}&unitId={place.OrgUnit}", Ct);
        Assert.Equal(3, same.Json.GetProperty("nodes").GetArrayLength());

        // The CSV lists reporting lines; a bad format is refused.
        var csv = await _h.PostAsync(hr, "/api/hrm/org-chart/export", new { companyId = company, format = "csv" }, Ct);
        Assert.Equal(HttpStatusCode.OK, csv.Status);
        Assert.StartsWith("code,title,reports_to_code,unit_code,budgeted_fte,filled_fte,vacant,status,holders", csv.Body, StringComparison.Ordinal);
        Assert.Contains("CH-LEAD,Title CH-LEAD,CH-BOSS,", csv.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(hr, "/api/hrm/org-chart/export", new { companyId = company, format = "png" }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_POS_04_a_headcount_company_chart_follows_the_managers_and_the_pdf_export_prints_the_boxes_in_either_language()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync("headcount");
        var head = await HireAsync(hr, company, place, "Head");
        var staff = await HireAsync(hr, company, place, "Staff", manager: head);

        var chart = await _h.GetAsync(hr, $"/api/hrm/org-chart?companyId={company}", Ct);
        Assert.True(chart.Status == HttpStatusCode.OK, chart.Body);
        var nodes = chart.Json.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(head.ToString(), nodes.Single(n => n.GetProperty("id").GetString() == staff.ToString()).GetProperty("parentId").GetString());
        Assert.All(nodes, n => Assert.False(n.GetProperty("vacant").GetBoolean()));

        foreach (var language in new[] { "en", "ur" })
        {
            var pdf = await _h.PostAsync(hr, "/api/hrm/org-chart/export", new { companyId = company, format = "pdf", language, labels = new { title = "Org chart", asOf = "As of", vacant = "Vacant" } }, Ct);
            Assert.True(pdf.Status == HttpStatusCode.OK, pdf.Body);
            Assert.StartsWith("%PDF", pdf.Body, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ unit coordination

    private async Task<(Guid Hierarchy, Guid Division, Guid Department, Guid Other)> UnitsAsync(Guid company, Guid root)
    {
        var hierarchy = Guid.CreateVersion7();
        var system = Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        await _h.ExecAsync("""
            insert into org.org_hierarchies (id, code, name_i18n, kind, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Reporting"}', 'reporting', now(), $3, now(), $3)
            """, Ct, hierarchy, "SH" + Guid.NewGuid().ToString("N")[..8], system);
        var type = await _h.ScalarAsync<Guid>("select unit_type_id from org.org_units where company_id = $1 limit 1", Ct, company);
        async Task<Guid> Unit(string name, Guid? parent)
        {
            var id = Guid.CreateVersion7();
            await _h.ExecAsync("""
                insert into org.org_units (id, company_id, code, name_i18n, unit_type_id, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4::jsonb, $5, now(), $6, now(), $6)
                """, Ct, id, company, name + "-" + id.ToString("N")[..6], "{\"en\":\"" + name + "\"}", type, system);
            await _h.ExecAsync("""
                insert into org.org_unit_links (id, hierarchy_id, unit_id, parent_unit_id, valid_from, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, '2020-01-01', now(), $5, now(), $5)
                """, Ct, Guid.CreateVersion7(), hierarchy, id, parent, system);
            return id;
        }

        var division = await Unit("DIVISION", null);
        var department = await Unit("DEPARTMENT", division);
        var other = await Unit("OTHER", null);
        _ = root;
        return (hierarchy, division, department, other);
    }

    [Fact]
    public async Task FR_HRM_POS_08_a_unit_move_keeps_its_positions_and_people_raises_the_structure_event_and_a_unit_in_use_cannot_be_switched_off()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var (company, place) = await CompanyAsync("position");
        var (hierarchy, division, department, other) = await UnitsAsync(company, place.OrgUnit);
        var post = await PositionAsync(hr, company, department, "MV-POST");
        await HireAsync(hr, company, place, "Mover", post, unit: department);

        // Moving the department under another division changes only the unit's link: the position and the assignment still name the department.
        var moveDate = Iso(Today.AddDays(5));
        var moved = await _h.PostAsync(admin, $"/api/org/org-units/{department}/move", new { hierarchyId = hierarchy, newParentId = other, effectiveFrom = moveDate, reason = "Reorganisation" }, Ct);
        Assert.Equal(HttpStatusCode.Created, moved.Status);
        Assert.Equal(department, await _h.ScalarAsync<Guid>("select org_unit_id from hrm.positions where id = $1", Ct, post));
        Assert.Equal(1, await _h.ScalarAsync<long>("select count(*) from hrm.employment_assignments a join hrm.employments e on e.id = a.employment_id where a.org_unit_id = $1 and e.company_id = $2", Ct, department, company));
        var before = await _h.GetAsync(admin, $"/api/org/hierarchies/{hierarchy}/tree?asOf={Iso(Today.AddDays(4))}", Ct);
        Assert.Contains(division.ToString(), before.Body, StringComparison.Ordinal);

        // The kernel wrote org.unit_moved; HR's consumer turns it into hrm.org_structure_changed with the posts and people that moved along.
        var kernelEvent = await _h.ScalarAsync<Guid>("select id from iam.outbox_messages where event_type = 'org.unit_moved' and aggregate_id = $1 order by occurred_at desc limit 1", Ct, department);
        await using (var connection = await _h.OwnerAsync(null, Ct))
        {
            await using var tx = await connection.BeginTransactionAsync(Ct);
            var payload = JsonDocument.Parse(await _h.ScalarAsync<string>("select payload::text from iam.outbox_messages where id = $1", Ct, kernelEvent)).RootElement.Clone();
            var consumer = new Erp.Hrm.Infrastructure.Positions.OrgStructureConsumer(_h.Clock);
            Assert.True(consumer.Accepts("org.unit_moved"));
            await consumer.HandleAsync(new Erp.Kernel.Contracts.Jobs.OutboxEvent(kernelEvent, "iam", "org.unit_moved", "org.org_unit", department, _h.Clock.UtcNow, payload, default), connection, tx, Ct);
            await tx.CommitAsync(Ct);
        }

        var body = JsonDocument.Parse(await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.org_structure_changed' and aggregate_id = $1 order by occurred_at desc limit 1", Ct, department)).RootElement;
        Assert.Equal(1, body.GetProperty("positions").GetInt32());
        Assert.Equal(1, body.GetProperty("people").GetInt32());
        Assert.Equal(other, body.GetProperty("newParentId").GetGuid());

        // A unit with a position or a person cannot be switched off; one with nothing can.
        var blocked = await _h.DeleteAsync(admin, $"/api/org/org-units/{department}?rowVersion={await _h.ScalarAsync<long>("select row_version from org.org_units where id = $1", Ct, department)}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Equal("errors.hrm.org_unit.has_positions", blocked.Text("title"));
        Assert.False(await _h.ScalarAsync<bool>("select is_deleted from org.org_units where id = $1", Ct, department));
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(admin, $"/api/org/org-units/{division}?rowVersion={await _h.ScalarAsync<long>("select row_version from org.org_units where id = $1", Ct, division)}", Ct)).Status);
    }
}
