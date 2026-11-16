using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// "The last tenant admin cannot be removed or disabled" (spec 02 BR, spec 04 ROL BR; slice S1.06 item 9). A tenant
/// admin is a user who is not a system user, not archived or deleted, and holds an open-ended (no end date) assignment
/// of the role <c>tenant_admin</c>, directly or through an active group. Every change that can remove one (user status,
/// ending an assignment, archiving a role or group, removing a group member or group role) counts before and after the
/// change inside the same transaction and is refused when it would leave none: no active one when there was one, or
/// none at all (invited and locked included) when there was one.
/// </summary>
internal static class TenantAdminGuard
{
    public const string LastAdmin = "errors.tenant_admin.last_admin";

    public sealed record Count(long Active, long Any);

    /// <summary>Takes the guard's lock (one such change at a time per customer) and counts the tenant admins.</summary>
    public static async Task<Count> CountAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        await IdentityDb.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('iam.tenant_admin_guard'))", ct).ConfigureAwait(false);
        await using var command = IdentityDb.Command(c, tx, """
            with admin_role as (
                select id from iam.roles where lower(code) = 'tenant_admin' and not is_deleted and is_active
            ), admins as (
                select ur.user_id from iam.user_roles ur where ur.role_id in (select id from admin_role) and ur.valid_to is null
                union
                select m.user_id from iam.user_group_roles gr
                join iam.user_groups g on g.id = gr.group_id and not g.is_deleted and g.is_active
                join iam.user_group_members m on m.group_id = gr.group_id
                where gr.role_id in (select id from admin_role) and gr.valid_to is null
            )
            select count(*) filter (where u.status = 'active'), count(*)
            from iam.users u join admins a on a.user_id = u.id
            where not u.is_system and not u.is_deleted and u.status in ('invited', 'active', 'locked')
            """);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new Count(reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>True when the change made since <paramref name="before"/> removed the last tenant admin.</summary>
    public static async Task<bool> RemovedLastAsync(NpgsqlConnection c, NpgsqlTransaction tx, Count before, CancellationToken ct)
    {
        var after = await CountAsync(c, tx, ct).ConfigureAwait(false);
        return (before.Active > 0 && after.Active == 0) || (before.Any > 0 && after.Any == 0);
    }
}
