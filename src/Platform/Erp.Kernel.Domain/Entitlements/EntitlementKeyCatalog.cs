namespace Erp.Kernel.Domain.Entitlements;

/// <summary>One feature, add-on or pack key of the D-24 catalogue: its parent key (null only for <c>platform.base</c>) and module.</summary>
public sealed record EntitlementKey(string Key, string? Parent, string Module);

/// <summary>
/// The entitlement key catalogue (spec 14 section 8, D-24; slice S1.09 ruling Q1) as data: the feature keys of the modules
/// this product has, their parents and modules, the base key each module needs, the pack and add-on keys and the limit
/// keys. Loaded from <c>Entitlements/entitlement-keys.json</c> of the kernel; nothing here names an edition (spec 16 SC-3).
/// </summary>
public sealed class EntitlementKeyCatalog
{
    public EntitlementKeyCatalog(
        IEnumerable<EntitlementKey> features,
        IEnumerable<EntitlementKey> addons,
        IEnumerable<EntitlementKey> packs,
        IReadOnlyDictionary<string, string> moduleBaseKeys,
        IEnumerable<string> limits)
    {
        ArgumentNullException.ThrowIfNull(moduleBaseKeys);
        Features = features.ToDictionary(k => k.Key, StringComparer.Ordinal);
        Addons = addons.ToDictionary(k => k.Key, StringComparer.Ordinal);
        Packs = packs.ToDictionary(k => k.Key, StringComparer.Ordinal);
        ModuleBaseKeys = new Dictionary<string, string>(moduleBaseKeys, StringComparer.Ordinal);
        Limits = new HashSet<string>(limits, StringComparer.Ordinal);

        var all = Features.Values.Concat(Addons.Values).Concat(Packs.Values).ToList();
        foreach (var key in all)
        {
            if (key.Parent is null ? key.Key != PlatformBase : !all.Exists(k => k.Key == key.Parent))
            {
                throw new InvalidDataException($"Entitlement key catalogue: '{key.Key}' names an unknown parent '{key.Parent}'.");
            }
        }

        foreach (var (module, baseKey) in ModuleBaseKeys)
        {
            if (!Features.TryGetValue(baseKey, out var feature) || feature.Module != module)
            {
                throw new InvalidDataException($"Entitlement key catalogue: module '{module}' needs a base key of its own, not '{baseKey}'.");
            }
        }
    }

    /// <summary>The key every entitlement holds (kernel, admin console, audit; D-24).</summary>
    public const string PlatformBase = "platform.base";

    /// <summary>The kernel module: always installed, never listed in <c>claims.modules</c>.</summary>
    public const string KernelModule = "kernel";

    public IReadOnlyDictionary<string, EntitlementKey> Features { get; }

    public IReadOnlyDictionary<string, EntitlementKey> Addons { get; }

    public IReadOnlyDictionary<string, EntitlementKey> Packs { get; }

    /// <summary>Business module → the feature key a licence for the module must hold (ruling B3: <c>hrm</c> needs <c>hrm.core</c>).</summary>
    public IReadOnlyDictionary<string, string> ModuleBaseKeys { get; }

    public IReadOnlySet<string> Limits { get; }

    /// <summary>The feature keys a module unlocks (for <c>cp.module_catalog.entitlement_keys</c>).</summary>
    public IReadOnlyList<string> KeysOf(string module) =>
        Features.Values.Where(k => k.Module == module).Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
}
