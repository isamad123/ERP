using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// Controlled recovery and override of a customer's tenant administration by a platform Super Admin (product-owner
/// ruling on R-008, CR-024): repair the protected Tenant Admin role, give the role to a named user, or take it away
/// from a named user even when they are the last tenant admin. Only platform users with role <c>platform_admin</c>, always
/// with a reason, one person (a two-person rule may come with the platform console, release 1.1). Each action is
/// recorded twice: an <c>iam.*</c> event in the customer's database (payload names the platform user and the reason)
/// and a row in cp.platform_audit (FR-ADM-PLT-10). Nobody can remove the role's mandatory permissions.
/// </summary>
public sealed class PlatformAdminRecovery(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    public const string SuperAdminRole = "platform_admin";

    /// <summary>What a command changed (false when nothing needed changing) and what the operator should know.</summary>
    public sealed record Outcome(bool Changed, IReadOnlyList<string> Messages);

    /// <summary>Un-archives (or recreates) the Tenant Admin role and adds back its mandatory and shipped permissions.</summary>
    public Task<Outcome> RepairRoleAsync(PlatformActor actor, string tenantCode, string? reason, CancellationToken cancellationToken) =>
        RunAsync(actor, tenantCode, reason, "tenant.admin_role.repair", async (c, tx, modules, now) =>
        {
            var messages = new List<string>();
            var role = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select id from iam.roles where lower(code) = lower($1) and not is_deleted", cancellationToken, ProtectedRoles.TenantAdmin).ConfigureAwait(false);
            if (role is null)
            {
                role = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select id from iam.roles where lower(code) = lower($1) order by deleted_at desc nulls last limit 1", cancellationToken, ProtectedRoles.TenantAdmin).ConfigureAwait(false);
                if (role is { } archived)
                {
                    await IdentityDb.ExecuteAsync(c, tx, """
                        update iam.roles set is_deleted = false, deleted_at = null, deleted_by = null, is_active = true, updated_at = $2, updated_by = $3,
                            row_version = row_version + 1 where id = $1
                        """, cancellationToken, archived, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
                    messages.Add("the archived Tenant Admin role was restored");
                }
                else
                {
                    role = Guid.CreateVersion7(now);
                    await IdentityDb.ExecuteAsync(c, tx, """
                        insert into iam.roles (id, code, name_i18n, source, baseline_code, created_at, created_by, updated_at, updated_by)
                        values ($1, $2, '{"en":"Tenant Admin"}', 'module', $2, $3, $4, $3, $4)
                        """, cancellationToken, role, ProtectedRoles.TenantAdmin, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
                    messages.Add("the Tenant Admin role was missing and was created");
                }
            }

            await IdentityDb.ExecuteAsync(c, tx, "update iam.roles set is_active = true where id = $1 and not is_active", cancellationToken, role).ConfigureAwait(false);

            // The mandatory permissions plus every shipped baseline permission of the role; the customer's additions stay.
            var registered = new List<string>();
            await using (var codes = IdentityDb.Command(c, tx, "select code from iam.permissions where retired_at is null"))
            {
                registered.AddRange(await AccessDb.TextsAsync(codes, cancellationToken).ConfigureAwait(false));
            }

            var shipped = migrations.AccessFolders(modules).Select(AccessManifest.Load)
                .SelectMany(m => m.Roles).Where(r => ProtectedRoles.IsProtected(r.Code))
                .SelectMany(r => r.Permissions).SelectMany(p => AccessSetup.Matching(p, registered));
            var wanted = ProtectedRoles.Mandatory.Concat(shipped).Distinct(StringComparer.Ordinal).ToArray();
            var added = new List<string>();
            await using (var grant = IdentityDb.Command(c, tx, """
                insert into iam.role_permissions (role_id, permission_id)
                select $1, p.id from iam.permissions p where p.code = any($2) and p.retired_at is null
                on conflict do nothing
                returning (select code from iam.permissions where id = permission_id)
                """, role, wanted))
            {
                added.AddRange(await AccessDb.TextsAsync(grant, cancellationToken).ConfigureAwait(false));
            }

            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.role_data_scopes (role_id, resource_type, data_scope_id)
                select $1, '*', id from iam.data_scopes where lower(code) = 'all' on conflict do nothing
                """, cancellationToken, role).ConfigureAwait(false);
            if (added.Count > 0)
            {
                messages.Add($"{added.Count} permission(s) added back: {string.Join(", ", added.Order(StringComparer.Ordinal).Take(12))}{(added.Count > 12 ? ", ..." : string.Empty)}");
            }

            var changed = messages.Count > 0;
            if (!changed)
            {
                messages.Add("the Tenant Admin role was already complete; nothing changed");
            }

            return (changed, messages, AccessEvents.PlatformAdminRoleRepaired, "iam.role", role.Value, (object)new { added });
        }, cancellationToken);

    /// <summary>Gives the Tenant Admin role (all companies, from today, open-ended) to the user with this e-mail.</summary>
    public Task<Outcome> GrantAsync(PlatformActor actor, string tenantCode, string email, string? reason, CancellationToken cancellationToken) =>
        RunAsync(actor, tenantCode, reason, "tenant.admin.grant", async (c, tx, modules, now) =>
        {
            var (userId, status) = await UserAsync(c, tx, email, cancellationToken).ConfigureAwait(false);
            var role = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select id from iam.roles where lower(code) = lower($1) and not is_deleted and is_active", cancellationToken, ProtectedRoles.TenantAdmin).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{tenantCode} has no active Tenant Admin role; run `tenant admin-role-repair` first.");
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var messages = new List<string>();
            var holds = await IdentityDb.ScalarAsync<bool>(c, tx, """
                select exists (select 1 from iam.user_roles where user_id = $1 and role_id = $2 and company_id is null and valid_from <= $3 and valid_to is null)
                """, cancellationToken, userId, role, today).ConfigureAwait(false);
            if (holds)
            {
                messages.Add($"{email} already holds the Tenant Admin role; nothing changed");
            }
            else
            {
                await IdentityDb.ExecuteAsync(c, tx, """
                    insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $3, null, $4, $5, $6, $7, $6, $7)
                    """, cancellationToken, Guid.CreateVersion7(now), userId, role, "platform recovery: " + reason, today, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
                messages.Add($"{email} now holds the Tenant Admin role (all companies, from {today:yyyy-MM-dd})");
            }

            if (status == "invited")
            {
                messages.Add($"{email} has not activated the account yet: issue a link with `tenant admin-activation-link --code {tenantCode}` if they are the first administrator");
            }
            else if (status is "disabled" or "locked")
            {
                messages.Add($"{email} is {status}: the role applies once the account is active again");
            }

            return (!holds, messages, AccessEvents.PlatformAdminGranted, "iam.user", userId, (object)new { email, status });
        }, cancellationToken);

    /// <summary>
    /// Takes the Tenant Admin role away from the user with this e-mail, even when they are the last tenant admin: their
    /// open assignments end today and their sessions end. Groups that still give them the role are reported.
    /// </summary>
    public Task<Outcome> RevokeAsync(PlatformActor actor, string tenantCode, string email, string? reason, CancellationToken cancellationToken) =>
        RunAsync(actor, tenantCode, reason, "tenant.admin.revoke", async (c, tx, modules, now) =>
        {
            var (userId, _) = await UserAsync(c, tx, email, cancellationToken).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var messages = new List<string>();
            var ended = await IdentityDb.ExecuteAsync(c, tx, """
                update iam.user_roles ur set valid_to = $3, updated_at = $4, updated_by = $5, row_version = ur.row_version + 1
                from iam.roles r
                where ur.role_id = r.id and lower(r.code) = lower($2) and ur.user_id = $1 and ur.valid_from < $3 and (ur.valid_to is null or ur.valid_to > $3)
                """, cancellationToken, userId, ProtectedRoles.TenantAdmin, today, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
            ended += await IdentityDb.ExecuteAsync(c, tx, """
                delete from iam.user_roles ur using iam.roles r
                where ur.role_id = r.id and lower(r.code) = lower($2) and ur.user_id = $1 and ur.valid_from >= $3
                """, cancellationToken, userId, ProtectedRoles.TenantAdmin, today).ConfigureAwait(false);
            var sessions = await IdentityDb.RevokeAllSessionsAsync(c, tx, userId, "platform_admin_revoked", now, cancellationToken).ConfigureAwait(false);
            messages.Add($"{ended} Tenant Admin assignment(s) of {email} ended; {sessions} session(s) ended");

            var groups = new List<string>();
            await using (var command = IdentityDb.Command(c, tx, """
                select distinct g.code from iam.user_group_roles gr
                join iam.roles r on r.id = gr.role_id and lower(r.code) = lower($2)
                join iam.user_groups g on g.id = gr.group_id and not g.is_deleted
                join iam.user_group_members m on m.group_id = g.id and m.user_id = $1
                where gr.valid_to is null or gr.valid_to > $3
                """, userId, ProtectedRoles.TenantAdmin, today))
            {
                groups.AddRange(await AccessDb.TextsAsync(command, cancellationToken).ConfigureAwait(false));
            }

            if (groups.Count > 0)
            {
                messages.Add($"{email} still receives the role through group(s) {string.Join(", ", groups)}: remove them from the group as well");
            }

            var left = await TenantAdminGuard.CountAsync(c, tx, today, cancellationToken).ConfigureAwait(false);
            if (left.Any == 0)
            {
                messages.Add($"WARNING: {tenantCode} now has no tenant administrator; give the role to someone with `tenant admin-grant`");
            }

            return (ended > 0 || sessions > 0, messages, AccessEvents.PlatformAdminRevoked, "iam.user", userId, (object)new { email, assignments_ended = ended, sessions_ended = sessions, groups });
        }, cancellationToken);

    private delegate Task<(bool Changed, List<string> Messages, string EventType, string AggregateType, Guid AggregateId, object Details)> Work(
        NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlySet<string> modules, DateTimeOffset now);

    private async Task<Outcome> RunAsync(PlatformActor actor, string tenantCode, string? reason, string auditAction, Work work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!string.Equals(actor.Role, SuperAdminRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{actor.Email} has the platform role {actor.Role}; only a {SuperAdminRole} may recover or override tenant administration.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("--reason is required: say why this recovery or override is needed.");
        }

        var host = await TenantHosts.PrimaryAsync(options, tenantCode, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} is not registered.");
        var tenant = await new TenantDirectory(options, clock).FindByHostAsync(host, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} has no database yet.");
        var now = clock.UtcNow;
        (bool Changed, List<string> Messages, string EventType, string AggregateType, Guid AggregateId, object Details) result;
        await using (var owner = await new TenantProvisioner(options, secrets, clock, migrations).OpenTenantAsync(tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
        await using (var tx = await owner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            result = await work(owner, tx, tenant.InstalledModules, now).ConfigureAwait(false);
            var platformUser = new { id = actor.Id, email = actor.Email, role = actor.Role };
            await AccessDb.RaiseAsync(owner, tx, result.EventType, result.AggregateType, result.AggregateId, BaselineSeed.SystemUserId, now,
                new { platform_user = platformUser, reason, changed = result.Changed, details = result.Details }, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cp = new NpgsqlConnection(options.ConnectionString))
        {
            await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
            await PlatformAudit.WriteAsync(cp, null, clock, actor, auditAction, tenant.TenantId,
                new { code = tenantCode, reason, changed = result.Changed, messages = result.Messages, details = result.Details }, cancellationToken).ConfigureAwait(false);
        }

        return new Outcome(result.Changed, result.Messages);
    }

    private static async Task<(Guid Id, string Status)> UserAsync(NpgsqlConnection c, NpgsqlTransaction tx, string email, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, "select id, status from iam.users where lower(email) = lower($1) and not is_deleted and not is_system", email.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No user with e-mail {email} in this customer.");
        }

        var result = (reader.GetGuid(0), reader.GetString(1));
        return result.Item2 == "archived" ? throw new InvalidOperationException($"{email} is archived.") : result;
    }
}
