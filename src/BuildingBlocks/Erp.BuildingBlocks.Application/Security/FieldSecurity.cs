using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Entities;

namespace Erp.BuildingBlocks.Application.Security;

/// <summary>What a role may do with one field (FR-KRN-IAM-13). Ordered from closed to open: the most open wins (Q4).</summary>
public enum FieldAccessLevel
{
    Hidden = 0,
    Masked = 1,
    ReadOnly = 2,
    Editable = 3,
}

/// <summary>
/// One field of a resource: its key in field rules (the column name, for example <c>gross_amount</c>), its JSON name in
/// API answers and its field class (data-design key <c>field_class</c>: salary, personal, restricted).
/// <see cref="IsCustom"/> marks the custom-field container; its keys are ruled as <c>custom.&lt;key&gt;</c> (FR-KRN-MET-02).
/// </summary>
public sealed record FieldInfo(string Key, string PropertyName, string? FieldClass, bool IsCustom = false)
{
    public string JsonName { get; } = JsonNamingPolicy.CamelCase.ConvertName(PropertyName);
}

/// <summary>The fields of one resource as the API shows them (generated from the data design).</summary>
public sealed class FieldCatalog(string resource, IReadOnlyList<FieldInfo> fields)
{
    public string Resource { get; } = resource;

    public IReadOnlyList<FieldInfo> Fields { get; } = fields;

    public FieldInfo? Find(string key) => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// The access of the current user to each field of a resource, already merged over their roles. A custom field without
/// its own rule follows the <c>custom</c> field; a key the catalogue does not know is hidden.
/// </summary>
public sealed class FieldAccessMap
{
    public const string CustomPrefix = "custom.";

    private readonly Dictionary<string, FieldAccessLevel> _levels;

    public FieldAccessMap(IReadOnlyDictionary<string, FieldAccessLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        _levels = new Dictionary<string, FieldAccessLevel>(levels, StringComparer.Ordinal);
    }

    /// <summary>Every field editable (the tenant-isolation suite's allow-all double, and resources without rules).</summary>
    public static FieldAccessMap Unrestricted(FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new FieldAccessMap(catalog.Fields.ToDictionary(f => f.Key, _ => FieldAccessLevel.Editable, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, FieldAccessLevel> Levels => _levels;

    public FieldAccessLevel Of(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_levels.TryGetValue(key, out var level))
        {
            return level;
        }

        return key.StartsWith(CustomPrefix, StringComparison.Ordinal) && _levels.TryGetValue("custom", out var custom) ? custom : FieldAccessLevel.Hidden;
    }

    public bool CanEdit(string key) => Of(key) == FieldAccessLevel.Editable;

    /// <summary>The most open access of several maps, field by field (ruling Q4); no map: every field hidden.</summary>
    public static FieldAccessMap Merge(IReadOnlyCollection<FieldAccessMap> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var keys = maps.SelectMany(m => m._levels.Keys).Distinct(StringComparer.Ordinal);
        return new FieldAccessMap(keys.ToDictionary(k => k, k => maps.Max(m => m.Of(k)), StringComparer.Ordinal));
    }

    /// <summary>True when both maps give every field the same access.</summary>
    public bool SameAs(FieldAccessMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _levels.Keys.Union(other._levels.Keys, StringComparer.Ordinal).All(k => Of(k) == other.Of(k));
    }
}

/// <summary>
/// One role assignment's part in a field-access decision: the rows its data scope covers (null: every row) and the
/// fields as that role alone sees them.
/// </summary>
public sealed record FieldAccessPart<T>(Expression<Func<T, bool>>? Covers, FieldAccessMap Map)
    where T : class;

/// <summary>
/// Field access per row (review S1.06 finding 3, CR-025): a row's fields are the most open access among the roles that
/// grant the action <b>and whose data scope covers that row</b>, so a rule written for one's own records (the Employee
/// role's salary rule) never opens the fields of rows seen through another role. <see cref="Uniform"/> is set when the
/// answer is the same for every row, which needs no query.
/// </summary>
public sealed class FieldAccessPlan<T>(FieldAccessMap? uniform, IReadOnlyList<FieldAccessPart<T>> parts)
    where T : class
{
    public FieldAccessMap? Uniform { get; } = uniform;

    public IReadOnlyList<FieldAccessPart<T>> Parts { get; } = parts;
}

/// <summary>Resolves a <see cref="FieldAccessPlan{T}"/> for stored rows.</summary>
public static class FieldAccessPlans
{
    /// <summary>A plan with the same answer for every row.</summary>
    public static FieldAccessPlan<T> Same<T>(FieldAccessMap map)
        where T : class => new(map, []);

    /// <summary>
    /// The field access of each stored row: one id query per part whose scope is not "every row". A row no part covers
    /// (outside the caller's scope, so the scope check answers for it) is judged by all parts together.
    /// </summary>
    public static async Task<IReadOnlyList<FieldAccessMap>> ResolveAsync<T>(this FieldAccessPlan<T> plan, IReadOnlyList<T> rows, IScopeQuerySource source, CancellationToken cancellationToken)
        where T : class, IEntity
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(source);
        if (plan.Uniform is { } uniform)
        {
            return rows.Select(_ => uniform).ToList();
        }

        var ids = rows.Select(r => r.Id).Distinct().ToArray();
        var covered = new List<HashSet<Guid>?>(plan.Parts.Count);
        foreach (var part in plan.Parts)
        {
            covered.Add(part.Covers is null || ids.Length == 0
                ? null
                : (await source.IdsAsync(source.Rows<T>().Where(part.Covers).Where(e => ids.Contains(e.Id)), cancellationToken).ConfigureAwait(false)).ToHashSet());
        }

        var all = FieldAccessMap.Merge(plan.Parts.Select(p => p.Map).ToList());
        var cache = new Dictionary<string, FieldAccessMap>(StringComparer.Ordinal);
        var result = new List<FieldAccessMap>(rows.Count);
        foreach (var row in rows)
        {
            var hits = Enumerable.Range(0, plan.Parts.Count).Where(i => covered[i] is null || covered[i]!.Contains(row.Id)).ToList();
            if (hits.Count == 0)
            {
                result.Add(all);
                continue;
            }

            var key = string.Join(',', hits);
            if (!cache.TryGetValue(key, out var map))
            {
                map = FieldAccessMap.Merge(hits.Select(i => plan.Parts[i].Map).ToList());
                cache[key] = map;
            }

            result.Add(map);
        }

        return result;
    }
}

/// <summary>
/// Field access of the current user (FR-KRN-IAM-13, FR-KRN-MET-02, GHR-06): the merged access of their roles that grant
/// <c>resource.action</c> and are valid for the row's company (all companies when the row has none).
/// </summary>
public interface IFieldAccess
{
    /// <summary>
    /// The merged access of the roles granting the action that are valid for the company (all companies when null).
    /// Used for new rows (create) and for "what may the caller open" checks; stored rows use <see cref="PlanAsync"/>.
    /// </summary>
    ValueTask<FieldAccessMap> ForAsync(FieldCatalog catalog, string action, Guid? companyId, CancellationToken cancellationToken);

