using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// One access-control change as data: what the requester asked for. It is applied at once, or stored as the payload of
/// an iam.maker_checker_requests row and applied by the approver in the approval's transaction (FR-KRN-IAM-14).
/// </summary>
internal sealed record AccessChange
{
    public const string SetPermissions = "iam.role.set_permissions";
    public const string SetDataScopes = "iam.role.set_data_scopes";
    public const string SetFieldPolicies = "iam.role.set_field_policies";
    public const string Assign = "iam.role.assign";
    public const string EndAssignment = "iam.role.end_assignment";
    public const string AssignGroup = "iam.group.assign";
    public const string EndGroupRole = "iam.group.end_role";
    public const string AddMember = "iam.group.add_member";
    public const string RemoveMember = "iam.group.remove_member";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public required string Kind { get; init; }

    public Guid? RoleId { get; init; }

    public Guid? UserId { get; init; }

    public Guid? GroupId { get; init; }

    public Guid? CompanyId { get; init; }

    /// <summary>The iam.user_roles or iam.user_group_roles row an end applies to.</summary>
    public Guid? AssignmentId { get; init; }

    public DateOnly? ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }

    public DateOnly? EndDate { get; init; }

    public IReadOnlyList<string>? Permissions { get; init; }

    public IReadOnlyList<RoleScopeChange>? Scopes { get; init; }

    public IReadOnlyList<RoleFieldChange>? Fields { get; init; }

    public string? Reason { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AccessChange FromJson(string json) => JsonSerializer.Deserialize<AccessChange>(json, Json) ?? throw new InvalidDataException("empty change");

    /// <summary>Subject of the maker-checker request: the role, or the assignment, group or user it changes.</summary>
    public (string Type, Guid Id) Subject() => Kind switch
    {
        EndAssignment => ("iam.user_roles", AssignmentId!.Value),
        EndGroupRole => ("iam.user_group_roles", AssignmentId!.Value),
        AddMember or RemoveMember or AssignGroup => ("iam.user_groups", GroupId!.Value),
        Assign => ("iam.users", UserId!.Value),
        _ => ("iam.roles", RoleId!.Value),
    };
}

/// <summary>Parameterized SQL shared by the access-control services.</summary>
internal static class AccessDb
{
    public const string SystemRoleTenantAdmin = "tenant_admin";

    public static Task RaiseAsync(NpgsqlConnection c, NpgsqlTransaction tx, string eventType, string aggregateType, Guid aggregateId, Guid actor, DateTimeOffset now, object payload, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, $4, $5, $6, $7, null)
            """, ct, Guid.CreateVersion7(now), now, eventType, aggregateType, aggregateId,
            new JsonPayload(JsonSerializer.Serialize(payload, JsonOptions)), new JsonPayload(JsonSerializer.Serialize(new { actor })));

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>True when the role holds a (not retired) permission flagged sensitive: a privileged role (Q10).</summary>
    public static async Task<bool> IsSensitiveRoleAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid roleId, CancellationToken ct) =>
        await IdentityDb.ScalarAsync<bool>(c, tx, """
            select exists (select 1 from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id and p.retired_at is null and p.is_sensitive
                           where rp.role_id = $1)
            """, ct, roleId).ConfigureAwait(false);

    public static async Task<List<string>> RolePermissionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid roleId, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, """
            select p.code from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id and p.retired_at is null
            where rp.role_id = $1 order by p.code
            """, roleId);
        return await TextsAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>The role row, locked for the change; null when it does not exist or is archived.</summary>
    public static async Task<(string Code, long RowVersion)?> LockRoleAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid roleId, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, "select code, row_version from iam.roles where id = $1 and not is_deleted for update", roleId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    /// <summary>Marks the role changed by a person: <c>migrate</c> then leaves it alone (ruling Q8, CR-020).</summary>
    public static Task TouchRoleAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid roleId, Guid actor, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, "update iam.roles set updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", ct, roleId, now, actor);

    public static async Task<bool> SettingOnAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, CancellationToken ct) =>
        await IdentityDb.ScalarAsync<string>(c, tx, """
            select coalesce((select value from cfg.setting_values where setting_key = $1 and layer = 'tenant' and scope_id is null),
                            (select default_value from cfg.settings where key = $1))::text
            """, ct, key).ConfigureAwait(false) == "true";

    public static async Task<int> SettingIntAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, int fallback, CancellationToken ct)
    {
        var text = await IdentityDb.ScalarAsync<string>(c, tx, """
            select coalesce((select value from cfg.setting_values where setting_key = $1 and layer = 'tenant' and scope_id is null),
                            (select default_value from cfg.settings where key = $1))::text
            """, ct, key).ConfigureAwait(false);
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
    }

    public static async Task<List<string>> TextsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public static async Task<List<Guid>> IdsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var list = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetGuid(0));
        }

        return list;
    }
}
