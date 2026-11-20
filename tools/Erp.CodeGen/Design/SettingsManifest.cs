using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Kernel.Domain.Settings;

namespace Erp.CodeGen.Design;

/// <summary>One setting declared in a module manifest (slice S1.09a, ruling Q10).</summary>
public sealed record ManifestSetting(
    string Key,
    string Module,
    string Type,
    JsonNode Default,
    string ValidationJson,
    IReadOnlyList<string> Layers,
    string? RoleRule,
    bool TightenOnly,
    bool SelfService,
    bool Visible,
    bool RequiresApproval,
    string Area,
    string? Unit,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    IReadOnlyDictionary<string, string> Help);

/// <summary>One feature flag declared in a module manifest (ruling Q14).</summary>
public sealed record ManifestFlag(
    string Key,
    string Module,
    bool Default,
    string? EntitlementKey,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    IReadOnlyDictionary<string, string> Help);

/// <summary>A module's settings manifest: the module ids (schemas) it speaks for, its settings and its flags.</summary>
public sealed record ModuleSettingsManifest(string Module, string Folder, IReadOnlyList<string> ModuleIds, IReadOnlyList<ManifestSetting> Settings, IReadOnlyList<ManifestFlag> Flags);

/// <summary>
/// Reads and checks the settings manifests, <c>&lt;module&gt;/Settings/settings.yaml</c> (slice S1.09a build item 1, rulings
/// Q10, Q11, Q14; CR-036, CR-037). Every default is checked against the kernel's own setting rules
/// (<see cref="SettingValidation"/>, compiled into the generator), so a manifest can never ship a value the server refuses.
/// </summary>
public static partial class SettingsManifestLoader
{
    public const string ManifestPath = "Settings/settings.yaml";

    private static readonly string[] SettingKeys =
        ["key", "type", "default", "validation", "layers", "role_rule", "tighten_only", "self_service", "visible", "requires_approval", "area", "unit", "name", "description", "help"];

    private static readonly string[] FlagKeys = ["key", "default", "entitlement_key", "name", "description", "help"];

    private static readonly string[] Languages = ["en", "ur"];

    /// <summary>The manifests of the kernel and of each business module, where the file exists.</summary>
    public static IReadOnlyList<ModuleSettingsManifest> Load(string root, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var modules = new (string Module, string Folder, IReadOnlyList<string> Ids)[]
        {
            ("kernel", "src/Platform/Erp.Kernel.Infrastructure", settings.KernelSchemas),
            ("hrm", "src/Modules/Hrm/Erp.Hrm.Infrastructure", settings.HrmSchemas),
        };
        var result = new List<ModuleSettingsManifest>();
        foreach (var (module, folder, ids) in modules)
        {
            var path = Path.Combine(root, folder, ManifestPath);
            if (File.Exists(path))
            {
                result.Add(Read(module, folder, ids, YamlNode.Load(path), folder + "/" + ManifestPath));
            }
        }

        var duplicates = result.SelectMany(m => m.Settings.Select(s => s.Key).Concat(m.Flags.Select(f => f.Key)))
            .GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        return duplicates.Count > 0 ? throw new DesignException(string.Join(", ", duplicates), "setting or flag declared more than once") : result;
    }

