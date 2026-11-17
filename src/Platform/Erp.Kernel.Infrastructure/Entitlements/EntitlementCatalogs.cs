using System.Text.Json.Nodes;
using Erp.Kernel.Domain.Entitlements;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>The entitlement key catalogue of this product (<c>Entitlements/entitlement-keys.json</c>, embedded; slice S1.09 ruling Q1).</summary>
public static class EntitlementCatalogs
{
    private static readonly Lazy<EntitlementKeyCatalog> Instance = new(Load);

    public static EntitlementKeyCatalog Keys => Instance.Value;

    /// <summary>Parses and checks the claims of an entitlement against the catalogue.</summary>
    public static EntitlementClaims Parse(JsonObject claims) => EntitlementClaims.Parse(claims, Keys);

    /// <summary>Parses stored claims (cp.entitlements.claims, meta.local_entitlement).</summary>
    public static EntitlementClaims Parse(string claimsJson) =>
        Parse(JsonNode.Parse(claimsJson) as JsonObject ?? throw new InvalidDataException("The stored claims are not a JSON object."));

    private static EntitlementKeyCatalog Load()
    {
        using var stream = typeof(EntitlementCatalogs).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Entitlements.entitlement-keys.json")
            ?? throw new InvalidOperationException("entitlement-keys.json is not embedded.");
        var root = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("entitlement-keys.json is not an object.");
        return new EntitlementKeyCatalog(
            ReadKeys(root, "features"),
            ReadKeys(root, "addons"),
            ReadKeys(root, "packs"),
            root["modules"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal),
            root["limits"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    private static List<EntitlementKey> ReadKeys(JsonObject root, string name) =>
        root[name]!.AsArray().Select(k => new EntitlementKey(k!["key"]!.GetValue<string>(), k["parent"]?.GetValue<string>(), k["module"]!.GetValue<string>())).ToList();
}
