namespace Erp.ArchitectureTests.Rules;

/// <summary>Locates the repository from the test output folder, so rules read the real files.</summary>
public static class RepositoryLayout
{
    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    public static string Root => LazyRoot.Value;

    public static string Src => Path.Combine(Root, "src");

    /// <summary>Folders whose code must never carry a tenant_id (D-47): business modules and the kernel.</summary>
    public static IReadOnlyList<string> TenantDatabaseCodeFolders =>
        [Path.Combine(Src, "Modules"), Path.Combine(Src, "Platform")];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            $"Erp.sln not found above {AppContext.BaseDirectory}; architecture tests must run inside the repository.");
    }
}
