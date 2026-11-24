using System.Data.Common;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Packs;

/// <summary>
/// Permissions of the pack manager (slice S1.23b, FR-ADM-PKG-01, FR-KRN-CFG-02). The generated writes of <c>cfg.installed_packs</c>
/// and <c>cfg.pack_items</c> are closed; reading the manager and the generated reads share <see cref="Read"/>. Installing a pack
/// needs <see cref="Install"/>, upgrading it and taking a pack value need <see cref="Upgrade"/>; while
/// <c>cfg.packs.requires_approval</c> is on, each waits for a second person holding <see cref="Approve"/> (FR-XM-32).
/// </summary>
public static class PackPermissions
{
    public const string Read = "cfg.installed_packs.read";
    public const string Install = "cfg.installed_packs.create";
    public const string Upgrade = "cfg.installed_packs.update";
    public const string Approve = "cfg.installed_packs.approve";

    /// <summary>Hand-written codes; descriptions live in <c>packs.*.json</c>.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Install, "cfg", "permissions.cfg.installed_packs.create", false),
        new(Upgrade, "cfg", "permissions.cfg.installed_packs.update", false),
        new(Approve, "cfg", "permissions.cfg.installed_packs.approve", true),
    ];
}

/// <summary>Where a handler's objects live: once for the customer, or once per company of the pack's country (ruling Q9).</summary>
public enum PackObjectScope
{
    Customer,
    CompanyOfCountry,
}

/// <summary>
/// The transaction a pack object is read or written in (one transaction per pack, Build item 10). <see cref="CountryCode"/> is
/// the country of a country pack (null for other kinds); <see cref="CompanyId"/> is set for objects installed per company.
/// </summary>
public sealed record PackObjectContext(
    DbConnection Connection,
    DbTransaction Transaction,
    DateTimeOffset Now,
    DateOnly Today,
    Guid ActorUserId,
    string? CountryCode,
    Guid? CompanyId);

/// <summary>
/// An object as it now is, in the shape of the pack's value (after <see cref="IPackObjectHandler.Normalize"/>), so the installer can
/// tell whether the customer changed it (FR-HRM-POL-06): a value that hashes differently from what the installer left is the
/// customer's. <see cref="EntityType"/> and <see cref="EntityId"/> name the record (for the change log and the diff view).
/// </summary>
public sealed record PackObjectState(JsonObject Value, string EntityType, Guid EntityId);

/// <summary>
/// One record a handler created, changed or retired, for the change log (written by the installer in the same transaction).
/// <see cref="Action"/> is <c>create</c>, <c>update</c> or <c>delete</c>.
/// </summary>
public sealed record PackObjectChange(string EntityType, Guid EntityId, Guid? CompanyId, string Action, JsonObject? Before, JsonObject? After);

/// <summary>A pack object the installer cannot write: a localization key and the object it is about.</summary>
public sealed class PackObjectRefusedException(string errorKey, string detail) : Exception(detail)
{
    public string ErrorKey { get; } = errorKey;
}

/// <summary>
/// The handler of one pack object type, registered by the module that owns the records (D-10; slice S1.23 Build item 9). The
/// kernel's installer reads and writes every object only through its handler and never touches transactional rows
/// (FR-KRN-CFG-02, FR-XM-05/06). Handlers are deterministic: <see cref="ReadAsync"/> after <see cref="WriteAsync"/> gives the
/// normalized value written.
/// </summary>
public interface IPackObjectHandler
{
    /// <summary>The object type of the manifest, e.g. <c>holiday</c>.</summary>
    string ObjectType { get; }

    /// <summary>The module that owns the records (<c>kernel</c>, <c>hrm</c>).</summary>
    string ModuleId { get; }

    PackObjectScope Scope { get; }

    /// <summary>Objects are applied in this order (a calendar before its holidays, a list before its items); retired in reverse.</summary>
    int Order { get; }

    /// <summary>The refusal of a key and value (shape only; needs no database), or null. <c>pack build</c> and install both run it.</summary>
    string? Validate(string key, JsonObject value);

    /// <summary>The value in its full shape (defaults filled), as <see cref="ReadAsync"/> gives it back.</summary>
    JsonObject Normalize(JsonObject value);

    /// <summary>The object as it now is, or null when it does not exist.</summary>
    Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken);

    /// <summary>Makes <paramref name="value"/> the object's effective value (creates it when missing); throws <see cref="PackObjectRefusedException"/>.</summary>
    Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken);

    /// <summary>
    /// The object left the pack: deactivates it, or removes it only where nothing can refer to it. An object still in use is
    /// left as it is (an empty answer).
    /// </summary>
    Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken);
}
