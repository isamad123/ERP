using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// The generic push driver (slice S1.27b Build item 7, CR-088 slice S1.27 Q4): any device or gateway that can send an HTTPS request pushes
/// <c>{"records":[{"userNo":"12","at":"2026-11-24T08:01:02+05:00","direction":"in","key":"optional own record id"}]}</c> to the push endpoint,
/// signed with the device's secret. Offline buffering is the sender's: it may send hours later with the original times. A record with any
/// other member, or a body of the wrong shape, is refused as a whole and never logged, so a template, photo or name cannot slip in (FR-HRM-ATT-20).
/// </summary>
public sealed class GenericPushDriver : IAttendanceDeviceDriver
{
    public const string NotPullable = "errors.hrm.device.not_pullable";
    public const int MaxRecords = 2000;
    public const int MaxUserNoLength = 50;

    private static readonly HashSet<string> Known = new(["userNo", "at", "direction", "key"], StringComparer.Ordinal);

    public string Provider => DeviceProviders.GenericPush;

    public bool CanPull => false;

    public Task<DeviceHealth> CheckHealthAsync(DeviceConnection device, CancellationToken cancellationToken) => Task.FromResult(new DeviceHealth(true));

    public Task<DevicePullResult> PullAsync(DeviceConnection device, DateTimeOffset? since, CancellationToken cancellationToken) =>
        Task.FromResult(new DevicePullResult(false, [], NotPullable));

    public IReadOnlyList<DeviceLogRecord>? ParsePush(string body, DeviceConnection device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array
                || records.GetArrayLength() > MaxRecords)
            {
                return null;
            }

            var list = new List<DeviceLogRecord>(records.GetArrayLength());
            foreach (var item in records.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => !Known.Contains(p.Name)))
                {
                    return null;
                }

                if (!item.TryGetProperty("userNo", out var user) || user.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(user.GetString()) || user.GetString()!.Length > MaxUserNoLength
                    || !item.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String || !TryTime(at.GetString()!, device.Zone, out var when))
                {
                    return null;
                }

                var direction = item.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String && AttendanceDirections.All.Contains(d.GetString()!, StringComparer.Ordinal)
                    ? d.GetString()! : AttendanceDirections.Unknown;
                var key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (key is { Length: > 100 })
                {
                    return null;
                }

                list.Add(new DeviceLogRecord(user.GetString()!.Trim(), when, direction, key));
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A time with an offset is exact; one without is the device's wall clock in the company's time zone.</summary>
    private static bool TryTime(string text, TimeZoneInfo zone, out DateTimeOffset when)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when) && (text.EndsWith('Z') || text.Contains('+', StringComparison.Ordinal) || text.LastIndexOf('-') > 9))
        {
            when = when.ToUniversalTime();
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            when = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            return true;
        }

        return false;
    }
}
