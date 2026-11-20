using System.Text.Json;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>A row of cfg.settings with everything the settings centre shows (FR-ADM-SET-01, FR-ADM-SET-02).</summary>
public sealed record SettingRow(
    string Key,
    string Module,
    string DataType,
    JsonElement Default,
    JsonElement Validation,
    IReadOnlyList<string> Layers,
    bool RequiresApproval,
    JsonElement Description,
    bool IsVisible,
    string? Area,
    JsonElement? Name,
    JsonElement? Help,
    string? Unit,
    string? RoleRule,
    bool SelfService,
    bool TightenOnly)
{
    public SettingDefinition Definition { get; } = new(Key, DataType, Default, SettingValidation.Parse(Validation), Layers, RoleRule, TightenOnly);
}

/// <summary>A stored value of one setting (a row of cfg.setting_values).</summary>
public sealed record SettingValueRow(Guid Id, string Key, StoredSettingValue Value, long RowVersion);

/// <summary>A row of cfg.feature_flags.</summary>
public sealed record FlagRow(string Key, bool DefaultEnabled, JsonElement Description, string? Module, string? EntitlementKey, JsonElement? Name, JsonElement? Help);

/// <summary>A row of cfg.feature_flag_overrides.</summary>
public sealed record FlagOverrideRow(Guid Id, string Key, FlagOverride Override, long RowVersion);

/// <summary>
/// Reads definitions and stored values on a given connection and transaction (slice S1.09a build item 2), for callers that
/// must read inside their own transaction (identity, numbering, access) and for the cache's loads. Context rules (ruling
/// Q11): the company in context, the roles the user holds on the day and whose assignment covers that company (all their
/// roles when no company is in context), and the user's own value.
/// </summary>
public static class SettingsStore
{
    private const string DefinitionColumns = """
        s.key, s.module_id, s.data_type, s.default_value::text, s.validation::text, s.allowed_layers, s.requires_approval, s.description_i18n::text,
        s.is_visible, s.area, s.name_i18n::text, s.help_i18n::text, s.unit, s.role_rule, s.self_service, s.tighten_only
        """;

