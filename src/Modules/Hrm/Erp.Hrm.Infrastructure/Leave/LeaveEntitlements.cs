using Erp.Hrm.Application.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// An automatic ledger entry, or HR's adjustment settling an automatic period, as the job and the recompute read it: its net quantity once a
/// reversal is counted.
/// </summary>
internal sealed record PostedEntry(Guid Id, Guid LeaveTypeId, int LeaveYear, DateOnly EntryDate, string Kind, decimal Quantity, string Key, Guid? PolicyVersionId, Guid? ReversedById, decimal Net, bool IsSettlement = false)
{
    /// <summary>The group a posting belongs to: a leaver correction counts with its year's grant.</summary>
    public string Group => Key.EndsWith("/leaver", StringComparison.Ordinal) ? Key[..^"/leaver".Length] : Key;
}

internal static class PostedEntries
{
    /// <summary>The automatic entries of an employment (one leave type when given) from a date, each with its reversal.</summary>
    public static async Task<List<PostedEntry>> ReadAsync(HrmDbContext db, Guid employmentId, Guid? leaveTypeId, DateOnly from, CancellationToken ct)
    {
        var rows = await db.Set<LeaveLedger>().AsNoTracking()
            .Where(x => x.EmploymentId == employmentId && (x.SourceType == LeaveLedgerStore.Automatic || x.SourceType!.StartsWith(LeaveLedgerStore.SettlementPrefix))
                && x.EntryDate >= from && (leaveTypeId == null || x.LeaveTypeId == leaveTypeId))
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.Id).ToList();
        var reversals = ids.Count == 0 ? [] : await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.ReversesEntryId != null && ids.Contains(x.ReversesEntryId.Value))
            .Select(x => new { Of = x.ReversesEntryId!.Value, x.Id, x.Quantity }).ToListAsync(ct).ConfigureAwait(false);
        var byOriginal = reversals.ToDictionary(r => r.Of);
        return [.. rows.Select(r =>
        {
            var reversal = byOriginal.GetValueOrDefault(r.Id);
            var settled = LeaveLedgerStore.SettledPeriod(r.SourceType);
            return new PostedEntry(r.Id, r.LeaveTypeId, r.LeaveYear, r.EntryDate, r.Kind, r.Quantity, settled ?? r.Reason ?? string.Empty, r.PolicyVersionId, reversal?.Id,
                r.Quantity + (reversal?.Quantity ?? 0m), settled is not null);
        })];
    }
}

/// <summary>
/// Posts what a plan calls for and is not yet in the ledger (the accrual job, slice S1.32 Build item 3): each planned posting once; a yearly
/// grant given before the employment's end was known gets one leaver correction (key <c>Y2026/leaver</c>, on the last day) for the difference.
/// A grant HR reversed is left to HR. Runs inside the caller's transaction, under the lock of each employment and leave type.
/// </summary>
internal static class AccrualPoster
{
    public static async Task<int> PostAsync(HrmDbContext db, Employment e, EmploymentPlan plan, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var posted = 0;
        foreach (var byType in plan.Postings.GroupBy(p => p.LeaveTypeId))
        {
            await LeaveLedgerStore.LockAsync(db, e.Id, byType.Key, ct).ConfigureAwait(false);
            var existing = await PostedEntries.ReadAsync(db, e.Id, byType.Key, plan.HorizonFrom, ct).ConfigureAwait(false);
            // A period HR settled by hand is HR's from then on: the job posts nothing more for it.
            var keys = existing.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var p in byType)
            {
                if (!keys.Contains(p.Key))
                {
                    posted += await InsertAsync(db, e, p, p.Key, p.EntryDate, p.Quantity, now, actor, ct).ConfigureAwait(false) ? 1 : 0;
                    continue;
                }

                var leaverKey = EntitlementRules.LeaverKey(p.LeaveYear);
                if (p.LeaverDate is { } last && p.Kind == "grant" && !keys.Contains(leaverKey))
                {
                    var grant = existing.Where(x => x.Key == p.Key).ToList();
                    if (grant.Any(x => x.ReversedById is not null || x.IsSettlement))
                    {
                        continue;
                    }

                    var difference = p.Quantity - grant.Sum(x => x.Net);
                    if (difference != 0m)
                    {
                        posted += await InsertAsync(db, e, p, leaverKey, last, difference, now, actor, ct).ConfigureAwait(false) ? 1 : 0;
                    }
                }
            }
        }

