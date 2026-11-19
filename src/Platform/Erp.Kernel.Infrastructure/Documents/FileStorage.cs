using System.Security.Cryptography;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Where file contents live (slice S1.11c1 Build item 17; KRN-DOC-02, KRN-TEN-03, ruling Q20). One contract for a local
/// folder (also a mounted network share) and an S3-compatible store. Keys always start with the customer's prefix, so one
/// customer can neither list nor open another's files; contents arrive already encrypted.
/// </summary>
public interface IFileStore
{
    Task PutAsync(string key, byte[] content, CancellationToken cancellationToken);

    /// <summary>The stored bytes, or null when the key does not exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Host settings of the file store: <c>Files:Store</c> (<c>local</c> or <c>s3</c>) and <c>Files:Root</c> for the local folder.</summary>
public sealed record FileStoreOptions(string Store, string? Root);

/// <summary>Contents as files below a root folder. A key may not leave the root (no <c>..</c>, no absolute path).</summary>
public sealed class LocalFolderFileStore(string root) : IFileStore
{
    // Without its trailing separator, so a root set as "/data/files/" still accepts its own keys (S1.11c1 review finding 6).
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(File.Exists(PathOf(key)));

    private string PathOf(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("A file key must stay inside the store.", nameof(key));
        }

        return path;
    }
}

/// <summary>
/// One file key per customer in the secret store (<c>tenants/{code}/file-key-v1</c>, KRN-TEN-03). Created with the
/// customer's database by provisioning and the fleet migrator, like <see cref="TenantFieldKeys"/>.
/// </summary>
public static class TenantFileKeys
{
    public const byte KeyVersion = 1;

    public static string Reference(string tenantCode) => $"tenants/{tenantCode}/file-key-v{KeyVersion}";

    /// <summary>The customer's part of every store key.</summary>
    public static string Prefix(string tenantCode) => $"tenants/{tenantCode}/files";

    public static async Task EnsureAsync(ISecretStore secrets, string tenantCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (await secrets.GetAsync(Reference(tenantCode), cancellationToken).ConfigureAwait(false) is null)
        {
            await secrets.PutAsync(Reference(tenantCode), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Encrypts and decrypts contents with the bound customer's file key (AES-256-GCM; the file id is bound as associated data).</summary>
public sealed class FileEncryption(ITenantContext tenant, ISecretStore secrets)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Keys = new(StringComparer.Ordinal);

    public async Task<byte[]> EncryptAsync(Guid fileId, byte[] plaintext, CancellationToken cancellationToken) =>
        (await EncryptorAsync(cancellationToken).ConfigureAwait(false)).Encrypt(plaintext, Purpose(fileId));

    public async Task<byte[]> DecryptAsync(Guid fileId, byte[] ciphertext, CancellationToken cancellationToken) =>
        (await EncryptorAsync(cancellationToken).ConfigureAwait(false)).Decrypt(ciphertext, Purpose(fileId));

    private static string Purpose(Guid fileId) => "doc.files:" + fileId.ToString("D");

    private async Task<IFieldEncryptor> EncryptorAsync(CancellationToken cancellationToken)
    {
        var code = tenant.TenantCode;
        if (!Keys.TryGetValue(code, out var key))
        {
            var stored = await secrets.GetAsync(TenantFileKeys.Reference(code), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No file key for customer {code}; run the migrator (it creates the key).");
            key = Keys.GetOrAdd(code, Convert.FromBase64String(stored));
        }

        return new AesGcmFieldEncryptor(key, TenantFileKeys.KeyVersion);
    }
}
