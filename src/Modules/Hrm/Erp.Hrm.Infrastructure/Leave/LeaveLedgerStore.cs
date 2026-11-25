using System.Globalization;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// Writing to the leave ledger (slice S1.32 Build item 4, ruling Q8). Every write first takes the advisory lock of the employment and leave
/// type, so a balance check and its entry are one step; entries are only ever added (the database refuses update, delete and truncate, and
/// checks that a reversal negates exactly one entry). Automatic postings are written with <c>on conflict do nothing</c> on the unique index of
/// (source type, source id, kind, leave type): each employment, type and period is credited once, however often the job runs.
/// </summary>
internal static class LeaveLedgerStore
{
    /// <summary>Source type of the accrual job's grants and accruals (source id from <see cref="Application.Leave.EntitlementRules.SourceId"/>).</summary>
    public const string Automatic = "hrm.leave_accrual";

    /// <summary>Source type of HR's opening balances, grants and adjustments (the entry itself is the record: its creator and reason).</summary>
    public const string Manual = "hrm.leave_manual";

    /// <summary>Source type of a reversal (source id: the reversed entry).</summary>
    public const string Reversal = "hrm.leave_reversal";

    /// <summary>
    /// Source type prefix of HR's adjustment that settles a difference of one automatic period: the period key follows (<c>…:M2026-03</c>), so
    /// the recompute counts it with that period (review S1.32a finding 1).
    /// </summary>
    public const string SettlementPrefix = "hrm.leave_settlement:";

    /// <summary>The kinds HR posts by hand.</summary>
    public static readonly string[] ManualKinds = ["opening", "grant", "adjustment"];

    /// <summary>The kinds a reversal may undo here; usage, year-end, transfer and comp-off entries are undone by their own processes (S1.32b).</summary>
    public static readonly string[] ReversibleKinds = ["opening", "grant", "accrual", "adjustment"];

    /// <summary>
    /// Serializes the writes of one employment and leave type until the transaction ends, and holds the type row for share, so a change of the
    /// type's code, counting or unit waits for the entry and then sees it (review S1.32a finding 9).
    /// </summary>
    public static async Task LockAsync(HrmDbContext db, Guid employmentId, Guid leaveTypeId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({"hrm.leave:" + employmentId.ToString("D", CultureInfo.InvariantCulture) + ":" + leaveTypeId.ToString("D", CultureInfo.InvariantCulture)}, 0))", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.leave_types where id = {leaveTypeId} for share", ct).ConfigureAwait(false);
    }

    /// <summary>The locks of one employment and several leave types, always in the same order (by type id), so two writers never wait for each other (review S1.32b finding 13).</summary>
    public static async Task LockTypesAsync(HrmDbContext db, Guid employmentId, IEnumerable<Guid?> leaveTypeIds, CancellationToken ct)
    {
        foreach (var type in leaveTypeIds.Where(t => t is not null).Select(t => t!.Value).Distinct().Order())
        {
            await LockAsync(db, employmentId, type, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The period key of a settlement's source type, or null.</summary>
    public static string? SettledPeriod(string? sourceType) =>
        sourceType is not null && sourceType.StartsWith(SettlementPrefix, StringComparison.Ordinal) ? sourceType[SettlementPrefix.Length..] : null;

    /// <summary>Adds an automatic posting once; false when it was already there.</summary>
    public static async Task<bool> InsertAutomaticAsync(
        HrmDbContext db, Guid id, Guid companyId, Guid employmentId, Guid leaveTypeId, int leaveYear, DateOnly entryDate, string kind, decimal quantity, Guid sourceId,
        Guid policyVersionId, string key, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, expires_on, source_type, source_id,
                                          policy_version_id, reverses_entry_id, reason, created_at, created_by)
            values ({id}, {companyId}, {employmentId}, {leaveTypeId}, {leaveYear}, {entryDate}, {kind}, {quantity}, null, {Automatic}, {sourceId},
                    {policyVersionId}, null, {key}, {now}, {actor})
            on conflict (source_type, source_id, kind, leave_type_id) where source_id is not null do nothing
            """, ct).ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>
    /// The lowest balance of the leave year from <paramref name="from"/> on, as if <paramref name="extra"/> were added on that date: the running
    /// total by entry date (an entry counts from its date). Used to refuse a negative entry the policy does not allow.
    /// </summary>
    public static async Task<decimal> LowestFromAsync(HrmDbContext db, Guid employmentId, Guid leaveTypeId, int leaveYear, DateOnly from, decimal extra, CancellationToken ct)
    {
        var entries = await db.Set<LeaveLedger>().AsNoTracking()
            .Where(x => x.EmploymentId == employmentId && x.LeaveTypeId == leaveTypeId && x.LeaveYear == leaveYear)
            .Select(x => new { x.EntryDate, x.Quantity }).ToListAsync(ct).ConfigureAwait(false);
        var byDate = entries.GroupBy(x => x.EntryDate).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        byDate[from] = byDate.GetValueOrDefault(from) + extra;
        var running = 0m;
        var lowest = decimal.MaxValue;
        foreach (var (date, sum) in byDate.OrderBy(x => x.Key))
        {
            running += sum;
            if (date >= from && running < lowest)
            {
                lowest = running;
            }
        }

        return lowest;
    }
}
