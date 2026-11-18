using System.Security.Cryptography;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>The kinds of content the kernel accepts (ruling Q22): checked from the first bytes, never from the name or the declared type alone.</summary>
public static class FileValidation
{
    /// <summary>Allowed type codes and the content types they stand for.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["pdf"] = ["application/pdf"],
        ["jpeg"] = ["image/jpeg"],
        ["png"] = ["image/png"],
        ["word"] = ["application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
        ["excel"] = ["application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
        ["text"] = ["text/plain"],
        ["csv"] = ["text/csv", "text/plain"],
    };

    public const string DefaultAllowed = "pdf,jpeg,png,word,excel,text,csv";

    /// <summary>The type code the content really is, or null when it matches none of the allowed kinds.</summary>
    public static string? Detect(ReadOnlySpan<byte> content, string fileName)
    {
        if (content.Length >= 5 && content[..5].SequenceEqual("%PDF-"u8))
        {
            return "pdf";
        }

        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "jpeg";
        }

        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "png";
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            return ext is ".xls" ? "excel" : ext is ".doc" ? "word" : null;
        }

        if (content.Length >= 4 && content[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return ext is ".docx" ? "word" : ext is ".xlsx" ? "excel" : null;
        }

        return IsText(content) ? (ext is ".csv" ? "csv" : "text") : null;
    }

    public static string ContentTypeOf(string type, string fileName) => type switch
    {
        "word" => Path.GetExtension(fileName).Equals(".doc", StringComparison.OrdinalIgnoreCase) ? Types[type][0] : Types[type][1],
        "excel" => Path.GetExtension(fileName).Equals(".xls", StringComparison.OrdinalIgnoreCase) ? Types[type][0] : Types[type][1],
        _ => Types[type][0],
    };

    public static string Checksum(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Text has no control characters other than tab, line feed, carriage return and form feed, and is valid UTF-8.</summary>
    private static bool IsText(ReadOnlySpan<byte> content)
    {
        foreach (var b in content)
        {
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D or 0x0C))
            {
                return false;
            }
        }

        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetCharCount(content);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
