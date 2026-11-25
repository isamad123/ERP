using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Erp.Hrm.Application.Leave;

/// <summary>
/// One ledger entry of a leave year as the expiry simulation needs it. <see cref="ExpiresLotId"/> is set on an expiry entry already posted
/// (kind <c>lapse</c> or <c>comp_off_expired</c> with source <c>hrm.leave_expiry</c>): the lot it closed.
/// </summary>
public sealed record LotLine(Guid Id, DateOnly Date, decimal Quantity, DateOnly? ExpiresOn, Guid? ReversesEntryId, Guid? ExpiresLotId, string Kind);

/// <summary>An expiry not yet posted: the lot, the day the entry is dated (the day after the lot's last usable day), the quantity left and the kind.</summary>
public sealed record LotExpiry(Guid LotId, DateOnly On, decimal Quantity, string Kind);

/// <summary>A lot still usable at the end of the projection: what is left of it and its last usable day.</summary>
public sealed record OpenLot(Guid LotId, decimal Remaining, DateOnly ExpiresOn, string Kind);

/// <summary>The balance at the end of each day that has an entry or an expiry, the expiries still to post and the lots still open at the end.</summary>
public sealed record LotProjection(IReadOnlyList<(DateOnly Date, decimal Balance)> Running, IReadOnlyList<LotExpiry> Expiries, decimal Closing, IReadOnlyList<OpenLot> Open)
{
    /// <summary>The lowest balance at the end of any day from <paramref name="from"/> on (the balance carried into that day when nothing happens on it).</summary>
    public decimal LowestFrom(DateOnly from)
    {
        var before = Running.Where(r => r.Date < from).Select(r => r.Balance).DefaultIfEmpty(0m).Last();
        return Running.Where(r => r.Date >= from).Select(r => r.Balance).Append(before).Min();
    }
}

/// <summary>
/// Expiring leave (slice S1.32 Build items 11 and 12; FR-HRM-LVE-03 carry-forward expiry, LVE-08 comp-off expiry). An entry that adds leave and
/// has an expiry date is a lot, usable up to and including that date; what is left of it the next day expires. Taking leave uses the lot that
/// expires first (then leave that never expires); a reversal of a lot takes that lot; a posted expiry closes its own lot. A reversal of leave
/// taken undoes exactly what that leave took (review S1.32b finding 1): dated on or before the leave it reverses, the two cancel out and neither
/// is simulated; dated after it, it gives back to each lot what the leave took from it (to the leave that never expires when the lot has expired
/// since). Pure: the same entries always give the same answer, so the expiry job, the request check and the year-end agree.
/// </summary>
public static class LeaveLots
{
    /// <summary>Source type of an expiry posted by the job <c>hrm.leave_expiry</c> (source id: the lot).</summary>
    public const string ExpirySource = "hrm.leave_expiry";

