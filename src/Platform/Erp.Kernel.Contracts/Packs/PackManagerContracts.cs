using System.Text.Json.Nodes;

namespace Erp.Kernel.Contracts.Packs;

/// <summary>Object types in a pack and how many objects of each (the contents summary, FR-ADM-PKG-01).</summary>
public sealed record PackContentCount(string Type, int Count);

/// <summary>A newer published version of an installed pack (FR-ADM-PKG-01 "available updates with release notes").</summary>
public sealed record PackUpdateView(string Version, DateTimeOffset ReleasedAt, IReadOnlyDictionary<string, string> ReleaseNotes, bool Compatible, bool Licensed);

/// <summary>An installed pack as the pack manager shows it.</summary>
public sealed record InstalledPackView(
    string PackId,
    string Kind,
    string? Country,
    IReadOnlyDictionary<string, string> Title,
    string Version,
    string? PreviousVersion,
    DateTimeOffset InstalledAt,
    DateTimeOffset? UpgradedAt,
    string CompatibleFrom,
    string? CompatibleTo,
    bool Compatible,
    IReadOnlyDictionary<string, string> ReleaseNotes,
    IReadOnlyList<PackContentCount> Contents,
    int Overridden,
    int Conflicts,
    int Retired,
    IReadOnlyList<PackUpdateView> Updates);

/// <summary>A pack of the catalogue that is not installed (the wizard's pack step and the manager's "available").</summary>
public sealed record AvailablePackView(
    string PackId,
    string Kind,
    string? Country,
    IReadOnlyDictionary<string, string> Title,
    string Version,
    DateTimeOffset ReleasedAt,
    IReadOnlyDictionary<string, string> ReleaseNotes,
    bool Licensed,
    bool Compatible,
    IReadOnlyList<PackContentCount> Contents);

/// <summary>An install, upgrade or "take the pack value" that waits for a second person.</summary>
public sealed record PendingPackChange(Guid RequestId, string Action, string PackId, string? Version, Guid? ItemId, DateTimeOffset RequestedAt, bool Mine);

/// <summary>The pack manager (FR-ADM-PKG-01): installed packs, packs available to install, and changes waiting for approval.</summary>
public sealed record PackManagerView(
    string ProductVersion,
    IReadOnlyList<InstalledPackView> Installed,
    IReadOnlyList<AvailablePackView> Available,
    IReadOnlyList<PendingPackChange> Pending,
    bool ApprovalRequired);

/// <summary>
/// One object of an installed pack with the pack's value and the customer's (the diff view, FR-HRM-POL-06): <see cref="Changed"/> is
/// worked out now (the customer's value no longer equals what the installer left); <see cref="ConflictVersion"/> and
/// <see cref="ConflictValue"/> are the pack's newer value that an upgrade did not apply.
/// </summary>
public sealed record PackItemView(
    Guid Id,
    string Type,
    string Key,
    Guid? CompanyId,
    string? CompanyCode,
    string Version,
    bool Changed,
    JsonObject? PackValue,
    JsonObject? CustomerValue,
    string? ConflictVersion,
    JsonObject? ConflictValue,
    string? RetiredIn);

/// <summary>The objects of one installed pack.</summary>
public sealed record PackItemsView(string PackId, string Version, int Total, bool Truncated, IReadOnlyList<PackItemView> Items);

/// <summary>Install or upgrade: the version (the latest compatible one when empty) and the reason shown to an approver.</summary>
public sealed record PackInstallRequest(string? PackId, string? Version, string? Reason);

/// <summary>"Take the pack value" of one object, with the reason shown to an approver.</summary>
public sealed record PackReasonRequest(string? Reason);

/// <summary>The answer of a pack action: an HTTP status, a refusal key and explanation, or the body (applied report or pending request).</summary>
public sealed record PackActionResult(int Status, object? Body = null, string? ErrorKey = null, string? Detail = null);

/// <summary>The pack manager's service (slice S1.23b Build items 10 and 11; contract "Pack manager API").</summary>
public interface IPackManager
{
    Task<PackManagerView> OverviewAsync(CancellationToken cancellationToken);

    /// <summary>The objects of an installed pack (null when it is not installed); <paramref name="changedOnly"/> keeps the changed ones and the conflicts.</summary>
    Task<PackItemsView?> ItemsAsync(string packId, bool changedOnly, CancellationToken cancellationToken);

    Task<PackActionResult> InstallAsync(PackInstallRequest request, CancellationToken cancellationToken);

    Task<PackActionResult> UpgradeAsync(string packId, PackInstallRequest request, CancellationToken cancellationToken);

    Task<PackActionResult> TakePackValueAsync(Guid itemId, PackReasonRequest request, CancellationToken cancellationToken);
}
