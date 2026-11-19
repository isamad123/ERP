using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Entitlements;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// Delegation and out-of-office substitution (FR-KRN-WFL-05, FR-HRM-SSP-07, FR-ADM-ROL-09, FR-HRM-ROL-05; slice S1.10
/// ruling Q8, S1.10b rulings P2 and P5). A delegation is a row of <c>iam.delegations</c>:
/// <list type="bullet">
/// <item>whole days in the customer's time zone (<c>org.time_zone</c>): from the start of the first day to the end of the
/// last (<c>valid_to</c> is the start of the day after); at most <c>wf.delegation.max_days</c> days; it ends by itself;</item>
/// <item><c>scope</c> = <c>{"document_types": [...], "company_ids": [...]}</c>, an empty list meaning all;</item>
/// <item>the delegate is an active user other than the delegator; one delegator's delegations may not overlap in time for
/// the same documents (a document type and a company both lists cover), to the same or to another person;</item>
/// <item>it can be revoked at any time while it has not ended. Every change is in the change log.</item>
/// </list>
/// The engine reads it at the moment of each decision (<see cref="WorkflowEngine.DelegationCovers"/>), so a delegation
/// applies at once to open and new tasks and stops at once when it ends or is revoked.
/// Self-service acts on the caller's own approvals; administrators need <c>iam.delegations.create</c> /
/// <c>iam.delegations.update</c> through a role whose data scope for <c>iam.delegations</c> is "all".
/// </summary>
public sealed class DelegationService(ITenantConnectionProvider tenant, IClock clock, ICurrentActor actor, WorkflowCatalog catalog) : IDelegationService
{
    public const string NotFound = "errors.delegation.not_found";
    private const string Resource = "iam.delegations";

    private sealed record Scope(
        [property: JsonPropertyName("document_types")] List<string> DocumentTypes,
        [property: JsonPropertyName("company_ids")] List<Guid> CompanyIds);

    private sealed record Row(Guid Id, Guid From, Guid To, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, Scope Scope, string? Reason, string Status, long RowVersion);

    public async Task<MyDelegations> MineAsync(CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var zone = await ZoneAsync(work, cancellationToken).ConfigureAwait(false);
        var rows = await RowsAsync(work, "(from_user_id = $1 or to_user_id = $1) and valid_to > $2 - interval '400 days' order by valid_from desc, id desc limit 500",
            cancellationToken, work.Actor, work.Now).ConfigureAwait(false);
        return new MyDelegations([.. rows.Where(r => r.From == work.Actor).Select(r => View(r, zone, work.Now))], [.. rows.Where(r => r.To == work.Actor).Select(r => View(r, zone, work.Now))]);
    }

    public Task<DelegationView> CreateMineAsync(DelegationRequest request, CancellationToken cancellationToken) =>
        CreateCoreAsync(request, self: true, cancellationToken);

    public Task<DelegationView> CreateAsync(DelegationRequest request, CancellationToken cancellationToken) =>
        CreateCoreAsync(request, self: false, cancellationToken);

    public Task<DelegationView> RevokeMineAsync(Guid id, RevokeDelegationRequest request, CancellationToken cancellationToken) =>
        RevokeCoreAsync(id, request, self: true, cancellationToken);

    public Task<DelegationView> RevokeAsync(Guid id, RevokeDelegationRequest request, CancellationToken cancellationToken) =>
        RevokeCoreAsync(id, request, self: false, cancellationToken);

