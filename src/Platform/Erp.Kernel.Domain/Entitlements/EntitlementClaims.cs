using System.Globalization;
using System.Text.Json.Nodes;

namespace Erp.Kernel.Domain.Entitlements;

/// <summary>
/// The licence-state periods of an entitlement (slice S1.09 ruling Q4): Warning for the last <see cref="WarningDays"/> days
/// up to the end date, Grace for <see cref="GraceDays"/> days after it, then Read-only for <see cref="ReadOnlyDays"/> days,
/// then Locked. The product defaults are 30, 14 and 30; the signed contract may give others (<c>claims.term</c>).
/// </summary>
public sealed record LicenceTerm(int WarningDays, int GraceDays, int ReadOnlyDays, DateOnly? MaintenanceExpiresAt)
{
    public static LicenceTerm Default { get; } = new(30, 14, 30, null);
}

/// <summary>
/// The signed claims of an entitlement (FR-KRN-ENT-01; slice S1.09 ruling Q1, CR-036 item 7; format in
/// docs/registers/contracts.md): deployment, modules, feature keys, packs, add-ons, limits, term, support tier and
/// source flag. Edition, start and end dates stay beside the claims in the payload. <see cref="Parse"/> refuses claims
/// that break the key catalogue: every key needs <c>platform.base</c>, a key needs its parent, a feature key needs its
/// module listed, and a listed module needs its base key (ruling B3). A missing limit means no limit.
/// </summary>
public sealed record EntitlementClaims(
    string Deployment,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Packs,
    IReadOnlyList<string> Addons,
    IReadOnlyDictionary<string, long> Limits,
    LicenceTerm Term,
    string? SupportTier,
    bool Source)
{
    private static readonly string[] Deployments = ["saas", "on_prem"];

    private static readonly string[] KnownClaims = ["deployment", "modules", "features", "packs", "addons", "limits", "term", "support_tier", "source"];

    private static readonly string[] KnownTerm = ["maintenance_expires_at", "warning_days", "grace_days", "read_only_days"];

    /// <summary>The longest state period a contract may give, so dates never overflow (ten years).</summary>
    public const int MaxPeriodDays = 3653;

    public bool HasFeature(string key) => Features.Contains(key, StringComparer.Ordinal);

    public bool HasModule(string module) => module == EntitlementKeyCatalog.KernelModule || Modules.Contains(module, StringComparer.Ordinal);

    /// <summary>The limit for <paramref name="key"/>, or null when the entitlement sets none (no limit).</summary>
    public long? Limit(string key) => Limits.TryGetValue(key, out var value) ? value : null;

    /// <summary>Reads and checks claims; every problem is an <see cref="InvalidDataException"/> naming the claim.</summary>
    public static EntitlementClaims Parse(JsonObject claims, EntitlementKeyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(catalog);
        var unknown = claims.Select(p => p.Key).Except(KnownClaims, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException("Unknown entitlement claims: " + string.Join(", ", unknown) + ".");
        }

        var deployment = claims["deployment"] is JsonValue d && d.TryGetValue<string>(out var dep) ? dep : null;
        if (deployment is null || !Deployments.Contains(deployment, StringComparer.Ordinal))
        {
            throw new InvalidDataException("claims.deployment must be saas or on_prem.");
        }

        var modules = Texts(claims, "modules", required: true);
        var features = Texts(claims, "features", required: true);
        var packs = Texts(claims, "packs", required: false);
        var addons = Texts(claims, "addons", required: false);

        if (!features.Contains(EntitlementKeyCatalog.PlatformBase, StringComparer.Ordinal))
        {
            throw new InvalidDataException("claims.features must hold platform.base: every key needs it.");
        }

        foreach (var module in modules)
        {
            if (module == EntitlementKeyCatalog.KernelModule)
            {
                throw new InvalidDataException("claims.modules lists business modules only; the kernel is always installed.");
            }

            if (!catalog.ModuleBaseKeys.TryGetValue(module, out var baseKey))
            {
                throw new InvalidDataException($"claims.modules: '{module}' is not a module of this product.");
            }

            if (!features.Contains(baseKey, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"claims.modules: module '{module}' needs its feature key '{baseKey}' in claims.features.");
            }
        }

        CheckKeys(features, catalog.Features, "features", features, modules);
        CheckKeys(addons, catalog.Addons, "addons", features.Concat(addons).ToList(), modules);
        CheckKeys(packs, catalog.Packs, "packs", features.Concat(packs).ToList(), modules);

        var limits = new Dictionary<string, long>(StringComparer.Ordinal);
        if (claims["limits"] is { } rawLimits)
        {
            if (rawLimits is not JsonObject limitObject)
            {
                throw new InvalidDataException("claims.limits must be an object of limit key to whole number.");
            }

            foreach (var (key, value) in limitObject)
            {
                if (!catalog.Limits.Contains(key))
                {
                    throw new InvalidDataException($"claims.limits: '{key}' is not a limit key (spec 14 section 8.3).");
                }

                if (value is not JsonValue v || !v.TryGetValue<long>(out var number) || number < 0)
                {
                    throw new InvalidDataException($"claims.limits.{key} must be a whole number of 0 or more.");
                }

                limits[key] = number;
            }
        }

        var supportTier = claims["support_tier"] switch
        {
            null => null,
            JsonValue s when s.TryGetValue<string>(out var tier) && tier.Length is > 0 and <= 40 => tier,
            _ => throw new InvalidDataException("claims.support_tier must be a short text."),
        };
        var source = claims["source"] switch
        {
            null => false,
            JsonValue s when s.TryGetValue<bool>(out var flag) => flag,
            _ => throw new InvalidDataException("claims.source must be true or false."),
        };

        return new EntitlementClaims(deployment, modules, features, packs, addons, limits, ReadTerm(claims["term"]), supportTier, source);
    }

    private static LicenceTerm ReadTerm(JsonNode? node)
    {
        if (node is null)
        {
            return LicenceTerm.Default;
        }

        if (node is not JsonObject term)
        {
            throw new InvalidDataException("claims.term must be an object.");
        }

        var unknown = term.Select(p => p.Key).Except(KnownTerm, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException("Unknown claims.term entries: " + string.Join(", ", unknown) + ".");
        }

        DateOnly? maintenance = null;
        if (term["maintenance_expires_at"] is { } m)
        {
            maintenance = m is JsonValue mv && mv.TryGetValue<string>(out var text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new InvalidDataException("claims.term.maintenance_expires_at must be a date (yyyy-MM-dd).");
        }

        return new LicenceTerm(
            Days(term, "warning_days", LicenceTerm.Default.WarningDays),
            Days(term, "grace_days", LicenceTerm.Default.GraceDays),
            Days(term, "read_only_days", LicenceTerm.Default.ReadOnlyDays),
            maintenance);
    }

    private static int Days(JsonObject term, string name, int fallback) => term[name] switch
    {
        null => fallback,
        JsonValue v when v.TryGetValue<int>(out var days) && days is >= 0 and <= MaxPeriodDays => days,
        _ => throw new InvalidDataException($"claims.term.{name} must be a whole number of days from 0 to {MaxPeriodDays}."),
    };

    private static void CheckKeys(IReadOnlyList<string> keys, IReadOnlyDictionary<string, EntitlementKey> known, string claim, IReadOnlyList<string> held, IReadOnlyList<string> modules)
    {
        foreach (var key in keys)
        {
            if (!known.TryGetValue(key, out var entry))
            {
                throw new InvalidDataException($"claims.{claim}: '{key}' is not a key of the entitlement key catalogue.");
            }

            if (entry.Parent is { } parent && !held.Contains(parent, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"claims.{claim}: '{key}' needs its parent key '{parent}'.");
            }

            if (entry.Module != EntitlementKeyCatalog.KernelModule && !modules.Contains(entry.Module, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"claims.{claim}: '{key}' needs module '{entry.Module}' in claims.modules.");
            }
        }
    }

    private static List<string> Texts(JsonObject claims, string name, bool required)
    {
        var node = claims[name];
        if (node is null)
        {
            return required ? throw new InvalidDataException($"claims.{name} is required (an empty list is allowed).") : [];
        }

        if (node is not JsonArray array)
        {
            throw new InvalidDataException($"claims.{name} must be a list of keys.");
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            var value = item is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0 ? text : throw new InvalidDataException($"claims.{name} holds an empty or non-text entry.");
            if (values.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"claims.{name} lists '{value}' twice.");
            }

            values.Add(value);
        }

        return values;
    }
}