    public static async Task<Dictionary<string, SettingRow>> DefinitionsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, IReadOnlyCollection<string>? keys, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, $"select {DefinitionColumns} from cfg.settings s where $1::text[] is null or s.key = any($1) order by s.key",
            (object?)keys?.ToArray() ?? DBNull.Value);
        command.Parameters[0].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text;
        var result = new Dictionary<string, SettingRow>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new SettingRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), Json(reader.GetString(3)), Json(reader.GetString(4)),
                reader.GetFieldValue<string[]>(5), reader.GetBoolean(6), Json(reader.GetString(7)), reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : Json(reader.GetString(10)),
                reader.IsDBNull(11) ? null : Json(reader.GetString(11)), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetBoolean(14), reader.GetBoolean(15));
            result[row.Key] = row;
        }

        return result;
    }

    /// <summary>
    /// Stored values of the keys (all keys when null) that apply to the context: pack and tenant values, the company's,
    /// the given roles' and the user's. <paramref name="allCompanies"/> and <paramref name="allRoles"/> read every company
    /// or role value (the cache's load); user values are read only for <paramref name="userId"/>.
    /// </summary>
    public static async Task<List<SettingValueRow>> ValuesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, IReadOnlyCollection<string>? keys, Guid? companyId, IReadOnlyCollection<Guid> roleIds, Guid? userId, CancellationToken ct, bool allCompanies = false, bool allRoles = false)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        await using var command = IdentityDb.Command(c, tx, """
            select v.id, v.setting_key, v.layer, v.scope_id, v.value::text, v.updated_at, v.updated_by, v.row_version
            from cfg.setting_values v
            where ($1::text[] is null or v.setting_key = any($1))
              and (v.layer in ('pack', 'tenant')
                   or (v.layer = 'company' and ($5 or v.scope_id = $2))
                   or (v.layer = 'role' and ($6 or v.scope_id = any($3)))
                   or (v.layer = 'user' and v.scope_id = $4))
            order by v.setting_key, v.layer, v.scope_id
            """, (object?)keys?.ToArray() ?? DBNull.Value, (object?)companyId ?? DBNull.Value, roleIds.ToArray(), (object?)userId ?? DBNull.Value, allCompanies, allRoles);
        command.Parameters[0].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text;
        command.Parameters[1].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid;
        command.Parameters[3].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        return await ReadValuesAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>The user-layer values of one user (the cache loads them on first use per user).</summary>
    public static async Task<List<SettingValueRow>> UserValuesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid userId, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, """
            select v.id, v.setting_key, v.layer, v.scope_id, v.value::text, v.updated_at, v.updated_by, v.row_version
            from cfg.setting_values v where v.layer = 'user' and v.scope_id = $1 order by v.setting_key
            """, userId);
        return await ReadValuesAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The roles that count for the role layer (ruling Q11): held on <paramref name="today"/> (own and group assignments, as the
    /// access engine reads them) through an assignment that covers the company in context, or any assignment when there is none.
    /// </summary>
    public static async Task<Guid[]> RoleIdsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid userId, Guid? companyId, DateOnly today, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, Access.AccessSnapshot.AssignmentsCte + """
            select distinct role_id from valid where $3::uuid is null or company_id is null or company_id = $3 order by role_id
            """, userId, today, (object?)companyId ?? DBNull.Value);
        command.Parameters[1].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date;
        command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    /// <summary>
    /// The effective values of the keys for a context, read now in the caller's transaction (no cache). With a company in
    /// context, the country-pack defaults of its country are the pack layer (<see cref="CountryPackDefaults"/>, CR-061).
    /// </summary>
    public static async Task<Dictionary<string, ResolvedSetting>> ResolveAsync(NpgsqlConnection c, NpgsqlTransaction? tx, IReadOnlyCollection<string> keys, Guid? companyId, Guid? userId, DateOnly today, CancellationToken ct)
    {
        var definitions = await DefinitionsAsync(c, tx, keys, ct).ConfigureAwait(false);
        var roles = userId is { } user && definitions.Values.Any(d => d.RoleRule is not null)
            ? await RoleIdsAsync(c, tx, user, companyId, today, ct).ConfigureAwait(false)
            : [];
        var values = (await ValuesAsync(c, tx, keys, companyId, roles, userId, ct).ConfigureAwait(false)).ToLookup(v => v.Key, StringComparer.Ordinal);
        var country = companyId is { } company && definitions.Keys.Any(CountryPackDefaults.Keys.Contains)
            ? await CountryPackDefaults.CountryAsync(c, tx, company, ct).ConfigureAwait(false)
            : null;
        return definitions.ToDictionary(d => d.Key, d => SettingResolution.Resolve(d.Value.Definition, CountryPackDefaults.With(values[d.Key].Select(v => v.Value), country, d.Key)), StringComparer.Ordinal);
    }

    public static async Task<Dictionary<string, FlagRow>> FlagsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, """
            select key, default_enabled, description_i18n::text, module_id, entitlement_key, name_i18n::text, help_i18n::text from cfg.feature_flags order by key
            """);
        var result = new Dictionary<string, FlagRow>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new FlagRow(reader.GetString(0), reader.GetBoolean(1), Json(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : Json(reader.GetString(5)), reader.IsDBNull(6) ? null : Json(reader.GetString(6)));
        }

        return result;
    }

    public static async Task<List<FlagOverrideRow>> FlagOverridesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string? key, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, """
            select id, flag_key, layer, scope_id, enabled, updated_at, updated_by, row_version from cfg.feature_flag_overrides
            where $1::text is null or flag_key = $1 order by flag_key, layer, scope_id
            """, (object?)key ?? DBNull.Value);
        command.Parameters[0].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        var result = new List<FlagOverrideRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new FlagOverrideRow(reader.GetGuid(0), reader.GetString(1),
                new FlagOverride(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetBoolean(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(6)),
                reader.GetInt64(7)));
        }

        return result;
    }

    /// <summary>The user groups the user belongs to (active groups only), for flag overrides by group.</summary>
    public static async Task<Guid[]> GroupIdsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid userId, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, """
            select m.group_id from iam.user_group_members m join iam.user_groups g on g.id = m.group_id and not g.is_deleted and g.is_active
            where m.user_id = $1 order by m.group_id
            """, userId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    /// <summary>The settings and flags change counters (cfg.config_versions; 0 before the first change).</summary>
    public static async Task<(long Settings, long Flags)> VersionsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, """
            select coalesce(max(version) filter (where name = 'settings'), 0), coalesce(max(version) filter (where name = 'flags'), 0) from cfg.config_versions
            """);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<List<SettingValueRow>> ReadValuesAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var result = new List<SettingValueRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SettingValueRow(reader.GetGuid(0), reader.GetString(1),
                new StoredSettingValue(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), Json(reader.GetString(4)), reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(6)),
                reader.GetInt64(7)));
        }

        return result;
    }
}
