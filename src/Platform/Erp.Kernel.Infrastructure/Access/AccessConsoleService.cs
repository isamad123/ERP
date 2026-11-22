using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// The access console's read views (slice S1.13b): a group with its members and roles, the permission change report (from
/// the audit change log, field names only) and the role usage report (ruling Q8). Every value is a parameter; the endpoints
/// check the permissions.
/// </summary>
public sealed class AccessConsoleService(ITenantConnectionProvider tenant, IClock clock) : IAccessConsoleService
{
    private const int DefaultPage = 50;
    private const int MaxPage = 200;
    private const int ReportCap = 500;
    private const int DefaultInactiveDays = 90;

    private static readonly string[] ChangeTypes =
        ["iam.roles", "iam.role_permissions", "iam.role_data_scopes", "iam.field_policies", "iam.user_roles", "iam.user_group_roles", "iam.user_group_members"];

    public async Task<GroupDetail?> GroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        string code;
        string name;
        bool active;
        long version;
        await using (var command = IdentityDb.Command(c, null, "select code, name_i18n, is_active and not is_deleted, row_version from iam.user_groups where id = $1", groupId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            (code, name, active, version) = (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt64(3));
        }

        var members = new List<GroupMemberLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select u.id, u.display_name, u.username from iam.user_group_members m join iam.users u on u.id = m.user_id
            where m.group_id = $1 and not u.is_deleted and not u.is_system and u.status <> 'archived'
            order by lower(u.display_name), lower(u.username), u.id
            """, groupId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(new GroupMemberLine(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var roles = new List<UserRoleLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select gr.id, gr.role_id, r.code, r.name_i18n, gr.company_id, gr.valid_from, gr.valid_to, gr.reason
            from iam.user_group_roles gr join iam.roles r on r.id = gr.role_id
            where gr.group_id = $1 order by gr.valid_from desc, r.code, gr.id
            """, groupId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var from = DateOnly.FromDateTime(reader.GetDateTime(5));
                DateOnly? to = reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6));
                roles.Add(new UserRoleLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), Names(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    from, to, reader.IsDBNull(7) ? null : reader.GetString(7), from <= today && (to is null || to >= today)));
            }
        }

        return new GroupDetail(groupId, code, Names(name), active, version, members, roles);
    }

    public async Task<PermissionChangePage> PermissionChangesAsync(DateTimeOffset? since, DateTimeOffset? until, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit <= 0 ? DefaultPage : limit, 1, MaxPage);
        var after = ParseCursor(cursor);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var rows = new List<(Guid Id, DateTimeOffset At, Guid? Actor, string? ActorName, string Type, Guid EntityId, string Action, string? Source, string Changes)>();
        await using (var command = IdentityDb.Command(c, null, """
            select l.id, l.occurred_at, l.actor_user_id, u.display_name, l.entity_type, l.entity_id, l.action, l.source, l.changes::text
            from aud.change_log l left join iam.users u on u.id = l.actor_user_id
            where l.entity_type = any($1)
              and ($2::timestamptz is null or l.occurred_at >= $2)
              and ($3::timestamptz is null or l.occurred_at <= $3)
              and ($4::timestamptz is null or l.occurred_at < $4)
            order by l.occurred_at desc, l.id desc
            limit $5
            """, ChangeTypes, since, until, after, limit + 1))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetGuid(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
            }
        }

        var page = rows.Take(limit).ToList();
        var parsed = page.Select(r => FieldsAndRole(r.Changes)).ToList();
        var roleIds = page.Select((r, i) => r.Type == "iam.roles" ? r.EntityId : parsed[i].RoleId).Where(i => i is not null).Select(i => i!.Value).Distinct().ToArray();
        var codes = new Dictionary<Guid, string>();
        if (roleIds.Length > 0)
        {
            await using var command = IdentityDb.Command(c, null, "select id, code from iam.roles where id = any($1)", roleIds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                codes[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        var items = page.Select((r, i) =>
        {
            var roleId = r.Type == "iam.roles" ? r.EntityId : parsed[i].RoleId;
            return new PermissionChangeItem(r.Id, r.At, r.Actor, r.ActorName, r.Type, r.EntityId, r.Action, r.Source, parsed[i].Fields, roleId is { } id && codes.TryGetValue(id, out var code) ? code : null);
        }).ToList();
        return new PermissionChangePage(items, rows.Count > limit ? Cursor(page[^1].At, page[^1].Id) : null);
    }

    public async Task<RoleUsageReport> RoleUsageAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        int days;
        await using (var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            days = await AccessDb.SettingIntAsync(c, tx, IdentitySettings.ReportInactiveDays, DefaultInactiveDays, cancellationToken).ConfigureAwait(false);
        }

        var unused = new List<UnusedPermissionLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select p.code, p.module_id, p.is_sensitive
            from iam.permissions p
            where p.retired_at is null and not exists (
                select 1 from iam.role_permissions rp
                join iam.roles r on r.id = rp.role_id and not r.is_deleted and r.is_active
                where rp.permission_id = p.id and (
                    exists (select 1 from iam.user_roles ur join iam.users u on u.id = ur.user_id and u.status = 'active' and not u.is_deleted and not u.is_system
                            where ur.role_id = r.id and ur.valid_from <= $1 and (ur.valid_to is null or ur.valid_to >= $1))
                    or exists (select 1 from iam.user_group_roles gr
                               join iam.user_groups g on g.id = gr.group_id and not g.is_deleted
                               join iam.user_group_members m on m.group_id = g.id
                               join iam.users u on u.id = m.user_id and u.status = 'active' and not u.is_deleted and not u.is_system
                               where gr.role_id = r.id and gr.valid_from <= $1 and (gr.valid_to is null or gr.valid_to >= $1))))
            order by p.code
            limit $2
            """, today, ReportCap))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var code = reader.GetString(0);
                var parts = code.Split('.');
                unused.Add(new UnusedPermissionLine(code, reader.GetString(1), parts.Length > 2 ? string.Join('.', parts[1..^1]) : parts.ElementAtOrDefault(1) ?? string.Empty, parts[^1], reader.GetBoolean(2)));
            }
        }

        var inactive = new List<InactiveUserLine>();
        await using (var command = IdentityDb.Command(c, null, """
            select id, display_name, username, status, last_login_at from iam.users
            where status = 'active' and not is_deleted and not is_system and (last_login_at is null or last_login_at < $1)
            order by last_login_at nulls first, lower(username)
            limit $2
            """, now.AddDays(-days), ReportCap))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inactive.Add(new InactiveUserLine(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        return new RoleUsageReport(days, unused, inactive);
    }

    /// <summary>Field names of a change and the role it concerns when the change carries a role id (never values).</summary>
    private static (List<string> Fields, Guid? RoleId) FieldsAndRole(string changes)
    {
        var fields = new List<string>();
        Guid? role = null;
        using var document = JsonDocument.Parse(changes);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = field.GetString()!;
                fields.Add(name);
                if (name == "role_id")
                {
                    foreach (var side in new[] { "after", "before" })
                    {
                        if (role is null && entry.TryGetProperty(side, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
                        {
                            role = id;
                        }
                    }
                }
            }
        }

        return (fields, role);
    }

    // The same cursor text as the audit search: microseconds since the epoch, then the id.
    private static string Cursor(DateTimeOffset at, Guid id) =>
        ((at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10).ToString(CultureInfo.InvariantCulture) + "~" + id.ToString("N");

    private static DateTimeOffset? ParseCursor(string? cursor) =>
        Audit.AuditQueryService.ParseCursor(cursor) is var (at, _) ? at : null;

    private static Dictionary<string, string> Names(string json)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    names[property.Name] = property.Value.GetString()!;
                }
            }
        }

        return names;
    }
}
