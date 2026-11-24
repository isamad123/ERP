using System.Buffers.Binary;
using System.Net.Sockets;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// The small part of the ZKTeco-family wire protocol (TCP framing) the attendance pull needs (slice S1.27b, CR-088 slice S1.27 Q4), written for
/// this product because no permissive-licence .NET library exists (D-09; the common implementations are GPL or unlicensed). Only the commands in
/// <see cref="Allowed"/> can be sent: connect, authenticate, read sizes, read attendance records, free the buffer and exit. The commands that read
/// users and their fingerprint or face templates are not in the list, so no template can be requested (FR-HRM-ATT-20).
/// </summary>
public static class ZkProtocol
{
    public const ushort Connect = 1000;
    public const ushort Exit = 1001;
    public const ushort Auth = 1102;
    public const ushort GetFreeSizes = 50;
    public const ushort AttendanceLogRequest = 13;
    public const ushort FreeData = 1502;
    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckData = 2002;
    public const ushort AckUnauthorized = 2005;
    public const ushort PrepareData = 1500;
    public const ushort Data = 1501;

    public static readonly ushort[] Allowed = [Connect, Exit, Auth, GetFreeSizes, AttendanceLogRequest, FreeData];

    private static readonly byte[] Magic = [0x50, 0x50, 0x82, 0x7D];

    public const int MaxPacket = 4 * 1024 * 1024;

    public sealed record Frame(ushort Command, ushort Session, ushort Reply, byte[] Data);

    public static byte[] Encode(ushort command, ushort session, ushort reply, ReadOnlySpan<byte> data)
    {
        var body = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), command);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), session);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), reply);
        data.CopyTo(body.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), Checksum(body));
        var packet = new byte[8 + body.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)body.Length);
        body.CopyTo(packet, 8);
        return packet;
    }

    /// <summary>The device checksum: one's complement of the 16-bit word sum with end-around carry, the checksum field being zero.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> packet)
    {
        long sum = 0;
        var i = 0;
        for (; i + 1 < packet.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(packet[i..]);
            if (sum > ushort.MaxValue)
            {
                sum -= ushort.MaxValue;
            }
        }

        if (i < packet.Length)
        {
            sum += packet[i];
        }

        while (sum > ushort.MaxValue)
        {
            sum -= ushort.MaxValue;
        }

        sum = ~sum;
        while (sum < 0)
        {
            sum += ushort.MaxValue;
        }

        return (ushort)sum;
    }

    public static async Task<Frame?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[8];
        if (!await ReadExactAsync(stream, head, ct).ConfigureAwait(false) || !head.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (length < 8 || length > MaxPacket)
        {
            return null;
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Frame(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)), body[8..]);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>The authentication key the device expects for a communication key and the session (the device vendor's published scheme).</summary>
    public static byte[] CommKey(uint key, ushort session, byte ticks = 50)
    {
        uint reversed = 0;
        for (var i = 0; i < 32; i++)
        {
            reversed = (reversed << 1) | ((key >> i) & 1);
        }

        var k = reversed + session;
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, k);
        b[0] ^= (byte)'Z';
        b[1] ^= (byte)'K';
        b[2] ^= (byte)'S';
        b[3] ^= (byte)'O';
        var swapped = new[] { b[2], b[3], b[0], b[1] };
        return [(byte)(swapped[0] ^ ticks), (byte)(swapped[1] ^ ticks), ticks, (byte)(swapped[3] ^ ticks)];
    }

    /// <summary>The device's packed time: 2000-based year, 12 months of 31 days, seconds in the day.</summary>
    public static DateTime DecodeTime(uint t)
    {
        var second = (int)(t % 60);
        t /= 60;
        var minute = (int)(t % 60);
        t /= 60;
        var hour = (int)(t % 24);
        t /= 24;
        var day = (int)(t % 31) + 1;
        t /= 31;
        var month = (int)(t % 12) + 1;
        t /= 12;
        var year = (int)t + 2000;
        return day > DateTime.DaysInMonth(year, month) ? DateTime.MinValue : new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    public static uint EncodeTime(DateTime time) =>
        (uint)((((((time.Year % 100) * 12 * 31) + ((time.Month - 1) * 31) + time.Day - 1) * 86400L) + (((time.Hour * 60) + time.Minute) * 60) + time.Second));
}