    public static LotProjection Project(IEnumerable<LotLine> entries, DateOnly yearEnd)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var all = entries.ToList();
        var byId = all.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());

        // A reversal of a negative entry dated on or before it: both are left out (the leave was never taken).
        var dropped = new HashSet<Guid>();
        foreach (var r in all.Where(l => l.Quantity > 0m && l.ReversesEntryId is not null))
        {
            if (byId.TryGetValue(r.ReversesEntryId!.Value, out var reversed) && reversed.Quantity < 0m && r.Date <= reversed.Date)
            {
                dropped.Add(r.Id);
                dropped.Add(reversed.Id);
            }
        }

        var lines = all.Where(l => !dropped.Contains(l.Id)).OrderBy(l => l.Date).ThenBy(l => l.Quantity < 0m ? 1 : 0).ThenBy(l => l.Id).ToList();
        var lots = new Dictionary<Guid, Lot>();
        var taken = new Dictionary<Guid, List<(Lot Lot, decimal Quantity)>>();
        var closedByPosting = lines.Where(l => l.ExpiresLotId is not null).Select(l => l.ExpiresLotId!.Value).ToHashSet();
        var balance = 0m;
        var running = new List<(DateOnly, decimal)>();
        var expiries = new List<LotExpiry>();
        var i = 0;
        while (true)
        {
            // The next day with something to do: an entry, or the day after a lot's last usable day (within the leave year).
            DateOnly? next = i < lines.Count ? lines[i].Date : null;
            var due = lots.Values.Where(l => l.Remaining > 0m && !l.Expired && !closedByPosting.Contains(l.Id) && l.ExpiresOn < yearEnd)
                .Select(l => l.ExpiresOn.AddDays(1)).DefaultIfEmpty(DateOnly.MaxValue).Min();
            if (next is null && due == DateOnly.MaxValue)
            {
                break;
            }

            var day = next is { } n && n <= due ? n : due;

            // Expiries first: a lot is usable through its expiry date, so on the day after, what is left of it goes.
            foreach (var lot in lots.Values.Where(l => !l.Expired && !closedByPosting.Contains(l.Id) && l.ExpiresOn.AddDays(1) <= day).OrderBy(l => l.ExpiresOn).ThenBy(l => l.Id).ToList())
            {
                lot.Expired = true;
                if (lot.Remaining > 0m)
                {
                    expiries.Add(new LotExpiry(lot.Id, lot.ExpiresOn.AddDays(1), lot.Remaining, ExpiryKind(lot.Kind)));
                    balance -= lot.Remaining;
                    lot.Remaining = 0m;
                }
            }

            while (i < lines.Count && lines[i].Date == day)
            {
                var line = lines[i++];
                balance += line.Quantity;
                if (line.Quantity > 0m)
                {
                    if (line.ReversesEntryId is { } undone && taken.TryGetValue(undone, out var used))
                    {
                        // A later reversal of leave taken gives back to each lot what that leave took; the part of a lot expired since stays in the balance as leave that does not expire (only HR reverses after the fact).
                        foreach (var (lot, quantity) in used)
                        {
                            if (!lot.Expired && lot.ExpiresOn >= day)
                            {
                                lot.Remaining += quantity;
                            }
                        }

                        continue;
                    }

                    if (line.ExpiresOn is { } expires && line.ReversesEntryId is null)
                    {
                        var lot = new Lot(line.Id, expires, line.Kind, line.Quantity);
                        lots[line.Id] = lot;
                        if (expires < day)
                        {
                            lot.Expired = true; // dated after its own expiry: nothing to use (refused at posting, kept safe here)
                            if (!closedByPosting.Contains(line.Id))
                            {
                                expiries.Add(new LotExpiry(line.Id, day, line.Quantity, ExpiryKind(line.Kind)));
                                balance -= line.Quantity;
                                lot.Remaining = 0m;
                            }
                        }
                    }

                    continue;
                }

                var take = -line.Quantity;
                var record = new List<(Lot, decimal)>();
                var own = line.ExpiresLotId ?? line.ReversesEntryId;
                if (own is { } target && lots.TryGetValue(target, out var named))
                {
                    var used = Math.Min(named.Remaining, take);
                    named.Remaining -= used;
                    take -= used;
                    if (line.ExpiresLotId is not null)
                    {
                        named.Expired = true;
                    }
                }
                else
                {
                    foreach (var lot in lots.Values.Where(l => l.Remaining > 0m && !l.Expired && l.ExpiresOn >= day).OrderBy(l => l.ExpiresOn).ThenBy(l => l.Id))
                    {
                        if (take <= 0m)
                        {
                            break;
                        }

                        var used = Math.Min(lot.Remaining, take);
                        lot.Remaining -= used;
                        take -= used;
                        record.Add((lot, used));
                    }
                }

                taken[line.Id] = record;
            }

            running.Add((day, balance));
        }

        var open = lots.Values.Where(l => !l.Expired && l.Remaining > 0m && !closedByPosting.Contains(l.Id)).OrderBy(l => l.ExpiresOn).ThenBy(l => l.Id)
            .Select(l => new OpenLot(l.Id, l.Remaining, l.ExpiresOn, l.Kind)).ToList();
        return new LotProjection(running, expiries, balance, open);
    }

    /// <summary>The source id of an expiry entry: the lot itself (one expiry per lot, the ledger's unique index).</summary>
    public static Guid ExpirySourceId(Guid lotId) => lotId;

    private static string ExpiryKind(string lotKind) => lotKind == "comp_off_earned" ? "comp_off_expired" : "lapse";

    private sealed class Lot(Guid id, DateOnly expiresOn, string kind, decimal quantity)
    {
        public Guid Id { get; } = id;

        public DateOnly ExpiresOn { get; } = expiresOn;

        public string Kind { get; } = kind;

        public decimal Remaining { get; set; } = quantity;

        public bool Expired { get; set; }
    }
}

/// <summary>What the year-end does with one closing balance (FR-HRM-LVE-11, LVE-03): carried forward, paid out and lapsed, in the type's unit.</summary>
public sealed record YearEndSplit(decimal Closing, decimal CarryForward, decimal Encash, decimal Lapse, DateOnly? CarryExpiresOn);

/// <summary>
/// The year-end rules of one leave type (slice S1.32 Build item 12; CR-086 S1.32b detail): a positive balance carries forward up to the cap
/// (all of it when the policy sets carry-forward without a cap; nothing when it sets no carry-forward), then what is left is paid out up to
/// the encashment cap (all of it without a cap; nothing without encashment), and the rest lapses. A negative balance is carried into the next
/// year as it is (what was taken in advance stays owed). The carried leave expires the given number of months into the new year.
/// </summary>
public static class YearEndRules
{
    public static YearEndSplit Split(decimal closing, LeaveTypeRules rules, DateOnly nextYearStart)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (closing <= 0m)
        {
            return new YearEndSplit(closing, closing, 0m, 0m, null);
        }

        var carry = !rules.HasCarryForward ? 0m : Math.Min(closing, rules.CarryForwardCap ?? closing);
        var rest = closing - carry;
        var encash = !rules.HasEncashment ? 0m : Math.Min(rest, rules.EncashmentCap ?? rest);
        var lapse = rest - encash;
        var expires = carry > 0m && rules.CarryForwardExpiryMonths is { } months ? nextYearStart.AddMonths(months).AddDays(-1) : (DateOnly?)null;
        return new YearEndSplit(closing, carry, encash, lapse, expires);
    }

    /// <summary>
    /// The source id of a year-end entry: one per run, employment, leave type and side (<c>out</c> in the closing year, <c>in</c> in the next), so
    /// the ledger's unique index lets a confirmation post each entry once.
    /// </summary>
    public static Guid SourceId(Guid runId, Guid employmentId, Guid leaveTypeId, string side) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"hrm.leave_year_end|{runId:D}|{employmentId:D}|{leaveTypeId:D}|{side}"))).AsSpan(0, 16));
}