    private async Task<DelegationView> CreateCoreAsync(DelegationRequest request, bool self, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        if (!self)
        {
            await RequireAdministratorAsync(work, WorkflowPermissions.DelegationsCreate, ct).ConfigureAwait(false);
        }

        var from = self ? work.Actor : request.FromUserId ?? throw new WorkflowException("errors.delegation.from_required");
        var to = request.ToUserId ?? throw new WorkflowException("errors.delegation.to_required");
        if (to == from)
        {
            throw new WorkflowException("errors.delegation.to_self");
        }

        // An administrator acts for others, never for themselves: naming oneself as delegate would hand the administrator
        // another person's right to decide without anyone agreeing (S1.10b review finding 2, product owner's ruling 2(a)).
        if (!self && to == work.Actor)
        {
            throw new WorkflowException("errors.delegation.to_self_admin");
        }

        if (!self && !await work.ScalarAsync<bool>("select exists (select 1 from iam.users where id = $1 and not is_deleted and not is_system)", ct, from).ConfigureAwait(false))
        {
            throw new WorkflowException("errors.delegation.from_unknown");
        }

        if (!await ApproverResolution.ActiveUserAsync(work, to, ct).ConfigureAwait(false))
        {
            throw new WorkflowException("errors.delegation.to_not_active");
        }

        var (first, last) = (request.From ?? throw new WorkflowException("errors.delegation.dates_required"), request.To ?? throw new WorkflowException("errors.delegation.dates_required"));
        var zone = await ZoneAsync(work, ct).ConfigureAwait(false);
        if (last < first)
        {
            throw new WorkflowException("errors.delegation.dates_order");
        }

        if (first < WorkingCalendar.LocalDate(work.Now, zone))
        {
            throw new WorkflowException("errors.delegation.start_in_past");
        }

        var maxDays = await IntSettingAsync(work, KernelSettingKeys.WfDelegationMaxDays, 90, ct).ConfigureAwait(false);
        if (last.DayNumber - first.DayNumber + 1 > maxDays)
        {
            throw new WorkflowException("errors.delegation.too_long");
        }

        var types = (request.DocumentTypes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (types.Any(t => catalog.Find(t) is null))
        {
            throw new WorkflowException("errors.delegation.type_unknown");
        }

        var companies = (request.CompanyIds ?? []).Distinct().Order().ToList();
        if (companies.Count > 0
            && await work.ScalarAsync<int>("select count(*)::int from org.companies where id = any($1) and not is_deleted", ct, companies.ToArray()).ConfigureAwait(false) != companies.Count)
        {
            throw new WorkflowException("errors.delegation.company_unknown");
        }

        var validFrom = WorkingCalendar.StartOfDay(first, zone);
        var validTo = WorkingCalendar.StartOfDay(last.AddDays(1), zone);
        var scope = new Scope(types, companies);

        // One delegator's delegations, checked and written one at a time.
        await work.ExecuteAsync("select pg_advisory_xact_lock(hashtext('iam.delegations'), hashtext($1))", ct, from.ToString()).ConfigureAwait(false);
        var overlapping = await RowsAsync(work, "from_user_id = $1 and status = 'active' and valid_from < $3 and valid_to > $2", ct, from, validFrom, validTo).ConfigureAwait(false);
        if (overlapping.Any(o => Intersects(o.Scope.DocumentTypes, types) && Intersects(o.Scope.CompanyIds, companies)))
        {
            throw new WorkflowException("errors.delegation.overlap");
        }

        var id = work.NewId();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var scopeJson = JsonSerializer.Serialize(scope);
        await work.ExecuteAsync("""
            insert into iam.delegations (id, from_user_id, to_user_id, scope, valid_from, valid_to, reason, status, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, 'active', $8, $9, $8, $9, 1)
            """, ct, id, from, to, new JsonPayload(scopeJson), validFrom, validTo, reason, work.Now, work.Actor).ConfigureAwait(false);
        work.Change(Resource, id, null, "create", ("from_user_id", null, from), ("to_user_id", null, to), ("scope", null, scope), ("valid_from", null, validFrom),
            ("valid_to", null, validTo), ("reason", null, reason), ("status", null, "active"));
        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return View(new Row(id, from, to, validFrom, validTo, scope, reason, "active", 1), zone, work.Now);
    }

    private async Task<DelegationView> RevokeCoreAsync(Guid id, RevokeDelegationRequest request, bool self, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        if (!self)
        {
            await RequireAdministratorAsync(work, WorkflowPermissions.DelegationsUpdate, ct).ConfigureAwait(false);
        }

        var row = (await RowsAsync(work, "id = $1 for update", ct, id).ConfigureAwait(false)).FirstOrDefault();
        if (row is null || (self && row.From != work.Actor))
        {
            throw new KeyNotFoundException(NotFound);
        }

        if (row.Status != "active" || row.ValidTo <= work.Now)
        {
            throw new WorkflowException("errors.delegation.not_in_force");
        }

        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        await work.ExecuteAsync("""
            update iam.delegations set status = 'revoked', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
            """, ct, id, work.Now, work.Actor).ConfigureAwait(false);
        work.Change(Resource, id, null, "status", ("status", "active", "revoked"), ("revoke_reason", null, reason));
        var zone = await ZoneAsync(work, ct).ConfigureAwait(false);
        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return View(row with { Status = "revoked", RowVersion = row.RowVersion + 1 }, zone, work.Now);
    }

    /// <summary>Administrators act for anyone only through a role whose data scope for delegations is "all" (an "own" scope is self-service).</summary>
    private static async Task RequireAdministratorAsync(WorkflowWork work, string permission, CancellationToken ct)
    {
        var snapshot = await AccessSnapshot.LoadAsync(work.Connection, work.Transaction, work.Actor, ApproverResolution.Today(work), ct).ConfigureAwait(false);
        if (!snapshot.Granting(permission).Any(g => g.Role.ScopeFor(Resource) is { Kind: ScopeRule.All, Conditions.Count: 0 }))
        {
            throw new AccessDeniedException("errors.access.forbidden");
        }
    }

    private static bool Intersects<T>(List<T> a, List<T> b) => a.Count == 0 || b.Count == 0 || a.Intersect(b).Any();

    private static DelegationView View(Row r, TimeZoneInfo zone, DateTimeOffset now) => new(
        r.Id, r.From, r.To, WorkingCalendar.LocalDate(r.ValidFrom, zone), WorkingCalendar.LocalDate(r.ValidTo, zone).AddDays(-1), r.ValidFrom, r.ValidTo,
        r.Scope.DocumentTypes, r.Scope.CompanyIds, r.Reason, r.Status, r.Status == "active" && r.ValidFrom <= now && r.ValidTo > now, r.RowVersion);

    private static async Task<List<Row>> RowsAsync(WorkflowWork work, string where, CancellationToken ct, params object?[] values)
    {
        var rows = new List<Row>();
        await using var command = work.Command("select id, from_user_id, to_user_id, valid_from, valid_to, scope::text, reason, status, row_version from iam.delegations where " + where, values);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var scope = JsonSerializer.Deserialize<Scope>(r.GetString(5)) ?? new Scope([], []);
            rows.Add(new Row(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
                new Scope(scope.DocumentTypes ?? [], scope.CompanyIds ?? []), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetInt64(8)));
        }

        return rows;
    }

    /// <summary>The customer's time zone (ruling P5: there is no per-user time zone yet).</summary>
    private static async Task<TimeZoneInfo> ZoneAsync(WorkflowWork work, CancellationToken ct) =>
        WorkingCalendar.Zone(await CustomerLicence.TimeZoneAsync(work.Connection, work.Transaction, DateOnly.FromDateTime(work.Now.UtcDateTime), ct).ConfigureAwait(false));

    private static async Task<int> IntSettingAsync(WorkflowWork work, string key, int fallback, CancellationToken ct)
    {
        var resolved = await SettingsStore.ResolveAsync(work.Connection, work.Transaction, [key], null, null, DateOnly.FromDateTime(work.Now.UtcDateTime), ct).ConfigureAwait(false);
        return resolved.TryGetValue(key, out var v) && v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var n) ? n : fallback;
    }
}
