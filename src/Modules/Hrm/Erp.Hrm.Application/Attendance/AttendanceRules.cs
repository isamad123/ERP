using System.Globalization;
using System.Net;

namespace Erp.Hrm.Application.Attendance;

/// <summary>Pure rules of attendance capture (slice S1.27a): network ranges, QR periods and the duplicate window. No clock and no database.</summary>
public static class AttendanceRules
{
    /// <summary>
    /// Whether an address is inside one of the ranges (<c>203.0.113.0/24</c>, <c>10.1.2.3</c> for a single address, IPv6 too). No ranges means anywhere.
    /// A range that cannot be read never matches; an unknown address is refused when ranges exist.
    /// </summary>
    public static bool AddressAllowed(string? address, IReadOnlyCollection<string> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        foreach (var range in ranges)
        {
            var text = range?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (text.Contains('/', StringComparison.Ordinal))
            {
                if (IPNetwork.TryParse(text, out var network) && network.Contains(ip))
                {
                    return true;
                }
            }
            else if (IPAddress.TryParse(text, out var single) && single.Equals(ip))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a text is a range or single address <see cref="AddressAllowed"/> can use.</summary>
    public static bool IsRange(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (text.Contains('/', StringComparison.Ordinal) ? IPNetwork.TryParse(text.Trim(), out _) : IPAddress.TryParse(text.Trim(), out _));

    /// <summary>The QR period a moment falls in: whole windows since the Unix epoch.</summary>
    public static long QrPeriod(DateTimeOffset now, int windowSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);
        return now.ToUnixTimeSeconds() / windowSeconds;
    }

    /// <summary>When a QR period ends (the code shown then changes).</summary>
    public static DateTimeOffset QrPeriodEnd(long period, int windowSeconds) => DateTimeOffset.FromUnixTimeSeconds((period + 1) * windowSeconds);

    /// <summary>A scanned code is accepted in its own period and the one before (a person who scans just before it changes).</summary>
    public static bool QrPeriodAccepted(long codePeriod, long currentPeriod) => codePeriod == currentPeriod || codePeriod == currentPeriod - 1;

    /// <summary>
    /// Whether a punch is a duplicate of an earlier counted one: same employment and direction, and the two within the window (either
    /// way round, because a late batch can bring an earlier punch after a later one that is already stored and cannot change).
    /// </summary>
    public static bool WithinWindow(DateTimeOffset eventAt, DateTimeOffset other, int windowSeconds) =>
        windowSeconds > 0 && Math.Abs((eventAt - other).TotalSeconds) <= windowSeconds;

    /// <summary>The key stored for a punch: unique per source (and, for the web, QR and kiosk, per person or station).</summary>
    public static string StoredKey(string source, string scope, string key) =>
        string.Create(CultureInfo.InvariantCulture, $"{source}:{scope}:{key}");
}
