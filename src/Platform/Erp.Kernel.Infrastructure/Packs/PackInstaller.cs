using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Contracts.Packs;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>A refusal of a whole install or upgrade: an HTTP status, a localization key and a plain explanation.</summary>
public sealed class PackRefusedException(int status, string errorKey, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string ErrorKey { get; } = errorKey;
}

/// <summary>The refusal keys of the installer (texts in <c>packs.*.json</c>).</summary>
public static class PackErrors
{
    public const string NotFound = "errors.packs.not_found";
    public const string AlreadyInstalled = "errors.packs.already_installed";
    public const string NotInstalled = "errors.packs.not_installed";
    public const string NotNewer = "errors.packs.not_newer";
    public const string Incompatible = "errors.packs.incompatible";
    public const string Dependency = "errors.packs.dependency_missing";
    public const string NotLicensed = "errors.packs.not_licensed";
    public const string UnknownType = "errors.packs.unknown_object_type";
    public const string InvalidObject = "errors.packs.invalid_object";
    public const string ObjectRefused = "errors.packs.object_refused";
    public const string Signature = "errors.packs.signature";
    public const string NoConflict = "errors.packs.no_pack_value";
    public const string ItemNotFound = "errors.packs.item_not_found";
    public const string ChangedSinceRequest = "errors.packs.changed_since_request";
}

/// <summary>What happened to one object (per company for company objects).</summary>
public sealed record PackItemOutcome(string Type, string Key, Guid? CompanyId, string Outcome);

/// <summary>
/// The report of an install or upgrade (FR-KRN-CFG-02 "report conflicts"): counts per outcome and the objects the customer had
/// changed (kept, with the pack's value offered).
/// </summary>
public sealed record PackApplyReport(string PackId, string Version, string? PreviousVersion, int Added, int Updated, int Unchanged, int Kept, int Retired, IReadOnlyList<PackItemOutcome> Conflicts, int LeftInPlace = 0);

/// <summary>Outcomes of <see cref="PackItemOutcome"/>.</summary>
public static class PackOutcomes
{
    public const string Added = "added";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
    public const string Kept = "kept";
    public const string Retired = "retired";
    public const string Left = "left_in_place";
}

/// <summary>
/// Installs and upgrades packs in a customer database (slice S1.23 Build item 10, ruling Q7; FR-KRN-CFG-02, FR-HRM-POL-06). One
/// transaction per pack (the caller's) holding a lock per pack id. Every object goes through the handler of its type (D-10) and is
/// recorded in <c>cfg.pack_items</c> with the pack's value, its hash and the hash of what the installer left. On upgrade an object
/// whose present value no longer hashes like what the installer left was changed by the customer: it is kept and its new pack value
/// is recorded as a conflict; unchanged objects take the new value, new ones are added, removed ones retired. Handlers never touch
/// transactional rows. Every object change and the installed pack itself are written to the change log.
/// </summary>
public sealed class PackInstaller(IEnumerable<IPackObjectHandler> handlers)
{
    private readonly Dictionary<string, IPackObjectHandler> _handlers = handlers.ToDictionary(h => h.ObjectType, StringComparer.Ordinal);

    public IReadOnlyCollection<IPackObjectHandler> Handlers => _handlers.Values;

