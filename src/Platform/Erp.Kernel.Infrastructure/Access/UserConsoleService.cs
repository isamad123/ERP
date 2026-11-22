using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// The administrator's views over users (slice S1.13a): the user list with roles, companies and second-factor state, the
/// access data of one user, the small people lookup, and the two single-item revocations of the user record. Every value is
/// a parameter. Reads need <c>iam.users.read</c>; the endpoints check it, and the lookup checks its own rule.
/// </summary>
public sealed class UserConsoleService(ITenantConnectionProvider tenant, IClock clock, ICurrentActor actor) : IUserConsoleService
{
    private const int DefaultPage = 50;
    private const int MaxPage = 200;
    private const int LookupPage = 20;

    public async Task<UserOverviewPage> OverviewAsync(UserOverviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit <= 0 ? DefaultPage : query.Limit, 1, MaxPage);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + EscapeLike(query.Search.Trim()) + "%";
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var command = IdentityDb.Command(c, null, """
            with active_roles as (
                select ur.user_id, ur.role_id, ur.company_id
                from iam.user_roles ur
                join iam.roles r on r.id = ur.role_id and not r.is_deleted
                where ur.valid_from <= $1 and (ur.valid_to is null or ur.valid_to >= $1)
            )
            select u.id, u.username, u.display_name, u.email, u.status, u.last_login_at, u.default_company_id, u.access_valid_to,
                   exists (select 1 from iam.mfa_devices m where m.user_id = u.id and m.status = 'active') as has_mfa,
                   coalesce((select jsonb_agg(jsonb_build_object('id', r.id, 'code', r.code, 'name', r.name_i18n) order by r.code)
                             from (select distinct role_id from active_roles a where a.user_id = u.id) x
                             join iam.roles r on r.id = x.role_id), '[]'::jsonb) as roles,
                   coalesce((select array_agg(distinct a.company_id) filter (where a.company_id is not null) from active_roles a where a.user_id = u.id), '{}') as companies,
                   exists (select 1 from active_roles a where a.user_id = u.id and a.company_id is null) as all_companies
            from iam.users u
            where not u.is_deleted and not u.is_system
              and ($2::text is null or u.username ilike $2 or u.email ilike $2 or u.display_name ilike $2)
              and ($3::text is null or u.status = $3)
              and ($4::uuid is null or exists (select 1 from active_roles a where a.user_id = u.id and a.role_id = $4))
              and ($5::uuid is null or u.default_company_id = $5
                   or exists (select 1 from active_roles a where a.user_id = u.id and (a.company_id = $5 or a.company_id is null)))
              and ($6::boolean is null or $6 = exists (select 1 from iam.mfa_devices m where m.user_id = u.id and m.status = 'active'))
              and ($7::text is null or lower(u.username) > $7)
            order by lower(u.username)
            limit $8
            """, today, search, query.Status, query.RoleId, query.CompanyId, query.HasMfa, string.IsNullOrEmpty(query.Cursor) ? null : query.Cursor.ToLowerInvariant(), limit + 1);
        var items = new List<UserOverviewItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var roles = new List<RoleChip>();
                using (var json = JsonDocument.Parse(reader.GetString(9)))
                {
                    foreach (var element in json.RootElement.EnumerateArray())
                    {
                        roles.Add(new RoleChip(element.GetProperty("id").GetGuid(), element.GetProperty("code").GetString()!, Names(element.GetProperty("name"))));
                    }
                }

