using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.Hrm.Application.Attendance;

/// <summary>Register or change a device (slice S1.27b Build item 6). <see cref="Credentials"/> goes to the secret store and is never returned.</summary>
public sealed class DeviceRequest
{
    public Guid? CompanyId { get; init; }

    public Guid? LocationId { get; init; }

    public string? SerialNo { get; init; }

    public string? Model { get; init; }

    /// <summary>The driver: <c>generic_push</c> or <c>zkteco</c>.</summary>
    public string? Provider { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary><c>tcp</c>, <c>udp</c> or <c>http</c>.</summary>
    public string? Protocol { get; init; }

    /// <summary>The device password, communication key or push secret. Written to the secret store only; an empty value on a change keeps the stored one.</summary>
    public string? Credentials { get; init; }

    /// <summary><c>active</c> or <c>retired</c>.</summary>
    public string? Status { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>Map a device user number to an employment (slice S1.27b Build item 8).</summary>
public sealed class MapRequest
{
    public Guid? CompanyId { get; init; }

    /// <summary>The device, or null for all devices of the company (a badge or PIN number).</summary>
    public Guid? DeviceId { get; init; }

    public string? DeviceUserNo { get; init; }

    public Guid? EmploymentId { get; init; }

    /// <summary>The employee number, when the employment id is not known (the console types it); one of the two is needed.</summary>
    public string? EmployeeNo { get; init; }

    public DateOnly? ValidFrom { get; init; }

    /// <summary>The date the employee consented to biometric capture; required while <c>hrm.att.biometric_enabled</c> is on.</summary>
    public DateOnly? ConsentDate { get; init; }
}

public sealed class EndMapRequest
{
    /// <summary>The first day without the mapping; defaults to today.</summary>
    public DateOnly? EndDate { get; init; }

    public long? RowVersion { get; init; }
}

public sealed class RemovalDoneRequest
{
    public long? RowVersion { get; init; }
}

public sealed class DiscardRequest
{
    public long? RowVersion { get; init; }
}

public sealed class PunchLogQuery
{
    public Guid? CompanyId { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public string? Source { get; init; }

    public Guid? DeviceId { get; init; }

    public Guid? EmploymentId { get; init; }

    public string? EmployeeNo { get; init; }

    public bool? IncludeDuplicates { get; init; }

    public int? Limit { get; init; }

    /// <summary>Continue after the last event of the previous page (<c>eventAt</c>, <c>id</c> as <c>eventAt|id</c>).</summary>
    public string? After { get; init; }
}

/// <summary>The device registry and console (slice S1.27b Build items 6 and 9; FR-HRM-ATT-02).</summary>
public interface IDeviceService
{
    Task<PeopleResult> CreateAsync(DeviceRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid deviceId, DeviceRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ListAsync(Guid? companyId, CancellationToken cancellationToken);

    /// <summary>Asks for a device to be read at the next minute: sets <c>sync_requested_at</c> for the sync job.</summary>
    Task<PeopleResult> SyncNowAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<PeopleResult> PunchLogAsync(PunchLogQuery query, CancellationToken cancellationToken);
}

/// <summary>Device-user mapping, consent, removal tasks and unmapped punches (slice S1.27b Build item 8; FR-HRM-ATT-03, ATT-20).</summary>
public interface IDeviceMappingService
{
    Task<PeopleResult> MapAsync(MapRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> EndAsync(Guid mapId, EndMapRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ConfirmRemovalAsync(Guid mapId, RemovalDoneRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ListAsync(Guid? companyId, Guid? deviceId, string? state, CancellationToken cancellationToken);

    Task<PeopleResult> UnmappedAsync(Guid? companyId, Guid? deviceId, string? status, CancellationToken cancellationToken);

    Task<PeopleResult> DiscardAsync(Guid punchId, DiscardRequest request, CancellationToken cancellationToken);
}

/// <summary>The signed push of a device or its gateway (slice S1.27b Build item 7).</summary>
public interface IDevicePushService
{
    /// <summary>Verifies the signature against the device's secret, parses the body through the device's driver and ingests the punches.</summary>
    Task<PeopleResult> PushAsync(Guid deviceId, string body, string? signature, CancellationToken cancellationToken);
}

/// <summary>Pure rules of the device services: directions, keys, signatures and connection checks.</summary>
public static class DeviceRules
{
    public const string SignaturePrefix = "v1=";

    /// <summary>Seconds a signed request stays acceptable (replays older than this are refused).</summary>
    public const int SignatureToleranceSeconds = 300;

    public static readonly IReadOnlyList<string> Protocols = ["tcp", "udp", "http"];

    /// <summary>Direction of a device punch status code (ZKTeco family: 0 in, 1 out, 2 break out, 3 break in, 4 overtime in, 5 overtime out).</summary>
    public static string DirectionOf(int status) => status switch
    {
        0 or 4 => AttendanceDirections.In,
        1 or 5 => AttendanceDirections.Out,
        2 => AttendanceDirections.BreakOut,
        3 => AttendanceDirections.BreakIn,
        _ => AttendanceDirections.Unknown,
    };

    /// <summary>The event store key of a device punch: the device's own record key when it sends one, else its user, time and direction.</summary>
    public static string PunchKey(Guid deviceId, DeviceLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var tail = string.IsNullOrWhiteSpace(record.SourceKey)
            ? string.Create(CultureInfo.InvariantCulture, $"{record.DeviceUserNo}:{record.At.ToUnixTimeSeconds()}:{record.Direction}")
            : record.SourceKey.Trim();
        return $"biometric:{deviceId:N}:{tail}";
    }

    /// <summary>The signature a pusher computes: <c>v1=</c> and the hex HMAC-SHA256 of <c>timestamp + "." + body</c> under the device's secret.</summary>
    public static string Sign(string secret, long timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{body}")));
        return SignaturePrefix + Convert.ToHexStringLower(mac);
    }

    /// <summary>The header value is <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>. True only when the timestamp is within the tolerance and the mac matches (constant-time compare).</summary>
    public static bool VerifySignature(string secret, string? header, string body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        long? stamp = null;
        string? mac = null;
        foreach (var part in header.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("t=", StringComparison.Ordinal) && long.TryParse(p.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                stamp = t;
            }
            else if (p.StartsWith(SignaturePrefix, StringComparison.Ordinal))
            {
                mac = p;
            }
        }

        if (stamp is not { } ts || mac is null || Math.Abs(now.ToUnixTimeSeconds() - ts) > SignatureToleranceSeconds)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, ts, body));
        var given = Encoding.ASCII.GetBytes(mac);
        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>The header a pusher sends for a body at a time.</summary>
    public static string SignatureHeader(string secret, long timestamp, string body) =>
        string.Create(CultureInfo.InvariantCulture, $"t={timestamp},") + Sign(secret, timestamp, body);

    /// <summary>A device shows online while it was heard from within the offline window (setting <c>hrm.att.device_offline_minutes</c>).</summary>
    public static bool IsOnline(DateTimeOffset? lastSeenAt, DateTimeOffset now, int offlineMinutes) =>
        lastSeenAt is { } seen && now - seen <= TimeSpan.FromMinutes(offlineMinutes);

    /// <summary>A host name or address with no scheme, path, port or spaces.</summary>
    public static bool IsHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 255 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_' or '[' or ']');
}
