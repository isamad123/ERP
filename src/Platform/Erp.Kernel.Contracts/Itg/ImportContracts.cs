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

/// <summary>One record an import batch created, as a rollback guard sees it.</summary>
public sealed record ImportCreatedRecord(string Table, Guid Id);

/// <summary>
/// A module's say in a rollback (slice S1.16a review finding 5): a record an import created may now be in use by records the kernel
/// cannot see (there are no foreign keys across modules, D-10; an HR assignment may point at an imported unit or location). A module
/// registers a guard that answers a localization key (<c>errors.import.rollback_in_use</c>) when any of the records is still used, or
/// null when the rollback may go on. The kernel itself checks the references inside its own schemas. The HR module implements none yet.
/// </summary>
public interface IImportRollbackGuard
{
    Task<string?> BlockedAsync(IReadOnlyList<ImportCreatedRecord> created, CancellationToken cancellationToken);
}
