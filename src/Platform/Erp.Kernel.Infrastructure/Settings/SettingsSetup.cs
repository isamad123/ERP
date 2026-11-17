using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>A module's settings manifest as the generator writes it (<c>Access/settings.g.json</c>, slice S1.09a).</summary>
public sealed class SettingsManifest
{
    public const string FileName = "settings.g.json";

    [JsonPropertyName("module")]
    public required string Module { get; init; }

    /// <summary>The module ids (schemas) this manifest speaks for: their settings not listed here are hidden.</summary>
    [JsonPropertyName("moduleIds")]
    public required IReadOnlyList<string> ModuleIds { get; init; }

    [JsonPropertyName("settings")]
    public required IReadOnlyList<ManifestSettingEntry> Settings { get; init; }

    [JsonPropertyName("flags")]
    public required IReadOnlyList<ManifestFlagEntry> Flags { get; init; }

    /// <summary>The manifest in a module's manifest folder, or null when the module declares no settings.</summary>
    public static SettingsManifest? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<SettingsManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is empty.")
            : null;
    }

    /// <summary>The kernel's manifest, also embedded in this assembly (the kernel is always installed).</summary>
    public static SettingsManifest Kernel { get; } = LoadKernel();

    private static SettingsManifest LoadKernel()
    {
        using var stream = typeof(SettingsManifest).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Access.settings.g.json")
            ?? throw new InvalidOperationException("settings.g.json is not embedded.");
        return JsonSerializer.Deserialize<SettingsManifest>(stream) ?? throw new InvalidDataException("settings.g.json is empty.");
    }
}

public sealed class ManifestSettingEntry
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("module")]
    public required string Module { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("default")]
    public required JsonNode Default { get; init; }

    [JsonPropertyName("validation")]
    public required JsonObject Validation { get; init; }

    [JsonPropertyName("layers")]
    public required IReadOnlyList<string> Layers { get; init; }

    [JsonPropertyName("roleRule")]
    public string? RoleRule { get; init; }

    [JsonPropertyName("tightenOnly")]
    public bool TightenOnly { get; init; }

    [JsonPropertyName("selfService")]
    public bool SelfService { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; init; }

    [JsonPropertyName("area")]
    public required string Area { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    [JsonPropertyName("name")]
    public required JsonObject Name { get; init; }

    [JsonPropertyName("description")]
    public required JsonObject Description { get; init; }

    [JsonPropertyName("help")]
    public required JsonObject Help { get; init; }
}

public sealed class ManifestFlagEntry
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("module")]
    public required string Module { get; init; }

    [JsonPropertyName("default")]
    public bool Default { get; init; }

    [JsonPropertyName("entitlementKey")]
    public string? EntitlementKey { get; init; }

    [JsonPropertyName("name")]
    public required JsonObject Name { get; init; }

    [JsonPropertyName("description")]
    public required JsonObject Description { get; init; }

    [JsonPropertyName("help")]
    public required JsonObject Help { get; init; }
}

