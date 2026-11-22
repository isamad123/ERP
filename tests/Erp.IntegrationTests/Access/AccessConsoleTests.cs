#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// The access console's read views (slice S1.13b, CR-069): a group with members and roles, the permission change report and
/// the role usage report, through the real API pipeline, the real access engine and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AccessConsoleTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task FR_ADM_USR_06_S113b_group_detail_lists_members_and_roles_with_ended_ones_and_refuses_strangers()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var member = await _h.UserAsync(Ct);
        var created = await _h.PostAsync(admin, "/api/iam/user-groups", new { code = "s113b_" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Console group" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var group = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/iam/user-groups/{group}/members", new { userId = member }, Ct)).Status);
        var live = await _h.RoleAsync(Ct, ["org.companies.read"]);
        var ended = await _h.RoleAsync(Ct, ["org.companies.read"]);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/iam/user-groups/{group}/roles", new { roleId = live, reason = "team" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/iam/user-groups/{group}/roles", new { roleId = ended, validFrom = _h.Today.AddDays(-20), validTo = _h.Today.AddDays(-5), reason = "old" }, Ct)).Status);

        var answer = await _h.GetAsync(admin, $"/api/iam/user-groups/{group}/detail", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal("Console group", answer.Json.GetProperty("name").GetProperty("en").GetString());
        Assert.True(answer.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal([member], answer.Json.GetProperty("members").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray());
        var roles = answer.Json.GetProperty("roles").EnumerateArray().ToDictionary(e => e.GetProperty("roleId").GetGuid());
        Assert.True(roles[live].GetProperty("inForce").GetBoolean());
        Assert.False(roles[ended].GetProperty("inForce").GetBoolean());
        Assert.Equal("old", roles[ended].GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(admin, $"/api/iam/user-groups/{Guid.NewGuid()}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(await _h.UserAsync(Ct, null, "business_user"), $"/api/iam/user-groups/{group}/detail", Ct)).Status);
    }

    [Fact]
    public async Task FR_ADM_ROL_11_S113b_permission_change_report_lists_who_changed_which_role_with_field_names_only()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var code = "s113b_" + Guid.NewGuid().ToString("N")[..8];
        var created = await _h.PostAsync(admin, "/api/iam/roles", new { code, name = new { en = "Report role" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var role = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/iam/roles/{role}/permissions", new { permissions = new[] { "org.companies.read" }, reason = "report" }, Ct)).Status);
        var person = await _h.UserAsync(Ct);
        await _h.AssignAsync(person, role, null, _h.Today, null, Ct);

        var answer = await _h.GetAsync(admin, "/api/iam/reports/permission-changes?limit=200", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        var items = answer.Json.GetProperty("items").EnumerateArray().ToList();
        var mine = items.Where(e => e.GetProperty("roleCode").GetString() == code).ToList();
        Assert.NotEmpty(mine);
        var roleEntry = mine.First(e => e.GetProperty("entityType").GetString() == "iam.roles");
        Assert.Equal(admin, roleEntry.GetProperty("actorUserId").GetGuid());
        Assert.False(string.IsNullOrEmpty(roleEntry.GetProperty("actorName").GetString()));
        Assert.Contains("code", roleEntry.GetProperty("fields").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(code + "\"", roleEntry.GetRawText().Replace("\"roleCode\":\"" + code, string.Empty, StringComparison.Ordinal)); // values never leave the audit log
        Assert.DoesNotContain("Report role", answer.Json.GetRawText());

        // Paging: a one-row page has a cursor, and the next page continues without repeats.
        var first = await _h.GetAsync(admin, "/api/iam/reports/permission-changes?limit=1", Ct);
        var next = first.Json.GetProperty("next").GetString();
        Assert.NotNull(next);
        var second = await _h.GetAsync(admin, $"/api/iam/reports/permission-changes?limit=1&cursor={Uri.EscapeDataString(next!)}", Ct);
        Assert.NotEqual(first.Json.GetProperty("items")[0].GetProperty("id").GetGuid(), second.Json.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(admin, "/api/iam/reports/permission-changes?cursor=junk", Ct)).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(await _h.UserAsync(Ct, null, "business_user"), "/api/iam/reports/permission-changes", Ct)).Status);
    }

    [Fact]
    public async Task FR_ADM_ROL_11_S113b_role_usage_report_lists_unused_permissions_and_inactive_users_by_the_setting()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var holder = await _h.UserAsync(Ct);
        var recent = await _h.UserAsync(Ct);
        var stale = await _h.UserAsync(Ct);
        var never = await _h.UserAsync(Ct);
        await _h.ExecAsync("update iam.users set last_login_at = $2 where id = $1", Ct, recent, _h.Clock.UtcNow.AddDays(-5));
        await _h.ExecAsync("update iam.users set last_login_at = $2 where id = $1", Ct, stale, _h.Clock.UtcNow.AddDays(-45));
        await _h.ExecAsync("update iam.users set last_login_at = null where id = $1", Ct, never);

        var answer = await _h.GetAsync(admin, "/api/iam/reports/role-usage", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(90, answer.Json.GetProperty("inactiveDays").GetInt32());
        var unused = answer.Json.GetProperty("unusedPermissions").EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();
        Assert.NotEmpty(unused); // the baseline roles do not hold every permission
        var probe = unused[0]!;
        var entry = answer.Json.GetProperty("unusedPermissions").EnumerateArray().First(e => e.GetProperty("code").GetString() == probe);
        Assert.Equal(probe, entry.GetProperty("module").GetString() + "." + entry.GetProperty("resource").GetString() + "." + entry.GetProperty("action").GetString());

        // A role nobody holds does not use its permission; once an active user holds it, the permission leaves the list.
        var role = await _h.RoleAsync(Ct, [probe]);
        Assert.Contains(probe, (await _h.GetAsync(admin, "/api/iam/reports/role-usage", Ct)).Json.GetProperty("unusedPermissions").EnumerateArray().Select(e => e.GetProperty("code").GetString()));
        await _h.AssignAsync(holder, role, null, _h.Today.AddDays(-1), null, Ct);
        Assert.DoesNotContain(probe, (await _h.GetAsync(admin, "/api/iam/reports/role-usage", Ct)).Json.GetProperty("unusedPermissions").EnumerateArray().Select(e => e.GetProperty("code").GetString()));
        var inactive = answer.Json.GetProperty("inactiveUsers").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(never, inactive);
        Assert.DoesNotContain(stale, inactive); // 45 days is inside 90
        Assert.DoesNotContain(recent, inactive);

        // The setting decides: 30 days lists the stale user, and a recent one stays out.
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, "/api/iam/settings/iam.report.inactive_days", new { value = 30, reason = "test" }, Ct)).Status);
        var thirty = await _h.GetAsync(admin, "/api/iam/reports/role-usage", Ct);
        Assert.Equal(30, thirty.Json.GetProperty("inactiveDays").GetInt32());
        var inactive30 = thirty.Json.GetProperty("inactiveUsers").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(stale, inactive30);
        Assert.DoesNotContain(recent, inactive30);

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(await _h.UserAsync(Ct, null, "business_user"), "/api/iam/reports/role-usage", Ct)).Status);
    }
}
