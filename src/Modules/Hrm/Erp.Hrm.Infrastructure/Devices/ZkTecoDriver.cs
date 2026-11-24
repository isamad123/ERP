using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// The pull driver for the ZKTeco-family protocol (slice S1.27b Build item 7, CR-088 slice S1.27 Q4): connects over TCP, authenticates with the
/// communication key from the secret store when the device asks, reads the number of records and the attendance log, and leaves. It reads
/// punches only (user number, time, direction): it never asks for users, fingerprint or face templates (see <see cref="ZkProtocol.Allowed"/>),
/// and what it returns has no place for one. Errors carry a localization key and a short reason, never the key or any device data.
/// Written against the published behaviour of the protocol and exercised against an in-process device model; a real device of the family is to
/// be tried on site before go-live (R-157).
/// </summary>
public sealed class ZkTecoDriver : IAttendanceDeviceDriver
{
    public const string Unreachable = "errors.hrm.device.unreachable";
    public const string AuthFailed = "errors.hrm.device.auth_failed";
    public const string BadReply = "errors.hrm.device.bad_reply";
    public const string Unsupported = "errors.hrm.device.unsupported_protocol";
    public const int DefaultPort = 4370;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public string Provider => DeviceProviders.ZkTeco;

    public bool CanPull => true;

    public IReadOnlyList<DeviceLogRecord>? ParsePush(string body, DeviceConnection device) => null;