    /// <summary>
    /// Field access for a search over many records of the company (audit change search and export, review S1.07 finding
    /// 1): only roles granting the action whose data scope covers every record of <paramref name="catalog"/>'s resource in
    /// that company count (scope "all" or the company itself, without conditions; every role when the resource is not
    /// <paramref name="isScoped"/>). A role that opens a field on some records only (the Employee's own salary) opens it
    /// in the record's history, never in a search across records.
    /// </summary>
    ValueTask<FieldAccessMap> ForEveryRowAsync(FieldCatalog catalog, bool isScoped, string action, Guid? companyId, CancellationToken cancellationToken);

    /// <summary>Field access per stored row: only roles whose data scope covers the row count (review S1.06 finding 3).</summary>
    ValueTask<FieldAccessPlan<T>> PlanAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, string action, IScopeQuerySource? source, CancellationToken cancellationToken)
        where T : class;
}

/// <summary>
/// Applies field access to API answers and requests: hidden fields are left out, masked fields show only their last four
/// characters (ruling Q5), and a change to a field that is not editable is reported, never silently dropped.
/// </summary>
public static class FieldShaping
{
    public const string NotEditable = "errors.access.field_not_editable";

    private const string MaskPrefix = "****";

    public static JsonObject Shape(JsonObject answer, FieldCatalog catalog, FieldAccessMap access)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(access);
        foreach (var field in catalog.Fields)
        {
            var level = access.Of(field.Key);
            if (level == FieldAccessLevel.Hidden)
            {
                answer.Remove(field.JsonName);
                continue;
            }

            if (field.IsCustom)
            {
                if (answer[field.JsonName] is JsonObject custom)
                {
                    ShapeCustom(custom, access);
                }

                continue;
            }

            if (level == FieldAccessLevel.Masked && answer.ContainsKey(field.JsonName))
            {
                answer[field.JsonName] = Mask(answer[field.JsonName]);
            }
        }

        return answer;
    }

    /// <summary>
    /// Applies the rules of each custom field (<c>custom.&lt;key&gt;</c>, FR-KRN-MET-02) to a custom-field object in place:
    /// hidden keys are removed and masked keys masked. Also used for custom values before and after in the audit history.
    /// </summary>
    public static void ShapeCustom(JsonObject custom, FieldAccessMap access)
    {
        ArgumentNullException.ThrowIfNull(custom);
        ArgumentNullException.ThrowIfNull(access);
        foreach (var key in custom.Select(p => p.Key).ToList())
        {
            var keyLevel = access.Of(FieldAccessMap.CustomPrefix + key);
            if (keyLevel == FieldAccessLevel.Hidden)
            {
                custom.Remove(key);
            }
            else if (keyLevel == FieldAccessLevel.Masked)
            {
                custom[key] = Mask(custom[key]);
            }
        }
    }

