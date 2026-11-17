using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// A module's workflow manifest <c>&lt;module&gt;.Infrastructure/Access/workflow.json</c> (slice S1.10 build item 1, ruling
/// Q11): the document types it sends for approval with their fields and default routes. Hand-written data, shipped to the
/// migrator with the other access manifests and embedded for the API host.
/// </summary>
public static class WorkflowManifest
{
    public const string FileName = "workflow.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Reads a manifest; its content is checked by <see cref="WorkflowCatalog"/>.</summary>
    public static IReadOnlyList<WorkflowDocumentType> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ManifestFile>(json, Options) ?? throw new InvalidDataException("empty workflow manifest");
        return [.. file.DocumentTypes.Select(t => new WorkflowDocumentType(
            t.Code ?? string.Empty,
            file.Module ?? string.Empty,
            t.EntitlementKey ?? string.Empty,
            t.Name ?? new Dictionary<string, string>(),
            [.. (t.Fields ?? []).Select(f => new WorkflowField(f.Name ?? string.Empty, f.Type ?? string.Empty, f.Label ?? new Dictionary<string, string>(), f.Choices ?? []))],
            t.CompanyField ?? string.Empty,
            t.SubjectFields ?? [],
            t.Title ?? new Dictionary<string, string>(),
            t.Link ?? string.Empty,
            t.BulkApproval,
            t.RequiresApproval,
            t.DecidePermission,
            t.DefaultRoute.ValueKind == JsonValueKind.Undefined ? default : t.DefaultRoute.Clone()))];
    }

    /// <summary>The document types of the manifest folders (one <c>workflow.json</c> per module that has any).</summary>
    public static IReadOnlyList<WorkflowDocumentType> FromFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var types = new List<WorkflowDocumentType>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
            {
                types.AddRange(Parse(File.ReadAllText(path)));
            }
        }

        return types;
    }

    private sealed class ManifestFile
    {
        public string? Module { get; init; }

        public List<TypeEntry> DocumentTypes { get; init; } = [];
    }

    private sealed class TypeEntry
    {
        public string? Code { get; init; }

        public string? EntitlementKey { get; init; }

        public Dictionary<string, string>? Name { get; init; }

        public List<FieldEntry>? Fields { get; init; }

        public string? CompanyField { get; init; }

        public List<string>? SubjectFields { get; init; }

        public Dictionary<string, string>? Title { get; init; }

        public string? Link { get; init; }

        public bool BulkApproval { get; init; }

        public bool RequiresApproval { get; init; }

        public string? DecidePermission { get; init; }

        public JsonElement DefaultRoute { get; init; }
    }

    private sealed class FieldEntry
    {
        public string? Name { get; init; }

        public string? Type { get; init; }

        public Dictionary<string, string>? Label { get; init; }

        public List<string>? Choices { get; init; }
    }
}

/// <summary>A module's document types read from its embedded manifest (registered by the host per module).</summary>
public sealed class WorkflowManifestSource(string json) : IWorkflowDocumentTypeSource
{
    public IReadOnlyList<WorkflowDocumentType> Types { get; } = WorkflowManifest.Parse(json);
}

/// <summary>
/// Every registered document type (slice S1.10 build item 1), checked when the host starts: a wrong manifest stops the host
/// instead of failing a request later. A document type's fields are what its route conditions and approver fields may use.
/// </summary>
public sealed partial class WorkflowCatalog
{
    private readonly Dictionary<string, WorkflowDocumentType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, RouteField>> _fields = new(StringComparer.Ordinal);

    public WorkflowCatalog(IEnumerable<IWorkflowDocumentTypeSource> sources)
        : this(sources?.SelectMany(s => s.Types) ?? throw new ArgumentNullException(nameof(sources)))
    {
    }

