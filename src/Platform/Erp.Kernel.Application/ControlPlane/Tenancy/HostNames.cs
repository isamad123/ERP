using System.Globalization;

namespace Erp.Kernel.Application.ControlPlane.Tenancy;

/// <summary>Normalizes the request host the way cp.tenant_domains stores it: lower case, no port, no trailing dot.</summary>
public static class HostNames
{
    /// <summary>The normalized host, or null when the value is not a plausible DNS host name.</summary>
    public static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var value = host.Trim();
        if (value.StartsWith('['))
        {
            return null; // IPv6 literal: customers are reached by name only
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var port = value[(colon + 1)..];
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return null;
            }

            value = value[..colon];
        }

        value = value.TrimEnd('.').ToLowerInvariant();
        if (value.Length is 0 or > 253)
        {
            return null;
        }

        var labels = value.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') || !label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return null;
            }
        }

        // An all-digit last label is an IPv4 address (or not a DNS name): customers are reached by name only (S1.03 review finding 12).
        return labels[^1].All(char.IsAsciiDigit) ? null : value;
    }
}
