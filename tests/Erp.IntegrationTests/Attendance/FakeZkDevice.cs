using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// A small in-process model of a ZKTeco-family clock machine for the pull driver tests (slice S1.27b): it speaks the TCP framing, asks for the
/// communication key when one is set, answers the record count and the attendance log (as a prepared transfer of data frames) and records every
/// command it is sent, so a test can prove that only the allowed commands, never a user or template request, were issued (FR-HRM-ATT-20).
/// </summary>
public sealed class FakeZkDevice : IAsyncDisposable
{
    private static readonly byte[] Magic = [0x50, 0x50, 0x82, 0x7D];
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeZkDevice(uint? commKey, IReadOnlyList<(string User, DateTime Local, int Punch)> records, bool recordSize16 = false)
    {
        CommKey = commKey;
        Records = records;
        RecordSize16 = recordSize16;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public uint? CommKey { get; }

    public IReadOnlyList<(string User, DateTime Local, int Punch)> Records { get; set; }

    public bool RecordSize16 { get; }

    public List<ushort> Commands { get; } = [];

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            ushort session = 0x1234;
            var authed = CommKey is null;
            try
            {
                while (true)
                {
                    var head = new byte[8];
                    if (!await ReadAsync(stream, head))
                    {
                        return;
                    }

                    var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4))];
                    if (!await ReadAsync(stream, body))
                    {
                        return;
                    }

                    var command = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    var reply = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
                    lock (Commands)
                    {
                        Commands.Add(command);
                    }

                    switch (command)
                    {
                        case 1000:
                            await SendAsync(stream, authed ? (ushort)2000 : (ushort)2005, session, reply, []);
                            break;
                        case 1102:
                            authed = body.AsSpan(8).SequenceEqual(Key(CommKey!.Value, session));
                            await SendAsync(stream, authed ? (ushort)2000 : (ushort)2005, session, reply, []);
                            break;
                        case 50 when authed:
                            var sizes = new byte[80];
                            BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(32), Records.Count);
                            await SendAsync(stream, 2000, session, reply, sizes);
                            break;
                        case 13 when authed:
                            var data = Log();
                            await SendAsync(stream, 1500, session, reply, BitConverter.GetBytes((uint)data.Length));
                            for (var i = 0; i < data.Length; i += 1024)
                            {
                                await SendAsync(stream, 1501, session, reply, data.AsSpan(i, Math.Min(1024, data.Length - i)).ToArray());
                            }

                            await SendAsync(stream, 2000, session, reply, []);
                            break;
                        case 1502 or 1001 when authed:
                            await SendAsync(stream, 2000, session, reply, []);
                            if (command == 1001)
                            {
                                return;
                            }

                            break;
                        default:
                            await SendAsync(stream, 2005, session, reply, []);
                            break;
                    }
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private byte[] Log()
    {
        var size = RecordSize16 ? 16 : 40;
        var data = new byte[4 + (Records.Count * size)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)(Records.Count * size));
        for (var i = 0; i < Records.Count; i++)
        {
            var (user, local, punch) = Records[i];
            var r = data.AsSpan(4 + (i * size), size);
            var stamp = (uint)(((((local.Year % 100) * 12 * 31) + ((local.Month - 1) * 31) + local.Day - 1) * 86400L) + (((local.Hour * 60) + local.Minute) * 60) + local.Second);
            if (RecordSize16)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(r, uint.Parse(user, System.Globalization.CultureInfo.InvariantCulture));
                BinaryPrimitives.WriteUInt32LittleEndian(r[4..], stamp);
                r[9] = (byte)punch;
            }
            else
            {
                Encoding.ASCII.GetBytes(user).CopyTo(r[2..]);
                BinaryPrimitives.WriteUInt32LittleEndian(r[27..], stamp);
                r[31] = (byte)punch;
            }
        }

        return data;
    }

    /// <summary>The vendor's published authentication key for a communication key and session (the device's side of the exchange).</summary>
    private static byte[] Key(uint key, ushort session)
    {
        uint reversed = 0;
        for (var i = 0; i < 32; i++)
        {
            reversed = (reversed << 1) | ((key >> i) & 1);
        }

        var b = BitConverter.GetBytes(reversed + session);
        b[0] ^= (byte)'Z';
        b[1] ^= (byte)'K';
        b[2] ^= (byte)'S';
        b[3] ^= (byte)'O';
        byte t = 50;
        return [(byte)(b[2] ^ t), (byte)(b[3] ^ t), t, (byte)(b[1] ^ t)];
    }

    private static async Task SendAsync(NetworkStream stream, ushort command, ushort session, ushort reply, byte[] data)
    {
        var body = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, command);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), session);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), reply);
        data.CopyTo(body, 8);
        var packet = new byte[8 + body.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)body.Length);
        body.CopyTo(packet, 8);
        await stream.WriteAsync(packet);
    }

    private static async Task<bool> ReadAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
