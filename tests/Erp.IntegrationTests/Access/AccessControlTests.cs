#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// Slice S1.06 acceptance through the real API pipeline and the real access engine: permissions, assignment dates and
/// companies, data scopes, field security, "cannot grant what you do not hold", the permission registry and baseline
/// roles, and the privileged-role identity policy. Maker-checker and the last tenant admin: <see cref="AccessGovernanceTests"/>.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AccessControlTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ permissions and assignments

    [Fact]
    public async Task FR_KRN_IAM_10_FR_KRN_IAM_11_FR_ADM_ROL_04_an_endpoint_answers_403_without_the_permission_and_200_with_it_until_the_assignment_ends()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var user = await _h.UserAsync(Ct);
        var path = $"/api/org/companies/{company}";
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, path, Ct)).Status);

        var role = await _h.RoleAsync(Ct, ["org.companies.read"], [("org.companies", "all")]);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var assigned = await _h.PostAsync(admin, "/api/iam/user-roles", new { userId = user, roleId = role, reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.Created, assigned.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user, path, Ct)).Status); // applies on the very next call (Q9)

        // Ending the assignment today removes access on the next call.
        var assignment = assigned.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/iam/user-roles/{assignment}/end", new { reason = "left the team" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, path, Ct)).Status);

        // An assignment with an end date stops counting on that date (injected clock).
        await _h.AssignAsync(user, role, null, _h.Today, _h.Today.AddDays(3), Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user, path, Ct)).Status);
        _h.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, path, Ct)).Status);
    }

    [Fact]
    public async Task FR_ADM_ROL_04_ruling_A_a_role_assigned_for_company_A_grants_nothing_in_company_B()
    {
        var companyA = await _h.DefaultCompanyAsync(Ct);
        var companyB = await _h.CompanyAsync(Ct);
        var branchA = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted limit 1", Ct, companyA);
        var branchB = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted limit 1", Ct, companyB);
        var role = await _h.RoleAsync(Ct, ["org.branches.read", "org.branches.create"], [("org.branches", "all")]);
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, role, companyA, _h.Today, null, Ct);

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user, $"/api/org/branches/{branchA}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"/api/org/branches/{branchB}", Ct)).Status);
        var list = await _h.GetAsync(user, $"/api/org/branches?companyId={companyB}", Ct);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Empty(list.Json.GetProperty("items").EnumerateArray());

        // Writes: a branch for company B is outside the scope of a company-A role; one for company A is allowed.
        var refused = await _h.PostAsync(user, "/api/org/branches", new { companyId = companyB, code = "B" + Guid.NewGuid().ToString("N")[..6], nameI18n = new { en = "B" } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.access.out_of_scope", refused.Text("title"));
        var allowed = await _h.PostAsync(user, "/api/org/branches", new { companyId = companyA, code = "A" + Guid.NewGuid().ToString("N")[..6], nameI18n = new { en = "A" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, allowed.Status);
    }

    // ------------------------------------------------------------------ data scopes (FR-KRN-IAM-12)

    private sealed record Workforce(Guid Me, Guid CompanyB, Guid BranchB, Guid UnitOther, Guid EMe, Guid ERep, Guid ESub, Guid EOther, Guid EB);

    /// <summary>
    /// Me (employment EMe in unit U1, branch 1) manage ERep (unit U2 under U1, branch 2); ESub is in U2 with another
    /// manager; EOther is in an unrelated unit U3; EB belongs to another company.
    /// </summary>
    private async Task<Workforce> WorkforceAsync(Guid me)
    {
        var companyA = await _h.DefaultCompanyAsync(Ct);
        var companyB = await _h.CompanyAsync(Ct);
        var branch1 = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted order by created_at limit 1", Ct, companyA);
        var branch2 = (Guid)(await _h.RowAsync("org.branches", companyA, null, Ct))["id"]!;
        var hierarchy = (Guid)(await _h.RowAsync("org.org_hierarchies", companyA, new Dictionary<string, object?> { ["kind"] = "reporting" }, Ct))["id"]!;
        var u1 = (Guid)(await _h.RowAsync("org.org_units", companyA, null, Ct))["id"]!;
        var u2 = (Guid)(await _h.RowAsync("org.org_units", companyA, null, Ct))["id"]!;
        var u3 = (Guid)(await _h.RowAsync("org.org_units", companyA, null, Ct))["id"]!;
        await _h.RowAsync("org.org_unit_links", companyA, new Dictionary<string, object?> { ["hierarchy_id"] = hierarchy, ["unit_id"] = u2, ["parent_unit_id"] = u1, ["valid_from"] = new DateOnly(2026, 1, 1), ["valid_to"] = null }, Ct);

        async Task<Guid> Employment(Guid company, Guid? user)
        {
            var row = await _h.RowAsync("hrm.employments", company, new Dictionary<string, object?> { ["user_ref"] = user, ["status"] = "active", ["is_primary"] = false }, Ct);
            return (Guid)row["id"]!;
        }

        async Task Assignment(Guid employment, Guid unit, Guid branch, Guid? manager) =>
            await _h.RowAsync("hrm.employment_assignments", companyA, new Dictionary<string, object?>
            {
                ["employment_id"] = employment, ["org_unit_id"] = unit, ["branch_id"] = branch, ["manager_employment_id"] = manager,
                ["valid_from"] = new DateOnly(2026, 1, 1), ["valid_to"] = null,
            }, Ct);

        var eMe = await Employment(companyA, me);
        var eRep = await Employment(companyA, null);
        var eSub = await Employment(companyA, null);
        var eOther = await Employment(companyA, null);
        var eB = await Employment(companyB, null);
        await Assignment(eMe, u1, branch1, null);
        await Assignment(eRep, u2, branch2, eMe);
        await Assignment(eSub, u2, branch1, null);
        await Assignment(eOther, u3, branch1, null);
        return new Workforce(me, companyB, branch2, u3, eMe, eRep, eSub, eOther, eB);
    }

    private async Task<HashSet<Guid>> VisibleAsync(Guid user, Workforce w)
    {
        var visible = new HashSet<Guid>();
        foreach (var id in new[] { w.EMe, w.ERep, w.ESub, w.EOther, w.EB })
        {
            var answer = await _h.GetAsync(user, $"/api/hrm/employments/{id}", Ct);
            Assert.True(answer.Status is HttpStatusCode.OK or HttpStatusCode.NotFound, $"{(int)answer.Status} {answer.Body}");
            if (answer.Status == HttpStatusCode.OK)
            {
                visible.Add(id);
            }
        }

        return visible;
    }

    [Fact]
    public async Task FR_KRN_IAM_12_FR_ADM_ROL_06_each_scope_kind_returns_exactly_the_expected_rows_and_no_scope_returns_none()
    {
        var me = await _h.UserAsync(Ct);
        var w = await WorkforceAsync(me);
        var cases = new (string Name, object? Scope, Guid[] Expected)[]
        {
            ("own", "own", [w.EMe]),
            ("direct_team", "direct_team", [w.ERep]),
            ("subtree", "subtree", [w.EMe, w.ERep, w.ESub]),
            ("org_units", await _h.ScopeAsync("org_units", new { org_unit_ids = new[] { w.UnitOther } }, Ct), [w.EOther]),
            ("branches", await _h.ScopeAsync("branches", new { branch_ids = new[] { w.BranchB } }, Ct), [w.ERep]),
            ("companies", await _h.ScopeAsync("companies", new { company_ids = new[] { w.CompanyB } }, Ct), [w.EB]),
            ("all", "all", [w.EMe, w.ERep, w.ESub, w.EOther, w.EB]),
            ("attribute condition", await _h.ScopeAsync("all", new { conditions = new[] { new { attribute = "status", @in = new[] { "on_notice" } } } }, Ct), []),
            ("no scope (deny by default)", null, []),
        };
        foreach (var (name, scope, expected) in cases)
        {
            var role = await _h.RoleAsync(Ct, ["hrm.employments.read"], scope is null ? [] : [("hrm.employments", scope)]);
            var assignment = await _h.AssignAsync(me, role, null, _h.Today, null, Ct);
            Assert.True(expected.ToHashSet().SetEquals(await VisibleAsync(me, w)), $"scope {name}");
            await _h.ExecAsync("delete from iam.user_roles where id = $1", Ct, assignment);
        }

        // Attribute conditions match: the same condition on the value the rows have.
        var active = await _h.ScopeAsync("all", new { conditions = new[] { new { attribute = "status", equals = "active" } } }, Ct);
        var byStatus = await _h.RoleAsync(Ct, ["hrm.employments.read"], [("hrm.employments", active)]);
        await _h.AssignAsync(me, byStatus, null, _h.Today, null, Ct);
        Assert.True(new[] { w.EMe, w.ERep, w.ESub, w.EOther, w.EB }.ToHashSet().SetEquals(await VisibleAsync(me, w)));

        // Several roles: the rows of any of them (Q4).
        await _h.ExecAsync("delete from iam.user_roles where user_id = $1", Ct, me);
        await _h.AssignAsync(me, await _h.RoleAsync(Ct, ["hrm.employments.read"], [("hrm.employments", "own")]), null, _h.Today, null, Ct);
        await _h.AssignAsync(me, await _h.RoleAsync(Ct, ["hrm.employments.read"], [("hrm.employments", "direct_team")]), null, _h.Today, null, Ct);
        Assert.True(new[] { w.EMe, w.ERep }.ToHashSet().SetEquals(await VisibleAsync(me, w)));
    }

    [Fact]
    public async Task Spec02_AC_GHR_06_a_line_manager_sees_only_the_team_without_salary_and_a_payroll_officer_sees_salary_of_assigned_companies_only()
    {
        var manager = await _h.UserAsync(Ct, null, "line_manager", "employee");
        var w = await WorkforceAsync(manager);
        var companyA = await _h.DefaultCompanyAsync(Ct);

        // Line manager (+ employee): own and team employment records only.
        Assert.True(new[] { w.EMe, w.ERep }.ToHashSet().SetEquals(await VisibleAsync(manager, w)));

        // Salary rows: one in company A (the report), one in company B.
        var salaryA = await _h.RowAsync("hrm.compensation_assignments", companyA, new Dictionary<string, object?> { ["employment_id"] = w.ERep, ["gross_amount"] = 150000.0000m }, Ct);
        var salaryB = await _h.RowAsync("hrm.compensation_assignments", w.CompanyB, new Dictionary<string, object?> { ["employment_id"] = w.EB, ["gross_amount"] = 99000.0000m }, Ct);
        var pathA = $"/api/hrm/compensation-assignments/{salaryA["id"]}";
        var pathB = $"/api/hrm/compensation-assignments/{salaryB["id"]}";

        // The line manager has no salary permission at all (spec 06a 28.1 "View salary data: No").
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(manager, pathA, Ct)).Status);

        // A role that may read the rows but has no salary rule still never sees the amount (GHR-06: hidden unless granted).
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.compensation_assignments.read"], [("*", "all")]), null, _h.Today, null, Ct);
        var hidden = await _h.GetAsync(reader, pathA, Ct);
        Assert.Equal(HttpStatusCode.OK, hidden.Status);
        Assert.False(hidden.Json.TryGetProperty("grossAmount", out _), hidden.Body);

        // Payroll officer assigned for company A only: salary visible in A, company B's row not found.
        var officer = await _h.UserAsync(Ct);
        await _h.AssignAsync(officer, await _h.RoleIdAsync("payroll_officer", Ct), companyA, _h.Today, null, Ct);
        var seen = await _h.GetAsync(officer, pathA, Ct);
        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.Equal(150000m, seen.Json.GetProperty("grossAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(officer, pathB, Ct)).Status);
    }

    // ------------------------------------------------------------------ field security (FR-KRN-IAM-13, FR-KRN-MET-02)

    [Fact]
    public async Task FR_KRN_IAM_13_hidden_fields_are_absent_masked_fields_are_masked_and_a_write_to_a_read_only_field_is_refused()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var legalName = await _h.ScalarAsync<string>("select legal_name from org.companies where id = $1", Ct, company);
        var path = $"/api/org/companies/{company}";

        async Task<Guid> UserWith(string access)
        {
            var user = await _h.UserAsync(Ct);
            await _h.AssignAsync(user, await _h.RoleAsync(Ct, ["org.companies.read", "org.companies.update"], [("org.companies", "all")], [("org.companies", "legal_name", access)]), null, _h.Today, null, Ct);
            return user;
        }

        var hiddenUser = await UserWith("hidden");
        var hidden = await _h.GetAsync(hiddenUser, path, Ct);
        Assert.Equal(HttpStatusCode.OK, hidden.Status);
        Assert.False(hidden.Json.TryGetProperty("legalName", out _), hidden.Body);
        Assert.True(hidden.Json.TryGetProperty("code", out _));

        var masked = await _h.GetAsync(await UserWith("masked"), path, Ct);
        Assert.Equal("****" + legalName[^4..], masked.Text("legalName"));

        // Read-only: sending the stored value back is fine, a different value is refused with a field-level error.
        var readOnlyUser = await UserWith("read_only");
        var current = JsonNode.Parse((await _h.GetAsync(readOnlyUser, path, Ct)).Body)!.AsObject();
        Assert.Equal(legalName, (string?)current["legalName"]);
        var same = await _h.PutAsync(readOnlyUser, path, current.ToJsonString(), Ct);
        Assert.Equal(HttpStatusCode.OK, same.Status);
        var changed = JsonNode.Parse(same.Body)!.AsObject();
        changed["legalName"] = "Someone Else Ltd";
        var refused = await _h.PutAsync(readOnlyUser, path, changed.ToJsonString(), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.access.field_not_editable", refused.Text("title"));
        Assert.True(refused.Json.GetProperty("errors").TryGetProperty("legalName", out _), refused.Body);
        Assert.Equal(legalName, await _h.ScalarAsync<string>("select legal_name from org.companies where id = $1", Ct, company));

        // Several roles: the most open access wins (Q4).
        var both = await _h.UserAsync(Ct);
        await _h.AssignAsync(both, await _h.RoleAsync(Ct, ["org.companies.read"], [("org.companies", "all")], [("org.companies", "legal_name", "hidden")]), null, _h.Today, null, Ct);
        await _h.AssignAsync(both, await _h.RoleAsync(Ct, ["org.companies.read"], [("org.companies", "all")], [("org.companies", "legal_name", "masked")]), null, _h.Today, null, Ct);
        Assert.Equal("****" + legalName[^4..], (await _h.GetAsync(both, path, Ct)).Text("legalName"));
    }

    [Fact]
    public async Task FR_KRN_MET_02_custom_fields_follow_their_own_rules_on_read_and_write()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var unit = await _h.RowAsync("org.org_units", company, null, Ct);
        var id = (Guid)unit["id"]!;
        await _h.ExecAsync("update org.org_units set custom = '{\"secret\":\"s1\",\"note\":\"n1\",\"free\":\"f1\"}' where id = $1", Ct, id);
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, await _h.RoleAsync(Ct, ["org.org_units.read", "org.org_units.update"], [("org.org_units", "all")],
            [("org.org_units", "custom.secret", "hidden"), ("org.org_units", "custom.note", "read_only")]), null, _h.Today, null, Ct);

        var read = JsonNode.Parse((await _h.GetAsync(user, $"/api/org/org-units/{id}", Ct)).Body)!.AsObject();
        var custom = read["custom"]!.AsObject();
        Assert.False(custom.ContainsKey("secret"));
        Assert.Equal("n1", (string?)custom["note"]);

        read["custom"] = new JsonObject { ["note"] = "changed", ["free"] = "f2" };
        var refused = await _h.PutAsync(user, $"/api/org/org-units/{id}", read.ToJsonString(), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.True(refused.Json.GetProperty("errors").TryGetProperty("custom.note", out _), refused.Body);

        read["custom"] = new JsonObject { ["note"] = "n1", ["free"] = "f2" };
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(user, $"/api/org/org-units/{id}", read.ToJsonString(), Ct)).Status);
        using var stored = JsonDocument.Parse(await _h.ScalarAsync<string>("select custom::text from org.org_units where id = $1", Ct, id));
        Assert.Equal("s1", stored.RootElement.GetProperty("secret").GetString()); // the hidden value was kept, never dropped
        Assert.Equal("n1", stored.RootElement.GetProperty("note").GetString());
        Assert.Equal("f2", stored.RootElement.GetProperty("free").GetString());
    }

    // ------------------------------------------------------------------ granting (spec 02 BR)

    [Fact]
    public async Task Spec02_BR_FR_KRN_IAM_11_a_user_cannot_add_to_a_role_or_assign_a_permission_they_do_not_hold()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var actor = await _h.UserAsync(Ct);
        await _h.AssignAsync(actor, await _h.RoleAsync(Ct, ["iam.roles.manage", "iam.roles.assign", "org.companies.read"], [("*", "all")]), company, _h.Today, null, Ct);
        var target = await _h.RoleAsync(Ct, []);

        var refused = await _h.PutAsync(actor, $"/api/iam/roles/{target}/permissions", new { permissions = new[] { "org.branches.read" }, reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.access.cannot_grant_unheld", refused.Text("title"));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(actor, $"/api/iam/roles/{target}/permissions", new { permissions = new[] { "org.companies.read" }, reason = "test" }, Ct)).Status);

        var other = await _h.UserAsync(Ct);
        var bigger = await _h.RoleAsync(Ct, ["org.branches.read"]);
        var assign = await _h.PostAsync(actor, "/api/iam/user-roles", new { userId = other, roleId = bigger, companyId = company, reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, assign.Status);
        Assert.Equal("errors.access.cannot_grant_unheld", assign.Text("title"));

        // The actor gives roles for company A only: an all-companies assignment is not theirs to give (ruling A).
        var allCompanies = await _h.PostAsync(actor, "/api/iam/user-roles", new { userId = other, roleId = target, reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, allCompanies.Status);
        Assert.Equal("errors.access.company_not_covered", allCompanies.Text("title"));
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(actor, "/api/iam/user-roles", new { userId = other, roleId = target, companyId = company, reason = "test" }, Ct)).Status);

        // Field rules cannot open a field wider than the caller's own access (salary is hidden for the actor).
        var field = await _h.PutAsync(actor, $"/api/iam/roles/{target}/field-policies", new { fields = new[] { new { resource = "hrm.compensation_assignments", field = "gross_amount", access = "read_only" } }, reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, field.Status);
    }

    [Fact]
    public async Task CR_020_CR_021_the_generated_write_endpoints_of_the_access_tables_are_closed()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        foreach (var route in new[] { "/api/iam/permissions", "/api/iam/role-permissions", "/api/iam/maker-checker-requests", "/api/iam/field-policies", "/api/iam/user-group-roles" })
        {
            var answer = await _h.PostAsync(admin, route, new { code = "x" }, Ct);
            Assert.True(answer.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, $"{route}: {(int)answer.Status}");
        }

        var user = await _h.ScalarAsync<Guid>("select id from iam.users where not is_system order by created_at limit 1", Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _h.SendDeleteAsync(admin, $"/api/iam/users/{user}?rowVersion=1")).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _h.PutAsync(admin, $"/api/iam/user-roles/{Guid.NewGuid()}", new { }, Ct)).Status);
    }

    // ------------------------------------------------------------------ registry and baseline roles

    [Fact]
    public async Task FR_KRN_IAM_10_FR_ADM_ROL_01_FR_ADM_ROL_03_registry_and_baseline_roles_are_seeded_once_and_a_customers_edit_survives_migrate()
    {
        await using var owner = await _h.OwnerAsync(null, Ct);
        var folders = fx.Migrations.AccessFolders(["hrm"]);
        var again = await AccessSetup.EnsureAsync(owner, null, folders, _h.Clock.UtcNow, Ct);
        Assert.Equal((0, 0, 0, 0), (again.PermissionsAdded, again.PermissionsRetired, again.RolesAdded, again.GrantsAdded));
        Assert.True(await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.permissions where retired_at is null", Ct) > 500);
        foreach (var code in new[] { "iam.self.manage", "iam.roles.manage", "hrm.employments.read", "org.companies.read" })
        {
            Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.permissions where code = $1", Ct, code));
        }

        // Every baseline role holds iam.self.manage; the System Administrator holds no business-data permission (HRM-ROL-06).
        Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(owner, """
            select count(*) from iam.roles r where r.source = 'module' and not exists (
                select 1 from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id where rp.role_id = r.id and p.code = 'iam.self.manage')
            """, Ct));
        Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(owner, """
            select count(*) from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id join iam.roles r on r.id = rp.role_id
            where r.code = 'system_admin' and (p.module_id in ('hrm', 'org') or p.code like 'iam.users.%')
            """, Ct));
        Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(owner, """
            select count(*) from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id join iam.roles r on r.id = rp.role_id
            where r.code = 'tenant_admin' and p.module_id = 'hrm'
            """, Ct)); // Q2: Tenant Admin gets no HR salary permission (no HR permission at all in this slice)

        // The customer edits a baseline role; a new shipped permission then reaches untouched roles only (Q8).
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var business = await _h.RoleIdAsync("business_user", Ct);
        var summary = (await _h.GetAsync(admin, $"/api/iam/roles/{business}/summary", Ct)).Json;
        var kept = summary.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Where(p => p != "ntf.in_app_items.update").ToArray();
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/iam/roles/{business}/permissions", new { permissions = kept, reason = "no updates" }, Ct)).Status);

        var extra = Directory.CreateTempSubdirectory("erp-s106-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(extra, "permissions.g.json"), """{"permissions":[{"code":"test.extra.read","module":"test","descriptionKey":"permissions.test.extra.read","sensitive":false}]}""", Ct);
            await File.WriteAllTextAsync(Path.Combine(extra, "baseline-roles.json"), """{"roles":[{"code":"auditor","extends":true,"permissions":["test.extra.read","ntf.in_app_items.update"]},{"code":"business_user","extends":true,"permissions":["test.extra.read"]}]}""", Ct);
            var upgrade = await AccessSetup.EnsureAsync(owner, null, [.. folders, extra], _h.Clock.UtcNow, Ct);
            Assert.Equal(1, upgrade.PermissionsAdded);
            Assert.Contains("business_user", upgrade.EditedRolesLeftAlone);
            Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id join iam.roles r on r.id = rp.role_id where r.code = 'auditor' and p.code = 'test.extra.read'", Ct));
            Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id join iam.roles r on r.id = rp.role_id where r.code = 'business_user' and p.code in ('test.extra.read', 'ntf.in_app_items.update')", Ct));
        }
        finally
        {
            Directory.Delete(extra, recursive: true);
        }

        // The code no module declares any more is retired, never deleted, and leaves the catalogue.
        var retire = await AccessSetup.EnsureAsync(owner, null, folders, _h.Clock.UtcNow, Ct);
        Assert.Equal(1, retire.PermissionsRetired);
        Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.permissions where code = 'test.extra.read' and retired_at is not null", Ct));
        var catalog = await _h.GetAsync(admin, "/api/iam/permission-catalog", Ct);
        Assert.Equal(HttpStatusCode.OK, catalog.Status);
        Assert.DoesNotContain("test.extra.read", catalog.Body, StringComparison.Ordinal);
        var companies = catalog.Json.EnumerateArray().Single(e => e.GetProperty("code").GetString() == "org.companies.read");
        Assert.Contains("tenant_admin", companies.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("org.companies", companies.GetProperty("resource").GetString());
    }

    [Fact]
    public async Task FR_ADM_ROL_02_roles_are_created_copied_renamed_compared_and_counted()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var code = "copy_" + Guid.NewGuid().ToString("N")[..8];
        var created = await _h.PostAsync(admin, "/api/iam/roles", new { code, name = new { en = "Copy of auditor", ur = "آڈیٹر کی نقل" }, copyFromBaseline = "business_user" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var copy = created.Json.GetProperty("id").GetGuid();
        var baseline = await _h.RoleIdAsync("business_user", Ct);

        var compare = await _h.GetAsync(admin, $"/api/iam/roles/compare?left={copy}&right={baseline}", Ct);
        Assert.Equal(HttpStatusCode.OK, compare.Status);
        Assert.Empty(compare.Json.GetProperty("onlyLeft").EnumerateArray());
        Assert.Empty(compare.Json.GetProperty("onlyRight").EnumerateArray());

        var summary = (await _h.GetAsync(admin, $"/api/iam/roles/{copy}", Ct)).Json;
        Assert.Equal("tenant", summary.GetProperty("source").GetString());
        Assert.Equal("business_user", summary.GetProperty("baselineCode").GetString());
        var renamed = await _h.PutAsync(admin, $"/api/iam/roles/{copy}", new { name = new { en = "Renamed" }, rowVersion = summary.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(admin, $"/api/iam/roles/{copy}", new { name = new { en = "Stale" }, rowVersion = 1 }, Ct)).Status);

        var holder = await _h.UserAsync(Ct);
        await _h.AssignAsync(holder, copy, null, _h.Today, null, Ct);
        var counted = (await _h.GetAsync(admin, $"/api/iam/roles/{copy}/summary", Ct)).Json;
        Assert.Equal(1, counted.GetProperty("userCount").GetInt32());
        Assert.False(counted.GetProperty("isPrivileged").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(admin, "/api/iam/roles", new { code, name = new { en = "Again" } }, Ct)).Status);
    }

    // ------------------------------------------------------------------ previews (FR-ADM-ROL-06, FR-ADM-ROL-07)

    [Fact]
    public async Task FR_ADM_ROL_06_FR_ADM_ROL_07_previews_show_the_records_a_user_sees_and_the_fields_a_role_sees()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var me = await _h.UserAsync(Ct, null, "employee");
        var w = await WorkforceAsync(me);
        var preview = await _h.GetAsync(admin, $"/api/iam/data-scope-preview?userId={me}&resource=hrm.employments", Ct);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Equal(1, preview.Json.GetProperty("count").GetInt64());
        Assert.Equal(w.EMe, preview.Json.GetProperty("firstPage")[0].GetGuid());

        var officer = await _h.RoleIdAsync("payroll_officer", Ct);
        var fields = await _h.GetAsync(admin, $"/api/iam/roles/{officer}/field-preview?resource=hrm.compensation_assignments", Ct);
        Assert.Equal(HttpStatusCode.OK, fields.Status);
        Assert.Equal("read_only", Access(fields, "gross_amount"));
        var manager = await _h.RoleIdAsync("line_manager", Ct);
        Assert.Equal("hidden", Access(await _h.GetAsync(admin, $"/api/iam/roles/{manager}/field-preview?resource=hrm.compensation_assignments", Ct), "gross_amount"));

        // Previews are sensitive (they reveal record ids): an ordinary user cannot run them.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(me, $"/api/iam/data-scope-preview?userId={me}&resource=hrm.employments", Ct)).Status);
    }

    private static string? Access(Answer preview, string field) =>
        preview.Json.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("field").GetString() == field).GetProperty("access").GetString();

    // ------------------------------------------------------------------ identity leftovers (FR-ADM-USR-20, FR-ADM-USR-21, Q10)

    [Fact]
    public async Task FR_ADM_USR_20_FR_ADM_USR_21_FR_KRN_IAM_02_privileged_roles_require_MFA_and_longer_passwords_and_roles_can_require_MFA()
    {
        var plain = await _h.UserAsync(Ct, null, "business_user");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        await using var owner = await _h.OwnerAsync(null, Ct);
        var plainPolicy = await IdentitySettings.ReadAsync(owner, null, plain, _h.Clock.UtcNow, Ct);
        var adminPolicy = await IdentitySettings.ReadAsync(owner, null, admin, _h.Clock.UtcNow, Ct);
        Assert.False(plainPolicy.Mfa.Required);
        Assert.Equal(10, plainPolicy.Password.MinLength);
        Assert.True(adminPolicy.Mfa.Required);
        Assert.NotNull(adminPolicy.Mfa.RequiredSince); // the grace period runs from the assignment
        Assert.Equal(14, adminPolicy.Password.MinLength);

        // Per-role MFA (role layer of iam.mfa.required).
        var business = await _h.RoleIdAsync("business_user", Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'iam.mfa.required', 'role', $2, 'true'::jsonb, now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), business, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        try
        {
            Assert.True((await IdentitySettings.ReadAsync(owner, null, plain, _h.Clock.UtcNow, Ct)).Mfa.Required);
        }
        finally
        {
            await AccessHarness.ExecAsync(owner, "delete from cfg.setting_values where setting_key = 'iam.mfa.required' and layer = 'role' and scope_id = $1", Ct, business);
        }
    }

    [Fact]
    public async Task Q9_reading_the_access_rules_fresh_on_every_call_costs_little()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin", "auditor", "hr_admin");
        await _h.GetAsync(admin, $"/api/org/companies/{company}", Ct);
        const int Calls = 40;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Calls; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(admin, $"/api/org/companies/{company}", Ct)).Status);
        }

        var average = watch.Elapsed.TotalMilliseconds / Calls;
        TestContext.Current.TestOutputHelper?.WriteLine($"S1.06 timing: {Calls} reads with 3 roles averaged {average:F1} ms per call (access rules read fresh each call, ruling D).");
        Assert.True(average < 250, $"average {average:F1} ms");
    }
}
