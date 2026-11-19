using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Erp.Kernel.Infrastructure.ControlPlane.Backup;

/// <summary>
/// What a backup holds (FR-ADM-DAT-08; S1.03 review finding 2): the customer, the database and, for every table, its row
/// count and content checksum taken in the same snapshot as the dump. It is written in front of the dump inside the
/// encrypted file, so it is authenticated like the dump itself: "ERPM1" | 4-byte length | UTF-8 JSON.
/// </summary>
public sealed record BackupManifest(string Tenant, string Database, DateTimeOffset TakenAt, IReadOnlyDictionary<string, string> Tables)
{
    private static readonly byte[] Magic = "ERPM1"u8.ToArray();
    private const int MaxLength = 64 << 20;

    public static int HeaderLength => Magic.Length + 4;

    public byte[] ToBytes()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tenant"] = Tenant,
            ["database"] = Database,
            ["taken_at"] = TakenAt,
            ["tables"] = Tables,
        });
        var bytes = new byte[HeaderLength + json.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Magic.Length), json.Length);
        json.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    /// <summary>Length of the JSON that follows <paramref name="header"/>; refuses a file without a manifest.</summary>
    public static int JsonLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength || !header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The backup file has no manifest (it was made by an earlier version, or it is not an ERP backup); it cannot be verified. Take a new backup.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header[Magic.Length..]);
        return length is > 0 and <= MaxLength ? length : throw new InvalidDataException("The backup manifest is damaged.");
    }

    public static BackupManifest Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(json));
        var root = document.RootElement;
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in root.GetProperty("tables").EnumerateObject())
        {
            tables[table.Name] = table.Value.GetString() ?? throw new InvalidDataException("The backup manifest is damaged.");
        }

        return new BackupManifest(
            root.GetProperty("tenant").GetString() ?? throw new InvalidDataException("The backup manifest names no customer."),
            root.GetProperty("database").GetString() ?? string.Empty,
            root.GetProperty("taken_at").GetDateTimeOffset(),
            tables);
    }
}

/// <summary>A read-only stream that yields <c>prefix</c> and then everything from <c>rest</c> (manifest in front of the dump).</summary>
internal sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        return rest.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }

        return rest.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A write-only stream that takes the manifest off the front of the decrypted backup and passes the rest (the dump) on.
/// <c>onManifest</c> runs before any dump byte is passed on, so a refused manifest stops the restore before it starts.
/// </summary>
internal sealed class ManifestSplitter(Stream dump, Action<BackupManifest> onManifest) : Stream
{
    private readonly MemoryStream _head = new();
    private int _needed = BackupManifest.HeaderLength;
    private bool _headerRead;

    public BackupManifest? Manifest { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (Manifest is null && buffer.Length > 0)
        {
            var n = Math.Min(buffer.Length, _needed - (int)_head.Length);
            _head.Write(buffer.Span[..n]);
            buffer = buffer[n..];
            if (_head.Length < _needed)
            {
                return;
            }

            if (!_headerRead)
            {
                _needed = BackupManifest.HeaderLength + BackupManifest.JsonLength(_head.GetBuffer().AsSpan(0, (int)_head.Length));
                _headerRead = true;
                continue;
            }

            Manifest = BackupManifest.Parse(_head.GetBuffer().AsSpan(BackupManifest.HeaderLength, _needed - BackupManifest.HeaderLength));
            onManifest(Manifest);
        }

        if (buffer.Length > 0)
        {
            await dump.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Flush() => dump.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _head.Dispose();
        }

        base.Dispose(disposing);
    }
}
