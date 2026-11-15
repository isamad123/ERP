using System.Xml.Linq;

namespace Erp.ArchitectureTests.Rules;

public enum ProjectKind
{
    BuildingBlocks,
    Kernel,
    BusinessModule,
    Host,
    Tool,
    Test,
}

/// <summary>One project, classified by where it lives and by its layer suffix.</summary>
public sealed record ProjectInfo(string Name, ProjectKind Kind, string Module, string? Layer, IReadOnlyList<string> References)
{
    public static readonly string[] Layers = ["Contracts", "Domain", "Application", "Infrastructure", "Api"];

    public static ProjectInfo Create(string name, ProjectKind kind, IReadOnlyList<string> references)
    {
        var lastDot = name.LastIndexOf('.');
        var suffix = lastDot < 0 ? null : name[(lastDot + 1)..];
        var layer = suffix is not null && Layers.Contains(suffix) ? suffix : null;
        var module = layer is null ? name : name[..lastDot];
        return new ProjectInfo(name, kind, module, layer, references);
    }
}

/// <summary>Reads every .csproj in the repository and its ProjectReference items.</summary>
public static class ProjectGraph
{
    public static IReadOnlyList<ProjectInfo> Load(string root)
    {
        var result = new List<ProjectInfo>();
        foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var references = XDocument.Load(file)
                .Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
                .Where(n => n.Length > 0)
                .ToList();
            result.Add(ProjectInfo.Create(Path.GetFileNameWithoutExtension(file), Classify(relative), references));
        }

        return result;
    }

    private static ProjectKind Classify(string relativePath) => relativePath switch
    {
        _ when relativePath.StartsWith("src/BuildingBlocks/", StringComparison.Ordinal) => ProjectKind.BuildingBlocks,
        _ when relativePath.StartsWith("src/Platform/", StringComparison.Ordinal) => ProjectKind.Kernel,
        _ when relativePath.StartsWith("src/Modules/", StringComparison.Ordinal) => ProjectKind.BusinessModule,
        _ when relativePath.StartsWith("src/Host/", StringComparison.Ordinal) => ProjectKind.Host,
        _ when relativePath.StartsWith("tools/", StringComparison.Ordinal) => ProjectKind.Tool,
        _ when relativePath.StartsWith("tests/", StringComparison.Ordinal) => ProjectKind.Test,
        _ => throw new InvalidOperationException(
            $"Project {relativePath} is outside src/BuildingBlocks, src/Platform, src/Modules, src/Host, tools or tests."),
    };
}
