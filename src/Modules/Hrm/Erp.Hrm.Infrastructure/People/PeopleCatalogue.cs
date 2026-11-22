using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Infrastructure.People;

/// <summary>
/// The provisional person catalogue (slice S1.20a, ruling Q3): identifier kinds with their format rules, marital statuses and
/// relations, read from the embedded <c>people-catalogue.json</c>. Pack data, never code: S1.23 moves the identifier kinds into the
/// Pakistan pack and S1.15b option lists replace the two code lists.
/// </summary>
public sealed class PeopleCatalogue
{
    private readonly Dictionary<string, Regex> _patterns;

    private PeopleCatalogue(IReadOnlyList<IdentifierKind> kinds, IReadOnlyList<CatalogueCode> maritalStatuses, IReadOnlyList<CatalogueCode> relations)
    {
        IdentifierKinds = kinds;
        MaritalStatuses = maritalStatuses;
        Relations = relations;
        _patterns = kinds.ToDictionary(k => k.Code + "|" + k.Country, k => new Regex(k.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), StringComparer.Ordinal);
    }

    public IReadOnlyList<IdentifierKind> IdentifierKinds { get; }

    public IReadOnlyList<CatalogueCode> MaritalStatuses { get; }

    public IReadOnlyList<CatalogueCode> Relations { get; }

    public static PeopleCatalogue Default { get; } = Load();

    /// <summary>The kind for a country: its own entry, else an entry valid for any country (<c>*</c>).</summary>
    public IdentifierKind? Find(string kind, string country) =>
        IdentifierKinds.FirstOrDefault(k => k.Code == kind && k.Country == country) ?? IdentifierKinds.FirstOrDefault(k => k.Code == kind && k.Country == "*");

    public bool Matches(IdentifierKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return _patterns[kind.Code + "|" + kind.Country].IsMatch(value);
    }

    /// <summary>
    /// The value the blind index is taken over: capital letters and digits only, so "12345-1234567-1" and "1234512345671" are the
    /// same identifier (FR-HRM-EMP-18: exact match on the blind index).
    /// </summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var b = new StringBuilder(value.Length);
        foreach (var ch in value.ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                b.Append(ch);
            }
        }

        return b.ToString();
    }

    /// <summary>The stored form: a 13-digit CNIC or NICOP in its dash layout (ruling Q3), other kinds trimmed and in capitals.</summary>
    public static string Display(IdentifierKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var normal = Normalize(value);
        return kind.Code is "cnic" or "nicop" && normal.Length == 13 ? $"{normal[..5]}-{normal[5..12]}-{normal[12..]}" : value.Trim().ToUpperInvariant();
    }

    /// <summary>A person's name as duplicate detection compares it: letters only, lower case, parts in order, single spaces.</summary>
    public static string NormalizeName(string? first, string? middle, string? last)
    {
        var parts = new[] { first, middle, last }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new string([.. p!.ToLowerInvariant().Where(c => char.IsLetter(c) || char.IsWhiteSpace(c))]))
            .SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.Join(' ', parts);
    }

    private static PeopleCatalogue Load()
    {
        using var stream = typeof(PeopleCatalogue).Assembly.GetManifestResourceStream("Erp.Hrm.Infrastructure.People.people-catalogue.json")
            ?? throw new InvalidOperationException("people-catalogue.json is not embedded.");
        var file = JsonSerializer.Deserialize<CatalogueFile>(stream, JsonSerializerOptions.Web) ?? throw new InvalidDataException("people-catalogue.json is empty.");
        return new PeopleCatalogue(file.IdentifierKinds, file.MaritalStatuses, file.Relations);
    }

    private sealed class CatalogueFile
    {
        public List<IdentifierKind> IdentifierKinds { get; init; } = [];

        public List<CatalogueCode> MaritalStatuses { get; init; } = [];

        public List<CatalogueCode> Relations { get; init; } = [];
    }
}
