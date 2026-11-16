using System.Security.Cryptography;
using System.Text;
using Erp.BuildingBlocks.Infrastructure.Migrations;

namespace Erp.Kernel.Infrastructure.ControlPlane.Migration;

/// <summary>One migration file: V{version}__{module}__{description}.sql (spec 03 section 9).</summary>
public sealed record MigrationScript(string Path, string Name, string Module, string Version)
{
    /// <summary>SHA-256 of the file with line endings normalized, so a checkout on any system gives the same value.</summary>
    public string Checksum()
    {
        var text = File.ReadAllText(Path).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static MigrationScript FromPath(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var parts = System.IO.Path.GetFileNameWithoutExtension(path).Split("__");
        if (parts.Length < 3 || parts[0].Length < 2 || parts[0][0] != 'V')
        {
            throw new InvalidDataException($"{name} does not follow V{{version}}__{{module}}__{{description}}.sql");
        }

        return new MigrationScript(path, name, parts[1], parts[0][1..]);
    }
}

/// <summary>
/// The migration folders of each module of the customer database and of the control plane. The kernel is always
/// installed; business modules only when licensed (FR-KRN-TEN-02). Each module may also ship an access manifest folder
/// (slice S1.06: <c>permissions.g.json</c> and <c>baseline-roles.json</c>), registered after its migrations.
/// </summary>
public sealed class MigrationSet(
    IReadOnlyDictionary<string, IReadOnlyList<string>> tenantModuleFolders,
    IReadOnlyList<string> controlPlaneFolders,
    IReadOnlyDictionary<string, string>? accessFolders = null)
{
    public const string Kernel = "kernel";

    /// <summary>The access manifest folders of the given modules (kernel first), where they exist.</summary>
    public IReadOnlyList<string> AccessFolders(IEnumerable<string> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var wanted = new[] { Kernel }.Concat(modules).Distinct(StringComparer.Ordinal);
        return accessFolders is null ? [] : wanted.Where(accessFolders.ContainsKey).Select(m => accessFolders[m]).Where(Directory.Exists).ToList();
    }

    public IReadOnlyCollection<string> Modules => tenantModuleFolders.Keys.ToList();

    public IReadOnlyList<string> ControlPlaneScripts => SqlScriptRunner.Scripts([.. controlPlaneFolders]);

    /// <summary>The scripts of the given modules (kernel always included), in file-name order across modules.</summary>
    public IReadOnlyList<MigrationScript> TenantScripts(IEnumerable<string> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var wanted = modules.Append(Kernel).Distinct(StringComparer.Ordinal).ToList();
        var unknown = wanted.Where(m => !tenantModuleFolders.ContainsKey(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"No migrations are known for module(s) {string.Join(", ", unknown)}.");
        }

        return SqlScriptRunner.Scripts([.. wanted.SelectMany(m => tenantModuleFolders[m])])
            .Select(MigrationScript.FromPath)
            .ToList();
    }

    /// <summary>The standard layout: the repository source tree, or the copy next to the migrator executable.</summary>
    public static MigrationSet FromRoot(string root, IEnumerable<string>? extraKernelFolders = null) =>
        new(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [Kernel] = [System.IO.Path.Combine(root, "src/Platform/Erp.Kernel.Infrastructure/Migrations"), .. extraKernelFolders ?? []],
                ["hrm"] = [System.IO.Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Migrations")],
            },
            [System.IO.Path.Combine(root, "src/Platform/Erp.Kernel.Infrastructure/ControlPlane/Migrations")],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Kernel] = System.IO.Path.Combine(root, "src/Platform/Erp.Kernel.Infrastructure/Access"),
                ["hrm"] = System.IO.Path.Combine(root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access"),
            });
}
