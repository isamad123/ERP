using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Erp.Kernel.Domain.Identity;

/// <summary>
/// Time-based one-time passwords for authenticator apps (RFC 6238 over RFC 4226: HMAC-SHA1, 30-second steps, 6 digits),
/// FR-KRN-IAM-02. One step either side is accepted for clock drift. A code is accepted only for a step later than the
/// last one used, so a code cannot be replayed.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20;

    public static long StepAt(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / StepSeconds;

    public static DateTimeOffset StartOfStep(long step) => DateTimeOffset.FromUnixTimeSeconds(step * StepSeconds);

    /// <summary>The code of <paramref name="secret"/> for time step <paramref name="step"/>.</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 6238 authenticator apps use HMAC-SHA1; it is not used here as a collision-resistant hash.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = 1;
        for (var i = 0; i < digits; i++)
        {
            modulo *= 10;
        }

        return (binary % modulo).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The step a code matches (now, one step before or one after), or null. Steps at or before
    /// <paramref name="lastUsedStep"/> are refused (replay).
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (lastUsedStep is { } used && step <= used)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(secret, step)), System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>The <c>otpauth://</c> link authenticator apps read from a QR code.</summary>
    public static string EnrolmentUri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}

/// <summary>RFC 4648 base32 without padding, as used by authenticator apps.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = text.TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Not a base32 string.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
