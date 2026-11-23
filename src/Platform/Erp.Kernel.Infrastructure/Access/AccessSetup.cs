using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Contracts.Security;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>One module's access manifest (slice S1.06): its permission codes and the baseline roles it ships or extends.</summary>
public sealed class AccessManifest
{
    /// <summary>The folder the manifest was read from.</summary>
    public string Module { get; init; } = string.Empty;

    public List<PermissionDefinition> Permissions { get; } = [];

    public List<BaselineRole> Roles { get; } = [];

    public List<BaselineScope> DataScopes { get; } = [];

    /// <summary>
    /// Reads <c>permissions.g.json</c> (generated), <c>permissions.json</c> (a module's hand-written permissions: the actions of
    /// its purpose-built services, slice S1.20a, CR-076) and <c>baseline-roles.json</c> (hand-written data) of a folder.
    /// </summary>
    public static AccessManifest Load(string folder)
    {
        var manifest = new AccessManifest { Module = folder };
        foreach (var name in new[] { "permissions.g.json", "permissions.json" })
        {
            var permissions = Path.Combine(folder, name);
            if (File.Exists(permissions))
            {
                var file = JsonSerializer.Deserialize<PermissionFile>(File.ReadAllText(permissions)) ?? throw new InvalidDataException(permissions + " is empty.");
                manifest.Permissions.AddRange(file.Permissions.Select(p => new PermissionDefinition(p.Code, p.Module, p.DescriptionKey, p.Sensitive)));
            }
        }

        var roles = Path.Combine(folder, "baseline-roles.json");
        if (File.Exists(roles))
        {
            var file = JsonSerializer.Deserialize<RolesFile>(File.ReadAllText(roles)) ?? throw new InvalidDataException(roles + " is empty.");
            manifest.Roles.AddRange(file.Roles);
            manifest.DataScopes.AddRange(file.DataScopes ?? []);
        }

        return manifest;
    }

    private sealed class PermissionFile
    {
        [JsonPropertyName("permissions")]
        public List<PermissionEntry> Permissions { get; init; } = [];
    }

    private sealed class PermissionEntry
    {
        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        [JsonPropertyName("module")]
        public string Module { get; init; } = string.Empty;

        [JsonPropertyName("descriptionKey")]
        public string DescriptionKey { get; init; } = string.Empty;

        [JsonPropertyName("sensitive")]
        public bool Sensitive { get; init; }
    }

    private sealed class RolesFile
    {
        [JsonPropertyName("roles")]
        public List<BaselineRole> Roles { get; init; } = [];

        [JsonPropertyName("data_scopes")]
        public List<BaselineScope>? DataScopes { get; init; }
    }
}

/// <summary>A shipped role (or an extension of another module's role when <see cref="Extends"/> is set).</summary>
public sealed class BaselineRole
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public Dictionary<string, string>? Name { get; init; }

    [JsonPropertyName("extends")]
    public bool Extends { get; init; }

    /// <summary>Codes or patterns; <c>*</c> stands for any text (for example <c>org.*.read</c>).</summary>
    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; init; } = [];

    /// <summary>Resource (or <c>*</c>) to the code of a baseline data scope.</summary>
    [JsonPropertyName("scopes")]
    public Dictionary<string, string>? Scopes { get; init; }

    [JsonPropertyName("fields")]
    public List<BaselineField>? Fields { get; init; }
}

public sealed class BaselineField
{
    [JsonPropertyName("resource")]
    public string Resource { get; init; } = string.Empty;

    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("access")]
    public string Access { get; init; } = string.Empty;
}

public sealed class BaselineScope
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; init; } = [];
}

/// <summary>What one run of <see cref="AccessSetup.EnsureAsync"/> changed (for the migrator's output and the tests).</summary>
public sealed record AccessSetupResult(int PermissionsAdded, int PermissionsRetired, int RolesAdded, int GrantsAdded, IReadOnlyList<string> EditedRolesLeftAlone)
{
    /// <summary>
    /// Mandatory permissions the protected Tenant Admin role still lacks after this run (CR-024). Tenant administration
    /// cannot cause this; only a direct database change can. An unedited role gets them back from the baseline top-up
    /// above; for an edited one <c>migrate</c> only reports it, and a platform Super Admin runs <c>tenant admin-role-repair</c>.
    /// </summary>
    public IReadOnlyList<string> ProtectedRoleProblems { get; init; } = [];
}

