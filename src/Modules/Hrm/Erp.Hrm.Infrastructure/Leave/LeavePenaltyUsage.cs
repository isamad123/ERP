using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>What became of a leave deduction: the usage entry posted, or the reason (a localization key) none was.</summary>
internal sealed record PenaltyLeavePosting(Guid? LeaveTypeId, Guid? EntryId, string? ProblemKey);

/// <summary>
/// Leave deducted for lateness (slice S1.29 Build item 7, FR-HRM-ATT-13, through the leave ledger of S1.32): one <c>usage</c> entry of the penalty's
/// units on the company's leave type with the policy's code, dated on the late day, in that day's leave year, naming the penalty as its source
/// (the ledger's unique index posts it once). A waiver or a cancellation gives it back by one <c>usage_reversal</c> (FR-XM-06). The entry takes
/// the employment-and-type lock like every ledger write. A missing or retired leave type, or a closed leave year, posts nothing and says so; the
/// balance may go below zero, as HR sees on the balance (recorded ruling, CR-089 S1.29b detail).
/// </summary>
internal static class LeavePenaltyUsage
{
    public const string Source = "hrm.attendance_penalty";

    public static async Task<PenaltyLeavePosting> PostAsync(
        HrmDbContext db, IServiceProvider services, Employment e, Guid penaltyId, DateOnly date, string leaveTypeCode, decimal units, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == e.CompanyId && t.IsActive && !t.IsDeleted).ToListAsync(ct).ConfigureAwait(false);
        var type = types.Find(t => string.Equals(t.Code, leaveTypeCode, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            return new PenaltyLeavePosting(null, null, Application.Attendance.LatenessAndOvertime.PenaltyNoLeaveType);
        }

        var policy = await new LeavePolicyReader(Policies.PolicyResolver.For(db, services)).AtAsync(e.Id, date, ct).ConfigureAwait(false);
        var rules = policy?.For(type.Code);
        var year = await new LeaveYearSetting(services.GetRequiredService<ISettings>()).YearAsync(e.CompanyId, rules, date, ct).ConfigureAwait(false);
        if (await LeaveYearEnds.ClosedAsync(db, e.CompanyId, type.Id, year.Year, ct).ConfigureAwait(false))
        {
            return new PenaltyLeavePosting(type.Id, null, Application.Attendance.LatenessAndOvertime.PenaltyYearClosed);
        }

        await LeaveLedgerStore.LockAsync(db, e.Id, type.Id, ct).ConfigureAwait(false);
        var entry = new LeaveLedger
        {
            Id = Guid.CreateVersion7(now), CompanyId = e.CompanyId, EmploymentId = e.Id, LeaveTypeId = type.Id, LeaveYear = year.Year, EntryDate = date, Kind = "usage",
            Quantity = -units, SourceType = Source, SourceId = penaltyId, PolicyVersionId = policy?.Resolution.Policy?.VersionId, Reason = "attendance_penalty",
            CreatedAt = now, CreatedBy = actor,
        };
        db.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Entry(entry).State = EntityState.Detached;
        return new PenaltyLeavePosting(type.Id, entry.Id, null);
    }

    /// <summary>
    /// Gives a penalty's leave back (one <c>usage_reversal</c> of its entry, same leave year and date); null when it has no entry, is reversed already,
    /// or its leave year is closed (then the leave stays used: a closed year takes no entry, S1.32b).
    /// </summary>
    public static async Task<Guid?> ReverseAsync(HrmDbContext db, Guid? entryId, string reason, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        if (entryId is not { } id)
        {
            return null;
        }

        var usage = await db.Set<LeaveLedger>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (usage is null || await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.ReversesEntryId == id, ct).ConfigureAwait(false)
            || await LeaveYearEnds.ClosedAsync(db, usage.CompanyId, usage.LeaveTypeId, usage.LeaveYear, ct).ConfigureAwait(false))
        {
            return null;
        }

        await LeaveLedgerStore.LockAsync(db, usage.EmploymentId, usage.LeaveTypeId, ct).ConfigureAwait(false);
        var reversal = new LeaveLedger
        {
            Id = Guid.CreateVersion7(now), CompanyId = usage.CompanyId, EmploymentId = usage.EmploymentId, LeaveTypeId = usage.LeaveTypeId, LeaveYear = usage.LeaveYear,
            EntryDate = usage.EntryDate, Kind = "usage_reversal", Quantity = -usage.Quantity, SourceType = Source, SourceId = usage.SourceId, PolicyVersionId = usage.PolicyVersionId,
            ReversesEntryId = usage.Id, Reason = reason, CreatedAt = now, CreatedBy = actor,
        };
        db.Add(reversal);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Entry(reversal).State = EntityState.Detached;
        return reversal.Id;
    }

    /// <summary>Whether a penalty's leave can still be given back (it has no entry, or the entry's leave year is open).</summary>
    public static async Task<bool> ReversibleAsync(HrmDbContext db, Guid? entryId, CancellationToken ct)
    {
        if (entryId is not { } id)
        {
            return true;
        }

        var usage = await db.Set<LeaveLedger>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return usage is null || !await LeaveYearEnds.ClosedAsync(db, usage.CompanyId, usage.LeaveTypeId, usage.LeaveYear, ct).ConfigureAwait(false);
    }
}
