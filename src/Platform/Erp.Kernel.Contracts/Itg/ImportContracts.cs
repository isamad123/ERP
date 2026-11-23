using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Itg;

/// <summary>Permissions of the import centre (slice S1.16a). Every import type also needs its own permission (the write permission of its table).</summary>
public static class ImportPermissions
{
    /// <summary>Upload, check, commit and roll back imports of the types the person may write.</summary>
    public const string Manage = "itg.imports.manage";

    /// <summary>Design and change import templates (mapping, transformations, defaults).</summary>
    public const string Design = "itg.imports.design";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Manage, "itg", "permissions.itg.imports.manage", false),
        new(Design, "itg", "permissions.itg.imports.design", false),
    ];
}

/// <summary>A module's import types (<c>Access/imports.json</c>, JSON), registered by the module in both hosts like its job manifest (D-10).</summary>
public sealed record ImportManifestSource(string Json);

/// <summary>Job codes of the import engine.</summary>
public static class ImportJobCodes
{
    public const string Validate = "kernel.import_validate";
    public const string Commit = "kernel.import_commit";
}