    public static ModuleSettingsManifest Read(string module, string folder, IReadOnlyList<string> moduleIds, object? document, string where)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        var root = YamlNode.Map(document, where);
        Unknown(root, ["settings", "flags"], where);
        var settings = YamlNode.List(root.GetValueOrDefault("settings"), where + " settings").Select(e => ReadSetting(e, moduleIds, where)).ToList();
        var flags = YamlNode.List(root.GetValueOrDefault("flags"), where + " flags").Select(e => ReadFlag(e, moduleIds, where)).ToList();
        return new ModuleSettingsManifest(module, folder, moduleIds, settings, flags);
    }

    private static ManifestSetting ReadSetting(object? entry, IReadOnlyList<string> moduleIds, string file)
    {
        var map = YamlNode.Map(entry, file);
        var key = YamlNode.Text(map.GetValueOrDefault("key"), file + " key");
        var where = $"{file} setting {key}";
        Unknown(map, SettingKeys, where);
        var moduleId = ModuleOf(key, moduleIds, where);
        var type = YamlNode.Text(map.GetValueOrDefault("type"), where + " type");
        if (!SettingTypes.All.Contains(type, StringComparer.Ordinal))
        {
            throw new DesignException(where, $"type must be one of {string.Join(", ", SettingTypes.All)}");
        }

        var validation = ReadValidation(map.GetValueOrDefault("validation"), where);
        if (validation.DefinitionProblems(type).ToList() is { Count: > 0 } ruleProblems)
        {
            throw new DesignException(where, string.Join("; ", ruleProblems));
        }

        if (!map.ContainsKey("default"))
        {
            throw new DesignException(where, "default is required");
        }

        var defaultValue = ReadValue(map["default"], type, validation, where + " default");
        using (var parsed = JsonDocument.Parse(defaultValue.ToJsonString()))
        {
            if (validation.Problem(type, parsed.RootElement) is { } problem)
            {
                throw new DesignException(where, $"the default breaks the setting's own rules ({problem})");
            }
        }

        var layers = YamlNode.Texts(map.GetValueOrDefault("layers"), where + " layers");
        if (layers.Except(SettingLayers.Stored).ToList() is { Count: > 0 } unknownLayers)
        {
            throw new DesignException(where, $"unknown layers {string.Join(", ", unknownLayers)}; allowed: {string.Join(", ", SettingLayers.Stored)}");
        }

        if (layers.Distinct(StringComparer.Ordinal).Count() != layers.Count)
        {
            throw new DesignException(where, "a layer is listed twice");
        }

        var roleRule = YamlNode.OptionalText(map, "role_rule", where);
        if (layers.Contains(SettingLayers.Role) != (roleRule is not null))
        {
            throw new DesignException(where, "the role layer needs a role_rule, and a role_rule needs the role layer (ruling Q11)");
        }

        if (roleRule is not null && !SettingRoleRules.Fits(roleRule, type))
        {
            throw new DesignException(where, "role_rule is any or all for bool settings, lowest or highest for int and decimal settings");
        }

        var tightenOnly = Flag(map, "tighten_only", false, where);
        if (tightenOnly && type != SettingTypes.Bool)
        {
            throw new DesignException(where, "tighten_only applies to bool settings only (CR-037)");
        }

        var selfService = Flag(map, "self_service", false, where);
        if (selfService && !layers.Contains(SettingLayers.User))
        {
            throw new DesignException(where, "self_service needs the user layer");
        }

        // S1.09a review findings 3 and 8 (CR-061): approval is a maker-checker request by an administrator, so it cannot
        // apply to iam.* keys (they keep the S1.06 rules) or to a value users set for themselves.
        var requiresApproval = Flag(map, "requires_approval", false, where);
        if (requiresApproval && key.StartsWith("iam.", StringComparison.Ordinal))
        {
            throw new DesignException(where, "requires_approval is not available for iam.* settings (they keep the S1.06 rules: second tenant admin)");
        }

        if (requiresApproval && selfService)
        {
            throw new DesignException(where, "a setting that requires_approval cannot be self_service");
        }

        var area = YamlNode.Text(map.GetValueOrDefault("area"), where + " area");
        if (area.Length > 40 || !AreaPattern().IsMatch(area))
        {
            throw new DesignException(where, "area is a code such as security.passwords (lower case, at most 40 characters)");
        }

        return new ManifestSetting(key, moduleId, type, defaultValue, validation.ToJson(), layers, roleRule, tightenOnly, selfService,
            Flag(map, "visible", true, where), requiresApproval, area, YamlNode.OptionalText(map, "unit", where),
            Texts(map, "name", where), Texts(map, "description", where), Texts(map, "help", where));
    }

    private static ManifestFlag ReadFlag(object? entry, IReadOnlyList<string> moduleIds, string file)
    {
        var map = YamlNode.Map(entry, file);
        var key = YamlNode.Text(map.GetValueOrDefault("key"), file + " flag key");
        var where = $"{file} flag {key}";
        Unknown(map, FlagKeys, where);
        var moduleId = ModuleOf(key, moduleIds, where);
        var entitlement = YamlNode.OptionalText(map, "entitlement_key", where);
        if (entitlement is not null && !KeyPattern().IsMatch(entitlement))
        {
            throw new DesignException(where, "entitlement_key is a feature key such as hrm.payroll");
        }

        if (!map.ContainsKey("default"))
        {
            throw new DesignException(where, "default is required");
        }

        return new ManifestFlag(key, moduleId, Flag(map, "default", false, where), entitlement, Texts(map, "name", where), Texts(map, "description", where), Texts(map, "help", where));
    }

    private static string ModuleOf(string key, IReadOnlyList<string> moduleIds, string where)
    {
        if (!KeyPattern().IsMatch(key) || key.Length > 120)
        {
            throw new DesignException(where, "a key is lower case, dot separated, such as iam.password.min_length");
        }

        var module = key[..key.IndexOf('.', StringComparison.Ordinal)];
        return moduleIds.Contains(module, StringComparer.Ordinal)
            ? module
            : throw new DesignException(where, $"the key must start with one of this module's ids ({string.Join(", ", moduleIds)})");
    }

    private static SettingValidation ReadValidation(object? node, string where)
    {
        var map = YamlNode.Map(node, where + " validation");
        Unknown(map, SettingValidation.Keys, where + " validation");
        decimal? Number(string name) => map.TryGetValue(name, out var v) && v is not null
            ? decimal.Parse(YamlNode.Text(v, where + " validation." + name), NumberStyles.Number, CultureInfo.InvariantCulture)
            : null;
        int? Whole(string name) => map.TryGetValue(name, out var v) && v is not null
            ? int.Parse(YamlNode.Text(v, where + " validation." + name), NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
        return new SettingValidation(
            Number("min"),
            Number("max"),
            map.TryGetValue("choices", out var c) && c is not null ? YamlNode.Texts(c, where + " validation.choices") : null,
            YamlNode.OptionalText(map, "pattern", where),
            Whole("max_length"),
            YamlNode.OptionalText(map, "item_type", where),
            Whole("min_items"),
            Whole("max_items"),
            YamlNode.OptionalText(map, "format", where));
    }

    /// <summary>A YAML value as JSON of the setting's type (YAML scalars arrive as text; <c>json</c> settings give JSON text).</summary>
    private static JsonNode ReadValue(object? node, string type, SettingValidation validation, string where)
    {
        string Scalar() => YamlNode.Text(node, where);
        try
        {
            return type switch
            {
                SettingTypes.Bool => Scalar() switch
                {
                    "true" => JsonValue.Create(true),
                    "false" => JsonValue.Create(false),
                    _ => throw new DesignException(where, "expected true or false"),
                },
                SettingTypes.WholeNumber => JsonValue.Create(long.Parse(Scalar(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
                SettingTypes.Number => JsonValue.Create(decimal.Parse(Scalar(), NumberStyles.Number, CultureInfo.InvariantCulture)),
                SettingTypes.Text or SettingTypes.Choice => JsonValue.Create(Scalar()),
                SettingTypes.List => new JsonArray(YamlNode.Texts(node, where)
                    .Select(item => validation.ItemType == SettingTypes.WholeNumber
                        ? (JsonNode)JsonValue.Create(long.Parse(item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
                        : JsonValue.Create(item))
                    .ToArray()),
                _ => JsonNode.Parse(Scalar()) ?? throw new DesignException(where, "a json default cannot be null"),
            };
        }
        catch (FormatException)
        {
            throw new DesignException(where, $"not a {type} value");
        }
        catch (JsonException)
        {
            throw new DesignException(where, "not valid JSON text");
        }
    }

    private static bool Flag(IReadOnlyDictionary<string, object?> map, string key, bool fallback, string where) =>
        map.TryGetValue(key, out var value) && value is not null
            ? YamlNode.Text(value, where + " " + key) switch
            {
                "true" => true,
                "false" => false,
                _ => throw new DesignException(where, key + " is true or false"),
            }
            : fallback;

    private static Dictionary<string, string> Texts(IReadOnlyDictionary<string, object?> map, string key, string where)
    {
        var texts = YamlNode.Map(map.GetValueOrDefault(key), $"{where} {key}");
        if (texts.Count != Languages.Length || !Languages.All(l => texts.TryGetValue(l, out var t) && t is string { Length: > 0 } s && s.Trim() == s))
        {
            throw new DesignException(where, $"{key} needs English and Urdu text (en, ur), nothing else");
        }

        return Languages.ToDictionary(l => l, l => (string)texts[l]!, StringComparer.Ordinal);
    }

    private static void Unknown(IReadOnlyDictionary<string, object?> map, IReadOnlyList<string> known, string where)
    {
        var unknown = map.Keys.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new DesignException(where, "unknown keys: " + string.Join(", ", unknown));
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*$")]
    private static partial Regex AreaPattern();
}
