#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.IntegrationTests.Numbering;

/// <summary>
/// The S1.08 review findings (<c>docs/reviews/S1.08-review.md</c>, CR-058), each reproduced as it was found and shown
/// refused now: two series printing the same number (finding 1), numbers taken outside the caller's data scope (2), a
/// number voided twice (3), the void report of an older company-wide scheme with <c>{branch}</c> (4), allocator
/// robustness (5) and the scheme service answering about another company (6).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class NumberingReviewFixTests(TenancyFixture fx) : IAsyncLifetime
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

    private async Task<string?> RefusedAsync(Guid user, object body)
    {
        var answer = await _h.PostAsync(user, Schemes, body, Ct);
        Assert.True(answer.Status == HttpStatusCode.BadRequest, answer.Body);
        return answer.Text("title");
    }

    private async Task<Guid> BranchAsync(Guid company, string code)
    {
        var id = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into org.branches (id, company_id, code, name_i18n, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, '{"en":"Test branch"}', now(), $4, now(), $4)
            """, Ct, id, company, code, BaselineSeed.SystemUserId);
        return id;
    }

    // ------------------------------------------------------------------ finding 1: no two series print the same number

    [Fact]
    public async Task FR_XM_06_FR_KRN_NUM_01_a_company_wide_scheme_cannot_print_branch_codes_so_a_branch_series_never_repeats_it()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var (branch, code) = await _rig.BranchAsync(company, Ct);

        // Found: company scheme SR-{branch}-{seq:5} printed SR-<code>-00001, and a branch scheme of the same shape printed it again.
        Assert.Equal("errors.numbering.pattern_branch_company_scheme", await RefusedAsync(admin, NewScheme(company, "hrm.service_request_no", "SR-{branch}-{seq:5}", "never")));
        var own = await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.service_request_no", "SR-{branch}-{seq:5}", "never", branch: branch), Ct);
        Assert.True(own.Status == HttpStatusCode.Created, own.Body);
        Assert.Equal($"SR-{code}-00001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.service_request_no", company, branch, null), Ct));
    }

    [Fact]
    public async Task FR_XM_06_FR_KRN_NUM_01_a_branch_code_must_be_followed_by_a_separator_so_codes_K_and_K1_never_print_the_same_number()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var k = await BranchAsync(company, "K");
        var k1 = await BranchAsync(company, "K1");

        // Found: SR{branch}{seq:3} gave SRK1001 for branch K1 (value 1) and for branch K (value 1001).
        Assert.Equal("errors.numbering.pattern_code_separator", await RefusedAsync(admin, NewScheme(company, "hrm.service_request_no", "SR{branch}{seq:3}", "never", branch: k1)));
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.service_request_no", "SR{branch}-{seq:3}", "never", branch: k1), Ct)).Status);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.service_request_no", "SR{branch}-{seq:3}", "never", start: 1001, branch: k), Ct)).Status);
        var a = await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.service_request_no", company, k1, null), Ct);
        var b = await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.service_request_no", company, k, null), Ct);
        Assert.Equal(("SRK1-001", "SRK-1001"), (a, b));
    }

    [Fact]
    public async Task FR_XM_06_FR_KRN_NUM_01_a_new_series_that_differs_from_an_earlier_one_only_in_padding_is_refused()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var old = await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.employee_no", "EMP-{seq:5}", "never"), Ct);
        Assert.True(old.Status == HttpStatusCode.Created, old.Body);
        Assert.Equal("EMP-00001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, null), Ct));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"{Schemes}/{old.Text("id")}/retire", new { rowVersion = old.Json.GetProperty("rowVersion").GetInt64() }, Ct)).Status);

        // Found: EMP-0{seq:4} after EMP-{seq:5} printed EMP-00001 again.
        Assert.Equal("errors.numbering.pattern_too_close", await RefusedAsync(admin, NewScheme(company, "hrm.employee_no", "EMP-0{seq:4}", "never")));
        Assert.Equal("errors.numbering.pattern_too_close", await RefusedAsync(admin, NewScheme(company, "hrm.employee_no", "EMP-{seq:6}", "never")));

        // A new text is a new series; the same pattern continues the old one (Q8).
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, Schemes, NewScheme(company, "hrm.employee_no", "E-{seq:5}", "never"), Ct)).Status);
        Assert.Equal("E-00001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, null), Ct));
    }

    [Fact]
    public async Task FR_XM_06_FR_HRM_EMP_09_a_repeated_contract_number_is_refused_by_the_database()
    {
        var company = await _h.CompanyAsync(Ct);
        var overrides = new Dictionary<string, object?> { ["contract_no"] = "CON-2026-00001" };
        await _h.RowAsync("hrm.contracts", company, overrides, Ct);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _h.RowAsync("hrm.contracts", company, overrides, Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("uq_contracts_company_id_contract_no", ex.ConstraintName);

        // Another company may use the same text (numbers are per company).
        await _h.RowAsync("hrm.contracts", await _h.CompanyAsync(Ct), overrides, Ct);
    }

    // ------------------------------------------------------------------ finding 2: no number outside the caller's data scope

    [Fact]
    public async Task FR_KRN_NUM_03_the_generated_create_takes_no_number_and_creates_no_scheme_in_a_company_outside_the_callers_scope()
    {
        var mine = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var withScheme = await _h.CompanyAsync(Ct);
        var scheme = await _rig.SchemeAsync(withScheme, "hrm.employee_no", "EMP-{seq:5}", "never", ct: Ct);
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employments.create"], [("hrm.employments", "all")]);
        await _h.AssignAsync(user, role, mine, _h.Today.AddDays(-1), null, Ct);

        foreach (var company in new[] { other, withScheme })
        {
            var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
            var answer = await _h.PostAsync(user, "/api/hrm/employments", new
            {
                companyId = company, personId = person, employeeType = "permanent", joiningDate = "2026-01-05",
                serviceStartDate = "2026-01-05", status = "active", isPrimary = true,
            }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        }

        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id = $1", Ct, other));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_series_state($1)", Ct, scheme));
        Assert.Equal("EMP-00001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", withScheme, null, null), Ct));
    }

    // ------------------------------------------------------------------ finding 3: one void per number, also across a continued series

    [Fact]
    public async Task FR_KRN_NUM_05_FR_XM_06_a_number_voided_once_cannot_be_voided_again_after_the_series_continued_under_a_new_scheme()
    {
        var company = await _h.CompanyAsync(Ct);
        var first = await _rig.SchemeAsync(company, "hrm.loan_no", "LN-{yyyy}-{seq:5}", "yearly", ct: Ct);
        var number = await _rig.PostAsync(new NumberRequest("hrm.loan_no", company, null, new DateOnly(2026, 3, 1)), true, Ct);
        await _rig.Voids.RecordAsync(new VoidRequest("hrm.loan_no", company, null, number, "cancelled", Guid.CreateVersion7()), null, Ct);
        await _h.ExecAsync("update cfg.numbering_schemes set status = 'retired', row_version = row_version + 1 where id = $1", Ct, first);
        await _rig.SchemeAsync(company, "hrm.loan_no", "LN-{yyyy}-{seq:5}", "yearly", ct: Ct);

        Assert.Equal("errors.numbering.already_voided",
            (await Assert.ThrowsAsync<NumberingException>(() => _rig.Voids.RecordAsync(new VoidRequest("hrm.loan_no", company, null, number, "cancelled again", null), null, Ct))).ErrorKey);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_voids where number = $1 and scheme_id = $2", Ct, number, first));
    }

    // ------------------------------------------------------------------ finding 4: the void report always answers

    [Fact]
    public async Task FR_KRN_NUM_05_the_void_report_of_a_company_wide_scheme_with_branch_made_before_CR_058_still_answers()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var (branch, code) = await _rig.BranchAsync(company, Ct);
        var scheme = await _rig.SchemeAsync(company, "hrm.service_request_no", "SR-{branch}-{seq:5}", "never", ct: Ct); // written directly, as before CR-058
        Assert.Equal($"SR-{code}-00001", await _rig.Allocator.AssignOnCreateAsync(new NumberRequest("hrm.service_request_no", company, branch, null), Ct));

        var report = await _h.GetAsync(admin, $"/api/cfg/numbering/void-report?schemeId={scheme}", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        var issued = report.Json.GetProperty("issued")[0];
        Assert.Equal("1", issued.GetProperty("firstNumber").GetString());
        Assert.Equal("1", issued.GetProperty("lastNumber").GetString());
    }

    // ------------------------------------------------------------------ finding 5: allocator robustness

    [Fact]
    public async Task FR_KRN_NUM_02_a_gap_free_number_that_cannot_be_printed_gives_its_value_back_even_if_the_caller_commits()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.letter_no", "ABCDEFGHIJKLMNOPQRSTUVWXYZ-ABCDE-{seq:5}", "never", gapFree: true, start: 99_999_999, ct: Ct);
        await using (var c = await _rig.App.OpenConnectionAsync(Ct))
        await using (var tx = await c.BeginTransactionAsync(Ct))
        {
            var ex = await Assert.ThrowsAsync<NumberingException>(() => _rig.Allocator.AssignAsync(new NumberRequest("hrm.letter_no", company, null, null), tx, Ct));
            Assert.Equal("errors.numbering.number_too_long", ex.ErrorKey);
            await tx.CommitAsync(Ct); // the transaction is still usable, and the counter did not move
        }

        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_counters c join cfg.numbering_schemes s on s.id = c.scheme_id where s.company_id = $1", Ct, company));
    }

    [Fact]
    public async Task FR_KRN_NUM_02_a_gap_free_number_needs_a_read_committed_transaction()
    {
        var company = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(company, "hrm.letter_no", "LTR-{yyyy}-{seq:5}", "yearly", gapFree: true, ct: Ct);
        await using var c = await _rig.App.OpenConnectionAsync(Ct);
        await using (var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, Ct))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _rig.Allocator.AssignAsync(new NumberRequest("hrm.letter_no", company, null, new DateOnly(2026, 3, 1)), tx, Ct));
            await tx.RollbackAsync(Ct);
        }

        Assert.Equal("LTR-2026-00001", await _rig.PostAsync(new NumberRequest("hrm.letter_no", company, null, new DateOnly(2026, 3, 1)), true, Ct));
    }

    // ------------------------------------------------------------------ finding 6: the scheme service answers nothing about another company

    [Fact]
    public async Task FR_KRN_NUM_01_a_company_admin_naming_another_company_is_refused_before_anything_about_it_is_read()
    {
        var company = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        await _rig.SchemeAsync(other, "hrm.loan_no", "LN-{yyyy}-{seq:5}", "yearly", ct: Ct);
        var companyAdmin = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyAdmin, await _h.RoleIdAsync("company_admin", Ct), company, _h.Today.AddDays(-1), null, Ct);

        // Found: 400 active_scheme_exists / branch_unknown told the caller about the other company's schemes and branches.
        foreach (var body in new[]
        {
            NewScheme(other, "hrm.loan_no", "LN-{yyyy}-{seq:5}", "yearly"),
            NewScheme(other, "hrm.service_request_no", "SR-{branch}-{seq:5}", "never", branch: Guid.CreateVersion7()),
            NewScheme(null, "hrm.person_no", "P-{seq:6}", "never"),
        })
        {
            var answer = await _h.PostAsync(companyAdmin, Schemes, body, Ct);
            Assert.True(answer.Status == HttpStatusCode.Forbidden, answer.Body);
        }
    }
}
