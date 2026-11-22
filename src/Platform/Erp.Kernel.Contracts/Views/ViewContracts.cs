using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Views;

/// <summary>
/// Permissions of saved list views (slice S1.12c Build item 17, S1.12 ruling Q12). Personal views need only the
/// self-service permission (<c>iam.self.manage</c>, every baseline role); sharing a view with a role needs this one.
/// </summary>
public static class ViewPermissions
{
    /// <summary>Share a saved view with the holders of a role (FR-ADM-NAV-02).</summary>
    public const string Share = "cfg.saved_views.share";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Share, "cfg", "permissions.cfg.saved_views.share", false),
    ];
}

/// <summary>Limits of saved views (also checked by the database, <c>ck_saved_views_*</c>).</summary>
public static class ViewLimits
{
    public const int NameLength = 80;

    public const int ListKeyLength = 100;

    public const int DefinitionBytes = 8192;

    /// <summary>Views one person may keep for one list.</summary>
    public const int PerPersonAndList = 50;
}

/// <summary>
/// A saved view of a list: its search, filters, sort and columns (<paramref name="Definition"/>, a JSON object the web
/// list engine reads and writes). <paramref name="Mine"/>: the signed-in person saved it and may change or delete it;
/// <paramref name="Role"/>: the role it is shared with (null: personal).
/// </summary>
public sealed record SavedViewItem(Guid Id, string ListKey, string Name, JsonElement Definition, SharedRole? Role, bool Mine, string OwnerName, long RowVersion, DateTimeOffset UpdatedAt);

/// <summary>The role a view is shared with: id, code and name per language.</summary>
public sealed record SharedRole(Guid Id, string Code, IReadOnlyDictionary<string, string> Name);

/// <summary>Create or change a view. <paramref name="RoleId"/> shares it with that role's holders (permission <see cref="ViewPermissions.Share"/>).</summary>
public sealed record SavedViewWrite(string? ListKey, string? Name, JsonElement? Definition, Guid? RoleId, long? RowVersion);

/// <summary>A role the person may share a view with (any active role; the server checks the share permission).</summary>
public sealed record ShareableRole(Guid Id, string Code, IReadOnlyDictionary<string, string> Name);

/// <summary>The outcome of a saved-view call: the view, or the error key (and field) the endpoint answers.</summary>
public sealed record ViewOutcome(SavedViewItem? View, int Status, string? ErrorKey = null, string? Field = null)
{
    public static ViewOutcome Ok(SavedViewItem view, int status = 200) => new(view, status);

    public static ViewOutcome Fail(int status, string key, string? field = null) => new(null, status, key, field);
}

/// <summary>
/// Saved list views (contract "Saved views API", S1.12c): the person's own views of a list and the views shared with a
/// role they hold today; only the owner changes or deletes a view; every change is in the change log.
/// </summary>
public interface ISavedViewService
{
    Task<IReadOnlyList<SavedViewItem>> ListAsync(string listKey, CancellationToken cancellationToken);

    Task<ViewOutcome> CreateAsync(SavedViewWrite write, CancellationToken cancellationToken);

    Task<ViewOutcome> UpdateAsync(Guid id, SavedViewWrite write, CancellationToken cancellationToken);

    /// <summary>Deletes the person's own view; 404 for a view that is not theirs, 409 when <paramref name="rowVersion"/> is stale.</summary>
    Task<ViewOutcome> DeleteAsync(Guid id, long rowVersion, CancellationToken cancellationToken);

    /// <summary>Roles a view can be shared with, for holders of the share permission.</summary>
    Task<IReadOnlyList<ShareableRole>> ShareableRolesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Label overrides in force (S1.12c Build item 15, FR-KRN-ORG-08, FR-KRN-MET-22): <c>cfg.labels</c> rows of the pack,
/// customer (<c>tenant</c>) and company layers, merged with the company winning over the customer over the pack, per
/// language. The web shows them wherever a label key is shown.
/// </summary>
public interface ILabelOverrideService
{
    /// <summary>Language → key → text. <paramref name="companyId"/> adds that company's layer when it is one of the person's companies.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(Guid? companyId, CancellationToken cancellationToken);
}
