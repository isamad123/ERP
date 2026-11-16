#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Access;

/// <summary>Slice S1.06: maker-checker for sensitive role changes (FR-KRN-IAM-14, Q6) and the last tenant admin (spec 02 BR).</summary>
[Collection(TenancyGroup.Name)]
public sealed class AccessGovernanceTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<long> PermissionCountAsync(Guid role, string code) =>
        _h.ScalarAsync<long>("select count(*) from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id where rp.role_id = $1 and p.code = $2", Ct, role, code);

    [Fact]
    public async Task FR_KRN_IAM_14_FR_ADM_ROL_04_a_sensitive_role_change_waits_for_a_second_admin_who_is_not_the_requester()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var sensitive = await _h.RoleAsync(Ct, ["iam.users.unlock"]);

        // 1. Permission change on a role holding a sensitive permission: pending, nothing applied.
        var pending = await _h.PutAsync(maker, $"/api/iam/roles/{sensitive}/permissions", new { permissions = new[] { "iam.users.unlock", "org.companies.read" }, reason = "helpdesk needs it" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, pending.Status);
        var request = pending.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(0L, await PermissionCountAsync(sensitive, "org.companies.read"));
        Assert.Equal("pending", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, request));

        // The requester cannot approve their own request (FR-XM-32).
        var own = await _h.PostAsync(maker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, own.Status);
        Assert.Equal("errors.maker_checker.own_request", own.Text("title"));

        // A second administrator approves: the change is applied in the same transaction.
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked" }, Ct)).Status);
        Assert.Equal(1L, await PermissionCountAsync(sensitive, "org.companies.read"));
        Assert.Equal(checker, await _h.ScalarAsync<Guid>("select decided_by from iam.maker_checker_requests where id = $1 and status = 'approved'", Ct, request));
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "again" }, Ct)).Status);

        // 2. Assigning the sensitive role: pending; rejection leaves nothing applied.
        var target = await _h.UserAsync(Ct);
        var assign = await _h.PostAsync(maker, "/api/iam/user-roles", new { userId = target, roleId = sensitive, reason = "helpdesk" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, assign.Status);
        var assignRequest = assign.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{assignRequest}/reject", new { reason = "" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{assignRequest}/reject", new { reason = "not approved" }, Ct)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.user_roles where user_id = $1", Ct, target));

        // 3. A request nobody decides expires after 7 days and applies nothing.
        var late = await _h.PostAsync(maker, "/api/iam/user-roles", new { userId = target, roleId = sensitive, reason = "helpdesk" }, Ct);
        var lateRequest = late.Json.GetProperty("requestId").GetGuid();
        _h.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var expired = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{lateRequest}/approve", new { reason = "too late" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, expired.Status);
        Assert.Equal("errors.maker_checker.expired", expired.Text("title"));
        Assert.Equal("expired", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, lateRequest));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.user_roles where user_id = $1", Ct, target));
        _h.Clock.Reset();

        // 4. The approver needs every permission the change grants.
        var limitedChecker = await _h.UserAsync(Ct);
        await _h.AssignAsync(limitedChecker, await _h.RoleAsync(Ct, ["iam.maker_checker.approve"]), null, _h.Today, null, Ct);
        var again = await _h.PostAsync(maker, "/api/iam/user-roles", new { userId = target, roleId = sensitive, reason = "helpdesk" }, Ct);
        var refused = await _h.PostAsync(limitedChecker, $"/api/iam/maker-checker-requests/{again.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.access.cannot_grant_unheld", refused.Text("title"));

        // Every step raised an event for the audit log (S1.07), with the actor in the headers.
        Assert.True(await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type like 'iam.maker_checker_%' and headers ->> 'actor' = $1", Ct, maker.ToString()) >= 3);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.maker_checker_approved' and aggregate_id = $1", Ct, request));
    }

    [Fact]
    public async Task FR_KRN_IAM_14_Q6_with_the_setting_off_a_sensitive_change_applies_at_once()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var sensitive = await _h.RoleAsync(Ct, ["iam.users.unlock"]);
        await SetMakerCheckerAsync(null, on: false);
        try
        {
            var applied = await _h.PutAsync(admin, $"/api/iam/roles/{sensitive}/permissions", new { permissions = new[] { "iam.users.unlock", "org.companies.read" }, reason = "direct" }, Ct);
            Assert.Equal(HttpStatusCode.OK, applied.Status);
            Assert.Equal(1L, await PermissionCountAsync(sensitive, "org.companies.read"));
        }
        finally
        {
            await SetMakerCheckerAsync(null, on: true);
        }
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

    [Fact]
    public async Task Spec02_BR_S106_item9_the_last_active_tenant_admin_cannot_be_disabled_archived_or_lose_the_role()
    {
        // A customer of its own: the provisioned first administrator is its only tenant admin.
        var code = await _h.ProvisionAsync("g", Ct);
        await SetMakerCheckerAsync(code, on: false);
        await using var owner = await _h.OwnerAsync(code, Ct);
        var first = await AccessHarness.ScalarAsync<Guid>(owner, "select u.id from iam.users u where not u.is_system", Ct);
        await AccessHarness.ExecAsync(owner, "update iam.users set status = 'active' where id = $1", Ct, first);
        var firstAssignment = await AccessHarness.ScalarAsync<Guid>(owner, "select id from iam.user_roles where user_id = $1", Ct, first);
        var adminRole = await AccessHarness.ScalarAsync<Guid>(owner, "select id from iam.roles where code = 'tenant_admin'", Ct);

        // The actor holds the tenant-admin role only until next month, so it is not itself a lasting tenant admin.
        var actor = await _h.UserAsync(Ct, code);
        await _h.AssignAsync(actor, adminRole, null, _h.Today, _h.Today.AddDays(30), Ct, code);

        async Task Refused(Answer answer)
        {
            Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
            Assert.Equal("errors.tenant_admin.last_admin", answer.Text("title"));
            await Task.CompletedTask;
        }

        await Refused(await _h.PostAsync(actor, $"/api/iam/users/{first}/status", new { status = "disabled", reason = "test" }, Ct, code));
        await Refused(await _h.PostAsync(actor, $"/api/iam/users/{first}/status", new { status = "locked", reason = "test" }, Ct, code)); // archiving needs disabling first, refused above
        await Refused(await _h.PostAsync(actor, $"/api/iam/user-roles/{firstAssignment}/end", new { reason = "test" }, Ct, code));
        await Refused(await _h.PostAsync(actor, $"/api/iam/roles/{adminRole}/archive", new { reason = "test" }, Ct, code));
        Assert.Equal("active", await AccessHarness.ScalarAsync<string>(owner, "select status from iam.users where id = $1", Ct, first));

        // A second tenant admin through a group: now the first may lose the role, and the group carries the last one.
        var second = await _h.UserAsync(Ct, code);
        var group = (await _h.PostAsync(actor, "/api/iam/user-groups", new { code = "admins", name = new { en = "Administrators" } }, Ct, code)).Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(actor, $"/api/iam/user-groups/{group}/members", new { userId = second }, Ct, code)).Status);
        var groupRole = await _h.PostAsync(actor, $"/api/iam/user-groups/{group}/roles", new { roleId = adminRole, reason = "admins group" }, Ct, code);
        Assert.Equal(HttpStatusCode.OK, groupRole.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(actor, $"/api/iam/user-roles/{firstAssignment}/end", new { reason = "moved to the group" }, Ct, code)).Status);

        await Refused(await _h.PostAsync(actor, $"/api/iam/user-groups/{group}/members/{second}/remove", null, Ct, code));
        await Refused(await _h.PostAsync(actor, $"/api/iam/user-groups/{group}/archive", new { reason = "test" }, Ct, code));
        await Refused(await _h.PostAsync(actor, $"/api/iam/user-group-roles/{groupRole.Json.GetProperty("id").GetGuid()}/end", new { reason = "test" }, Ct, code));
        await Refused(await _h.PostAsync(actor, $"/api/iam/users/{second}/status", new { status = "disabled", reason = "test" }, Ct, code));
        Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.user_group_members where group_id = $1", Ct, group));
    }
}