    public async Task<DeviceHealth> CheckHealthAsync(DeviceConnection device, CancellationToken cancellationToken)
    {
        var result = await RunAsync(device, async (session, ct) =>
        {
            await session.SendAsync(ZkProtocol.GetFreeSizes, [], ct).ConfigureAwait(false);
            return await session.ReadAsync(ct).ConfigureAwait(false) is { Command: ZkProtocol.AckOk or ZkProtocol.AckData } ? Array.Empty<DeviceLogRecord>() : null;
        }, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? new DeviceHealth(true) : new DeviceHealth(false, result.ErrorKey, result.Detail);
    }

    public Task<DevicePullResult> PullAsync(DeviceConnection device, DateTimeOffset? since, CancellationToken cancellationToken) =>
        RunAsync(device, async (session, ct) =>
        {
            await session.SendAsync(ZkProtocol.GetFreeSizes, [], ct).ConfigureAwait(false);
            var sizes = await session.ReadAsync(ct).ConfigureAwait(false);
            var records = sizes is { Data.Length: >= 36 } ? BinaryPrimitives.ReadInt32LittleEndian(sizes.Data.AsSpan(32)) : -1;
            if (records < 0)
            {
                return null;
            }

            if (records == 0)
            {
                return Array.Empty<DeviceLogRecord>();
            }

            await session.SendAsync(ZkProtocol.AttendanceLogRequest, [], ct).ConfigureAwait(false);
            var data = await session.ReadBulkAsync(ct).ConfigureAwait(false);
            if (data is null)
            {
                return null;
            }

            await session.SendAsync(ZkProtocol.FreeData, [], ct).ConfigureAwait(false);
            return Parse(data, records, device.Zone, since);
        }, cancellationToken);

    /// <summary>Splits the log into records (40, 16 or 8 bytes each, found from the record count) and keeps those at or after <paramref name="since"/>.</summary>
    public static IReadOnlyList<DeviceLogRecord>? Parse(byte[] data, int records, TimeZoneInfo zone, DateTimeOffset? since)
    {
        if (data.Length < 4 || records <= 0)
        {
            return null;
        }

        var total = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (total < 0 || total > data.Length - 4)
        {
            return null;
        }

        var size = total / records;
        if (size is not (40 or 16 or 8) || size * records != total)
        {
            return null;
        }

        var list = new List<DeviceLogRecord>(records);
        for (var i = 0; i < records; i++)
        {
            var r = data.AsSpan(4 + (i * size), size);
            string user;
            uint stamp;
            int punch;
            switch (size)
            {
                case 40:
                    user = Encoding.ASCII.GetString(r.Slice(2, 24)).TrimEnd('\0', ' ');
                    stamp = BinaryPrimitives.ReadUInt32LittleEndian(r[27..]);
                    punch = r[31];
                    break;
                case 16:
                    user = BinaryPrimitives.ReadUInt32LittleEndian(r).ToString(CultureInfo.InvariantCulture);
                    stamp = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
                    punch = r[9];
                    break;
                default:
                    user = BinaryPrimitives.ReadUInt16LittleEndian(r).ToString(CultureInfo.InvariantCulture);
                    stamp = BinaryPrimitives.ReadUInt32LittleEndian(r[3..]);
                    punch = r[7];
                    break;
            }

            var local = ZkProtocol.DecodeTime(stamp);
            if (local == DateTime.MinValue || user.Length == 0)
            {
                continue;
            }

            var at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            if (since is { } from && at < from)
            {
                continue;
            }

            list.Add(new DeviceLogRecord(user, at, DeviceRules.DirectionOf(punch)));
        }

        return list;
    }

    private static async Task<DevicePullResult> RunAsync(DeviceConnection device, Func<ZkSession, CancellationToken, Task<IReadOnlyList<DeviceLogRecord>?>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(device.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return new DevicePullResult(false, [], Unsupported);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(device.Host, device.Port == 0 ? DefaultPort : device.Port, timeout.Token).ConfigureAwait(false);
            var session = new ZkSession(client.GetStream());
            if (await session.OpenAsync(device.Secret, timeout.Token).ConfigureAwait(false) is { } failure)
            {
                return new DevicePullResult(false, [], failure);
            }

            try
            {
                var records = await work(session, timeout.Token).ConfigureAwait(false);
                return records is null ? new DevicePullResult(false, [], BadReply) : new DevicePullResult(true, records);
            }
            finally
            {
                await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DevicePullResult(false, [], Unreachable, "timeout");
        }
        catch (SocketException e)
        {
            return new DevicePullResult(false, [], Unreachable, e.SocketErrorCode.ToString());
        }
        catch (IOException)
        {
            return new DevicePullResult(false, [], Unreachable, "connection lost");
        }
    }

    private sealed class ZkSession(NetworkStream stream)
    {
        private ushort _session;
        private ushort _reply = ushort.MaxValue - 1;

        public async Task SendAsync(ushort command, byte[] data, CancellationToken ct)
        {
            if (!ZkProtocol.Allowed.Contains(command))
            {
                throw new InvalidOperationException("Command not allowed: attendance records only.");
            }

            _reply = _reply == ushort.MaxValue ? (ushort)0 : (ushort)(_reply + 1);
            await stream.WriteAsync(ZkProtocol.Encode(command, _session, _reply, data), ct).ConfigureAwait(false);
        }

        public Task<ZkProtocol.Frame?> ReadAsync(CancellationToken ct) => ZkProtocol.ReadFrameAsync(stream, ct);

        /// <summary>Opens the session; returns a localization key when it fails, else null.</summary>
        public async Task<string?> OpenAsync(string? secret, CancellationToken ct)
        {
            await SendAsync(ZkProtocol.Connect, [], ct).ConfigureAwait(false);
            var reply = await ReadAsync(ct).ConfigureAwait(false);
            if (reply is null)
            {
                return BadReply;
            }

            _session = reply.Session;
            if (reply.Command == ZkProtocol.AckOk)
            {
                return null;
            }

            if (reply.Command != ZkProtocol.AckUnauthorized)
            {
                return BadReply;
            }

            var key = uint.TryParse(secret, NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 0;
            await SendAsync(ZkProtocol.Auth, ZkProtocol.CommKey(key, _session), ct).ConfigureAwait(false);
            var auth = await ReadAsync(ct).ConfigureAwait(false);
            return auth is { Command: ZkProtocol.AckOk } ? null : AuthFailed;
        }

        /// <summary>Reads a reply that is one frame, or a prepared transfer of data frames; returns the whole data (size prefix included).</summary>
        public async Task<byte[]?> ReadBulkAsync(CancellationToken ct)
        {
            var first = await ReadAsync(ct).ConfigureAwait(false);
            if (first is null)
            {
                return null;
            }

            if (first.Command is ZkProtocol.AckOk or ZkProtocol.AckData)
            {
                return first.Data;
            }

            if (first.Command != ZkProtocol.PrepareData || first.Data.Length < 4)
            {
                return null;
            }

            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(first.Data);
            if (size < 4 || size > ZkProtocol.MaxPacket * 8)
            {
                return null;
            }

            using var buffer = new MemoryStream(size);
            while (buffer.Length < size)
            {
                var next = await ReadAsync(ct).ConfigureAwait(false);
                if (next is null || next.Command == ZkProtocol.AckError)
                {
                    return null;
                }

                if (next.Command == ZkProtocol.Data)
                {
                    buffer.Write(next.Data);
                }
            }

            return buffer.ToArray();
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            try
            {
                await SendAsync(ZkProtocol.Exit, [], ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The device has gone: nothing to close.
            }
        }
    }
}
