using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Packs;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>
/// The pack manager (slice S1.23 Build items 10 and 11; FR-ADM-PKG-01, FR-KRN-CFG-02, FR-HRM-POL-06). Lists installed packs with their
/// contents, compatibility, the objects the customer changed (worked out now) and the conflicts of the last upgrade, the catalogue's
/// newer versions and packs not yet installed; installs, upgrades and takes a pack value, each held for a second person while
/// <c>cfg.packs.requires_approval</c> is on. Packs come only from the vendor's catalogue and are verified again before every install.
/// </summary>
public sealed class PackManager(
    ITenantConnectionProvider tenant,
    ICurrentLicence licence,
    ControlPlaneOptions controlPlane,
    IClock clock,
    ICurrentActor actor,
    ISettings settings,
    IHeldChanges held,
    PackInstaller installer,
    IEnumerable<IPackTakeGuard> guards) : IPackManager
{
    /// <summary>The held-change kind of pack actions (payload <see cref="PackPlan"/>).</summary>
    public const string HeldKind = "cfg.pack.change";

    public const int MaxItemsShown = 2000;

    /// <summary>
    /// What a request asks for, as held for the approver (S1.23b review finding 4): install and upgrade carry the exact version resolved
    /// when it was asked; "take the pack value" carries the object (type, key, company) and the hash of the pack value then offered.
    /// </summary>
    internal sealed record PackPlan(string Action, string PackId, string? Version, Guid? ItemId, string? Type = null, string? Key = null, Guid? CompanyId = null, string? ValueHash = null);

    internal const string ActionInstall = "install";
    internal const string ActionUpgrade = "upgrade";
    internal const string ActionTake = "take_value";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<PackManagerView> OverviewAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var catalogue = await CatalogueAsync(cancellationToken).ConfigureAwait(false);
        var licensed = licence.Licence.Claims?.Packs ?? [];
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var installed = new List<InstalledPackView>();
        foreach (var row in await InstalledAsync(c, tx, cancellationToken).ConfigureAwait(false))
        {
            var manifest = PackManifest.FromJson(row.Manifest);
            var items = await ItemRowsAsync(c, tx, row.PackId, cancellationToken).ConfigureAwait(false);
            var live = items.Where(i => i.RetiredIn is null).ToList();
            var changed = 0;
            foreach (var item in live)
            {
                var current = await installer.CurrentAsync(c, tx, item.Type, item.Key, manifest.Country, now, cancellationToken).ConfigureAwait(false);
                changed += PackInstaller.Changed(current, item.AppliedHash) ? 1 : 0;
            }

            var updates = catalogue.Where(p => p.PackId == row.PackId && PackVersions.Compare(p.Version, row.Version) > 0)
                .Select(p => (Pack: p, Parsed: p.Parsed))
                .Select(p => new PackUpdateView(p.Pack.Version, p.Pack.ReleasedAt, p.Parsed.ReleaseNotes, p.Parsed.CompatibleWith(ProductRelease.Version), licensed.Contains(p.Parsed.Entitlement, StringComparer.Ordinal)))
                .ToList();
            installed.Add(new InstalledPackView(row.PackId, manifest.Kind, manifest.Country, manifest.Title, row.Version, row.PreviousVersion, row.InstalledAt, row.UpgradedAt,
                manifest.CompatibleFrom, manifest.CompatibleTo, manifest.CompatibleWith(ProductRelease.Version), manifest.ReleaseNotes,
                Contents(manifest), changed, live.Count(i => i.ConflictVersion is not null), items.Count(i => i.RetiredIn is not null), updates));
        }

        var available = catalogue.Where(p => installed.TrueForAll(i => i.PackId != p.PackId))
            .GroupBy(p => p.PackId, StringComparer.Ordinal)
            .Select(g => g.Select(p => (Pack: p, Parsed: p.Parsed)).FirstOrDefault(p => p.Parsed.CompatibleWith(ProductRelease.Version)) is { Pack: not null } latest
                ? new AvailablePackView(latest.Pack.PackId, latest.Parsed.Kind, latest.Parsed.Country, latest.Parsed.Title, latest.Pack.Version, latest.Pack.ReleasedAt,
                    latest.Parsed.ReleaseNotes, licensed.Contains(latest.Parsed.Entitlement, StringComparer.Ordinal), true, Contents(latest.Parsed))
                : null)
            .OfType<AvailablePackView>()
            .OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.PackId, StringComparer.Ordinal)
            .ToList();

        var pending = await PendingAsync(c, tx, cancellationToken).ConfigureAwait(false);
        await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new PackManagerView(ProductRelease.Version, installed, available, pending, await ApprovalRequiredAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<PackItemsView?> ItemsAsync(string packId, bool changedOnly, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var row = (await InstalledAsync(c, tx, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.PackId == packId);
        if (row is null)
        {
            return null;
        }

        var country = PackManifest.FromJson(row.Manifest).Country;
        var companies = new Dictionary<Guid, string>();
        await using (var command = Sql.Command(c, tx, "select id, code from org.companies", []))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                companies[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        var views = new List<PackItemView>();
        var total = 0;
        foreach (var item in (await ItemRowsAsync(c, tx, packId, cancellationToken).ConfigureAwait(false)).OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal))
        {
            var current = await installer.CurrentAsync(c, tx, item.Type, item.Key, country, now, cancellationToken).ConfigureAwait(false);
            var changed = item.RetiredIn is null && PackInstaller.Changed(current, item.AppliedHash);
            if (changedOnly && !changed && item.ConflictVersion is null)
            {
                continue;
            }

            total++;
            if (views.Count >= MaxItemsShown)
            {
                continue;
            }

            var (key, company) = PackInstaller.SplitKey(item.Key);
            views.Add(new PackItemView(item.Id, item.Type, key, company, company is { } id && companies.TryGetValue(id, out var code) ? code : null, item.Version, changed,
                item.PackValue, current?.Value, item.ConflictVersion, item.ConflictValue, item.RetiredIn));
        }

        await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new PackItemsView(packId, row.Version, total, total > views.Count, views);
    }

    public Task<PackActionResult> InstallAsync(PackInstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.PackId)
            ? Task.FromResult(new PackActionResult(400, ErrorKey: PackErrors.NotFound, Detail: "packId is required."))
            : RunAsync(new PackPlan(ActionInstall, request.PackId.Trim(), Blank(request.Version), null), request.Reason, cancellationToken);
    }

    public Task<PackActionResult> UpgradeAsync(string packId, PackInstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(new PackPlan(ActionUpgrade, packId, Blank(request.Version), null), request.Reason, cancellationToken);
    }

    public async Task<PackActionResult> TakePackValueAsync(Guid itemId, PackReasonRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PackPlan plan;
        await using (var c = await OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = Sql.Command(c, null, "select pack_id, object_type, object_key, coalesce(conflict_value, pack_value)::text from cfg.pack_items where id = $1", [itemId]))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new PackActionResult(404, ErrorKey: PackErrors.ItemNotFound);
            }

            var (key, company) = PackInstaller.SplitKey(reader.GetString(2));
            plan = new PackPlan(ActionTake, reader.GetString(0), null, itemId, reader.GetString(1), key, company,
                reader.IsDBNull(3) ? null : PackArchive.Hash(JsonNode.Parse(reader.GetString(3))));
        }

        // The owning module's own rule (S1.23b review finding 2): for a pack policy, HR's manage permission in its company and HR's approval setting.
        var decision = await TakeDecisionAsync(plan, cancellationToken).ConfigureAwait(false);
        return decision.Allowed
            ? await RunAsync(plan, request.Reason, cancellationToken, decision.RequiresApproval).ConfigureAwait(false)
            : new PackActionResult(403, ErrorKey: decision.ErrorKey ?? "errors.access.forbidden");
    }

    /// <summary>The guard of the object's module, or "allowed" for objects whose module adds no rule to the pack permissions.</summary>
    internal async Task<PackTakeDecision> TakeDecisionAsync(PackPlan plan, CancellationToken ct) =>
        guards.FirstOrDefault(g => g.ObjectType == plan.Type) is { } guard && plan.Key is not null
            ? await guard.DecideAsync(plan.Key, plan.CompanyId, ct).ConfigureAwait(false)
            : new PackTakeDecision(true, false);

    /// <summary>Checks the action now; holds it for a second person while the setting is on, otherwise applies it.</summary>
    private async Task<PackActionResult> RunAsync(PackPlan plan, string? reason, CancellationToken cancellationToken, bool moduleApproval = false)
    {
        if (reason is { Length: > 500 })
        {
            return new PackActionResult(400, ErrorKey: "errors.packs.reason_too_long");
        }

        try
        {
            var approval = moduleApproval || await ApprovalRequiredAsync(cancellationToken).ConfigureAwait(false);
            await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (approval)
            {
                plan = await CheckAsync(c, tx, plan, cancellationToken).ConfigureAwait(false);
                var payload = JsonSerializer.Serialize(plan, Web);
                var requestId = await held.SubmitAsync(new HeldChangeRequest(HeldKind, plan.ItemId is null ? "cfg.installed_packs" : "cfg.pack_items",
                    plan.ItemId ?? Subject(plan.PackId), payload, Blank(reason)), tx, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PackActionResult(202, new { status = "pending", requestId });
            }

            var body = await ApplyAsync(c, tx, plan, actor.UserId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PackActionResult(200, new { status = "applied", result = body });
        }
        catch (PackRefusedException ex)
        {
            return new PackActionResult(ex.Status, ErrorKey: ex.ErrorKey, Detail: ex.Message);
        }
    }

    /// <summary>
    /// The checks of an action without writing (the request and the approver's check). Returns the plan with the exact version of an install
    /// or upgrade; a held request whose version or offered value no longer matches is refused (S1.23b review finding 4).
    /// </summary>
    internal async Task<PackPlan> CheckAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackPlan plan, CancellationToken ct)
    {
        if (plan.Action == ActionTake)
        {
            await using var command = Sql.Command(c, tx, "select coalesce(conflict_value, pack_value)::text, retired_in from cfg.pack_items where id = $1", [plan.ItemId]);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new PackRefusedException(404, PackErrors.ItemNotFound, "The pack item does not exist.");
            }

            if (reader.IsDBNull(0) || !reader.IsDBNull(1))
            {
                throw new PackRefusedException(409, PackErrors.NoConflict, "The pack has no value to take for this object.");
            }

            if (plan.ValueHash is not null && PackArchive.Hash(JsonNode.Parse(reader.GetString(0))) != plan.ValueHash)
            {
                throw new PackRefusedException(409, PackErrors.ChangedSinceRequest, "The pack's value of this object changed after the request (a later upgrade); ask again.");
            }

            return plan;
        }

        var pack = await LoadAsync(plan, ct).ConfigureAwait(false);
        await installer.CheckAsync(c, tx, pack, plan.Action == ActionUpgrade, ct).ConfigureAwait(false);
        return plan with { Version = pack.Manifest.Version };
    }

    /// <summary>Applies an action in the caller's transaction as <paramref name="by"/> (the requester, or the approver of a held one).</summary>
    internal async Task<object> ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, PackPlan plan, Guid by, CancellationToken ct)
    {
        if (plan.Action == ActionTake)
        {
            return new { itemId = await installer.TakePackValueAsync(c, tx, plan.ItemId!.Value, by, clock.UtcNow, ct).ConfigureAwait(false) };
        }

        var pack = await LoadAsync(plan, ct).ConfigureAwait(false);
        return await installer.ApplyAsync(c, tx, pack, plan.Action == ActionUpgrade, by, clock.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>The pack from the catalogue, verified again, and licensed for this customer.</summary>
    private async Task<PackArchive> LoadAsync(PackPlan plan, CancellationToken ct)
    {
        PackArchive pack;
        try
        {
            await using var cp = new NpgsqlConnection(controlPlane.ConnectionString);
            await cp.OpenAsync(ct).ConfigureAwait(false);
            pack = await PackCatalog.LoadAsync(cp, plan.PackId, plan.Version, clock.UtcNow, ct).ConfigureAwait(false);
        }
        catch (PackNotFoundException ex)
        {
            throw new PackRefusedException(404, PackErrors.NotFound, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or CryptographicException)
        {
            throw new PackRefusedException(409, PackErrors.Signature, ex.Message);
        }

        var licensed = licence.Licence.Claims?.Packs ?? [];
        return licensed.Contains(pack.Manifest.Entitlement, StringComparer.Ordinal)
            ? pack
            : throw new PackRefusedException(403, PackErrors.NotLicensed, $"Pack {pack.Manifest.Id} needs the licence key {pack.Manifest.Entitlement}.");
    }

    internal static PackPlan? ReadPlan(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PackPlan>(payload, Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The subject of a held install or upgrade: a stable id per pack id (no row exists before the first install).</summary>
    internal static Guid Subject(string packId) => new(SHA256.HashData(Encoding.UTF8.GetBytes("cfg.pack:" + packId)).AsSpan(0, 16));

    private async Task<bool> ApprovalRequiredAsync(CancellationToken ct) =>
        (await settings.GetAsync(KernelSettingKeys.CfgPacksRequiresApproval, (Guid?)null, ct).ConfigureAwait(false)).Value.ValueKind == JsonValueKind.True;

    private async Task<IReadOnlyList<CatalogPack>> CatalogueAsync(CancellationToken ct)
    {
        await using var cp = new NpgsqlConnection(controlPlane.ConnectionString);
        await cp.OpenAsync(ct).ConfigureAwait(false);
        return await PackCatalog.ListAsync(cp, ct).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    private static List<PackContentCount> Contents(PackManifest manifest) =>
        [.. manifest.Objects.GroupBy(o => o.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new PackContentCount(g.Key, g.Count()))];

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private sealed record InstalledRow(string PackId, string Version, string? PreviousVersion, DateTimeOffset InstalledAt, DateTimeOffset? UpgradedAt, JsonObject Manifest);

    private static async Task<List<InstalledRow>> InstalledAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        var rows = new List<InstalledRow>();
        await using var command = Sql.Command(c, tx, """
            select pack_id, version, previous_version, installed_at, upgraded_at, manifest::text from cfg.installed_packs
            where status = 'installed' and manifest is not null order by pack_id
            """, []);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new InstalledRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), JsonNode.Parse(reader.GetString(5)) as JsonObject ?? []));
        }

        return rows;
    }

    private static async Task<List<PackInstaller.StoredItem>> ItemRowsAsync(NpgsqlConnection c, NpgsqlTransaction tx, string packId, CancellationToken ct)
    {
        var rows = new List<PackInstaller.StoredItem>();
        await using var command = Sql.Command(c, tx, """
            select id, pack_id, object_type, object_key, version, is_overridden, content_hash, applied_hash, pack_value::text, conflict_version, conflict_value::text, retired_in
            from cfg.pack_items where pack_id = $1
            """, [packId]);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(PackInstaller.Read(reader));
        }

        return rows;
    }

    private async Task<List<PendingPackChange>> PendingAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        var result = new List<PendingPackChange>();
        await using var command = Sql.Command(c, tx, """
            select id, payload::text, created_at, requested_by from iam.maker_checker_requests where action_type = $1 and status = 'pending' order by created_at, id
            """, [HeldKind]);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (ReadPlan(reader.GetString(1)) is { } plan)
            {
                result.Add(new PendingPackChange(reader.GetGuid(0), plan.Action, plan.PackId, plan.Version, plan.ItemId, reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3) == actor.UserId));
            }
        }

        return result;
    }
}

