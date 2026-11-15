using System.Security.Cryptography;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;

namespace Erp.Kernel.Infrastructure.ControlPlane.Secrets;

/// <summary>
/// Release 1.0 secret store (product owner ruling, S1.03): one file encrypted with AES-256-GCM. The file path and the
/// 32-byte master key come from server settings (<c>ERP_SECRETS_FILE</c>, <c>ERP_SECRETS_KEY</c> as base64) and are
/// never in the repository. Other stores (a cloud vault) can replace it behind <see cref="ISecretStore"/>.
/// File layout: "ERPS1" | 12-byte nonce | 16-byte tag | ciphertext of a JSON object reference → value.
/// </summary>
public sealed class EncryptedFileSecretStore : ISecretStore, IDisposable
{
    private static readonly byte[] Magic = "ERPS1"u8.ToArray();
    private readonly string _path;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cache;
    private DateTime _cacheStamp;

    public EncryptedFileSecretStore(string path, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("The secret store key must be 32 bytes (base64 of 32 random bytes).", nameof(key));
        }

        _path = Path.GetFullPath(path);
        _key = key.ToArray();
    }

    /// <summary>Builds the store from <c>ERP_SECRETS_FILE</c> and <c>ERP_SECRETS_KEY</c> values.</summary>
    public static EncryptedFileSecretStore FromSettings(string? path, string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException("ERP_SECRETS_FILE and ERP_SECRETS_KEY must be set (see docs/runbooks/tenant-operations.md).");
        }

        return new EncryptedFileSecretStore(path, Convert.FromBase64String(base64Key));
    }

    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return all.GetValueOrDefault(reference);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PutAsync(string reference, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // The lock file serializes writers in different processes (operator commands, migrator).
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            _cache = null;
            var all = new Dictionary<string, string>(await LoadAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal)
            {
                [reference] = value,
            };
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, Encrypt(JsonSerializer.SerializeToUtf8Bytes(all)), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
            _cache = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        _gate.Dispose();
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var stamp = File.GetLastWriteTimeUtc(_path);
        if (_cache is not null && stamp == _cacheStamp)
        {
            return _cache;
        }

        var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        var plain = Decrypt(bytes);
        _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        _cacheStamp = stamp;
        return _cache;
    }

    private byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(_key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }

        CryptographicOperations.ZeroMemory(plain);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    private byte[] Decrypt(byte[] file)
    {
        if (file.Length < Magic.Length + 28 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{_path} is not an ERP secret store file.");
        }

        var nonce = file.AsSpan(Magic.Length, 12);
        var tag = file.AsSpan(Magic.Length + 12, 16);
        var cipher = file.AsSpan(Magic.Length + 28);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidDataException($"{_path} cannot be decrypted with ERP_SECRETS_KEY (wrong key or damaged file).", ex);
        }

        return plain;
    }
}

/// <summary>A database login kept in the secret store as JSON (user name and password).</summary>
public sealed record DatabaseCredential(string Username, string Password)
{
    public string ToSecret() => JsonSerializer.Serialize(this);

    public static DatabaseCredential Parse(string secret) =>
        JsonSerializer.Deserialize<DatabaseCredential>(secret) ?? throw new InvalidDataException("A database credential secret is empty.");

    /// <summary>A new random password (32 bytes, URL-safe base64), never logged or stored outside the secret store.</summary>
    public static string NewPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public override string ToString() => $"DatabaseCredential {{ Username = {Username}, Password = *** }}";
}
