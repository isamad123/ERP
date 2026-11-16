#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// CR-024 (product-owner ruling on R-008): a platform Super Admin's controlled recovery and override of a customer's
/// tenant administration, with a mandatory reason, recorded in cp.platform_audit and in the customer's iam events
/// (FR-ADM-PLT-10). Runs against a customer of its own.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PlatformAdminRecoveryTests(TenancyFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlatformAdminRecovery Recovery => new(fx.Options, fx.Secrets, fx.Clock, fx.Migrations);

    [Fact]
    public async Task CR_024_FR_ADM_PLT_10_a_Super_Admin_repairs_the_Tenant_Admin_role_grants_and_revokes_it_and_every_step_is_audited()
    {
        var h = await AccessHarness.ForAsync(fx);
        var code = await h.ProvisionAsync("r", Ct);
        await using var owner = await h.OwnerAsync(code, Ct);
        var superAdmin = fx.Admin;
        Assert.Equal(PlatformAdminRecovery.SuperAdminRole, superAdmin.Role);

        // Only a platform_admin may do this, and only with a reason.
        var support = await SupportOperatorAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Recovery.RepairRoleAsync(support, code, "needed", Ct));
        }
        finally
        {
            // Other tests count the platform users of the shared control plane; the refused operator wrote nothing else.
            await using var cpCleanup = await fx.OpenControlPlaneAsync(CancellationToken.None);
            await TenancyFixture.ExecAsync(cpCleanup, "delete from cp.platform_users where id = $1", CancellationToken.None, support.Id);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => Recovery.RepairRoleAsync(superAdmin, code, " ", Ct));

        // Damage done outside tenant administration (a direct database edit): a mandatory permission gone, the role archived.
        var role = await AccessHarness.ScalarAsync<Guid>(owner, "select id from iam.roles where code = 'tenant_admin'", Ct);
        await AccessHarness.ExecAsync(owner, "delete from iam.role_permissions rp using iam.permissions p where rp.permission_id = p.id and rp.role_id = $1 and p.code = 'iam.roles.manage'", Ct, role);
        await AccessHarness.ExecAsync(owner, "update iam.roles set is_deleted = true, deleted_at = now() where id = $1", Ct, role);
        Assert.Equal(["tenant_admin lacks iam.roles.manage", "tenant_admin lacks iam.roles.assign"], await AccessSetup.ProtectedRoleProblemsAsync(owner, null, Ct));

        // 1. Repair.
        var repaired = await Recovery.RepairRoleAsync(superAdmin, code, "customer locked out after a database edit", Ct);
        Assert.True(repaired.Changed);
        Assert.Contains(repaired.Messages, m => m.Contains("restored", StringComparison.Ordinal));
        Assert.Empty(await AccessSetup.ProtectedRoleProblemsAsync(owner, null, Ct));
        Assert.False(await AccessHarness.ScalarAsync<bool>(owner, "select is_deleted from iam.roles where id = $1", Ct, role));
        Assert.False((await Recovery.RepairRoleAsync(superAdmin, code, "check again", Ct)).Changed);

        // 2. Grant the role to a named user, who can then administer.
        var user = await h.UserAsync(Ct, code);
        var email = await AccessHarness.ScalarAsync<string>(owner, "select email from iam.users where id = $1", Ct, user);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync(user, $"/api/iam/roles/{role}/summary", Ct, code)).Status);
        Assert.True((await Recovery.GrantAsync(superAdmin, code, email, "new administrator after the previous one left", Ct)).Changed);
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync(user, $"/api/iam/roles/{role}/summary", Ct, code)).Status);
        Assert.False((await Recovery.GrantAsync(superAdmin, code, email, "again", Ct)).Changed);

        // 3. Revoke, even from the last tenant admin (override): the provisioned first admin first, then the user.
        var first = await AccessHarness.ScalarAsync<string>(owner, "select email from iam.users where not is_system and id <> $1 order by created_at limit 1", Ct, user);
        Assert.DoesNotContain((await Recovery.RevokeAsync(superAdmin, code, first, "unused invitation", Ct)).Messages, m => m.StartsWith("WARNING", StringComparison.Ordinal));
        var last = await Recovery.RevokeAsync(superAdmin, code, email, "account compromised", Ct);
        Assert.True(last.Changed);
        Assert.Contains(last.Messages, m => m.StartsWith("WARNING", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync(user, $"/api/iam/roles/{role}/summary", Ct, code)).Status);

        // Nobody removed the mandatory permissions: the override is about people, not the role.
        Assert.Empty(await AccessSetup.ProtectedRoleProblemsAsync(owner, null, Ct));

        // Audit: one iam event per run in the customer's database, naming the Super Admin and the reason ...
        var events = new List<(string Type, string Email, string Reason)>();
        await using (var command = new Npgsql.NpgsqlCommand("select event_type, payload -> 'platform_user' ->> 'email', payload ->> 'reason' from iam.outbox_messages where event_type like 'iam.platform_admin_%' order by occurred_at, id", owner))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                events.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.Equal(["iam.platform_admin_role_repaired", "iam.platform_admin_role_repaired", "iam.platform_admin_granted", "iam.platform_admin_granted", "iam.platform_admin_revoked", "iam.platform_admin_revoked"], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(superAdmin.Email, e.Email));
        Assert.Equal("account compromised", events[^1].Reason);

        // ... and the same runs in the platform audit.
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        var tenantId = (await fx.TenantAsync(code, Ct)).TenantId;
        Assert.Equal(6L, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit where tenant_id = $1 and action like 'tenant.admin%' and platform_user_id = $2", Ct, tenantId, superAdmin.Id));
        var details = await AccessHarness.ScalarAsync<string>(cp, "select details::text from cp.platform_audit where tenant_id = $1 and action = 'tenant.admin.revoke' order by occurred_at desc limit 1", Ct, tenantId);
        Assert.Equal("account compromised", JsonDocument.Parse(details).RootElement.GetProperty("reason").GetString());
    }

    private async Task<PlatformActor> SupportOperatorAsync()
    {
        var id = Guid.CreateVersion7();
        var email = "support" + Guid.NewGuid().ToString("N")[..8] + "@vendor.test";
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        await TenancyFixture.ExecAsync(cp, """
            insert into cp.platform_users (id, email, display_name, role, mfa_enrolled, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'Support', 'support', true, 'active', now(), $1, now(), $1)
            """, Ct, id, email);
        return new PlatformActor(id, email, "support");
    }
}