                items.Add(new UserOverviewItem(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)), reader.GetBoolean(8), roles,
                    reader.GetFieldValue<Guid[]>(10), reader.GetBoolean(11)));
            }
        }

        var next = items.Count > limit ? items[limit - 1].Username.ToLowerInvariant() : null;
        return new UserOverviewPage(next is null ? items : items.Take(limit).ToList(), next);
    }

    public async Task<UserAccessView?> AccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        if (!await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from iam.users where id = $1 and not is_deleted)", cancellationToken, userId).ConfigureAwait(false))
        {
            return null;
        }

        var roles = new List<UserRoleLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select ur.id, ur.role_id, r.code, r.name_i18n, ur.company_id, ur.valid_from, ur.valid_to, ur.reason
            from iam.user_roles ur join iam.roles r on r.id = ur.role_id
            where ur.user_id = $1 order by ur.valid_from desc, r.code, ur.id
            """, userId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                roles.Add(ReadRole(reader, today));
            }
        }

        var groups = new List<UserGroupLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select g.id, g.code, g.name_i18n from iam.user_group_members m join iam.user_groups g on g.id = m.group_id
            where m.user_id = $1 and not g.is_deleted order by g.code
            """, userId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups.Add(new UserGroupLine(reader.GetGuid(0), reader.GetString(1), Names(reader.GetString(2)), []));
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var rows = new List<UserRoleLine>();
            await using var command = IdentityDb.Command(c, null, """
                select gr.id, gr.role_id, r.code, r.name_i18n, gr.company_id, gr.valid_from, gr.valid_to, gr.reason
                from iam.user_group_roles gr join iam.roles r on r.id = gr.role_id
                where gr.group_id = $1 order by gr.valid_from desc, r.code, gr.id
                """, groups[i].GroupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRole(reader, today));
            }

            groups[i] = groups[i] with { Roles = rows };
        }

        // Effective data scopes: the scopes of the roles in force today, direct or through a group, with where each comes from.
        var scopes = new List<UserScopeLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select rds.resource_type, s.id, s.code, s.kind, s.name_i18n, r.code, src.source
            from (
                select ur.role_id, 'role' as source from iam.user_roles ur
                where ur.user_id = $1 and ur.valid_from <= $2 and (ur.valid_to is null or ur.valid_to >= $2)
                union
                select gr.role_id, 'group:' || g.code from iam.user_group_members m
                join iam.user_groups g on g.id = m.group_id and not g.is_deleted
                join iam.user_group_roles gr on gr.group_id = g.id
                where m.user_id = $1 and gr.valid_from <= $2 and (gr.valid_to is null or gr.valid_to >= $2)
            ) src
            join iam.roles r on r.id = src.role_id and not r.is_deleted
            join iam.role_data_scopes rds on rds.role_id = r.id
            join iam.data_scopes s on s.id = rds.data_scope_id
            order by rds.resource_type, r.code, src.source
            """, userId, today))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                scopes.Add(new UserScopeLine(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), Names(reader.GetString(4)), reader.GetString(5), reader.GetString(6)));
            }
        }

        var delegations = new List<UserDelegationLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select d.id, d.from_user_id, f.display_name, d.to_user_id, t.display_name, d.valid_from, d.valid_to, d.status, d.reason
            from iam.delegations d join iam.users f on f.id = d.from_user_id join iam.users t on t.id = d.to_user_id
            where d.from_user_id = $1 or d.to_user_id = $1 order by d.valid_from desc, d.id
            """, userId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                delegations.Add(new UserDelegationLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        var factors = new List<UserMfaLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select id, kind, label, status, created_at, last_used_at from iam.mfa_devices
            where user_id = $1 and status <> 'revoked' order by created_at, id
            """, userId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                factors.Add(new UserMfaLine(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        var devices = new List<UserDeviceLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select id, label, created_at, expires_at, last_used_at from iam.trusted_devices
            where user_id = $1 and revoked_at is null and expires_at > $2 order by created_at, id
            """, userId, now))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                devices.Add(new UserDeviceLine(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        return new UserAccessView(userId, roles, groups, scopes, delegations, factors, devices);
    }

    public async Task<IReadOnlyList<PersonLookupItem>> PeopleAsync(string? search, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var like = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search.Trim()) + "%";
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        // The caller's companies: those of their assignments in force (and their default company). An assignment for all
        // companies, or an administrator without a company, opens the whole list.
        await using var command = IdentityDb.Command(c, null, """
            with mine as (
                select ur.company_id from iam.user_roles ur
                where ur.user_id = $1 and ur.valid_from <= $2 and (ur.valid_to is null or ur.valid_to >= $2)
                union all
                select gr.company_id from iam.user_group_members m join iam.user_group_roles gr on gr.group_id = m.group_id
                where m.user_id = $1 and gr.valid_from <= $2 and (gr.valid_to is null or gr.valid_to >= $2)
                union all
                select default_company_id from iam.users where id = $1
            )
            select u.id, u.display_name, u.username
            from iam.users u
            where u.status = 'active' and not u.is_deleted and not u.is_system
              and ($3::text is null or u.username ilike $3 or u.display_name ilike $3)
              and (exists (select 1 from iam.user_roles a where a.user_id = $1 and a.company_id is null and a.valid_from <= $2 and (a.valid_to is null or a.valid_to >= $2))
                   or u.id = $1
                   or u.default_company_id in (select company_id from mine where company_id is not null)
                   or exists (select 1 from iam.user_roles o where o.user_id = u.id and o.valid_from <= $2 and (o.valid_to is null or o.valid_to >= $2)
                              and (o.company_id is null or o.company_id in (select company_id from mine where company_id is not null))))
            order by lower(u.display_name), u.id
            limit $4
            """, actor.UserId, today, like, LookupPage);
        var list = new List<PersonLookupItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new PersonLookupItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return list;
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.users where id = $1 and not is_deleted)", cancellationToken, userId).ConfigureAwait(false))
        {
            return false;
        }

        if (await IdentityDb.RevokeSessionAsync(c, tx, sessionId, userId, "admin_revoked", now, cancellationToken).ConfigureAwait(false) == 1)
        {
            await IdentityDb.RaiseAsync(c, tx, IdentityEvents.SessionsRevoked, userId, actor.UserId, now, new { reason = "admin_revoked", session = sessionId, note = reason }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveTrustedDeviceAsync(Guid userId, Guid deviceId, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.users where id = $1 and not is_deleted)", cancellationToken, userId).ConfigureAwait(false))
        {
            return false;
        }

        await TrustedDevices.RemoveAsync(c, tx, userId, deviceId, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static UserRoleLine ReadRole(NpgsqlDataReader reader, DateOnly today)
    {
        var from = DateOnly.FromDateTime(reader.GetDateTime(5));
        DateOnly? to = reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6));
        return new UserRoleLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), Names(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetGuid(4),
            from, to, reader.IsDBNull(7) ? null : reader.GetString(7), from <= today && (to is null || to >= today));
    }

    private static Dictionary<string, string> Names(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Names(document.RootElement);
    }

    private static Dictionary<string, string> Names(JsonElement element)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    names[property.Name] = property.Value.GetString()!;
                }
            }
        }

        return names;
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
