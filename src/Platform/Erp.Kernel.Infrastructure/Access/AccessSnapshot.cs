using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>One role a user holds today, directly or through a group, for one company (null: all companies).</summary>
internal sealed record Assignment(Guid RoleId, Guid? CompanyId, DateTimeOffset CreatedAt);

/// <summary>"Column equals / is one of" on a column the table declares in <c>scope.attributes</c> (Q5).</summary>
internal sealed record AttributeCondition(string Attribute, IReadOnlyList<string> Values);

/// <summary>
/// A data scope as stored in iam.data_scopes: its kind and the parameters of the kind (<c>definition</c>):
/// <c>{"company_ids": [...]}</c>, <c>{"branch_ids": [...]}</c>, <c>{"org_unit_ids": [...], "include_children": true}</c>,
/// and for every kind optional <c>{"conditions": [{"attribute": "employee_type", "in": ["permanent"]}]}</c>.
/// </summary>
internal sealed record ScopeRule(string Kind, IReadOnlyList<Guid> Ids, bool IncludeChildren, IReadOnlyList<AttributeCondition> Conditions)
{
    public const string All = "all";
    public const string Own = "own";
    public const string DirectTeam = "direct_team";
    public const string Subtree = "subtree";
    public const string OrgUnits = "org_units";
    public const string Branches = "branches";
    public const string Companies = "companies";

    public static readonly string[] Kinds = [Own, DirectTeam, Subtree, OrgUnits, Branches, Companies, All];

    /// <summary>Reads a definition; anything it cannot read becomes a scope that matches nothing (never a wider one).</summary>
    public static ScopeRule Parse(string kind, string definitionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            var root = doc.RootElement;
            var ids = kind switch
            {
                Companies => ReadIds(root, "company_ids"),
                Branches => ReadIds(root, "branch_ids"),
                OrgUnits => ReadIds(root, "org_unit_ids"),
                _ => [],
            };
            var children = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("include_children", out var c) && c.ValueKind == JsonValueKind.True;
            var conditions = new List<AttributeCondition>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("conditions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var attribute = item.GetProperty("attribute").GetString() ?? throw new FormatException("attribute");
                    var values = item.TryGetProperty("in", out var any) ? any.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToList()
                        : [item.GetProperty("equals").GetString() ?? string.Empty];
                    conditions.Add(new AttributeCondition(attribute, values));
                }
            }

            return Kinds.Contains(kind) ? new ScopeRule(kind, ids, children, conditions) : Nothing;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return Nothing;
        }
    }

    /// <summary>A scope that matches no row (unreadable definition or unknown kind).</summary>
    public static ScopeRule Nothing { get; } = new(Companies, [], false, []);

    private static List<Guid> ReadIds(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetGuid()).ToList()
            : [];
}

/// <summary>What one role grants: permissions, a data scope per resource (<c>*</c>: every resource) and field rules.</summary>
internal sealed class RoleRules
{
    public const string AnyResource = "*";
    public const string ClassPrefix = "class:";

    public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);

    /// <summary>A role is privileged when it holds a permission flagged sensitive (ruling Q10).</summary>
    public bool IsPrivileged { get; set; }

    public Dictionary<string, ScopeRule> Scopes { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string Resource, string Field), FieldAccessLevel> Fields { get; } = [];

    public ScopeRule? ScopeFor(string resource) =>
        Scopes.TryGetValue(resource, out var scope) ? scope : Scopes.GetValueOrDefault(AnyResource);

    /// <summary>
    /// The role's access to one field: its own rule for the field, else its rule for the field's class on the resource,
    /// else on every resource (<c>*</c>, <c>class:salary</c>), else the default: salary hidden (GHR-06), anything else editable.
    /// </summary>
    public FieldAccessLevel FieldLevel(string resource, string key, string? fieldClass)
    {
        if (Fields.TryGetValue((resource, key), out var level))
        {
            return level;
        }

        if (key.StartsWith(FieldAccessMap.CustomPrefix, StringComparison.Ordinal))
        {
            return FieldLevel(resource, "custom", null);
        }

        if (fieldClass is not null
            && (Fields.TryGetValue((resource, ClassPrefix + fieldClass), out level) || Fields.TryGetValue((AnyResource, ClassPrefix + fieldClass), out level)))
        {
            return level;
        }

        return fieldClass == "salary" ? FieldAccessLevel.Hidden : FieldAccessLevel.Editable;
    }
}