/// <summary>
/// Access set-up of a customer database, run after its migrations by provisioning and by the migrator's <c>migrate</c>
/// command (slice S1.06 build items 1 and 3, CR-020):
/// <list type="number">
/// <item>Permission registry (FR-KRN-IAM-10): every installed module's codes plus the identity and access-control codes
/// are written into iam.permissions by code; codes no module declares any more get <c>retired_at</c> (never deleted,
/// they grant nothing).</item>
/// <item>Baseline data scopes (all, own, direct team, subtree) by code.</item>
/// <item>Baseline roles (FR-ADM-ROL-03, Q8): missing roles are added; permissions, scopes and field rules are added only
/// to baseline roles the customer has never changed (every change made through the role services is stamped with the
/// person's id, so <c>updated_by</c> is still the system user only for untouched roles). Nothing is ever removed.</item>
/// </list>
/// Idempotent: a second run changes nothing.
/// </summary>
public static class AccessSetup
{
    public static IReadOnlyList<PermissionDefinition> HandWritten { get; } = [.. IdentityPermissions.All, .. AccessControlPermissions.All, .. Contracts.Audit.AuditPermissions.All, .. Contracts.Numbering.NumberingPermissions.All, .. Contracts.Settings.SettingsPermissions.All, .. Contracts.Entitlements.LicencePermissions.All, .. Contracts.Workflow.WorkflowPermissions.All, .. Contracts.Jobs.JobPermissions.All, .. Contracts.Notifications.NotificationPermissions.All, .. Contracts.Documents.DocumentPermissions.All, .. Contracts.Documents.TemplatePermissions.All, .. Contracts.Views.ViewPermissions.All, .. Contracts.Metadata.MetadataPermissions.All, .. Contracts.Itg.ImportPermissions.All, .. Contracts.Setup.SetupPermissions.All];

    public static async Task<AccessSetupResult> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(manifestFolders);
        var manifests = manifestFolders.Select(AccessManifest.Load).ToList();
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One set-up at a time per customer database (provisioning and migrate may overlap).
            await Exec(connection, tx, "select pg_advisory_xact_lock(hashtext('iam.access_setup'))", cancellationToken).ConfigureAwait(false);
            var permissions = HandWritten.Concat(manifests.SelectMany(m => m.Permissions)).GroupBy(p => p.Code, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var (added, retired) = await RegisterAsync(connection, tx, permissions, now, cancellationToken).ConfigureAwait(false);
            await ScopesAsync(connection, tx, manifests.SelectMany(m => m.DataScopes), now, cancellationToken).ConfigureAwait(false);
            var (rolesAdded, grants, edited) = await RolesAsync(connection, tx, manifests, permissions.Select(p => p.Code).ToList(), now, cancellationToken).ConfigureAwait(false);
            var problems = await ProtectedRoleProblemsAsync(connection, tx, cancellationToken).ConfigureAwait(false);
            if (own)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new AccessSetupResult(added, retired, rolesAdded, grants, edited) { ProtectedRoleProblems = problems };
        }
        finally
        {
            if (own)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<(int Added, int Retired)> RegisterAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<PermissionDefinition> permissions, DateTimeOffset now, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var added = 0;
        foreach (var p in permissions)
        {
            var description = JsonSerializer.Serialize(new Dictionary<string, string> { ["key"] = p.DescriptionKey });
            await using var command = Command(c, tx, """
                insert into iam.permissions (id, code, module_id, description_i18n, is_sensitive, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                on conflict (code) do update set module_id = excluded.module_id, description_i18n = excluded.description_i18n,
                    is_sensitive = excluded.is_sensitive, retired_at = null, updated_at = excluded.updated_at, updated_by = excluded.updated_by,
                    row_version = iam.permissions.row_version + 1
                where (iam.permissions.module_id, iam.permissions.description_i18n, iam.permissions.is_sensitive, iam.permissions.retired_at)
                    is distinct from (excluded.module_id, excluded.description_i18n, excluded.is_sensitive, null::timestamptz)
                returning (xmax = 0)
                """, Guid.CreateVersion7(now), p.Code, p.ModuleId, new Json(description), p.IsSensitive, now, system);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
            {
                added++;
            }
        }

        await using var retire = Command(c, tx, """
            update iam.permissions set retired_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1
            where retired_at is null and not (code = any($1))
            """, permissions.Select(p => p.Code).ToArray(), now, system);
        var retired = await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (added, retired);
    }