/// <summary>
/// Settings set-up of a customer database (slice S1.09a build item 1, ruling Q10; part of FR-KRN-TEN-02): registers the
/// settings and feature flags of every installed module's manifest in cfg.settings and cfg.feature_flags. Run by
/// provisioning and by the migrator's <c>migrate</c> after the migrations, before access and numbering set-up. Idempotent,
/// keyed by setting key. A setting no longer declared by its module's manifest keeps its definition and every stored value
/// and is marked hidden (it appears again if declared again); customer values in cfg.setting_values are never touched.
/// Registration is not change-logged (machinery, like baseline roles); it raises the settings and flags change counters,
/// so running hosts refresh their cached definitions.
/// </summary>
public static class SettingsSetup
{
    /// <summary>Registers the manifests found in the modules' manifest folders; the kernel's embedded manifest when none is the kernel's.</summary>
    public static Task EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestFolders);
        var manifests = manifestFolders.Select(SettingsManifest.Load).OfType<SettingsManifest>().ToList();
        if (!manifests.Exists(m => m.Module == SettingsManifest.Kernel.Module))
        {
            manifests.Insert(0, SettingsManifest.Kernel);
        }

        return EnsureAsync(connection, transaction, manifests, cancellationToken);
    }

    public static async Task EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<SettingsManifest> manifests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(manifests);
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IdentityDb.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('cfg.settings_setup'))", cancellationToken).ConfigureAwait(false);
            foreach (var manifest in manifests)
            {
                foreach (var s in manifest.Settings)
                {
                    await IdentityDb.ExecuteAsync(connection, tx, """
                        insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n,
                                                  is_visible, area, name_i18n, help_i18n, unit, role_rule, self_service, tighten_only)
                        values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
                        on conflict (key) do update set module_id = excluded.module_id, data_type = excluded.data_type, default_value = excluded.default_value,
                            validation = excluded.validation, allowed_layers = excluded.allowed_layers, requires_approval = excluded.requires_approval,
                            description_i18n = excluded.description_i18n, is_visible = excluded.is_visible, area = excluded.area, name_i18n = excluded.name_i18n,
                            help_i18n = excluded.help_i18n, unit = excluded.unit, role_rule = excluded.role_rule, self_service = excluded.self_service,
                            tighten_only = excluded.tighten_only
                        where (cfg.settings.module_id, cfg.settings.data_type, cfg.settings.default_value, cfg.settings.validation, cfg.settings.allowed_layers,
                               cfg.settings.requires_approval, cfg.settings.description_i18n, cfg.settings.is_visible, cfg.settings.area, cfg.settings.name_i18n,
                               cfg.settings.help_i18n, cfg.settings.unit, cfg.settings.role_rule, cfg.settings.self_service, cfg.settings.tighten_only)
                              is distinct from
                              (excluded.module_id, excluded.data_type, excluded.default_value, excluded.validation, excluded.allowed_layers,
                               excluded.requires_approval, excluded.description_i18n, excluded.is_visible, excluded.area, excluded.name_i18n,
                               excluded.help_i18n, excluded.unit, excluded.role_rule, excluded.self_service, excluded.tighten_only)
                        """, cancellationToken, s.Key, s.Module, s.Type, new JsonPayload(s.Default.ToJsonString()), new JsonPayload(s.Validation.ToJsonString()),
                        s.Layers.ToArray(), s.RequiresApproval, new JsonPayload(s.Description.ToJsonString()), s.Visible, s.Area,
                        new JsonPayload(s.Name.ToJsonString()), new JsonPayload(s.Help.ToJsonString()), s.Unit, s.RoleRule, s.SelfService, s.TightenOnly).ConfigureAwait(false);
                }

                // Declared no more: keep the definition and its values, hide it (ruling Q10).
                await IdentityDb.ExecuteAsync(connection, tx, """
                    update cfg.settings set is_visible = false where is_visible and module_id = any($1) and not (key = any($2))
                    """, cancellationToken, manifest.ModuleIds.ToArray(), manifest.Settings.Select(s => s.Key).ToArray()).ConfigureAwait(false);

                foreach (var f in manifest.Flags)
                {
                    await IdentityDb.ExecuteAsync(connection, tx, """
                        insert into cfg.feature_flags (key, default_enabled, description_i18n, module_id, entitlement_key, name_i18n, help_i18n)
                        values ($1, $2, $3, $4, $5, $6, $7)
                        on conflict (key) do update set default_enabled = excluded.default_enabled, description_i18n = excluded.description_i18n,
                            module_id = excluded.module_id, entitlement_key = excluded.entitlement_key, name_i18n = excluded.name_i18n, help_i18n = excluded.help_i18n
                        where (cfg.feature_flags.default_enabled, cfg.feature_flags.description_i18n, cfg.feature_flags.module_id, cfg.feature_flags.entitlement_key,
                               cfg.feature_flags.name_i18n, cfg.feature_flags.help_i18n)
                              is distinct from
                              (excluded.default_enabled, excluded.description_i18n, excluded.module_id, excluded.entitlement_key, excluded.name_i18n, excluded.help_i18n)
                        """, cancellationToken, f.Key, f.Default, new JsonPayload(f.Description.ToJsonString()), f.Module, f.EntitlementKey,
                        new JsonPayload(f.Name.ToJsonString()), new JsonPayload(f.Help.ToJsonString())).ConfigureAwait(false);
                }
            }

            if (own)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (own)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