/// <summary>
/// Approval of a held pack action (kind <see cref="PackManager.HeldKind"/>): the approver needs <see cref="PackPermissions.Approve"/>; the
/// kernel refuses the requester (FR-XM-32). The action is checked again on the newest data and applied as the approver.
/// </summary>
public sealed class PackChangeHandler(IServiceProvider services, ICurrentActor actor) : IHeldChangeHandler
{
    // Resolved when used: the manager needs IHeldChanges, which needs every handler (a circle at construction).
    private PackManager Manager => services.GetRequiredService<PackManager>();

    public string Kind => PackManager.HeldKind;

    public string ApprovePermission => PackPermissions.Approve;

    public Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: false, cancellationToken);

    public Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: true, cancellationToken);

    private async Task<HeldChangeProblem?> RunAsync(HeldChange change, DbTransaction transaction, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (PackManager.ReadPlan(change.Payload) is not { } plan)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        // The approver of a "take the pack value" also needs the owning module's right (for a pack policy, HR's manage right in its company).
        if (plan.Action == PackManager.ActionTake && await Manager.TakeDecisionAsync(plan, cancellationToken).ConfigureAwait(false) is { Allowed: false } refused)
        {
            return new HeldChangeProblem(refused.ErrorKey ?? "errors.access.forbidden", Forbidden: true);
        }

        var tx = (NpgsqlTransaction)transaction;
        try
        {
            var checkedPlan = await Manager.CheckAsync(tx.Connection!, tx, plan, cancellationToken).ConfigureAwait(false);
            if (plan.Version is not null && checkedPlan.Version != plan.Version)
            {
                return new HeldChangeProblem(PackErrors.ChangedSinceRequest);
            }

            if (apply)
            {
                await Manager.ApplyAsync(tx.Connection!, tx, plan, actor.UserId, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (PackRefusedException ex)
        {
            return new HeldChangeProblem(ex.ErrorKey);
        }
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var plan = PackManager.ReadPlan(change.Payload);
        var details = new List<HeldChangeDetail>
        {
            new("packs.held.detail.action", "key", Text: "packs.held.action." + (plan?.Action ?? "unknown")),
            new("packs.held.detail.pack", "text", Text: plan?.PackId),
        };
        if (plan?.Version is { } version)
        {
            details.Add(new HeldChangeDetail("packs.held.detail.version", "text", Text: version));
        }

        // A "take the pack value" names the object and shows both values (S1.23b review finding 4).
        if (plan is { Action: PackManager.ActionTake, ItemId: { } itemId } && transaction is NpgsqlTransaction tx && tx.Connection is { } c)
        {
            details.Add(new HeldChangeDetail("packs.held.detail.object", "text", Text: $"{plan.Type} {plan.Key}"));
            if (plan.CompanyId is { } company)
            {
                details.Add(new HeldChangeDetail("packs.held.detail.company", "text",
                    Text: await Sql.ScalarAsync<string?>(c, tx, "select code from org.companies where id = $1", cancellationToken, company).ConfigureAwait(false)));
            }

            var item = await Sql.ScalarAsync<string?>(c, tx, "select coalesce(conflict_value, pack_value)::text from cfg.pack_items where id = $1", cancellationToken, itemId).ConfigureAwait(false);
            var country = await Sql.ScalarAsync<string?>(c, tx, "select manifest->>'country' from cfg.installed_packs where pack_id = $1", cancellationToken, plan.PackId).ConfigureAwait(false);
            var current = plan.Type is null ? null : await services.GetRequiredService<PackInstaller>().CurrentAsync(c, tx, plan.Type,
                plan.CompanyId is { } id ? plan.Key + "@" + id.ToString("D") : plan.Key!, country, services.GetRequiredService<IClock>().UtcNow, cancellationToken).ConfigureAwait(false);
            details.Add(new HeldChangeDetail("packs.held.detail.customer_value", "text", Text: current?.Value.ToJsonString()));
            details.Add(new HeldChangeDetail("packs.held.detail.pack_value", "text", Text: item));
        }

        return new HeldChangeDescription(plan is null ? null : new Dictionary<string, string> { ["en"] = plan.PackId, ["ur"] = plan.PackId }, details);
    }
}
