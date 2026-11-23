using System.Text.Json.Nodes;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Itg;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// Reads the import types of <c>Access/imports.json</c> (slice S1.16a, D-10): each module declares the types of data its
/// tables can take, the template columns, the key that finds an existing record, the permission and the writer. A manifest
/// problem stops start-up, like a job manifest problem.
/// </summary>
public static class ImportManifest
{
    private static readonly string[] ColumnTypes = ["text", "number", "date", "bool", "option"];

    /// <summary>The kernel's own manifest, embedded in this assembly.</summary>
    public static string KernelJson { get; } = LoadKernel();

    public static IReadOnlyList<ImportTypeDefinition> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("imports.json must be an object.");
        var module = root["module"]?.GetValue<string>() ?? throw new InvalidDataException("imports.json needs a module.");
        var types = new List<ImportTypeDefinition>();
        foreach (var node in root["types"] as JsonArray ?? [])
        {
            var t = node as JsonObject ?? throw new InvalidDataException("An import type must be an object.");
            var code = t["code"]?.GetValue<string>() ?? throw new InvalidDataException("An import type needs a code.");
            var columns = new List<ImportColumnDefinition>();
            foreach (var cn in t["columns"] as JsonArray ?? [])
            {
                var c = (JsonObject)cn!;
                var type = c["type"]?.GetValue<string>() ?? "text";
                if (!ColumnTypes.Contains(type, StringComparer.Ordinal))
                {
                    throw new InvalidDataException($"{code}: column type {type} is not known.");
                }

                var name = c["name"]!.GetValue<string>();
                columns.Add(new ImportColumnDefinition(
                    name,
                    c["field"]?.GetValue<string>() ?? name,
                    type,
                    c["required"]?.GetValue<bool>() ?? false,
                    Names(c["label"]),
                    c["lookup"]?.GetValue<string>(),
                    (c["options"] as JsonArray)?.Select(o => o!.GetValue<string>()).ToList(),
                    c["example"]?.GetValue<string>()));
            }

            var keys = (t["keyColumns"] as JsonArray)?.Select(k => k!.GetValue<string>()).ToList() ?? [];
            foreach (var key in keys)
            {
                if (!columns.Exists(c => c.Name == key))
                {
                    throw new InvalidDataException($"{code}: key column {key} is not a column of the type.");
                }
            }

            types.Add(new ImportTypeDefinition(
                code,
                module,
                t["table"]!.GetValue<string>(),
                Names(t["name"]),
                t["permission"]!.GetValue<string>(),
                keys,
                columns,
                t["writer"]?.GetValue<string>(),
                t["canUpdate"]?.GetValue<bool>() ?? false,
                t["postCommit"]?.GetValue<string>()));
        }

        return types;
    }

    public static IReadOnlyList<ImportTypeDefinition> FromFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var found = new List<ImportTypeDefinition>();
        foreach (var folder in folders)
        {
            var file = Path.Combine(folder, "imports.json");
            if (File.Exists(file))
            {
                found.AddRange(Parse(File.ReadAllText(file)));
            }
        }

        return found;
    }

    private static Dictionary<string, string> Names(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal) : [];

    private static string LoadKernel()
    {
        using var stream = typeof(ImportManifest).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Access.imports.json")
            ?? throw new InvalidOperationException("imports.json is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Every import type of the installed modules, by code (the kernel's plus the modules' sources).</summary>
public sealed class ImportCatalog
{
    private readonly Dictionary<string, ImportTypeDefinition> _types;

    public ImportCatalog(IEnumerable<ImportManifestSource> sources)
        : this(ImportManifest.Parse(ImportManifest.KernelJson).Concat((sources ?? throw new ArgumentNullException(nameof(sources))).SelectMany(s => ImportManifest.Parse(s.Json))))
    {
    }

    public ImportCatalog(IEnumerable<ImportTypeDefinition> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types.GroupBy(t => t.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ImportTypeDefinition> All => _types.Values;

    public ImportTypeDefinition? Find(string code) => _types.GetValueOrDefault(code);
}