    private static async Task ScopesAsync(NpgsqlConnection c, NpgsqlTransaction tx, IEnumerable<BaselineScope> scopes, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var scope in scopes)
        {
            await using var command = Command(c, tx, """
                insert into iam.data_scopes (id, code, name_i18n, kind, definition, created_at, created_by, updated_at, updated_by)
                select $1, $2, $3, $4, '{}'::jsonb, $5, $6, $5, $6
                where not exists (select 1 from iam.data_scopes where lower(code) = lower($2))
                """, Guid.CreateVersion7(now), scope.Code, new Json(JsonSerializer.Serialize(scope.Name)), scope.Kind, now, BaselineSeed.SystemUserId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<(int RolesAdded, int Grants, IReadOnlyList<string> Edited)> RolesAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<AccessManifest> manifests, IReadOnlyList<string> codes, DateTimeOffset now, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        int rolesAdded = 0, grants = 0;
        var edited = new List<string>();
        var definitions = manifests.SelectMany(m => m.Roles).ToList();
        foreach (var group in definitions.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase))
        {
            var main = group.FirstOrDefault(r => !r.Extends);
            if (main is null)
            {
                continue; // an extension of a role whose module is not installed
            }

            await using (var insert = Command(c, tx, """
                insert into iam.roles (id, code, name_i18n, source, baseline_code, created_at, created_by, updated_at, updated_by)
                select $1, $2, $3, 'module', $2, $4, $5, $4, $5
                where not exists (select 1 from iam.roles where lower(code) = lower($2) and not is_deleted)
                """, Guid.CreateVersion7(now), main.Code, new Json(JsonSerializer.Serialize(main.Name ?? new() { ["en"] = main.Code })), now, system))
            {
                rolesAdded += await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            Guid roleId;
            bool untouched;
            await using (var read = Command(c, tx, "select id, updated_by = $2 and created_by = $2 from iam.roles where lower(code) = lower($1) and not is_deleted for update", main.Code, system))
            await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                await reader.ReadAsync(ct).ConfigureAwait(false);
                roleId = reader.GetGuid(0);
                untouched = reader.GetBoolean(1);
            }

            if (!untouched)
            {
                edited.Add(main.Code); // the customer changed it: proposals for edited roles come with the pack loader (S1.23)
                continue;
            }

            // Names follow the shipped text while the customer has not changed the role.
            await Exec(c, tx, "update iam.roles set name_i18n = $2, row_version = row_version + 1 where id = $1 and name_i18n is distinct from $2", ct, roleId, new Json(JsonSerializer.Serialize(main.Name ?? new() { ["en"] = main.Code }))).ConfigureAwait(false);

            var wanted = group.SelectMany(r => r.Permissions).SelectMany(pattern => Matching(pattern, codes)).Distinct(StringComparer.Ordinal).ToArray();
            await using (var grant = Command(c, tx, """
                insert into iam.role_permissions (role_id, permission_id)
                select $1, p.id from iam.permissions p where p.code = any($2) and p.retired_at is null
                on conflict do nothing
                """, roleId, wanted))
            {
                grants += await grant.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var (resource, scopeCode) in group.SelectMany(r => r.Scopes ?? []))
            {
                grants += await ExecCount(c, tx, """
                    insert into iam.role_data_scopes (role_id, resource_type, data_scope_id)
                    select $1, $2, s.id from iam.data_scopes s where lower(s.code) = lower($3)
                    on conflict do nothing
                    """, ct, roleId, resource, scopeCode).ConfigureAwait(false);
            }

            foreach (var field in group.SelectMany(r => r.Fields ?? []))
            {
                grants += await ExecCount(c, tx, """
                    insert into iam.field_policies (id, role_id, resource_type, field_key, access, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                    on conflict (role_id, resource_type, field_key) do nothing
                    """, ct, Guid.CreateVersion7(now), roleId, field.Resource, field.Field, field.Access, now, system).ConfigureAwait(false);
            }
        }

        return (rolesAdded, grants, edited);
    }

    /// <summary>The mandatory permissions (CR-024) the protected Tenant Admin role lacks, as "tenant_admin lacks code" lines.</summary>
    public static async Task<IReadOnlyList<string>> ProtectedRoleProblemsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var problems = new List<string>();
        foreach (var code in ProtectedRoles.Mandatory)
        {
#pragma warning disable CA2100 // constant SQL text; every value is a parameter
            await using var command = new NpgsqlCommand("""
                select exists (select 1 from iam.roles r join iam.role_permissions rp on rp.role_id = r.id join iam.permissions p on p.id = rp.permission_id
                               where lower(r.code) = lower($1) and not r.is_deleted and p.code = $2 and p.retired_at is null)
                """, c, tx);
#pragma warning restore CA2100
            command.Parameters.Add(new NpgsqlParameter { Value = ProtectedRoles.TenantAdmin });
            command.Parameters.Add(new NpgsqlParameter { Value = code });
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
            {
                problems.Add($"{ProtectedRoles.TenantAdmin} lacks {code}");
            }
        }

        return problems;
    }

    /// <summary>The registered codes a manifest entry names: the code itself, or every code matching a pattern with <c>*</c>.</summary>
    public static IEnumerable<string> Matching(string pattern, IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return codes.Where(c => string.Equals(c, pattern, StringComparison.Ordinal));
        }

        var regex = new Regex("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return codes.Where(c => regex.IsMatch(c));
    }

    private sealed record Json(string Text);

    private static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params object?[] values)
    {
#pragma warning disable CA2100 // constant SQL text; every value is a parameter
        var command = new NpgsqlCommand(sql, c, tx);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(value switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value },
                Json json => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json.Text },
                string[] texts => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = texts },
                _ => new NpgsqlParameter { Value = value },
            });
        }

        return command;
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object?[] values) =>
        await ExecCount(c, tx, sql, ct, values).ConfigureAwait(false);

    private static async Task<int> ExecCount(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(c, tx, sql, values);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