    public WorkflowCatalog(IEnumerable<WorkflowDocumentType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var problems = new List<string>();
        foreach (var type in types)
        {
            var found = Check(type);
            if (!_types.TryAdd(type.Code, type))
            {
                found.Add("duplicate document type code");
            }

            problems.AddRange(found.Select(p => $"{type.Code}: {p}"));
            _fields[type.Code] = type.Fields.ToDictionary(f => f.Name, f => new RouteField(f.Name, f.Type, f.Choices), StringComparer.Ordinal);
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Workflow manifest problems: " + string.Join("; ", problems));
        }
    }

    public IReadOnlyCollection<WorkflowDocumentType> All => _types.Values;

    public WorkflowDocumentType? Find(string code) => _types.GetValueOrDefault(code);

    /// <summary>The route fields of a registered document type.</summary>
    public IReadOnlyDictionary<string, RouteField> Fields(string code) => _fields[code];

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_.]{0,80}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex("\\{([a-z0-9_]+)\\}")]
    private static partial Regex Placeholder();

    /// <summary>The problems of one document type (empty when it is valid).</summary>
    public static List<string> Check(WorkflowDocumentType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var problems = new List<string>();
        if (type.Code.Length > 40 || !CodePattern().IsMatch(type.Code) || type.Code[..type.Code.IndexOf('.', StringComparison.Ordinal)] != type.Module)
        {
            problems.Add("code must be <module>.<name> in lower case, at most 40 characters");
        }

        if (string.IsNullOrWhiteSpace(type.EntitlementKey))
        {
            problems.Add("entitlement_key is required");
        }

        Bilingual(type.Name, "name", problems);
        Bilingual(type.Title, "title", problems);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            if (!FieldPattern().IsMatch(field.Name) || !names.Add(field.Name))
            {
                problems.Add($"field '{field.Name}' has a bad or duplicate name");
            }

            if (!FieldTypes.All.Contains(field.Type, StringComparer.Ordinal))
            {
                problems.Add($"field '{field.Name}' has unknown type '{field.Type}'");
            }

            if ((field.Type == FieldTypes.Choice) != (field.Choices.Count > 0))
            {
                problems.Add($"field '{field.Name}': choices are required for a choice field and only for it");
            }

            Bilingual(field.Label, $"field '{field.Name}' label", problems);
        }

        var byName = type.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        if (!byName.TryGetValue(type.CompanyField, out var company) || company.Type != FieldTypes.Company)
        {
            problems.Add("company_field must name a field of type company");
        }

        foreach (var subject in type.SubjectFields)
        {
            if (!byName.TryGetValue(subject, out var s) || s.Type != FieldTypes.User)
            {
                problems.Add($"subject field '{subject}' must name a field of type user");
            }
        }

        if (string.IsNullOrWhiteSpace(type.Link))
        {
            problems.Add("link is required");
        }

        foreach (var text in type.Title.Values.Append(type.Link))
        {
            foreach (Match m in Placeholder().Matches(text))
            {
                if (m.Groups[1].Value != "document_id" && !byName.ContainsKey(m.Groups[1].Value))
                {
                    problems.Add($"placeholder {{{m.Groups[1].Value}}} names no field");
                }
            }
        }

        var fields = type.Fields.ToDictionary(f => f.Name, f => new RouteField(f.Name, f.Type, f.Choices), StringComparer.Ordinal);
        var (_, routeProblems) = RouteFormat.Parse(type.DefaultRoute, fields, type.BulkApproval);
        problems.AddRange(routeProblems.Select(p => "default_route " + p));
        return problems;
    }

    /// <summary>A title or link with its placeholders filled from the request's values.</summary>
    public static string Render(string template, Guid documentId, IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        return Placeholder().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (name == "document_id")
            {
                return documentId.ToString();
            }

            return values.TryGetValue(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString()!,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => v.GetRawText(),
            } : string.Empty;
        });
    }

    private static void Bilingual(IReadOnlyDictionary<string, string> texts, string what, List<string> problems)
    {
        if (!texts.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en) || !texts.TryGetValue("ur", out var ur) || string.IsNullOrWhiteSpace(ur))
        {
            problems.Add($"{what} needs English and Urdu text");
        }
    }
}