    /// <summary>
    /// The keys of salary, personal and restricted fields an answer shows unmasked (read-only or editable access and a value
    /// present in at least one shaped row), sorted; the access log records them (slice S1.07 Q6). Masked and hidden fields
    /// are not counted.
    /// </summary>
    public static IReadOnlyList<string> SensitiveShown(IReadOnlyList<JsonObject> shaped, FieldCatalog catalog, IReadOnlyList<FieldAccessMap> access)
    {
        ArgumentNullException.ThrowIfNull(shaped);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(access);
        var shown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var field in catalog.Fields.Where(f => f.FieldClass is not null && !f.IsCustom))
        {
            for (var i = 0; i < shaped.Count; i++)
            {
                if (access[i].Of(field.Key) >= FieldAccessLevel.ReadOnly && shaped[i][field.JsonName] is not null)
                {
                    shown.Add(field.Key);
                    break;
                }
            }
        }

        return [.. shown];
    }

    /// <summary>"****" followed by the last four characters of the value's text; null stays null.</summary>
    public static JsonNode? Mask(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value is JsonValue v && v.TryGetValue<decimal>(out var number)
            ? number.ToString("0.############################", CultureInfo.InvariantCulture)
            : value is JsonValue s && s.TryGetValue<string>(out var str) ? str : value.ToJsonString();
        return JsonValue.Create(text.Length <= 4 ? MaskPrefix : MaskPrefix + text[^4..]);
    }

    /// <summary>
    /// True when a request leaves a field the caller may not edit as it is: no value sent, or (on update) the same value
    /// as stored.
    /// </summary>
    public static bool Unchanged(object? requested, object? current, bool update) =>
        requested is null || (update && Same(requested, current));

    public static bool Same(object? requested, object? current) => (requested, current) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (JsonElement r, JsonDocument c) => JsonElement.DeepEquals(r, c.RootElement),
        (JsonElement r, JsonElement c) => JsonElement.DeepEquals(r, c),
        (string r, System.Net.IPAddress c) => System.Net.IPAddress.TryParse(r, out var ip) && ip.Equals(c),
        (string r, string c) => string.Equals(r, c, StringComparison.Ordinal),
        (System.Collections.IEnumerable r, System.Collections.IEnumerable c) => r.Cast<object?>().SequenceEqual(c.Cast<object?>()),
        _ => requested.Equals(current),
    };

    /// <summary>
    /// The custom-field object after a write (FR-KRN-MET-02). Editable keys take the requested value (a missing one is
    /// removed); other keys keep their stored value, and a different requested value for them is added to
    /// <paramref name="denied"/> as <c>custom.&lt;key&gt;</c>.
    /// </summary>
    public static JsonDocument MergeCustom(JsonElement? requested, JsonDocument? current, FieldAccessMap access, ICollection<string> denied)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(denied);
        var stored = current?.RootElement.ValueKind == JsonValueKind.Object ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject() : [];
        if (requested is not { ValueKind: JsonValueKind.Object } incoming)
        {
            return JsonDocument.Parse(stored.ToJsonString());
        }

        var result = new JsonObject();
        foreach (var (key, value) in stored)
        {
            if (!access.CanEdit(FieldAccessMap.CustomPrefix + key))
            {
                result[key] = value?.DeepClone();
            }
        }

        foreach (var property in incoming.EnumerateObject())
        {
            var key = FieldAccessMap.CustomPrefix + property.Name;
            var value = JsonNode.Parse(property.Value.GetRawText());
            if (access.CanEdit(key))
            {
                result[property.Name] = value;
            }
            else if (!(stored.TryGetPropertyValue(property.Name, out var old) && JsonNode.DeepEquals(value, old)))
            {
                denied.Add(key);
            }
        }

        return JsonDocument.Parse(result.ToJsonString());
    }
}

/// <summary>Records of a resource one user would see: the count and the ids of the first page (FR-ADM-ROL-06).</summary>
public sealed record ScopePreviewResult(long Count, IReadOnlyList<Guid> FirstPage);

/// <summary>
/// One resource of a module as access control sees it (generated per table): its fields and a way to count and list the
/// rows a given data-scope filter lets through. Lets the kernel preview any module's resource without referencing it.
/// </summary>
/// <remarks>
/// <see cref="ReadAccess"/> (slice S1.07): the caller's field access to one record for <c>read</c>, or null when the record
/// does not exist or is outside the caller's data scope (soft-deleted records count, so their history stays visible). The
/// record-history endpoint shows before and after values through it.
/// </remarks>
public sealed record ResourceAccess(
    string Resource,
    FieldCatalog Fields,
    bool IsScoped,
    Func<IServiceProvider, IDataScopeFilter, int, CancellationToken, Task<ScopePreviewResult>> Preview,
    Func<IServiceProvider, Guid, CancellationToken, Task<FieldAccessMap?>>? ReadAccess = null);

/// <summary>Every resource of the modules the host loads (registered by the host from each module's generated catalogue).</summary>
public sealed class ResourceCatalog(IEnumerable<ResourceAccess> resources)
{
    private readonly Dictionary<string, ResourceAccess> _byName = resources.ToDictionary(r => r.Resource, StringComparer.Ordinal);

    public IReadOnlyCollection<ResourceAccess> All => _byName.Values;

    public ResourceAccess? Find(string resource) => _byName.GetValueOrDefault(resource);
}
