using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Kernel.Contracts.Numbering;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Every numbered document type of the modules the host loads (generated <c>&lt;Module&gt;NumberingTypes</c>, slice S1.08
/// Q1). Provisioning and <c>migrate</c> read the same list from each module's <c>Access/numbering.g.json</c>.
/// </summary>
public sealed class DocumentTypeRegistry
{
    private readonly Dictionary<string, DocumentType> _types;

    public DocumentTypeRegistry(IEnumerable<IDocumentTypeSource> sources)
        : this(sources.SelectMany(s => s.Types))
    {
    }

    public DocumentTypeRegistry(IEnumerable<DocumentType> types) =>
        _types = types.ToDictionary(t => t.Code, StringComparer.Ordinal);

    public IReadOnlyCollection<DocumentType> All => [.. _types.Values.OrderBy(t => t.Code, StringComparer.Ordinal)];

    public DocumentType? Find(string? code) => code is null ? null : _types.GetValueOrDefault(code);

    public DocumentType Get(string? code) => Find(code) ?? throw new NumberingException("errors.numbering.type_unknown");

    /// <summary>The document types of the manifest folders (one <c>numbering.g.json</c> per module that has numbered tables).</summary>
    public static DocumentTypeRegistry FromManifestFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var types = new List<DocumentType>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, "numbering.g.json");
            if (!File.Exists(path))
            {
                continue;
            }

            var file = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(path)) ?? throw new InvalidDataException(path + " is empty.");
            types.AddRange(file.DocumentTypes.Select(t => new DocumentType(t.Code, t.Module, t.Table, t.Column, t.Assign, t.GapFreeAllowed, t.OverrideAllowed, t.TenantWide, t.DefaultPattern, t.DefaultReset, t.HasBranch)));
        }

        return new DocumentTypeRegistry(types);
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("documentTypes")]
        public List<ManifestEntry> DocumentTypes { get; init; } = [];
    }

    private sealed class ManifestEntry
    {
        [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
        [JsonPropertyName("module")] public string Module { get; init; } = string.Empty;
        [JsonPropertyName("table")] public string Table { get; init; } = string.Empty;
        [JsonPropertyName("column")] public string Column { get; init; } = string.Empty;
        [JsonPropertyName("assign")] public string Assign { get; init; } = string.Empty;
        [JsonPropertyName("gapFreeAllowed")] public bool GapFreeAllowed { get; init; }
        [JsonPropertyName("overrideAllowed")] public bool OverrideAllowed { get; init; }
        [JsonPropertyName("tenantWide")] public bool TenantWide { get; init; }
        [JsonPropertyName("defaultPattern")] public string DefaultPattern { get; init; } = string.Empty;
        [JsonPropertyName("defaultReset")] public string DefaultReset { get; init; } = string.Empty;
        [JsonPropertyName("hasBranch")] public bool HasBranch { get; init; }
    }
}
