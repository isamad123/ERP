using System.Text.RegularExpressions;

namespace Erp.ArchitectureTests.Rules;

/// <summary>Text scan of C# and SQL sources for a tenant_id column (D-47), including SQL inside C# strings.</summary>
public static partial class SourceTextScanner
{
    private static readonly string[] Extensions = [".cs", ".sql"];

    public static IReadOnlyList<string> FindTenantIdText(IEnumerable<string> folders)
    {
        var violations = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || IsExempt(folder, file))
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (TenantIdPattern().IsMatch(line))
                    {
                        violations.Add($"{file}:{lineNumber}: '{line.Trim()}' mentions tenant_id; business tables carry no tenant_id (D-47).");
                    }
                }
            }
        }

        return violations;
    }

    // Build output is skipped. Control-plane code (schema cp, approved design control-plane.yaml) lives in a
    // ControlPlane folder and is the one place a tenant_id is legitimate.
    private static bool IsExempt(string folder, string file)
    {
        var segments = Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj" or AssemblyScanner.ControlPlaneNamespaceSegment);
    }

    [GeneratedRegex(@"\btenant_id\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantIdPattern();
}