/// <summary>Everything the engine needs about one user on one date, read in a single round trip (ruling D: no shared cache).</summary>
internal sealed class AccessSnapshot
{
    public static AccessSnapshot Empty { get; } = new([], new Dictionary<Guid, RoleRules>());

    public AccessSnapshot(IReadOnlyList<Assignment> assignments, IReadOnlyDictionary<Guid, RoleRules> roles)
    {
        Assignments = assignments;
        Roles = roles;
    }

    public IReadOnlyList<Assignment> Assignments { get; }

    public IReadOnlyDictionary<Guid, RoleRules> Roles { get; }

    public bool Holds(string permission) => Assignments.Any(a => Roles.TryGetValue(a.RoleId, out var r) && r.Permissions.Contains(permission));

    /// <summary>The assignments whose role grants the permission.</summary>
    public IEnumerable<(Assignment Assignment, RoleRules Role)> Granting(string permission) =>
        Assignments.Select(a => (a, Roles.GetValueOrDefault(a.RoleId))).Where(x => x.Item2 is not null && x.Item2.Permissions.Contains(permission)).Select(x => (x.a, x.Item2!));

    /// <summary>
    /// The roles valid for the user today: their own assignments and their groups' (CR-019), for roles that are neither
    /// archived nor inactive, in groups that are neither archived nor inactive. Validity uses the '[)' date range of the
    /// eff trait, so an assignment ends at the start of its valid_to date.
    /// </summary>
    internal const string AssignmentsCte = """
        with assignments as (
            select ur.role_id, ur.company_id, ur.created_at from iam.user_roles ur
            where ur.user_id = $1 and ur.valid_from <= $2 and (ur.valid_to is null or ur.valid_to > $2)
            union all
            select gr.role_id, gr.company_id, gr.created_at from iam.user_group_roles gr
            join iam.user_group_members m on m.group_id = gr.group_id and m.user_id = $1
            join iam.user_groups g on g.id = gr.group_id and not g.is_deleted and g.is_active
            where gr.valid_from <= $2 and (gr.valid_to is null or gr.valid_to > $2)
        ), valid as (
            select a.role_id, a.company_id, a.created_at from assignments a
            join iam.roles r on r.id = a.role_id and not r.is_deleted and r.is_active
        )
        """;

    public static async Task<AccessSnapshot> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var sql in new[]
        {
            AssignmentsCte + "select role_id, company_id, created_at from valid",
            AssignmentsCte + """
                select rp.role_id, p.code, p.is_sensitive from iam.role_permissions rp
                join iam.permissions p on p.id = rp.permission_id and p.retired_at is null
                where rp.role_id in (select role_id from valid)
                """,
            AssignmentsCte + """
                select rds.role_id, rds.resource_type, ds.kind, ds.definition::text from iam.role_data_scopes rds
                join iam.data_scopes ds on ds.id = rds.data_scope_id and ds.is_active
                where rds.role_id in (select role_id from valid)
                """,
            AssignmentsCte + """
                select fp.role_id, fp.resource_type, fp.field_key, fp.access from iam.field_policies fp
                where fp.role_id in (select role_id from valid)
                """,
        })
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = today });
            batch.BatchCommands.Add(command);
        }

        var assignments = new List<Assignment>();
        var roles = new Dictionary<Guid, RoleRules>();
        RoleRules Role(Guid id) => roles.TryGetValue(id, out var r) ? r : roles[id] = new RoleRules();
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assignments.Add(new Assignment(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2)));
            Role(reader.GetGuid(0));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var role = Role(reader.GetGuid(0));
            role.Permissions.Add(reader.GetString(1));
            role.IsPrivileged |= reader.GetBoolean(2);
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Role(reader.GetGuid(0)).Scopes[reader.GetString(1)] = ScopeRule.Parse(reader.GetString(2), reader.GetString(3));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Role(reader.GetGuid(0)).Fields[(reader.GetString(1), reader.GetString(2))] = ParseLevel(reader.GetString(3));
        }

        return new AccessSnapshot(assignments, roles);
    }

    public static FieldAccessLevel ParseLevel(string access) => access switch
    {
        "editable" => FieldAccessLevel.Editable,
        "read_only" => FieldAccessLevel.ReadOnly,
        "masked" => FieldAccessLevel.Masked,
        _ => FieldAccessLevel.Hidden,
    };

    public static string LevelText(FieldAccessLevel level) => level switch
    {
        FieldAccessLevel.Editable => "editable",
        FieldAccessLevel.ReadOnly => "read_only",
        FieldAccessLevel.Masked => "masked",
        _ => "hidden",
    };
}
