using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>One leave type of the old employment in a transfer: its balance on the last day, the choice and what was or will be posted.</summary>
internal sealed record LeaveTransferItem(Guid LeaveTypeId, string Code, int LeaveYear, decimal Balance, string Choice, Guid? TargetTypeId, string Status, Guid SourceId);

/// <summary>
/// The leave side of a transfer between companies (slice S1.32 Build item 14, FR-XM-30, CR-095; replaces <c>hrm.todo_leave_ledger_transfer()</c>). On the
/// first employment's last day, each leave type's balance (expiring leave gone) follows the choice recorded on the transfer (<c>carry_over.leave</c>):
/// <c>carry</c> posts <c>transfer_out</c> on the old employment and <c>transfer_in</c> on the new one with the same source (the type of the new company
/// with the same code; a type with no match is listed for HR and nothing is posted for it until HR maps it); <c>encash</c> posts an
/// <c>encashment</c> that payroll pays (<see cref="ILeaveEncashments"/>); <c>lapse</c> posts a <c>lapse</c>. A negative balance moves with <c>carry</c>
/// and is otherwise listed for HR (final settlement). Every entry is posted once (the ledger's unique index), so running it again posts nothing new.
/// </summary>
internal static class LeaveTransfers
{
    public const string Source = "hrm.leave_transfer";
    public const string Posted = "posted";
    public const string NeedsMapping = "needs_mapping";
    public const string NegativeLeft = "negative_left";

    public const string WaitingYearEnd = "waiting_year_end";

