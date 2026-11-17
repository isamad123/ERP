#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Infrastructure.Generated;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Generated;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// The S1.06 fresh-session review findings 1 to 7 (docs/reviews/S1.06-review.md, CR-025), each reproduced through the
/// real API pipeline and the real access engine. Before the fixes every one of these tests failed.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AccessReviewFixTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Me (employment EMe) manages ERep; EOther has no link to me. All in the default company.</summary>
    private async Task<(Guid EMe, Guid ERep, Guid EOther)> TeamAsync(Guid me)
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var branch = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted order by created_at limit 1", Ct, company);
        var unit = (Guid)(await _h.RowAsync("org.org_units", company, null, Ct))["id"]!;
        async Task<Guid> Employment(Guid? user) =>
            (Guid)(await _h.RowAsync("hrm.employments", company, new Dictionary<string, object?> { ["user_ref"] = user, ["status"] = "active", ["is_primary"] = false }, Ct))["id"]!;
        async Task Assignment(Guid employment, Guid? manager) =>
            await _h.RowAsync("hrm.employment_assignments", company, new Dictionary<string, object?>
            {
                ["employment_id"] = employment, ["org_unit_id"] = unit, ["branch_id"] = branch, ["manager_employment_id"] = manager,
                ["valid_from"] = new DateOnly(2026, 1, 1), ["valid_to"] = null,
            }, Ct);
        var eMe = await Employment(me);
        var eRep = await Employment(null);
        var eOther = await Employment(null);
        await Assignment(eMe, null);
        await Assignment(eRep, eMe);
        await Assignment(eOther, null);
        return (eMe, eRep, eOther);
    }

    private async Task SetMakerCheckerAsync(string? code, bool on)
    {
        await using var owner = await _h.OwnerAsync(code, Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'iam.maker_checker.sensitive_roles', 'tenant', null, $2::jsonb, now(), $3, now(), $3)
            on conflict (setting_key, layer, scope_id) do update set value = excluded.value
            """, Ct, Guid.CreateVersion7(), on ? "true" : "false", BaselineSeed.SystemUserId);
    }

    // ------------------------------------------------------------------ finding 1: rows located through their parent

    [Fact]
    public async Task Review_S106_1_FR_KRN_IAM_12_PAY_19_tax_declaration_lines_follow_the_scope_of_their_declaration()
    {
        var me = await _h.UserAsync(Ct, null, "employee");
        var (eMe, _, eOther) = await TeamAsync(me);
        var company = await _h.DefaultCompanyAsync(Ct);
        var mine = await _h.RowAsync("hrm.tax_declarations", company, new Dictionary<string, object?> { ["employment_id"] = eMe }, Ct);
        var theirs = await _h.RowAsync("hrm.tax_declarations", company, new Dictionary<string, object?> { ["employment_id"] = eOther }, Ct);
        var myLine = await _h.RowAsync("hrm.tax_declaration_lines", company, new Dictionary<string, object?> { ["declaration_id"] = mine["id"], ["declared_amount"] = 111111.0000m }, Ct);
        var theirLine = await _h.RowAsync("hrm.tax_declaration_lines", company, new Dictionary<string, object?> { ["declaration_id"] = theirs["id"], ["declared_amount"] = 777777.0000m }, Ct);

        // The Employee role reads its own lines, amounts included (its salary rule), and not a colleague's.
        var own = await _h.GetAsync(me, $"/api/hrm/tax-declaration-lines/{myLine["id"]}", Ct);
        Assert.Equal(HttpStatusCode.OK, own.Status);
        Assert.Equal(111111m, own.Json.GetProperty("declaredAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(me, $"/api/hrm/tax-declaration-lines/{theirLine["id"]}", Ct)).Status);
        var list = await _h.GetAsync(me, "/api/hrm/tax-declaration-lines?limit=200", Ct);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var ids = list.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains((Guid)myLine["id"]!, ids);
        Assert.DoesNotContain((Guid)theirLine["id"]!, ids);
        Assert.DoesNotContain("777777", list.Body, StringComparison.Ordinal);

        // Writes are checked through the parent too: a line may be added to one's own declaration only.
        var writer = await _h.UserAsync(Ct);
        await _h.AssignAsync(writer, await _h.RoleAsync(Ct, ["hrm.tax_declaration_lines.read", "hrm.tax_declaration_lines.create"], [("*", "own")], [("*", "class:salary", "editable")]), null, _h.Today, null, Ct);
        var (wMe, _, _) = await TeamAsync(writer);
        var writersOwn = await _h.RowAsync("hrm.tax_declarations", company, new Dictionary<string, object?> { ["employment_id"] = wMe }, Ct);
        var refused = await _h.PostAsync(writer, "/api/hrm/tax-declaration-lines", new { declarationId = theirs["id"], kind = "zakat", declaredAmount = 5m, fileIds = Array.Empty<Guid>() }, Ct);
        Assert.True(refused.Status == HttpStatusCode.Forbidden, refused.Body);
        Assert.Equal(AccessDeniedException.OutOfScope, refused.Text("title"));
        var created = await _h.PostAsync(writer, "/api/hrm/tax-declaration-lines", new { declarationId = writersOwn["id"], kind = "zakat", declaredAmount = 5m, fileIds = Array.Empty<Guid>() }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
    }

    [Fact]
    public void Review_S106_T1_FR_KRN_IAM_12_a_baseline_role_with_a_narrow_scope_reads_only_scoped_tables()
    {
        var scoped = KernelResourceCatalog.All.Concat(HrmResourceCatalog.All).ToDictionary(r => r.Resource, r => r.IsScoped, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var manifest in fx.Migrations.AccessFolders(["hrm"]).Select(AccessManifest.Load))
        {
            foreach (var role in manifest.Roles)
            {
                var scopes = role.Scopes ?? [];
                foreach (var permission in role.Permissions.Where(p => !p.Contains('*', StringComparison.Ordinal)))
                {
                    var resource = permission[..permission.LastIndexOf('.')];
                    var scope = scopes.GetValueOrDefault(resource) ?? scopes.GetValueOrDefault("*");
                    if (scope is not null and not "all" && scoped.TryGetValue(resource, out var isScoped) && !isScoped)
                    {
                        problems.Add($"{role.Code}: {permission} with scope {scope} on a table without scope");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    // ------------------------------------------------------------------ finding 2: data scopes

    [Fact]
    public async Task Review_S106_2_FR_KRN_IAM_12_data_scopes_are_created_by_the_service_and_never_edited_through_the_generated_API()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var scope = await _h.ScopeAsync("own", new { }, Ct);
        var edit = await _h.PutAsync(admin, $"/api/iam/data-scopes/{scope}", new { code = "x" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "x" }, kind = "all", definition = new { }, isActive = true, rowVersion = 1 }, Ct);
        Assert.True(edit.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{(int)edit.Status} {edit.Body}");
        Assert.Equal("own", await _h.ScalarAsync<string>("select kind from iam.data_scopes where id = $1", Ct, scope));

        // The service: definitions must be exactly what the engine reads, naming records that exist.
        var company = await _h.DefaultCompanyAsync(Ct);
        async Task<Answer> Create(string kind, object definition) =>
            await _h.PostAsync(admin, "/api/iam/data-scopes", new { code = "s" + Guid.NewGuid().ToString("N")[..10], name = new { en = "Scope", ur = "دائرہ" }, kind, definition }, Ct);
        Assert.Equal("errors.scope.kind_invalid", (await Create("everything", new { })).Text("title"));
        Assert.Equal("errors.scope.definition_invalid", (await Create("companies", new { })).Text("title"));
        Assert.Equal("errors.scope.definition_invalid", (await Create("companies", new { company_ids = new[] { Guid.NewGuid() } })).Text("title"));
        Assert.Equal("errors.scope.definition_invalid", (await Create("own", new { company_ids = new[] { company } })).Text("title"));
        Assert.Equal("errors.scope.definition_invalid", (await Create("all", new { conditions = new[] { new { attribute = "status" } } })).Text("title"));
        var created = await Create("companies", new { company_ids = new[] { company }, conditions = new[] { new { attribute = "status", @in = new[] { "active" } } } });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.data_scope_created' and aggregate_id = $1", Ct, id));

        // Creating a scope needs Manage Roles.
        var plain = await _h.UserAsync(Ct, null, "business_user");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(plain, "/api/iam/data-scopes", new { code = "s" + Guid.NewGuid().ToString("N")[..10], name = new { en = "S" }, kind = "own" }, Ct)).Status);
    }

    // ------------------------------------------------------------------ finding 3: field access per row

    [Fact]
    public async Task Review_S106_3_GHR_06_FR_KRN_IAM_13_a_salary_rule_opens_only_the_rows_of_the_role_that_has_it()
    {
        var me = await _h.UserAsync(Ct, null, "employee");
        var (eMe, eRep, _) = await TeamAsync(me);
        var company = await _h.DefaultCompanyAsync(Ct);
        var ownPay = await _h.RowAsync("hrm.pay_results", company, new Dictionary<string, object?> { ["employment_id"] = eMe, ["gross"] = 90000.0000m }, Ct);
        var teamPay = await _h.RowAsync("hrm.pay_results", company, new Dictionary<string, object?> { ["employment_id"] = eRep, ["gross"] = 150000.0000m }, Ct);

        // A customer role "team pay status": the team's pay results without amounts (no salary rule: hidden, GHR-06).
        await _h.AssignAsync(me, await _h.RoleAsync(Ct, ["hrm.pay_results.read"], [("hrm.pay_results", "direct_team")]), null, _h.Today, null, Ct);

        var team = await _h.GetAsync(me, $"/api/hrm/pay-results/{teamPay["id"]}", Ct);
        Assert.Equal(HttpStatusCode.OK, team.Status);
        Assert.False(team.Json.TryGetProperty("gross", out _), team.Body);
        Assert.False(team.Json.TryGetProperty("net", out _), team.Body);
        var own = await _h.GetAsync(me, $"/api/hrm/pay-results/{ownPay["id"]}", Ct);
        Assert.Equal(90000m, own.Json.GetProperty("gross").GetDecimal()); // the Employee role's own payslip keeps its amounts

        // A list mixing both: each row shaped by the roles that cover it.
        var list = await _h.GetAsync(me, "/api/hrm/pay-results?limit=200", Ct);
        var items = list.Json.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.True(items[(Guid)ownPay["id"]!].TryGetProperty("gross", out _));
        Assert.False(items[(Guid)teamPay["id"]!].TryGetProperty("gross", out _));
        Assert.DoesNotContain("150000", list.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ finding 4: last tenant admin

    [Fact]
    public async Task Review_S106_4_Spec02_BR_a_future_dated_tenant_admin_does_not_count_as_the_last_admin()
    {
        var code = await _h.ProvisionAsync("f", Ct);
        await SetMakerCheckerAsync(code, on: false);
        await using var owner = await _h.OwnerAsync(code, Ct);
        var first = await AccessHarness.ScalarAsync<Guid>(owner, "select u.id from iam.users u where not u.is_system", Ct);
        await AccessHarness.ExecAsync(owner, "update iam.users set status = 'active' where id = $1", Ct, first);
        var firstAssignment = await AccessHarness.ScalarAsync<Guid>(owner, "select id from iam.user_roles where user_id = $1", Ct, first);
        var adminRole = await AccessHarness.ScalarAsync<Guid>(owner, "select id from iam.roles where code = 'tenant_admin'", Ct);
        var other = await _h.UserAsync(Ct, code);

        var future = await _h.PostAsync(first, "/api/iam/user-roles", new { userId = other, roleId = adminRole, validFrom = _h.Today.AddYears(5), reason = "later" }, Ct, code);
        Assert.Equal(HttpStatusCode.Created, future.Status);
        var end = await _h.PostAsync(first, $"/api/iam/user-roles/{firstAssignment}/end", new { reason = "leaving" }, Ct, code);
        Assert.Equal(HttpStatusCode.BadRequest, end.Status);
        Assert.Equal("errors.tenant_admin.last_admin", end.Text("title"));
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(first, "/api/iam/permission-catalog", Ct, code)).Status);
    }

    // ------------------------------------------------------------------ finding 5: iam.* settings

    [Fact]
    public async Task Review_S106_5_FR_KRN_IAM_14_identity_settings_change_only_through_the_service_and_switching_off_needs_a_second_admin()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        await SetMakerCheckerAsync(null, on: true); // a stored tenant value, whichever test ran first

        // The generated settings API cannot change iam.* keys (create and update). Since slice S1.09a (CR-036 part 4) its
        // write routes do not exist at all; the EF guard behind them still refuses such writes (KernelDbContext.UseAccessService).
        var generated = await _h.PostAsync(admin, "/api/cfg/setting-values", new { settingKey = "iam.maker_checker.sensitive_roles", layer = "user", scopeId = admin, value = false }, Ct);
        Assert.True(generated.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"generated create: {(int)generated.Status}");
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = 'iam.maker_checker.sensitive_roles' and layer = 'user'", Ct));
        var existing = await _h.ScalarAsync<Guid>("select id from cfg.setting_values where setting_key = 'iam.maker_checker.sensitive_roles' and layer = 'tenant' and scope_id is null", Ct);
        var version = await _h.ScalarAsync<long>("select row_version from cfg.setting_values where id = $1", Ct, existing);
        var update = await _h.PutAsync(admin, $"/api/cfg/setting-values/{existing}", new { settingKey = "iam.maker_checker.sensitive_roles", layer = "tenant", value = false, rowVersion = version }, Ct);
        Assert.True(update.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"generated update: {(int)update.Status}");
        Assert.Equal("true", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where id = $1", Ct, existing));
        Assert.Equal(version, await _h.ScalarAsync<long>("select row_version from cfg.setting_values where id = $1", Ct, existing));

        // The service validates, records history and an event.
        Assert.Equal("errors.settings.unknown_key", (await _h.PutAsync(admin, "/api/iam/settings/cfg.other", new { value = 1, reason = "t" }, Ct)).Text("title"));
        Assert.Equal("errors.settings.value_invalid", (await _h.PutAsync(admin, "/api/iam/settings/iam.session.idle_minutes", new { value = "ten", reason = "t" }, Ct)).Text("title"));
        Assert.Equal("errors.settings.value_invalid", (await _h.PutAsync(admin, "/api/iam/settings/iam.session.idle_minutes", new { value = 0, reason = "t" }, Ct)).Text("title"));
        Assert.Equal("errors.settings.layer_invalid", (await _h.PutAsync(admin, "/api/iam/settings/iam.session.idle_minutes", new { layer = "user", scopeId = admin, value = 30, reason = "t" }, Ct)).Text("title"));
        var set = await _h.PutAsync(admin, "/api/iam/settings/iam.session.idle_minutes", new { value = 31, reason = "office policy" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.Status);
        var row = set.Json.GetProperty("id").GetGuid();
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_value_history where setting_value_id = $1 and reason = 'office policy' and new_value = '31'::jsonb", Ct, row));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.setting_changed' and aggregate_id = $1", Ct, row));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, "/api/iam/settings/iam.session.idle_minutes", new { value = 30, reason = "back" }, Ct)).Status);

        // With two or more tenant admins, switching maker-checker off waits for a second one.
        try
        {
            var off = await _h.PutAsync(admin, "/api/iam/settings/iam.maker_checker.sensitive_roles", new { value = false, reason = "fewer approvals" }, Ct);
            Assert.Equal(HttpStatusCode.Accepted, off.Status);
            Assert.Equal("true", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where id = $1", Ct, existing));
            var request = off.Json.GetProperty("requestId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(admin, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "self" }, Ct)).Status);
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct)).Status);
            Assert.Equal("false", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where id = $1", Ct, existing));

            // Switching it back on needs nobody else.
            Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, "/api/iam/settings/iam.maker_checker.sensitive_roles", new { value = true, reason = "restore" }, Ct)).Status);
        }
        finally
        {
            await SetMakerCheckerAsync(null, on: true);
        }

        // A customer with a single administrator switches it off alone (Q6: micro customers).
        var code = await _h.ProvisionAsync("m", Ct);
        await using var owner = await _h.OwnerAsync(code, Ct);
        var only = await AccessHarness.ScalarAsync<Guid>(owner, "select u.id from iam.users u where not u.is_system", Ct);
        await AccessHarness.ExecAsync(owner, "update iam.users set status = 'active' where id = $1", Ct, only);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(only, "/api/iam/settings/iam.maker_checker.sensitive_roles", new { value = false, reason = "one admin" }, Ct, code)).Status);
    }

    // ------------------------------------------------------------------ finding 6: who may approve

    [Fact]
    public async Task Review_S106_6_FR_XM_32_ruling_A_an_approver_covers_the_company_and_never_approves_their_own_gain()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.DefaultCompanyAsync(Ct);
        var companyOnly = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyOnly, await _h.RoleIdAsync("tenant_admin", Ct), company, _h.Today, null, Ct);
        var sensitive = await _h.RoleIdAsync("company_admin", Ct); // holds iam.roles.assign: sensitive

        // An all-companies assignment cannot be approved by someone whose roles cover one company only.
        var target = await _h.UserAsync(Ct);
        var held = await _h.PostAsync(maker, "/api/iam/user-roles", new { userId = target, roleId = sensitive, reason = "all companies" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var request = held.Json.GetProperty("requestId").GetGuid();
        var refused = await _h.PostAsync(companyOnly, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "ok" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.access.company_not_covered", refused.Text("title"));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "ok" }, Ct)).Status);

        // Nobody approves a change that gives themselves the role.
        var forChecker = await _h.PostAsync(maker, "/api/iam/user-roles", new { userId = checker, roleId = sensitive, companyId = company, reason = "for the checker" }, Ct);
        var own = forChecker.Json.GetProperty("requestId").GetGuid();
        var gain = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{own}/approve", new { reason = "mine" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, gain.Status);
        Assert.Equal("errors.maker_checker.own_gain", gain.Text("title"));
        var third = await _h.UserAsync(Ct, null, "tenant_admin");
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(third, $"/api/iam/maker-checker-requests/{own}/approve", new { reason = "ok" }, Ct)).Status);
    }

    // ------------------------------------------------------------------ finding 7: held permission changes

    [Fact]
    public async Task Review_S106_7_FR_KRN_IAM_14_approving_a_held_permission_change_never_undoes_a_change_made_meanwhile()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var role = await _h.RoleAsync(Ct, ["org.companies.read", "iam.roles.assign"]); // sensitive: needs a second person

        var remove = await _h.PutAsync(maker, $"/api/iam/roles/{role}/permissions", new { permissions = new[] { "iam.roles.assign" }, reason = "no companies" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, remove.Status);
        var add = await _h.PutAsync(maker, $"/api/iam/roles/{role}/permissions", new { permissions = new[] { "org.companies.read", "iam.roles.assign", "org.branches.read" }, reason = "branches" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, add.Status);

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{add.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{remove.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct)).Status);

        var codes = (await _h.GetAsync(maker, $"/api/iam/roles/{role}/summary", Ct)).Json.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "iam.roles.assign", "org.branches.read" }, codes);
        var payload = await _h.ScalarAsync<string>("select payload::text from iam.maker_checker_requests where id = $1", Ct, remove.Json.GetProperty("requestId").GetGuid());
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(["org.companies.read"], doc.RootElement.GetProperty("removed").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