        return posted;
    }

    private static Task<bool> InsertAsync(HrmDbContext db, Employment e, PlannedPosting p, string key, DateOnly date, decimal quantity, DateTimeOffset now, Guid actor, CancellationToken ct) =>
        LeaveLedgerStore.InsertAutomaticAsync(db, Guid.CreateVersion7(now), e.CompanyId, e.Id, p.LeaveTypeId, p.LeaveYear, date, p.Kind, quantity,
            EntitlementRules.SourceId(e.Id, p.LeaveTypeId, key), p.PolicyVersionId, key, now, actor, ct);
}

/// <summary>One difference the recompute found (FR-HRM-LVE-14), with what explains it.</summary>
internal sealed record RecomputeDifference(
    Guid LeaveTypeId, string LeaveTypeCode, int LeaveYear, string Key, string Kind, decimal? Expected, decimal? Posted, DateOnly? Date, PlannedPosting? Plan,
    IReadOnlyList<PostedEntry> Entries, Guid? EntryId)
{
    public const string Missing = "missing";
    public const string Differs = "differs";
    public const string Unexpected = "unexpected";
    public const string BelowAllowed = "below_allowed";

    /// <summary>Entries labelled with a leave year whose dates the leave-year start in force today puts in another year (review S1.32a finding 3).</summary>
    public const string YearMoved = "year_moved";
}

/// <summary>The recompute of one employment: what was checked and every difference.</summary>
internal sealed record RecomputeReport(Guid EmploymentId, DateOnly Today, DateOnly HorizonFrom, int Checked, IReadOnlyList<RecomputeDifference> Differences, IReadOnlyList<object> Years);