    public static Guid SourceIdOf(Guid eventId, Guid leaveTypeId, Guid? lotId = null) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"hrm.leave_transfer|{eventId:D}|{leaveTypeId:D}{(lotId is { } l ? "|" + l.ToString("D") : string.Empty)}"))).AsSpan(0, 16));

    /// <summary>The separation event of a transfer from the employment (in force), or null.</summary>
    public static async Task<EmploymentEvent?> SeparationAsync(HrmDbContext db, Guid fromEmploymentId, CancellationToken ct)
    {
        var events = await db.Set<EmploymentEvent>().AsNoTracking()
            .Where(x => x.EmploymentId == fromEmploymentId && x.Kind == EmploymentApplier.SeparationKind && x.Status == "effective").ToListAsync(ct).ConfigureAwait(false);
        return events.FirstOrDefault(x => EmploymentApplier.NextEmployment(x) is not null);
    }

    /// <summary>
    /// What the transfer does with each leave type of the old employment (computed, then compared with what the ledger already holds). A type with a
    /// balance in an earlier leave year that its year-end has not closed waits for it (<c>waiting_year_end</c>, review S1.32b finding 5): the year-end
    /// carries that balance into the last day's year first, then the transfer moves it.
    /// </summary>
    public static async Task<List<LeaveTransferItem>> PlanAsync(HrmDbContext db, IServiceProvider services, Employment from, Employment to, EmploymentEvent separation, CancellationToken ct)
    {
        var choice = TransferApplier.CarryOver(separation)?.Leave ?? "carry";
        var lastDay = separation.EffectiveDate.AddDays(-1);
        var policies = new LeavePolicyReader(PolicyResolver.For(db, services));
        var years = new LeaveYearSetting(services.GetRequiredService<ISettings>());
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == from.CompanyId && !t.IsDeleted).OrderBy(t => t.Code).ToListAsync(ct).ConfigureAwait(false);
        var targets = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == to.CompanyId && !t.IsDeleted && t.IsActive).ToListAsync(ct).ConfigureAwait(false);
        var marker = Marker(separation);
        var items = new List<LeaveTransferItem>();
        foreach (var type in types)
        {
            var rules = (await policies.AtAsync(from.Id, lastDay, ct).ConfigureAwait(false))?.For(type.Code);
            var year = await years.YearAsync(from.CompanyId, rules, lastDay, ct).ConfigureAwait(false);
            var lines = await LeaveBalances.LinesAsync(db, from.Id, type.Id, year.Year, ct).ConfigureAwait(false);
            var sourceId = SourceIdOf(separation.Id, type.Id);
            var moved = await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.SourceType == Source && x.EmploymentId == from.Id && x.LeaveTypeId == type.Id && x.Reason == marker)
                .Select(x => x.Quantity).ToListAsync(ct).ConfigureAwait(false);
            var posted = moved.Count > 0;
            var balance = posted ? -moved.Sum() : LeaveLots.Project(lines.Where(l => l.Date <= lastDay), lastDay).Closing;
            var earlier = posted ? null : await EarlierOpenYearAsync(db, from, type, year.Year, ct).ConfigureAwait(false);
            if (balance == 0m && !posted && earlier is null)
            {
                continue;
            }

            var target = targets.FirstOrDefault(t => string.Equals(t.Code, type.Code, StringComparison.OrdinalIgnoreCase) && t.Counting == type.Counting && t.Unit == type.Unit);
            var status = posted ? Posted
                : earlier is not null ? WaitingYearEnd
                : choice == "carry" ? (target is null ? NeedsMapping : "due")
                : balance < 0m ? NegativeLeft : "due";
            items.Add(new LeaveTransferItem(type.Id, type.Code, year.Year, balance, choice, choice == "carry" ? target?.Id : null, status, sourceId));
        }

        return items;
    }

    /// <summary>An earlier leave year of the employment and type with a balance and no confirmed year-end, or null.</summary>
    private static async Task<int?> EarlierOpenYearAsync(HrmDbContext db, Employment from, LeaveType type, int year, CancellationToken ct)
    {
        var earlier = await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.EmploymentId == from.Id && x.LeaveTypeId == type.Id && x.LeaveYear < year)
            .Select(x => x.LeaveYear).Distinct().OrderBy(y => y).ToListAsync(ct).ConfigureAwait(false);
        foreach (var y in earlier)
        {
            if (!await LeaveYearEnds.ClosedAsync(db, from.CompanyId, type.Id, y, ct).ConfigureAwait(false)
                && await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.EmploymentId == from.Id && x.LeaveTypeId == type.Id && x.LeaveYear == y).SumAsync(x => x.Quantity, ct).ConfigureAwait(false) != 0m)
            {
                return y;
            }
        }

        return null;
    }

    /// <summary>The reason every entry of one transfer carries (its employment event), so a type counts as moved once whatever its pairs.</summary>
    private static string Marker(EmploymentEvent separation) => "transfer:" + separation.Id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Posts one item (the expiries due by the last day first, so the old year adds up); false when it was already posted. A carry moves each lot still
    /// usable with its own expiry as a pair of its own, and the rest as one pair (review S1.32b finding 5).
    /// </summary>
    public static async Task<bool> PostAsync(HrmDbContext db, IServiceProvider services, Employment from, Employment to, EmploymentEvent separation, LeaveTransferItem item, Guid? target,
        DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var lastDay = separation.EffectiveDate.AddDays(-1);
        var marker = Marker(separation);
        await LeaveLedgerStore.LockAsync(db, from.Id, item.LeaveTypeId, ct).ConfigureAwait(false);
        if (await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.SourceType == Source && x.EmploymentId == from.Id && x.LeaveTypeId == item.LeaveTypeId && x.Reason == marker, ct).ConfigureAwait(false))
        {
            return false;
        }

        var lines = await LeaveBalances.LinesAsync(db, from.Id, item.LeaveTypeId, item.LeaveYear, ct).ConfigureAwait(false);
        var projection = LeaveLots.Project(lines.Where(l => l.Date <= lastDay), lastDay);
        foreach (var expiry in projection.Expiries)
        {
            await InsertAsync(db, from.CompanyId, from.Id, item.LeaveTypeId, item.LeaveYear, expiry.On, expiry.Kind, -expiry.Quantity, null, LeaveLots.ExpirySource, LeaveLots.ExpirySourceId(expiry.LotId), "expired", now, actor, ct).ConfigureAwait(false);
        }

        var balance = projection.Closing;
        if (balance == 0m)
        {
            return false;
        }

        switch (item.Choice)
        {
            case "carry" when target is { } into:
                var rules = (await new LeavePolicyReader(PolicyResolver.For(db, services)).AtAsync(to.Id, separation.EffectiveDate, ct).ConfigureAwait(false))
                    ?.For((await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == into, ct).ConfigureAwait(false)).Code);
                var year = await new LeaveYearSetting(services.GetRequiredService<ISettings>()).YearAsync(to.CompanyId, rules, separation.EffectiveDate, ct).ConfigureAwait(false);
                await LeaveLedgerStore.LockAsync(db, to.Id, into, ct).ConfigureAwait(false);
                var lots = projection.Open.Where(l => l.ExpiresOn >= separation.EffectiveDate).ToList();
                foreach (var ending in projection.Open.Where(l => l.ExpiresOn < separation.EffectiveDate))
                {
                    // usable only up to the last day: it lapses there and does not move
                    await InsertAsync(db, from.CompanyId, from.Id, item.LeaveTypeId, item.LeaveYear, lastDay, ending.Kind == "comp_off_earned" ? "comp_off_expired" : "lapse", -ending.Remaining, null, Source,
                        SourceIdOf(separation.Id, item.LeaveTypeId, ending.LotId), marker, now, actor, ct).ConfigureAwait(false);
                }

                foreach (var lot in lots)
                {
                    var lotSource = SourceIdOf(separation.Id, item.LeaveTypeId, lot.LotId);
                    await InsertAsync(db, from.CompanyId, from.Id, item.LeaveTypeId, item.LeaveYear, lastDay, "transfer_out", -lot.Remaining, null, Source, lotSource, marker, now, actor, ct).ConfigureAwait(false);
                    await InsertAsync(db, to.CompanyId, to.Id, into, year.Year, separation.EffectiveDate, "transfer_in", lot.Remaining, lot.ExpiresOn, Source, lotSource, marker, now, actor, ct).ConfigureAwait(false);
                }

                var rest = balance - projection.Open.Sum(l => l.Remaining);
                if (rest != 0m)
                {
                    await InsertAsync(db, from.CompanyId, from.Id, item.LeaveTypeId, item.LeaveYear, lastDay, "transfer_out", -rest, null, Source, item.SourceId, marker, now, actor, ct).ConfigureAwait(false);
                    await InsertAsync(db, to.CompanyId, to.Id, into, year.Year, separation.EffectiveDate, "transfer_in", rest, null, Source, item.SourceId, marker, now, actor, ct).ConfigureAwait(false);
                }

                return true;
            case "encash" when balance > 0m:
                // Paid out by payroll: dated in the next open payroll period when the last day is locked (S1.31, R-304).
                await InsertAsync(db, from.CompanyId, from.Id, item.LeaveTypeId, item.LeaveYear, await Attendance.AttendancePeriodQueries.OpenDateAsync(db, from.Id, lastDay, ct).ConfigureAwait(false),
                    "encashment", -balance, null, Source, item.SourceId, marker, now, actor, ct).ConfigureAwait(false);
                return true;
            case "lapse" when balance > 0m:
                await InsertAsync(db, from.CompanyId, from.Id, item.LeaveTypeId, item.LeaveYear, lastDay, "lapse", -balance, null, Source, item.SourceId, marker, now, actor, ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Posts every item of a transfer from the employment that is due now (after the consumer, or once a year-end it waited for applied).</summary>
    public static async Task<int> PostDueAsync(HrmDbContext db, IServiceProvider services, Guid fromEmploymentId, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var separation = await SeparationAsync(db, fromEmploymentId, ct).ConfigureAwait(false);
        if (separation is null || EmploymentApplier.NextEmployment(separation) is not { } next)
        {
            return 0;
        }

        var from = await db.Set<Employment>().AsNoTracking().FirstAsync(x => x.Id == fromEmploymentId, ct).ConfigureAwait(false);
        var to = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == next, ct).ConfigureAwait(false);
        if (to is null)
        {
            return 0;
        }

        var posted = 0;
        foreach (var item in await PlanAsync(db, services, from, to, separation, ct).ConfigureAwait(false))
        {
            if (item.Status == "due" && await PostAsync(db, services, from, to, separation, item, item.TargetTypeId, now, actor, ct).ConfigureAwait(false))
            {
                posted++;
            }
        }

        return posted;
    }

    private static Task<int> InsertAsync(HrmDbContext db, Guid company, Guid employment, Guid type, int year, DateOnly date, string kind, decimal quantity, DateOnly? expires, string source, Guid sourceId,
        string reason, DateTimeOffset now, Guid actor, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, expires_on, source_type, source_id,
                                          policy_version_id, reverses_entry_id, reason, created_at, created_by)
            values ({Guid.CreateVersion7(now)}, {company}, {employment}, {type}, {year}, {date}, {kind}, {quantity}, {expires}, {source}, {sourceId}, null, null, {reason}, {now}, {actor})
            on conflict (source_type, source_id, kind, leave_type_id) where source_id is not null do nothing
            """, ct);
}

/// <summary>
/// The transfer's leave carry-over when the first employment ends (consumer of <c>hrm.employee_separated</c> with a next employment; FR-XM-30): every
/// item that can be posted is posted in the dispatcher's transaction; types waiting for HR's mapping are left for <c>/api/hrm/employments/{id}/leave-transfer</c>.
/// </summary>
public sealed class LeaveTransferConsumer(IServiceProvider services, IClock clock) : IEventConsumer
{
    public string Name => "hrm.leave_transfer";

    public bool Accepts(string eventType) => eventType == EmploymentEvents.EmployeeSeparated;

    public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);
        var p = message.Payload;
        if (!p.TryGetProperty("nextEmploymentId", out var n) || n.ValueKind != JsonValueKind.String || !Guid.TryParse(n.GetString(), out _))
        {
            return; // not a transfer between companies
        }

        var actor = message.Headers.ValueKind == JsonValueKind.Object && message.Headers.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.String && Guid.TryParse(a.GetString(), out var g) ? g : Guid.Empty;
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        await LeaveTransfers.PostDueAsync(db, services, p.GetProperty("employmentId").GetGuid(), clock.UtcNow, actor, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>HR's view and mapping of a transfer's leave (FR-XM-30): what was posted per type and the types waiting for a mapping.</summary>
internal sealed class LeaveTransferService(HrmDbContext db, IServiceProvider services, IClock clock, ICurrentActor actor, IPermissionChecker permissions, IDataScopeFilter scope) : ILeaveTransferService
{
    public const string NoTransfer = "errors.hrm.leave.transfer.none";
    public const string NotMappable = "errors.hrm.leave.transfer.not_mappable";
    public const string TargetInvalid = "errors.hrm.leave.transfer.target";

    public async Task<PeopleResult> GetAsync(Guid employmentId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.LedgerRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var found = await FindAsync(employmentId, "read", cancellationToken).ConfigureAwait(false);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var (from, to, separation) = (found.From!, found.To!, found.Separation!);
        var items = await LeaveTransfers.PlanAsync(db, services, from, to, separation, cancellationToken).ConfigureAwait(false);
        var targets = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == to.CompanyId && !t.IsDeleted && t.IsActive).OrderBy(t => t.Code)
            .Select(t => new { leaveTypeId = t.Id, code = t.Code, counting = t.Counting, unit = t.Unit }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            fromEmploymentId = from.Id,
            toEmploymentId = to.Id,
            eventId = separation.Id,
            lastDay = separation.EffectiveDate.AddDays(-1),
            choice = TransferApplier.CarryOver(separation)?.Leave ?? "carry",
            items = items.Select(i => new { leaveTypeId = i.LeaveTypeId, code = i.Code, leaveYear = i.LeaveYear, balance = i.Balance, choice = i.Choice, targetTypeId = i.TargetTypeId, status = i.Status }).ToList(),
            targets,
        });
    }

    public async Task<PeopleResult> MapAsync(Guid employmentId, LeaveTransferMapInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.LedgerPost, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var found = await FindAsync(employmentId, "post", cancellationToken).ConfigureAwait(false);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var (from, to, separation) = (found.From!, found.To!, found.Separation!);
        if (input.Maps is not { Count: > 0 } maps || maps.Any(m => m.FromLeaveTypeId is null || m.ToLeaveTypeId is null))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["maps"] = [PersonService.Required] });
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var items = await LeaveTransfers.PlanAsync(db, services, from, to, separation, cancellationToken).ConfigureAwait(false);
        var posted = 0;
        foreach (var map in maps)
        {
            var item = items.FirstOrDefault(i => i.LeaveTypeId == map.FromLeaveTypeId);
            if (item is null || item.Status != LeaveTransfers.NeedsMapping)
            {
                return PeopleResult.Conflict(NotMappable, new Dictionary<string, object?> { ["leaveTypeId"] = map.FromLeaveTypeId });
            }

            var fromType = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == item.LeaveTypeId, cancellationToken).ConfigureAwait(false);
            var target = await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == map.ToLeaveTypeId && t.CompanyId == to.CompanyId && t.IsActive && !t.IsDeleted, cancellationToken).ConfigureAwait(false);
            if (target is null || target.Counting != fromType.Counting || target.Unit != fromType.Unit)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["maps"] = [TargetInvalid] });
            }

            if (await LeaveTransfers.PostAsync(db, services, from, to, separation, item, target.Id, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false))
            {
                posted++;
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { posted });
    }

    /// <summary>The transfer an employment is part of (as the old or the new one), within the caller's scope on both.</summary>
    private async Task<(Employment? From, Employment? To, EmploymentEvent? Separation, PeopleResult? Refusal)> FindAsync(Guid employmentId, string action, CancellationToken ct)
    {
        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, ct).ConfigureAwait(false);
        if (e is null)
        {
            return (null, null, null, PeopleResult.NotFound);
        }

        var fromId = e.PreviousEmploymentId ?? e.Id;
        var from = fromId == e.Id ? e : await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == fromId, ct).ConfigureAwait(false);
        var separation = from is null ? null : await LeaveTransfers.SeparationAsync(db, from.Id, ct).ConfigureAwait(false);
        var toId = separation is null ? null : EmploymentApplier.NextEmployment(separation);
        var to = toId is null ? null : await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == toId, ct).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);
        foreach (var x in new[] { from, to }.Where(x => x is not null))
        {
            if (!await scope.AllowsAsync(new LeaveLedger { CompanyId = x!.CompanyId, EmploymentId = x.Id }, LeaveLedgerAccess.Scope, action, source, ct).ConfigureAwait(false))
            {
                return (null, null, null, PeopleResult.NotFound);
            }
        }

        return from is null || to is null || separation is null ? (null, null, null, PeopleResult.Conflict(NoTransfer)) : (from, to, separation, null);
    }
}
