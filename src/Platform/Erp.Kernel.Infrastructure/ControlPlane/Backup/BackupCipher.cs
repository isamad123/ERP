using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Erp.Kernel.Infrastructure.ControlPlane.Backup;

/// <summary>
/// Encrypted backup files (architecture reference section 11: encrypted backups). AES-256-GCM in 1 MiB chunks so
/// any size streams through: "ERPB1" | 8-byte file id | chunks of [1-byte final flag][4-byte length][16-byte tag][cipher].
/// The nonce is the file id plus the chunk number, and the flag and number are authenticated, so reordered,
/// altered or truncated files are refused.
/// </summary>
public static class BackupCipher
{
    private const int ChunkSize = 1 << 20;
    private static readonly byte[] Magic = "ERPB1"u8.ToArray();

    public static byte[] KeyFromSetting(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException("ERP_BACKUP_KEY must be set to base64 of 32 random bytes (see docs/runbooks/tenant-backup-restore.md).");
        }

        var key = Convert.FromBase64String(base64Key);
        return key.Length == 32 ? key : throw new InvalidOperationException("ERP_BACKUP_KEY must be 32 bytes.");
    }

    public static async Task<long> EncryptAsync(Stream plain, Stream output, byte[] key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(output);
        var fileId = RandomNumberGenerator.GetBytes(8);
        await output.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(fileId, cancellationToken).ConfigureAwait(false);
        using var aes = new AesGcm(key, 16);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var length = await FillAsync(plain, buffer, cancellationToken).ConfigureAwait(false);
        long total = 0;
        for (uint index = 0; ; index++)
        {
            var nextLength = length == ChunkSize ? await FillAsync(plain, next, cancellationToken).ConfigureAwait(false) : 0;
            var final = nextLength == 0;
            var cipher = new byte[length];
            var tag = new byte[16];
            aes.Encrypt(Nonce(fileId, index), buffer.AsSpan(0, length), cipher, tag, Aad(final, index));
            var head = new byte[5];
            head[0] = final ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(1), length);
            await output.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
            total += length;
            if (final)
            {
                return total;
            }

            (buffer, next) = (next, buffer);
            length = nextLength;
        }
    }

    public static async Task<long> DecryptAsync(Stream input, Stream plain, byte[] key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plain);
        var header = new byte[Magic.Length + 8];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an ERP encrypted backup file.");
        }

        var fileId = header[Magic.Length..];
        using var aes = new AesGcm(key, 16);
        long total = 0;
        for (uint index = 0; ; index++)
        {
            var head = new byte[5 + 16];
            try
            {
                await input.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("The backup file is truncated.", ex);
            }

            var final = head[0] == 1;
            var length = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1, 4));
            if (length is < 0 or > ChunkSize)
            {
                throw new InvalidDataException("The backup file is damaged.");
            }

            var cipher = new byte[length];
            await input.ReadExactlyAsync(cipher, cancellationToken).ConfigureAwait(false);
            var data = new byte[length];
            try
            {
                aes.Decrypt(Nonce(fileId, index), cipher, head.AsSpan(5, 16), data, Aad(final, index));
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new InvalidDataException("The backup file was altered or the key is wrong.", ex);
            }

            await plain.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            total += length;
            if (final)
            {
                return total;
            }
        }
    }

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }

    private static byte[] Nonce(byte[] fileId, uint index)
    {
        var nonce = new byte[12];
        fileId.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] Aad(bool final, uint index)
    {
        var aad = new byte[Magic.Length + 5];
        Magic.CopyTo(aad, 0);
        aad[Magic.Length] = final ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(Magic.Length + 1), index);
        return aad;
    }
}
