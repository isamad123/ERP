using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

/// <summary>The signed part of an entitlement: which customer, which version, edition, term and claims (FR-KRN-ENT-01).</summary>
public sealed record EntitlementPayload(string Tenant, int Version, string Edition, DateOnly EffectiveFrom, DateOnly? ExpiresAt, JsonObject Claims)
{
    private static readonly string[] Editions = ["starter", "business", "enterprise"];

    /// <summary>Licensed business modules from <c>claims.modules</c>; the kernel is always installed.</summary>
    public IReadOnlyList<string> Modules =>
        Claims["modules"] is JsonArray modules
            ? modules.Select(m => m?.GetValue<string>() ?? throw new InvalidDataException("claims.modules holds an empty entry")).ToList()
            : throw new InvalidDataException("The entitlement claims must list the licensed modules in claims.modules.");

    public JsonObject ToJson() => new()
    {
        ["tenant"] = Tenant,
        ["version"] = Version,
        ["edition"] = Edition,
        ["effective_from"] = EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["expires_at"] = ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["claims"] = Claims.DeepClone(),
    };

    public static EntitlementPayload FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var edition = Text(json, "edition");
        if (!Editions.Contains(edition, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Unknown edition '{edition}'.");
        }

        var expires = json["expires_at"]?.GetValue<string>();
        var payload = new EntitlementPayload(
            Text(json, "tenant"),
            json["version"]?.GetValue<int>() ?? throw new InvalidDataException("The entitlement has no version."),
            edition,
            DateOnly.ParseExact(Text(json, "effective_from"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            expires is null ? null : DateOnly.ParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            json["claims"] as JsonObject ?? throw new InvalidDataException("The entitlement has no claims object."));
        _ = payload.Modules;
        if (payload.ExpiresAt is { } end && end < payload.EffectiveFrom)
        {
            throw new InvalidDataException("The entitlement ends before it starts.");
        }

        return payload.Version < 1 ? throw new InvalidDataException("The entitlement version starts at 1.") : payload;
    }

    private static string Text(JsonObject json, string name) =>
        json[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidDataException($"The entitlement has no {name}.");
}

/// <summary>
/// An entitlement as the vendor issues it: payload, signing key id and an ECDSA P-256 / SHA-256 signature (IEEE P1363
/// format) over the canonical JSON of the payload. Only public keys are held by the platform (cp.signing_keys);
/// the private key stays with the issuer. Claims are checked by <see cref="EntitlementVerifier"/>; applying, history and the
/// state machine are slice S1.09b (<see cref="EntitlementApplier"/>, <c>LicenceStates</c>).
/// </summary>
public sealed record SignedEntitlement(EntitlementPayload Payload, string KeyId, byte[] Signature)
{
    public static SignedEntitlement Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("An entitlement file is a JSON object.");
        var payload = EntitlementPayload.FromJson(root["payload"] as JsonObject ?? throw new InvalidDataException("The entitlement file has no payload."));
        var keyId = root["key_id"]?.GetValue<string>() ?? throw new InvalidDataException("The entitlement file has no key_id.");
        var signature = Convert.FromBase64String(root["signature"]?.GetValue<string>() ?? throw new InvalidDataException("The entitlement file has no signature."));
        return new SignedEntitlement(payload, keyId, signature);
    }

    public string ToFileJson() => new JsonObject
    {
        ["payload"] = Payload.ToJson(),
        ["key_id"] = KeyId,
        ["signature"] = Convert.ToBase64String(Signature),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>True when the signature is valid for <paramref name="subjectPublicKeyInfo"/> (DER, as stored in cp.signing_keys).</summary>
    public bool Verify(byte[] subjectPublicKeyInfo)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return key.VerifyData(CanonicalJson.Bytes(Payload.ToJson()), Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Signs a payload with an issuer's private key (PEM). Used by the issuer's operator command, never by the API.</summary>
    public static SignedEntitlement Sign(EntitlementPayload payload, string keyId, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(CanonicalJson.Bytes(payload.ToJson()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedEntitlement(payload, keyId, signature);
    }
}

/// <summary>Canonical JSON for signatures: object keys in ordinal order, no white space, strings escaped by System.Text.Json.</summary>
public static class CanonicalJson
{
    public static byte[] Bytes(JsonNode? node)
    {
        var b = new StringBuilder();
        Write(node, b);
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    private static void Write(JsonNode? node, StringBuilder b)
    {
        switch (node)
        {
            case null:
                b.Append("null");
                break;
            case JsonObject obj:
                b.Append('{');
                var first = true;
                foreach (var (name, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        b.Append(',');
                    }

                    first = false;
                    b.Append(JsonSerializer.Serialize(name)).Append(':');
                    Write(value, b);
                }

                b.Append('}');
                break;
            case JsonArray array:
                b.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        b.Append(',');
                    }

                    Write(array[i], b);
                }

                b.Append(']');
                break;
            default:
                b.Append(node.ToJsonString());
                break;
        }
    }
}
