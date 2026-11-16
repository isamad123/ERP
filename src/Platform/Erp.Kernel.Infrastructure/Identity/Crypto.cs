using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSec.Cryptography;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// Argon2id password hashes (slice S1.05 build item 1) through libsodium (NSec). Parameters follow the OWASP minimum
/// (19 MiB, 2 passes, 1 lane) and are written into the PHC string, so stronger settings can be introduced later and
/// old hashes upgraded at the next successful sign-in (<see cref="Verify"/> reports <c>needsRehash</c>).
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    public const string AlgorithmLabel = "argon2id-v1";
    private const int MemoryKib = 19 * 1024;
    private const int Passes = 2;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Algorithm => AlgorithmLabel;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKib, Passes);
        return string.Create(CultureInfo.InvariantCulture, $"$argon2id$v=19$m={MemoryKib},t={Passes},p=1${B64(salt)}${B64(hash)}");
    }

    public bool Verify(string password, string storedHash, string algorithm, out bool needsRehash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(storedHash);
        needsRehash = false;
        if (algorithm != AlgorithmLabel || !TryParse(storedHash, out var memory, out var passes, out var salt, out var expected))
        {
            return false;
        }

        var actual = Derive(password, salt, memory, passes);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        needsRehash = ok && (memory != MemoryKib || passes != Passes);
        return ok;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int passes)
    {
        var parameters = new Argon2Parameters { DegreeOfParallelism = 1, MemorySize = memoryKib, NumberOfPasses = passes };
        var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(in parameters);
        return algorithm.DeriveBytes(password, salt, HashBytes);
    }

    private static bool TryParse(string phc, out int memory, out int passes, out byte[] salt, out byte[] hash)
    {
        memory = passes = 0;
        salt = hash = [];
        var parts = phc.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }

        foreach (var item in parts[3].Split(','))
        {
            var kv = item.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (kv[0])
            {
                case "m": memory = value; break;
                case "t": passes = value; break;
                case "p" when value == 1: break;
                default: return false;
            }
        }

        try
        {
            salt = Convert.FromBase64String(Pad(parts[4]));
            hash = Convert.FromBase64String(Pad(parts[5]));
        }
        catch (FormatException)
        {
            return false;
        }

        return memory >= 8 * 1024 && passes >= 1 && salt.Length >= 16 && hash.Length == HashBytes;
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static string Pad(string s) => s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
}

/// <summary>
/// High-entropy random tokens (activation, sign-in challenges, refresh tokens). Only their SHA-256 is stored (CR-010):
/// with 256 random bits a fast hash is enough, unlike passwords.
/// </summary>
public static class OpaqueTokens
{
    public static string New() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>A recovery code a person can type: 10 base32 characters in two groups (50 random bits).</summary>
    public static string NewRecoveryCode()
    {
        var text = Domain.Identity.Base32.Encode(RandomNumberGenerator.GetBytes(7))[..10];
        return text[..5] + "-" + text[5..];
    }

    public static string NormalizeRecoveryCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var clean = code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return clean.Length == 10 ? clean[..5] + "-" + clean[5..] : clean;
    }
}

/// <summary>
/// AES-256-GCM field encryption for <c>enc</c> columns (spec 03 section 3.3; slice S1.05 ruling P7). The purpose (for
/// example <c>iam.mfa_devices.secret</c>) is bound as associated data, so a ciphertext cannot be moved to another column.
/// Format: version byte (1) | key version byte | 12-byte nonce | 16-byte tag | ciphertext. Envelope keys (S1.07)
/// will use a new version byte; version 1 stays readable.
/// </summary>
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const byte FormatVersion = 1;
    private readonly byte[] _key;
    private readonly byte _keyVersion;

    public AesGcmFieldEncryptor(byte[] key, byte keyVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("A field key is 32 bytes.", nameof(key));
        }

        _key = key.ToArray();
        _keyVersion = keyVersion;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var output = new byte[2 + 12 + 16 + plaintext.Length];
        output[0] = FormatVersion;
        output[1] = _keyVersion;
        var nonce = output.AsSpan(2, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(30), output.AsSpan(14, 16), Encoding.UTF8.GetBytes(purpose));
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, string purpose)
    {
        if (ciphertext.Length < 30 || ciphertext[0] != FormatVersion || ciphertext[1] != _keyVersion)
        {
            throw new CryptographicException("Unknown field ciphertext format or key version.");
        }

        var plaintext = new byte[ciphertext.Length - 30];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(ciphertext.Slice(2, 12), ciphertext[30..], ciphertext.Slice(14, 16), plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    public byte[] BlindIndex(ReadOnlySpan<byte> normalizedPlaintext, string purpose)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, info: Encoding.UTF8.GetBytes("blind-index:" + purpose));
        return HMACSHA256.HashData(key, normalizedPlaintext);
    }
}

/// <summary>
/// One field key per customer in the secret store (<c>tenants/{code}/field-key-v1</c>). Created when the customer's
/// database is migrated (provisioning and the fleet migrator, one process), never lazily by the API, so two API
/// instances can never create different keys for the same customer.
/// </summary>
public static class TenantFieldKeys
{
    public const byte KeyVersion = 1;

