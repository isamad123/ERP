#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Infrastructure.Numbering;

namespace Erp.IntegrationTests.Numbering;

/// <summary>
/// Slice S1.08 through the real API pipeline and the real access engine: the scheme services (create, edit before first
/// use, retire, continuation), the document-type list and the void report, their permissions and roles (Q11), the change
/// log of scheme changes, and numbers given by the generated create endpoint (FR-KRN-NUM-03).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class NumberingApiTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Schemes = "/api/cfg/numbering/schemes";
    private AccessHarness _h = null!;
    private NumberingRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        _rig = await NumberingRig.StartAsync(_h, Ct);
    }

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    private static object NewScheme(Guid? company, string type, string pattern, string reset, bool gapFree = false, long start = 1, Guid? branch = null) =>
        new { companyId = company, branchId = branch, documentType = type, pattern, resetRule = reset, gapFree, startValue = start };

    // ------------------------------------------------------------------ FR-KRN-NUM-03: numbers come from the service, never from the caller

    [Fact]
    public async Task FR_KRN_NUM_03_FR_HRM_EMP_04_creating_an_employment_through_the_generated_api_gives_the_next_employee_number_and_no_update_changes_it()
    {
        var admin = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employments.create", "hrm.employments.update"], [("hrm.employments", "all")]);
        await _h.AssignAsync(admin, role, null, _h.Today.AddDays(-1), null, Ct);
        var company = await _h.CompanyAsync(Ct);
        var numbers = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
            var created = await _h.PostAsync(admin, "/api/hrm/employments", new
            {
                companyId = company, personId = person, employeeNo = "HACK-1", employeeType = "permanent", joiningDate = "2026-01-05",
                serviceStartDate = "2026-01-05", status = "active", isPrimary = true,
            }, Ct);
            Assert.True(created.Status == HttpStatusCode.Created, created.Body);
            numbers.Add(created.Text("employeeNo")!);
        }

        Assert.Equal(["EMP-00001", "EMP-00002"], numbers);

        // The number is read-only: an update that names another number leaves it as it is.
        var row = await _h.ScalarAsync<Guid>("select id from hrm.employments where company_id = $1 and employee_no = 'EMP-00001'", Ct, company);
        var current = await _h.GetAsync(admin, $"/api/hrm/employments/{row}", Ct);
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.Body, JsonSerializerOptions.Web)!;
        body["employeeNo"] = "EMP-99999";
        var updated = await _h.PutAsync(admin, $"/api/hrm/employments/{row}", body, Ct);
        Assert.True(updated.Status == HttpStatusCode.OK, updated.Body);
        Assert.Equal("EMP-00001", updated.Text("employeeNo"));
        Assert.Equal("EMP-00001", await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, row));
    }

    [Fact]
    public async Task FR_KRN_NUM_05_the_generated_create_and_update_endpoints_of_schemes_and_voids_are_closed()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        // No route answers a write: 404 (no endpoint) or 405 (method not allowed), never a created row.
        var voidWrite = await _h.PostAsync(admin, "/api/cfg/numbering-voids", new { schemeId = Guid.CreateVersion7(), number = "X", reason = "y" }, Ct);
        Assert.Contains(voidWrite.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        var schemeWrite = await _h.PostAsync(admin, "/api/cfg/numbering-schemes", NewScheme(company, "hrm.employee_no", "E{seq:3}", "never"), Ct);
        Assert.Contains(schemeWrite.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        var anyScheme = await _h.ScalarAsync<Guid>("select id from cfg.numbering_schemes limit 1", Ct);
        var schemeUpdate = await _h.PutAsync(admin, "/api/cfg/numbering-schemes/" + anyScheme, NewScheme(company, "hrm.employee_no", "E{seq:3}", "never"), Ct);
        Assert.Contains(schemeUpdate.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id = $1", Ct, company));
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(admin, "/api/cfg/numbering-schemes?companyId=" + company, Ct)).Status);
    }

    // ------------------------------------------------------------------ scheme services (FR-KRN-NUM-01, Q8)

    [Fact]
    public async Task FR_KRN_NUM_01_a_scheme_is_editable_until_its_first_number_then_locked_and_retiring_it_lets_the_same_pattern_continue_the_series()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var created = await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.contract_no", "C-{yyyy}-{seq:4}", "yearly", start: 5), Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.False(created.Json.GetProperty("inUse").GetBoolean());

        var edited = await _h.PutAsync(admin, $"{Schemes}/{id}", new { pattern = "CT-{yyyy}-{seq:4}", resetRule = "yearly", gapFree = false, startValue = 7, rowVersion = 1 }, Ct);
        Assert.True(edited.Status == HttpStatusCode.OK, edited.Body);
        Assert.Equal("CT-{yyyy}-{seq:4}", edited.Text("pattern"));

        var date = new DateOnly(2026, 2, 1);
        Assert.Equal("CT-2026-0007", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, date), Ct));
        Assert.Equal("CT-2026-0008", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, date), Ct));

        // After first use: pattern, reset rule, gap-free and start value are locked.
        var locked = await _h.PutAsync(admin, $"{Schemes}/{id}", new { pattern = "CT-{yyyy}-{seq:6}", resetRule = "yearly", gapFree = false, startValue = 7, rowVersion = 2 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, locked.Status);
        Assert.Contains("errors.numbering.scheme_in_use", locked.Body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"{Schemes}/{id}/retire", new { rowVersion = 2 }, Ct)).Status);

        // Same pattern with other settings is refused (P1); with the same settings the series continues, never from the start.
        var mismatch = await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.contract_no", "CT-{yyyy}-{seq:4}", "yearly", start: 1), Ct);
        Assert.Contains("errors.numbering.continuation_mismatch", mismatch.Body, StringComparison.Ordinal);
        var again = await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.contract_no", "CT-{yyyy}-{seq:4}", "yearly", start: 7), Ct);
        Assert.True(again.Status == HttpStatusCode.Created, again.Body);
        Assert.True(again.Json.GetProperty("inUse").GetBoolean());
        Assert.Equal("CT-2026-0009", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", company, null, date), Ct));

        // Scheme changes are in the change log (S1.07).
        var entries = await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'cfg.numbering_schemes' and entity_id = $1 and actor_user_id = $2", Ct, id, admin);
        Assert.Equal(3L, entries); // create, edit, retire
    }

    [Fact]
    public async Task FR_KRN_NUM_01_scheme_rules_refuse_invalid_patterns_branch_schemes_without_the_branch_token_and_a_second_active_scheme()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var (branch, _) = await _rig.BranchAsync(company, Ct);
        async Task<string> Refused(object body)
        {
            var answer = await _h.PostAsync(admin, Schemes, body, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
            return answer.Json.GetProperty("title").GetString()!;
        }

        Assert.Equal("errors.numbering.pattern_year_needed", await Refused(NewScheme(company, "hrm.leave_request_no", "LV-{seq:4}", "yearly")));
        Assert.Equal("errors.numbering.branch_token_needed", await Refused(NewScheme(company, "hrm.leave_request_no", "LV-{yyyy}-{seq:4}", "yearly", branch: branch)));
        Assert.Equal("errors.numbering.branch_not_allowed", await Refused(NewScheme(company, "hrm.employee_no", "E-{branch}-{seq:4}", "never", branch: branch)));
        Assert.Equal("errors.numbering.gap_free_not_allowed", await Refused(NewScheme(company, "hrm.employee_no", "E-{seq:4}", "never", gapFree: true)));
        Assert.Equal("errors.numbering.company_not_allowed", await Refused(NewScheme(company, "hrm.person_no", "P{seq:6}", "never")));
        Assert.Equal("errors.numbering.company_required", await Refused(NewScheme(null, "hrm.employee_no", "E{seq:6}", "never")));
        Assert.Equal("errors.numbering.type_unknown", await Refused(NewScheme(company, "hrm.unknown_no", "E{seq:6}", "never")));
        Assert.Equal("errors.numbering.start_value_invalid", await Refused(NewScheme(company, "hrm.employee_no", "E{seq:6}", "never", start: 0)));

        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.leave_request_no", "LV-{branch}-{yyyy}-{seq:4}", "yearly", branch: branch), Ct)).Status);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.leave_request_no", "LV-{yyyy}-{seq:6}", "yearly", gapFree: true), Ct)).Status);
        Assert.Equal("errors.numbering.active_scheme_exists", await Refused(NewScheme(company, "hrm.leave_request_no", "LV2-{yyyy}-{seq:6}", "yearly")));
    }

    // ------------------------------------------------------------------ document types and void report (Q10, Q11)

    [Fact]
    public async Task FR_KRN_NUM_05_the_void_report_lists_the_issued_range_per_period_and_each_void_with_reason_who_and_when()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var auditor = await _h.UserAsync(Ct, null, "auditor");
        var company = await _h.CompanyAsync(Ct);
        var scheme = await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        for (var i = 0; i < 4; i++)
        {
            await _rig.PostAsync(new NumberRequest("hrm.letter_no", company, null, new DateOnly(2026, 3, 1)), true, Ct);
        }

        await _rig.PostAsync(new NumberRequest("hrm.letter_no", company, null, new DateOnly(2025, 12, 1)), true, Ct);
        await _rig.Voids.RecordAsync(new VoidRequest("hrm.letter_no", company, null, "LTR-2026-00003", "issued in error", null), null, Ct);

        var report = await _h.GetAsync(auditor, $"/api/cfg/numbering/void-report?schemeId={scheme}&from=2026-01-01&to=2026-12-31", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        var issued = report.Json.GetProperty("issued");
        Assert.Equal(1, issued.GetArrayLength());
        Assert.Equal("2026", issued[0].GetProperty("period").GetString());
        Assert.Equal("LTR-2026-00001", issued[0].GetProperty("firstNumber").GetString());
        Assert.Equal("LTR-2026-00004", issued[0].GetProperty("lastNumber").GetString());
        Assert.Equal(4, issued[0].GetProperty("count").GetInt64());
        var voids = report.Json.GetProperty("voids");
        Assert.Equal(1, voids.GetArrayLength());
        Assert.Equal("LTR-2026-00003", voids[0].GetProperty("number").GetString());
        Assert.Equal("issued in error", voids[0].GetProperty("reason").GetString());

        var all = await _h.GetAsync(admin, $"/api/cfg/numbering/void-report?schemeId={scheme}", Ct);
        Assert.Equal(2, all.Json.GetProperty("issued").GetArrayLength());

        // Document types, with whether the company has an active scheme (data for the S1.18 readiness check).
        var types = await _h.GetAsync(admin, "/api/cfg/numbering/document-types?companyId=" + company, Ct);
        Assert.True(types.Status == HttpStatusCode.OK, types.Body);
        var byCode = types.Json.EnumerateArray().ToDictionary(t => t.GetProperty("code").GetString()!);
        Assert.Equal(9, byCode.Count);
        Assert.True(byCode["hrm.letter_no"].GetProperty("hasActiveScheme").GetBoolean());
        Assert.False(byCode["hrm.loan_no"].GetProperty("hasActiveScheme").GetBoolean());
        Assert.True(byCode["hrm.person_no"].GetProperty("tenantWide").GetBoolean());
    }

    [Fact]
    public async Task FR_KRN_NUM_01_permissions_tenant_admin_all_company_admin_own_companies_auditor_read_and_void_report()
    {
        var company = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var scheme = await _rig.SchemeAsync(other, "hrm.loan_no", "LN-{yyyy}-{seq:5}", "yearly", ct: Ct);
        var nobody = await _h.UserAsync(Ct);
        var auditor = await _h.UserAsync(Ct, null, "auditor");
        var companyAdmin = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyAdmin, await _h.RoleIdAsync("company_admin", Ct), company, _h.Today.AddDays(-1), null, Ct);

        var body = NewScheme(company, "hrm.loan_no", "L-{yyyy}-{seq:5}", "yearly");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Schemes, body, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(auditor, Schemes, body, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Schemes}/{scheme}/retire", new { rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, "/api/cfg/numbering/document-types", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"/api/cfg/numbering/void-report?schemeId={scheme}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(auditor, $"/api/cfg/numbering/void-report?schemeId={scheme}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(auditor, "/api/cfg/numbering/document-types", Ct)).Status);

        // Company Admin: its own company only.
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(companyAdmin, Schemes, body, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(companyAdmin, Schemes, NewScheme(other, "hrm.expense_claim_no", "X-{yyyy}-{seq:5}", "yearly"), Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(companyAdmin, $"{Schemes}/{scheme}/retire", new { rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(companyAdmin, $"/api/cfg/numbering/void-report?schemeId={scheme}", Ct)).Status);
    }

    // ------------------------------------------------------------------ defaults at provisioning and migrate (Q7, KRN-TEN-02)

    [Fact]
    public async Task FR_KRN_TEN_02_provisioning_seeds_a_default_scheme_per_company_and_document_type_and_a_second_run_changes_nothing()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        Assert.Equal(8L, await _h.ScalarAsync<long>("select count(distinct document_type) from cfg.numbering_schemes where company_id = $1", Ct, company));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id is null and document_type = 'hrm.person_no'", Ct));
        await using var owner = await _h.OwnerAsync(null, Ct);

        // Settings registered (P3, P4): product default January; a Pakistani company falls back to July from the country data.
        Assert.Equal("1", await _h.ScalarAsync<string>("select default_value::text from cfg.settings where key = 'cfg.numbering.fiscal_year_start_month'", Ct));
        var values = await NumberingSettings.ReadAsync(owner, null, company, Ct);
        Assert.Equal(7, values.FiscalYearStartMonth);
        Assert.Equal("Asia/Karachi", values.TimeZone);

        var newCompany = await _h.CompanyAsync(Ct); // added after provisioning: the next migrate gives it its defaults
        var created = await NumberingSetup.EnsureAsync(owner, null, fx.Migrations.AccessFolders(["hrm"]), _h.Clock.UtcNow, Ct);
        Assert.True(created >= 8);
        Assert.Equal(8L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id = $1", Ct, newCompany));
        Assert.Equal(0, await NumberingSetup.EnsureAsync(owner, null, fx.Migrations.AccessFolders(["hrm"]), _h.Clock.UtcNow, Ct));
    }
}