    /// <summary>The installed version of a pack, or null.</summary>
    public static Task<string?> InstalledVersionAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string packId, CancellationToken ct) =>
        Sql.ScalarAsync<string?>(c, tx, "select version from cfg.installed_packs where pack_id = $1 and status = 'installed'", ct, packId);

    /// <summary>
    /// Checks that <paramref name="pack"/> may be installed (<paramref name="upgrade"/> false) or upgraded to, without writing: product
    /// compatibility, dependencies, the installed version, every object type and value. The licence is the caller's check.
    /// </summary>
    public async Task<string?> CheckAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackArchive pack, bool upgrade, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var m = pack.Manifest;
        if (!m.CompatibleWith(ProductRelease.Version))
        {
            throw new PackRefusedException(409, PackErrors.Incompatible, $"Pack {m.Id} {m.Version} is for product {m.CompatibleFrom}{(m.CompatibleTo is { } to ? " to " + to : " or later")}, not {ProductRelease.Version}.");
        }

        var installed = await InstalledVersionAsync(c, tx, m.Id, ct).ConfigureAwait(false);
        if (!upgrade && installed is not null)
        {
            throw new PackRefusedException(409, PackErrors.AlreadyInstalled, $"Pack {m.Id} is already installed (version {installed}); upgrade it instead.");
        }

        if (upgrade && installed is null)
        {
            throw new PackRefusedException(409, PackErrors.NotInstalled, $"Pack {m.Id} is not installed; install it first.");
        }

        if (upgrade && PackVersions.Compare(m.Version, installed!) <= 0)
        {
            throw new PackRefusedException(409, PackErrors.NotNewer, $"Pack {m.Id} {m.Version} is not newer than the installed version {installed}.");
        }

        foreach (var dependency in m.Dependencies)
        {
            var have = await InstalledVersionAsync(c, tx, dependency.Id, ct).ConfigureAwait(false);
            if (have is null || PackVersions.Compare(have, dependency.From) < 0)
            {
                throw new PackRefusedException(409, PackErrors.Dependency, $"Pack {m.Id} needs pack {dependency.Id} {dependency.From} or later installed first.");
            }
        }

        foreach (var entry in m.Objects)
        {
            var handler = Handler(entry.Type);
            var file = pack.FileOf(entry);
            if (handler.Validate(entry.Key, file.Value) is { } problem)
            {
                throw new PackRefusedException(400, PackErrors.InvalidObject, $"{entry.Type} '{entry.Key}': {problem}");
            }
        }

        return installed;
    }

    /// <summary>Installs or upgrades <paramref name="pack"/> in the caller's transaction.</summary>
    public async Task<PackApplyReport> ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackArchive pack, bool upgrade, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var m = pack.Manifest;
        await LockAsync(c, tx, ct).ConfigureAwait(false);
        var previous = await CheckAsync(c, tx, pack, upgrade, ct).ConfigureAwait(false);
        var today = await TodayAsync(c, tx, now, ct).ConfigureAwait(false);
        var companies = await CompaniesAsync(c, tx, m.Country, ct).ConfigureAwait(false);
        var items = await ItemsAsync(c, tx, m.Id, ct).ConfigureAwait(false);
        var changes = new List<ChangeEntry>();
        var outcomes = new List<PackItemOutcome>();
        var seen = new HashSet<(string, string)>();

        foreach (var entry in m.Objects.OrderBy(o => Handler(o.Type).Order).ThenBy(o => o.Type, StringComparer.Ordinal).ThenBy(o => o.Key, StringComparer.Ordinal))
        {
            var handler = Handler(entry.Type);
            var value = handler.Normalize(pack.FileOf(entry).Value);
            var contentHash = PackArchive.Hash(value);
            foreach (var company in handler.Scope == PackObjectScope.CompanyOfCountry ? companies.Select(x => (Guid?)x) : [null])
            {
                var itemKey = company is { } id ? entry.Key + "@" + id.ToString("D") : entry.Key;
                seen.Add((entry.Type, itemKey));
                var context = new PackObjectContext(c, tx, now, today, actor, m.Country, company);
                var current = await handler.ReadAsync(context, entry.Key, ct).ConfigureAwait(false);
                items.TryGetValue((entry.Type, itemKey), out var item);
                var outcome = await ApplyObjectAsync(c, tx, m, handler, context, entry, itemKey, value, contentHash, current, item, now, actor, changes, ct).ConfigureAwait(false);
                outcomes.Add(new PackItemOutcome(entry.Type, entry.Key, company, outcome));
            }
        }

        // Objects the new version no longer holds: retired (reverse order: items before their lists), or kept when the customer changed them.
        foreach (var item in items.Values.Where(i => i.RetiredIn is null && !seen.Contains((i.Type, i.Key)))
            .OrderByDescending(i => _handlers.TryGetValue(i.Type, out var h) ? h.Order : 0).ThenBy(i => i.Key, StringComparer.Ordinal))
        {
            var handler = Handler(item.Type);
            var (key, company) = SplitKey(item.Key);
            var context = new PackObjectContext(c, tx, now, today, actor, m.Country, company);
            var current = await handler.ReadAsync(context, key, ct).ConfigureAwait(false);
            var changed = Changed(current, item.AppliedHash);
            if (!changed && current is not null)
            {
                foreach (var change in await handler.RetireAsync(context, key, ct).ConfigureAwait(false))
                {
                    changes.Add(Entry(change, now, actor));
                }

                // A handler leaves an object in use where it is (a policy still assigned, a past holiday): the item stays live, so a later
                // version can retire it, and the report says "left in place" (S1.23b review finding 7).
                var after = await handler.ReadAsync(context, key, ct).ConfigureAwait(false);
                if (after is not null && PackArchive.Hash(after.Value) == PackArchive.Hash(current.Value))
                {
                    outcomes.Add(new PackItemOutcome(item.Type, key, company, PackOutcomes.Left));
                    continue;
                }
            }

            await Sql.ExecuteAsync(c, tx, """
                update cfg.pack_items set retired_in = $2, is_overridden = $3, conflict_version = null, conflict_value = null,
                    updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
                """, ct, item.Id, m.Version, changed, now, actor).ConfigureAwait(false);
            outcomes.Add(new PackItemOutcome(item.Type, key, company, changed ? PackOutcomes.Kept : PackOutcomes.Retired));
        }

        changes.Add(await RecordInstalledAsync(c, tx, pack, previous, now, actor, ct).ConfigureAwait(false));
        await AuditDb.AppendChangesAsync(c, tx, changes, ct).ConfigureAwait(false);
        var conflicts = outcomes.Where(o => o.Outcome == PackOutcomes.Kept).ToList();
        return new PackApplyReport(m.Id, m.Version, previous,
            outcomes.Count(o => o.Outcome == PackOutcomes.Added), outcomes.Count(o => o.Outcome == PackOutcomes.Updated),
            outcomes.Count(o => o.Outcome == PackOutcomes.Unchanged), conflicts.Count, outcomes.Count(o => o.Outcome == PackOutcomes.Retired), conflicts, outcomes.Count(o => o.Outcome == PackOutcomes.Left));
    }

    /// <summary>
    /// "Take the pack value" of one object (ruling Q7): the pack's newer value of a conflict (or, without one, the value the pack
    /// installed) becomes the object's value, an ordinary audited change through its handler. Returns the item's id.
    /// </summary>
    public async Task<Guid> TakePackValueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid itemId, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var item = await ItemAsync(c, tx, itemId, ct).ConfigureAwait(false)
            ?? throw new PackRefusedException(404, PackErrors.ItemNotFound, "The pack item does not exist.");
        await LockAsync(c, tx, ct).ConfigureAwait(false);
        item = (await ItemAsync(c, tx, itemId, ct).ConfigureAwait(false))!;
        var value = item.ConflictValue ?? item.PackValue;
        if (value is null || item.RetiredIn is not null)
        {
            throw new PackRefusedException(409, PackErrors.NoConflict, "The pack has no value to take for this object.");
        }

        var handler = Handler(item.Type);
        var (key, company) = SplitKey(item.Key);
        var country = await Sql.ScalarAsync<string?>(c, tx, "select manifest->>'country' from cfg.installed_packs where pack_id = $1", ct, item.PackId).ConfigureAwait(false);
        var context = new PackObjectContext(c, tx, now, await TodayAsync(c, tx, now, ct).ConfigureAwait(false), actor, country, company);
        var current = await handler.ReadAsync(context, key, ct).ConfigureAwait(false);
        var changes = new List<ChangeEntry>();
        if (current is null || PackArchive.Hash(current.Value) != PackArchive.Hash(value))
        {
            changes.AddRange((await handler.WriteAsync(context, key, value, ct).ConfigureAwait(false)).Select(x => Entry(x, now, actor)));
        }

        var after = await handler.ReadAsync(context, key, ct).ConfigureAwait(false);
        await Sql.ExecuteAsync(c, tx, """
            update cfg.pack_items set version = coalesce(conflict_version, version), content_hash = $2, applied_hash = $3, pack_value = $4::jsonb,
                is_overridden = false, conflict_version = null, conflict_value = null, updated_at = $5, updated_by = $6, row_version = row_version + 1
            where id = $1
            """, ct, itemId, PackArchive.Hash(value), after is null ? null : PackArchive.Hash(after.Value), value.ToJsonString(), now, actor).ConfigureAwait(false);
        changes.Add(new ChangeEntry(Guid.CreateVersion7(now), now, Actor(actor), ActorKind(actor), "cfg.pack_items", itemId, company, "update",
            [new JsonObject { ["field"] = "taken_pack_value", ["after"] = item.ConflictVersion ?? item.Version }], null, AuditAmbient.Current?.CorrelationId,
            AuditAmbient.Current?.IpAddress, Source(actor), actor));
        await AuditDb.AppendChangesAsync(c, tx, changes, ct).ConfigureAwait(false);
        return itemId;
    }

    /// <summary>The present value of an item's object (null when the object no longer exists), for the diff view.</summary>
    public async Task<PackObjectState?> CurrentAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string type, string itemKey, string? country, DateTimeOffset now, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(type, out var handler))
        {
            return null;
        }

        var (key, company) = SplitKey(itemKey);
        var own = tx is null;
        var transaction = tx ?? await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            return await handler.ReadAsync(new PackObjectContext(c, transaction, now, DateOnly.FromDateTime(now.UtcDateTime), BaselineSeed.SystemUserId, country, company, ReadOnly: true), key, ct).ConfigureAwait(false);
        }
        finally
        {
            if (own)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One lock for every pack write of a customer (install, upgrade, take the pack value): two packs may hold the same object (a bank,
    /// a setting), so their installs take turns (S1.23b review finding 9).
    /// </summary>
    private static Task<int> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct) =>
        Sql.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('cfg.packs'))", ct);

    /// <summary>The customer's own date (customer-level <c>org.time_zone</c>), not the UTC date (S1.23b review finding 6).</summary>
    private static async Task<DateOnly> TodayAsync(NpgsqlConnection c, NpgsqlTransaction tx, DateTimeOffset now, CancellationToken ct)
    {
        var zone = await Entitlements.CustomerLicence.TimeZoneAsync(c, tx, DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false);
        return Erp.Kernel.Domain.Entitlements.LicenceStates.Today(now, Erp.Kernel.Domain.Entitlements.LicenceStates.Zone(zone));
    }

    /// <summary>True when the object is gone or no longer hashes like what the installer left (the customer changed it).</summary>
    public static bool Changed(PackObjectState? current, string? appliedHash) =>
        current is null || appliedHash is null || PackArchive.Hash(current.Value) != appliedHash;

    /// <summary>The manifest key and the company of an item key (<c>key@companyId</c> for company objects).</summary>
    public static (string Key, Guid? Company) SplitKey(string itemKey)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        var at = itemKey.LastIndexOf('@');
        return at > 0 && Guid.TryParse(itemKey[(at + 1)..], out var company) ? (itemKey[..at], company) : (itemKey, null);
    }

    // ------------------------------------------------------------------ one object

    private static async Task<string> ApplyObjectAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackManifest m, IPackObjectHandler handler, PackObjectContext context, PackObjectEntry entry,
        string itemKey, JsonObject value, string contentHash, PackObjectState? current, StoredItem? item, DateTimeOffset now, Guid actor, List<ChangeEntry> changes, CancellationToken ct)
    {
        async Task<string> WriteAsync()
        {
            foreach (var change in await Guarded(() => handler.WriteAsync(context, entry.Key, value, ct), entry).ConfigureAwait(false))
            {
                changes.Add(Entry(change, now, actor));
            }

            var after = await handler.ReadAsync(context, entry.Key, ct).ConfigureAwait(false)
                ?? throw new PackRefusedException(500, PackErrors.ObjectRefused, $"{entry.Type} '{entry.Key}' could not be read back after it was written.");
            return PackArchive.Hash(after.Value);
        }

        if (item is null)
        {
            if (current is not null && PackArchive.Hash(current.Value) != contentHash)
            {
                // The customer already has an object of this key: theirs stays, the pack's value is offered (ruling Q7).
                await InsertItemAsync(c, tx, m, entry.Type, itemKey, m.Version, overridden: true, contentHash, appliedHash: null, value, conflict: true, now, actor, ct).ConfigureAwait(false);
                return PackOutcomes.Kept;
            }

            var applied = current is null ? await WriteAsync().ConfigureAwait(false) : PackArchive.Hash(current.Value);
            await InsertItemAsync(c, tx, m, entry.Type, itemKey, m.Version, overridden: false, contentHash, applied, value, conflict: false, now, actor, ct).ConfigureAwait(false);
            return current is null ? PackOutcomes.Added : PackOutcomes.Unchanged;
        }

        var changed = Changed(current, item.AppliedHash);
        if (item.ContentHash == contentHash && item.RetiredIn is null)
        {
            // The pack's value did not change; the customer's change (if any) stays as it is.
            await Sql.ExecuteAsync(c, tx, """
                update cfg.pack_items set version = case when is_overridden or $3 then version else $2 end, is_overridden = $3,
                    updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
                """, ct, item.Id, m.Version, changed, now, actor).ConfigureAwait(false);
            return changed ? PackOutcomes.Kept : PackOutcomes.Unchanged;
        }

        if (!changed || (current is not null && PackArchive.Hash(current.Value) == contentHash))
        {
            var applied = changed ? PackArchive.Hash(current!.Value) : await WriteAsync().ConfigureAwait(false);
            await Sql.ExecuteAsync(c, tx, """
                update cfg.pack_items set version = $2, content_hash = $3, applied_hash = $4, pack_value = $5::jsonb, is_overridden = false,
                    conflict_version = null, conflict_value = null, retired_in = null, updated_at = $6, updated_by = $7, row_version = row_version + 1
                where id = $1
                """, ct, item.Id, m.Version, contentHash, applied, value.ToJsonString(), now, actor).ConfigureAwait(false);
            return changed ? PackOutcomes.Unchanged : PackOutcomes.Updated;
        }

        await Sql.ExecuteAsync(c, tx, """
            update cfg.pack_items set is_overridden = true, conflict_version = $2, conflict_value = $3::jsonb, retired_in = null,
                updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
            """, ct, item.Id, m.Version, value.ToJsonString(), now, actor).ConfigureAwait(false);
        return PackOutcomes.Kept;
    }

    private static async Task<IReadOnlyList<PackObjectChange>> Guarded(Func<Task<IReadOnlyList<PackObjectChange>>> write, PackObjectEntry entry)
    {
        try
        {
            return await write().ConfigureAwait(false);
        }
        catch (PackObjectRefusedException ex)
        {
            throw new PackRefusedException(400, ex.ErrorKey, $"{entry.Type} '{entry.Key}': {ex.Message}");
        }
    }

    private IPackObjectHandler Handler(string type) =>
        _handlers.TryGetValue(type, out var handler)
            ? handler
            : throw new PackRefusedException(400, PackErrors.UnknownType, $"No module of this product handles pack object type '{type}'.");

    private static async Task InsertItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackManifest m, string type, string itemKey, string version, bool overridden, string contentHash,
        string? appliedHash, JsonObject value, bool conflict, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        await using var command = Sql.Command(c, tx, """
            insert into cfg.pack_items (id, pack_id, object_type, object_key, version, is_overridden, content_hash, applied_hash, pack_value, conflict_version, conflict_value,
                created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $12, $13)
            """, [Guid.CreateVersion7(now), m.Id, type, itemKey, version, overridden, contentHash, appliedHash, value.ToJsonString(),
            conflict ? m.Version : null, conflict ? value.ToJsonString() : null, now, actor]);
        command.Parameters[8].NpgsqlDbType = NpgsqlDbType.Jsonb;
        command.Parameters[10].NpgsqlDbType = NpgsqlDbType.Jsonb;
        command.Parameters[7].NpgsqlDbType = NpgsqlDbType.Text;
        command.Parameters[9].NpgsqlDbType = NpgsqlDbType.Text;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<ChangeEntry> RecordInstalledAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackArchive pack, string? previous, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var m = pack.Manifest;
        var existing = await Sql.ScalarAsync<Guid?>(c, tx, "select id from cfg.installed_packs where pack_id = $1", ct, m.Id).ConfigureAwait(false);
        var id = existing ?? Guid.CreateVersion7(now);
        await using var command = Sql.Command(c, tx, existing is null
            ? """
              insert into cfg.installed_packs (id, pack_id, version, installed_at, manifest, status, created_at, created_by, updated_at, updated_by)
              values ($1, $2, $3, $4, $5, 'installed', $4, $6, $4, $6)
              """
            : """
              update cfg.installed_packs set version = $3, manifest = $5, previous_version = $7, upgraded_at = $4, status = 'installed',
                  updated_at = $4, updated_by = $6, row_version = row_version + 1 where id = $1 and pack_id = $2
              """, existing is null ? [id, m.Id, m.Version, now, pack.ManifestJson.ToJsonString(), actor] : [id, m.Id, m.Version, now, pack.ManifestJson.ToJsonString(), actor, previous]);
        command.Parameters[4].NpgsqlDbType = NpgsqlDbType.Jsonb;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return new ChangeEntry(Guid.CreateVersion7(now), now, Actor(actor), ActorKind(actor), "cfg.installed_packs", id, null, existing is null ? "create" : "update",
            [new JsonObject { ["field"] = "version", ["before"] = previous, ["after"] = m.Version }, new JsonObject { ["field"] = "pack_id", ["after"] = m.Id }],
            null, AuditAmbient.Current?.CorrelationId, AuditAmbient.Current?.IpAddress, Source(actor), actor);
    }

    private static ChangeEntry Entry(PackObjectChange change, DateTimeOffset now, Guid actor)
    {
        var fields = new JsonArray();
        var names = (change.Before?.Select(p => p.Key) ?? []).Concat(change.After?.Select(p => p.Key) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var before = change.Before?[name];
            var after = change.After?[name];
            if (PackArchive.Hash(before) != PackArchive.Hash(after))
            {
                fields.Add(new JsonObject { ["field"] = name, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() });
            }
        }

        fields.Add(new JsonObject { ["field"] = "source", ["after"] = "pack" });
        return new ChangeEntry(Guid.CreateVersion7(now), now, Actor(actor), ActorKind(actor), change.EntityType, change.EntityId, change.CompanyId, change.Action, fields,
            null, AuditAmbient.Current?.CorrelationId, AuditAmbient.Current?.IpAddress, Source(actor), actor);
    }

    private static Guid? Actor(Guid actor) => actor == BaselineSeed.SystemUserId ? null : actor;

    private static string ActorKind(Guid actor) => actor == BaselineSeed.SystemUserId ? ActorKinds.System : ActorKinds.User;

    private static string Source(Guid actor) => actor == BaselineSeed.SystemUserId && AuditAmbient.Current is null ? AuditSources.System : AuditAmbient.Source;

    private static async Task<List<Guid>> CompaniesAsync(NpgsqlConnection c, NpgsqlTransaction tx, string? country, CancellationToken ct)
    {
        var result = new List<Guid>();
        await using var command = Sql.Command(c, tx,
            "select id from org.companies where not is_deleted and ($1::text is null or btrim(country_code) = $1) order by id", [country]);
        command.Parameters[0].NpgsqlDbType = NpgsqlDbType.Text;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    internal sealed record StoredItem(Guid Id, string PackId, string Type, string Key, string Version, bool Overridden, string? ContentHash, string? AppliedHash,
        JsonObject? PackValue, string? ConflictVersion, JsonObject? ConflictValue, string? RetiredIn);

    private const string ItemColumns = "id, pack_id, object_type, object_key, version, is_overridden, content_hash, applied_hash, pack_value::text, conflict_version, conflict_value::text, retired_in";

    private static async Task<Dictionary<(string, string), StoredItem>> ItemsAsync(NpgsqlConnection c, NpgsqlTransaction tx, string packId, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), StoredItem>();
        await using var command = Sql.Command(c, tx, $"select {ItemColumns} from cfg.pack_items where pack_id = $1", [packId]);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var item = Read(reader);
            result[(item.Type, item.Key)] = item;
        }

        return result;
    }

    private static async Task<StoredItem?> ItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = Sql.Command(c, tx, $"select {ItemColumns} from cfg.pack_items where id = $1", [id]);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    internal static StoredItem Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : JsonNode.Parse(r.GetString(8)) as JsonObject,
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : JsonNode.Parse(r.GetString(10)) as JsonObject, r.IsDBNull(11) ? null : r.GetString(11));
}