    public static string Reference(string tenantCode) => $"tenants/{tenantCode}/field-key-v{KeyVersion}";

    public static async Task EnsureAsync(ISecretStore secrets, string tenantCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (await secrets.GetAsync(Reference(tenantCode), cancellationToken).ConfigureAwait(false) is null)
        {
            await secrets.PutAsync(Reference(tenantCode), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The bound customer's field encryptor, its key cached per customer for the life of the process.</summary>
public sealed class TenantFieldEncryptorProvider(ITenantContext tenant, ISecretStore secrets, FieldKeyCache cache) : IFieldEncryptorProvider
{
    public async ValueTask<IFieldEncryptor> GetAsync(CancellationToken cancellationToken)
    {
        var code = tenant.TenantCode;
        if (cache.Keys.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var stored = await secrets.GetAsync(TenantFieldKeys.Reference(code), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No field key for customer {code}; run the migrator (it creates the key).");
        var encryptor = new AesGcmFieldEncryptor(Convert.FromBase64String(stored), TenantFieldKeys.KeyVersion);
        return cache.Keys.GetOrAdd(code, encryptor);
    }
}

/// <summary>Process-wide cache of customer field encryptors (singleton).</summary>
public sealed class FieldKeyCache
{
    internal ConcurrentDictionary<string, IFieldEncryptor> Keys { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// ES256 keys that sign access tokens (slice S1.05 build item 5). The secret store holds the index
/// (<c>platform/token-signing/index</c>: current key id and all key ids still accepted) and each private key in PEM
/// (<c>platform/token-signing/{kid}</c>). Rotation adds a key and makes it current; the previous key keeps validating
/// tokens until it is dropped from the index.
/// </summary>
public sealed class TokenSigningKeys
{
    public const string IndexReference = "platform/token-signing/index";

    private TokenSigningKeys(SecurityKey current, IReadOnlyList<SecurityKey> validation)
    {
        Current = current;
        Validation = validation;
    }

    public SecurityKey Current { get; }

    /// <summary>Public keys of every key id in the index (current first).</summary>
    public IReadOnlyList<SecurityKey> Validation { get; }

    public static string KeyReference(string keyId) => "platform/token-signing/" + keyId;

    /// <summary>Creates the first key when none exists (run by the migrator's <c>migrate</c> command).</summary>
    public static async Task EnsureAsync(ISecretStore secrets, string newKeyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (await secrets.GetAsync(IndexReference, cancellationToken).ConfigureAwait(false) is null)
        {
            await RotateAsync(secrets, newKeyId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds a new key and makes it current; older keys stay accepted (at most the last three are kept).</summary>
    public static async Task RotateAsync(ISecretStore secrets, string newKeyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await secrets.PutAsync(KeyReference(newKeyId), key.ExportPkcs8PrivateKeyPem(), cancellationToken).ConfigureAwait(false);
        var index = await ReadIndexAsync(secrets, cancellationToken).ConfigureAwait(false);
        var ids = new[] { newKeyId }.Concat(index?.Keys ?? []).Distinct(StringComparer.Ordinal).Take(3).ToList();
        await secrets.PutAsync(IndexReference, JsonSerializer.Serialize(new KeyIndex(newKeyId, ids)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the keys; null when no key has been created yet.</summary>
    public static async Task<TokenSigningKeys?> LoadAsync(ISecretStore secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var index = await ReadIndexAsync(secrets, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return null;
        }

        var keys = new List<SecurityKey>();
        foreach (var id in index.Keys)
        {
            var pem = await secrets.GetAsync(KeyReference(id), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Token signing key {id} is listed but missing from the secret store.");
            var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            keys.Add(new ECDsaSecurityKey(ec) { KeyId = id });
        }

        return new TokenSigningKeys(keys.First(k => k.KeyId == index.Current), keys);
    }

    private static async Task<KeyIndex?> ReadIndexAsync(ISecretStore secrets, CancellationToken cancellationToken)
    {
        var text = await secrets.GetAsync(IndexReference, cancellationToken).ConfigureAwait(false);
        return text is null ? null : JsonSerializer.Deserialize<KeyIndex>(text);
    }

    private sealed record KeyIndex(string Current, List<string> Keys);
}

/// <summary>
/// Short-lived ES256 access tokens (5 minutes, ruling Q3) with the S1.03 contract: <c>sub</c> (user id), <c>tenant</c>
/// (customer code), plus <c>sid</c> (session id) and the key id in the header.
/// </summary>
public sealed class JwtAccessTokenIssuer(TokenSigningKeys keys, string issuer, string audience) : IAccessTokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, string tenantCode, Guid sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var expires = now + Lifetime;
        var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([
                new Claim("sub", userId.ToString()),
                new Claim("tenant", tenantCode),
                new Claim("sid", sessionId.ToString()),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.Current, SecurityAlgorithms.EcdsaSha256),
        });
        return (token, expires);
    }
}
