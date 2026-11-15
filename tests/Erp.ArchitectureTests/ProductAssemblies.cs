using Erp.ArchitectureTests.Rules;

namespace Erp.ArchitectureTests;

/// <summary>Finds the compiled product assemblies (src and tools) in the test output folder.</summary>
public static class ProductAssemblies
{
    private static readonly Lazy<IReadOnlyList<ProjectInfo>> LazyProjects = new(() => ProjectGraph.Load(RepositoryLayout.Root));

    public static IReadOnlyList<ProjectInfo> Projects => LazyProjects.Value;

    public static IReadOnlyList<ProjectInfo> Product =>
        Projects.Where(p => p.Kind is not ProjectKind.Test).ToList();

    public static string PathOf(ProjectInfo project)
    {
        var path = Path.Combine(AppContext.BaseDirectory, project.Name + ".dll");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"{project.Name}.dll is not in the architecture-test output. Reference the new project from a host " +
                "(or, for a tool, from Erp.ArchitectureTests) so the guard rails can see it.");
    }
}
