using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>The product release packs are checked against (compatible product versions, FR-KRN-CFG-01).</summary>
public static class ProductRelease
{
    /// <summary>Major and minor version of this build (codegen-settings.yaml <c>release</c>).</summary>
    public const string Version = "1.0";
}

/// <summary>One object of a pack: its type, key, file in the pack and the SHA-256 of that file (hex, lower case).</summary>
public sealed record PackObjectEntry(string Type, string Key, string File, string Sha256);

/// <summary>A pack this pack needs, installed at <see cref="From"/> or later.</summary>
public sealed record PackDependency(string Id, string From);

/// <summary>
/// The manifest of a pack (slice S1.23 Build item 7, ruling Q6; FR-KRN-CFG-01): id, kind, version, the country of a country
/// pack, the entitlement key that licenses it, titles and release notes per language, compatible product versions,
/// dependencies, the merge rule of every object type and the objects with the hash of each file. The signature covers the
/// canonical JSON of this manifest, so every file is covered through its hash.
/// </summary>
public sealed partial record PackManifest(
    int Format,
    string Id,
    string Kind,
    string Version,
    string? Country,
    string Entitlement,
    IReadOnlyDictionary<string, string> Title,
    IReadOnlyDictionary<string, string> ReleaseNotes,
    string CompatibleFrom,
    string? CompatibleTo,
    IReadOnlyList<PackDependency> Dependencies,
    IReadOnlyDictionary<string, string> MergeRules,
    IReadOnlyList<PackObjectEntry> Objects)
{
    public const int CurrentFormat = 1;

    /// <summary>The one merge rule of Release 1.0 (ruling Q7): the customer's value always stays; the pack's is offered.</summary>
    public const string KeepTenant = "keep_tenant";

    public const int MaxObjects = 20000;

    private static readonly string[] Kinds = ["country", "industry", "template", "language"];

    /// <summary>
    /// Object types that carry statutory content (rates, slabs, thresholds, statutes, forms). No handler exists for them in
    /// Release 1.0; they arrive with their slices after practitioner validation (AS-4, R-002, ruling Q11), so a pack naming one
    /// is refused by name even before the handler check.
    /// </summary>
    public static readonly IReadOnlySet<string> StatutoryTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "tax_table", "tax_slab", "tax_rate", "contribution_rate", "contribution_rule", "leave_statute", "working_time_limit",
        "statutory_form", "statutory_report", "statutory_deduction", "minimum_wage", "threshold", "rate", "slab",
    };

    public JsonObject ToJson() => new()
    {
        ["format"] = Format,
        ["id"] = Id,
        ["kind"] = Kind,
        ["version"] = Version,
        ["country"] = Country,
        ["entitlement"] = Entitlement,
        ["title"] = Texts(Title),
        ["releaseNotes"] = Texts(ReleaseNotes),
        ["compatible"] = new JsonObject { ["from"] = CompatibleFrom, ["to"] = CompatibleTo },
        ["dependencies"] = new JsonArray([.. Dependencies.Select(d => (JsonNode)new JsonObject { ["id"] = d.Id, ["from"] = d.From })]),
        ["mergeRules"] = new JsonObject(MergeRules.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        ["objects"] = new JsonArray([.. Objects.Select(o => (JsonNode)new JsonObject { ["type"] = o.Type, ["key"] = o.Key, ["file"] = o.File, ["sha256"] = o.Sha256 })]),
    };

    /// <summary>Reads and checks a manifest; every problem is an <see cref="InvalidDataException"/> naming the field.</summary>
    public static PackManifest FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var format = json["format"] is JsonValue f && f.TryGetValue<int>(out var n) ? n : throw Bad("format", "a whole number");
        if (format != CurrentFormat)
        {
            throw new InvalidDataException($"Pack format {format} is not supported by this release (format {CurrentFormat}).");
        }

        var id = Text(json, "id");
        if (!IdPattern().IsMatch(id))
        {
            throw Bad("id", "lower-case letters, digits and hyphens, 3 to 40 characters, starting with a letter (e.g. country-pk)");
        }

        var kind = Text(json, "kind");
        if (!Kinds.Contains(kind, StringComparer.Ordinal))
        {
            throw Bad("kind", "country, industry, template or language");
        }

        var version = Text(json, "version");
        _ = PackVersions.Parse(version, "version");
        var country = json["country"]?.GetValue<string>();
        if (kind == "country" ? country is null || !CountryPattern().IsMatch(country) : country is not null)
        {
            throw Bad("country", kind == "country" ? "the two capital letters of the country (a country pack has one)" : "absent (only a country pack has a country)");
        }

        var entitlement = Text(json, "entitlement");
        var compatible = json["compatible"] as JsonObject ?? throw Bad("compatible", "an object with from (and optionally to)");
        var from = compatible["from"]?.GetValue<string>() ?? throw Bad("compatible.from", "a product version such as 1.0");
        var to = compatible["to"]?.GetValue<string>();
        _ = PackVersions.ParseRelease(from, "compatible.from");
        if (to is not null && PackVersions.CompareRelease(PackVersions.ParseRelease(to, "compatible.to"), PackVersions.ParseRelease(from, "compatible.from")) < 0)
        {
            throw Bad("compatible.to", "not before compatible.from");
        }

        var dependencies = new List<PackDependency>();
        foreach (var node in json["dependencies"] as JsonArray ?? [])
        {
            var d = node as JsonObject ?? throw Bad("dependencies", "a list of {id, from}");
            var dependency = new PackDependency(Text(d, "id"), Text(d, "from"));
            _ = PackVersions.Parse(dependency.From, "dependencies.from");
            if (dependency.Id == id)
            {
                throw Bad("dependencies", "other packs (a pack cannot depend on itself)");
            }

            dependencies.Add(dependency);
        }

        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, rule) in json["mergeRules"] as JsonObject ?? throw Bad("mergeRules", "an object of object type to merge rule"))
        {
            var value = rule?.GetValue<string>();
            if (value != KeepTenant)
            {
                throw new InvalidDataException($"Merge rule '{value}' of '{type}' is not supported; Release 1.0 has '{KeepTenant}' only (ruling Q7).");
            }

            rules[type] = value;
        }

        var objects = new List<PackObjectEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in json["objects"] as JsonArray ?? throw Bad("objects", "a list"))
        {
            var o = node as JsonObject ?? throw Bad("objects", "a list of {type, key, file, sha256}");
            var entry = new PackObjectEntry(Text(o, "type"), Text(o, "key"), Text(o, "file"), Text(o, "sha256"));
            if (StatutoryTypes.Contains(entry.Type))
            {
                throw new InvalidDataException($"Object type '{entry.Type}' is statutory content; no pack may carry it before its slice and practitioner validation (AS-4, R-002).");
            }

            if (!TypePattern().IsMatch(entry.Type) || entry.Key.Length is 0 or > 200 || entry.Key.Contains('@', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Object '{entry.Type}' '{entry.Key}': the type is lower-case letters and underscores; the key has 1 to 200 characters and no '@'.");
            }

            if (!PackArchive.SafePath(entry.File) || !entry.File.StartsWith("objects/", StringComparison.Ordinal) || !files.Add(entry.File))
            {
                throw new InvalidDataException($"Object '{entry.Type}' '{entry.Key}': file '{entry.File}' must be a unique relative path under objects/.");
            }

            if (!Sha256Pattern().IsMatch(entry.Sha256))
            {
                throw new InvalidDataException($"Object '{entry.Type}' '{entry.Key}': sha256 must be 64 lower-case hexadecimal characters.");
            }

            if (!seen.Add(entry.Type + "|" + entry.Key))
            {
                throw new InvalidDataException($"Object '{entry.Type}' '{entry.Key}' is listed twice.");
            }

            if (!rules.ContainsKey(entry.Type))
            {
                throw new InvalidDataException($"Object type '{entry.Type}' has no merge rule in mergeRules.");
            }

            objects.Add(entry);
        }

        if (objects.Count > MaxObjects)
        {
            throw new InvalidDataException($"A pack holds at most {MaxObjects} objects.");
        }

        return new PackManifest(format, id, kind, version, country, entitlement, ReadTexts(json, "title", required: true), ReadTexts(json, "releaseNotes", required: false),
            from, to, dependencies, rules, objects);
    }

    /// <summary>True when this product release is within the pack's compatible versions.</summary>
    public bool CompatibleWith(string productVersion)
    {
        var product = PackVersions.ParseRelease(productVersion, "product");
        return PackVersions.CompareRelease(product, PackVersions.ParseRelease(CompatibleFrom, "compatible.from")) >= 0
            && (CompatibleTo is null || PackVersions.CompareRelease(product, PackVersions.ParseRelease(CompatibleTo, "compatible.to")) <= 0);
    }

    private static JsonObject Texts(IReadOnlyDictionary<string, string> texts) =>
        new(texts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));

    private static Dictionary<string, string> ReadTexts(JsonObject json, string name, bool required)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json[name] is not JsonObject texts)
        {
            return required ? throw Bad(name, "an object with an English text (en) and optionally Urdu (ur)") : result;
        }

        foreach (var (language, text) in texts)
        {
            var value = text?.GetValue<string>();
            if (language is not ("en" or "ur") || string.IsNullOrWhiteSpace(value) || value.Length > 2000)
            {
                throw Bad(name, "texts in en and ur of 1 to 2000 characters");
            }

            result[language] = value;
        }

        return required && !result.ContainsKey("en") ? throw Bad(name, "an English text (en)") : result;
    }

    private static string Text(JsonObject json, string name) =>
        json[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw Bad(name, "a text");

    private static InvalidDataException Bad(string field, string expected) => new($"Pack manifest: {field} must be {expected}.");

    [GeneratedRegex("^[a-z][a-z0-9-]{2,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPattern();

    [GeneratedRegex("^[a-z][a-z_]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}

/// <summary>Pack versions (<c>major.minor.patch</c>) and product releases (<c>major.minor</c>), compared as numbers.</summary>
public static partial class PackVersions
{
    public static (int Major, int Minor, int Patch) Parse(string version, string field)
    {
        var m = VersionPattern().Match(version ?? string.Empty);
        return m.Success
            ? (Int(m.Groups[1].Value), Int(m.Groups[2].Value), Int(m.Groups[3].Value))
            : throw new InvalidDataException($"Pack manifest: {field} must be a version such as 1.0.0.");
    }

    public static int Compare(string a, string b)
    {
        var x = Parse(a, "version");
        var y = Parse(b, "version");
        return x.CompareTo(y);
    }

    public static (int Major, int Minor) ParseRelease(string release, string field)
    {
        var m = ReleasePattern().Match(release ?? string.Empty);
        return m.Success
            ? (Int(m.Groups[1].Value), Int(m.Groups[2].Value))
            : throw new InvalidDataException($"Pack manifest: {field} must be a product version such as 1.0.");
    }

    public static int CompareRelease((int Major, int Minor) a, (int Major, int Minor) b) => a.CompareTo(b);

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex("^(0|[1-9][0-9]{0,3})\\.(0|[1-9][0-9]{0,3})\\.(0|[1-9][0-9]{0,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^(0|[1-9][0-9]{0,3})\\.(0|[1-9][0-9]{0,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();
}