/// <summary>
/// Recomputes an employment's balances from the ledger and the policies (slice S1.32 Build item 6, FR-HRM-LVE-14): the plan the accrual job
/// follows is compared with the automatic entries posted (a yearly grant with its leaver correction as one), and each leave year's running
/// balance with the negative limit of the policy in force on the day it fell. Differences name the entries and the policy version that
/// explain them. HR's adjustment that names the period it settles counts with that period, so a settled difference goes away. Retired types
/// are not compared with a plan (they get nothing new; their past entries stay as they are), only their balances are checked. An entry whose
/// leave year the start month in force today would put elsewhere is listed (<c>year_moved</c>). Nothing is changed: HR posts an adjustment
/// where one is needed.
/// </summary>
internal sealed class LeaveRecompute(HrmDbContext db, EntitlementPlanner planner, LeavePolicyReader policies, LeaveYearSetting years)
{
    public async Task<RecomputeReport> RunAsync(Employment e, IReadOnlyList<LeaveType> types, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(types);
        var plan = await planner.PlanAsync(e, types, today, ct).ConfigureAwait(false);
        var typeIds = types.Select(t => t.Id).ToHashSet();
        var codes = types.ToDictionary(t => t.Id, t => t.Code);
        var active = types.Where(t => t.IsActive && !t.IsDeleted).Select(t => t.Id).ToHashSet();
        var posted = (await PostedEntries.ReadAsync(db, e.Id, types.Count == 1 ? types[0].Id : null, plan.HorizonFrom, ct).ConfigureAwait(false))
            .Where(p => typeIds.Contains(p.LeaveTypeId)).ToList();
        var differences = new List<RecomputeDifference>();
        var checkedCount = 0;
        var expectedGroups = new HashSet<(Guid, string)>();
        foreach (var p in plan.Postings)
        {
            expectedGroups.Add((p.LeaveTypeId, p.Key));
            var entries = posted.Where(x => x.LeaveTypeId == p.LeaveTypeId && x.Group == p.Key).ToList();
            var net = entries.Sum(x => x.Net);
            if (entries.Count == 0)
            {
                differences.Add(new RecomputeDifference(p.LeaveTypeId, p.LeaveTypeCode, p.LeaveYear, p.Key, RecomputeDifference.Missing, p.Quantity, null, p.EntryDate, p, [], null));
            }
            else if (net != p.Quantity)
            {
                differences.Add(new RecomputeDifference(p.LeaveTypeId, p.LeaveTypeCode, p.LeaveYear, p.Key, RecomputeDifference.Differs, p.Quantity, net, p.EntryDate, p, entries, entries[0].Id));
            }
            else
            {
                checkedCount++;
            }
        }

        foreach (var group in posted.GroupBy(x => (x.LeaveTypeId, x.Group)).Where(g => !expectedGroups.Contains(g.Key) && active.Contains(g.Key.LeaveTypeId)))
        {
            var net = group.Sum(x => x.Net);
            if (net == 0m)
            {
                checkedCount++;
                continue;
            }

            var first = group.OrderBy(x => x.EntryDate).First();
            differences.Add(new RecomputeDifference(group.Key.LeaveTypeId, codes.GetValueOrDefault(group.Key.LeaveTypeId, string.Empty), first.LeaveYear, group.Key.Group,
                RecomputeDifference.Unexpected, null, net, first.EntryDate, null, [.. group], first.Id));
        }

        // Each leave year's running balance against the negative limit of the policy in force on the day (no policy: not below zero).
        var all = await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.EmploymentId == e.Id && typeIds.Contains(x.LeaveTypeId))
            .OrderBy(x => x.EntryDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        var yearList = new List<object>();
        foreach (var year in all.GroupBy(x => (x.LeaveTypeId, x.LeaveYear)).OrderBy(g => codes[g.Key.LeaveTypeId], StringComparer.Ordinal).ThenBy(g => g.Key.LeaveYear))
        {
            foreach (var entry in year)
            {
                var rulesThen = (await policies.AtAsync(e.Id, entry.EntryDate, ct).ConfigureAwait(false))?.For(codes[year.Key.LeaveTypeId]);
                if ((await years.YearAsync(e.CompanyId, rulesThen, entry.EntryDate, ct).ConfigureAwait(false)).Year != entry.LeaveYear)
                {
                    differences.Add(new RecomputeDifference(year.Key.LeaveTypeId, codes[year.Key.LeaveTypeId], year.Key.LeaveYear, string.Empty, RecomputeDifference.YearMoved,
                        null, null, entry.EntryDate, null, [], entry.Id));
                    break;
                }
            }

            var running = 0m;
            var flagged = false;
            foreach (var day in year.GroupBy(x => x.EntryDate))
            {
                running += day.Sum(x => x.Quantity);
                if (running < 0m && !flagged)
                {
                    var rules = (await policies.AtAsync(e.Id, day.Key, ct).ConfigureAwait(false))?.For(codes[year.Key.LeaveTypeId]);
                    if (running < -(rules?.NegativeAllowance ?? 0m))
                    {
                        flagged = true;
                        var last = day.Last();
                        differences.Add(new RecomputeDifference(year.Key.LeaveTypeId, codes[year.Key.LeaveTypeId], year.Key.LeaveYear, string.Empty, RecomputeDifference.BelowAllowed,
                            -(rules?.NegativeAllowance ?? 0m), running, day.Key, null, [], last.Id));
                    }
                }
            }

            var automatic = posted.Where(x => x.LeaveTypeId == year.Key.LeaveTypeId && x.LeaveYear == year.Key.LeaveYear).Sum(x => x.Net);
            var expected = plan.Postings.Where(x => x.LeaveTypeId == year.Key.LeaveTypeId && x.LeaveYear == year.Key.LeaveYear).Sum(x => x.Quantity);
            yearList.Add(new
            {
                leaveTypeId = year.Key.LeaveTypeId, leaveTypeCode = codes[year.Key.LeaveTypeId], leaveYear = year.Key.LeaveYear,
                balance = year.Sum(x => x.Quantity), automaticPosted = automatic, automaticExpected = expected, entries = year.Count(),
            });
        }

        return new RecomputeReport(e.Id, today, plan.HorizonFrom, checkedCount, differences, yearList);
    }
}
