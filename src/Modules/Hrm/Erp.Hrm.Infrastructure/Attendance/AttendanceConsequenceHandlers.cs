using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Leave;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The end of an approval of overtime (document type <c>hrm.overtime</c>, slice S1.29b Build items 9 and 10; FR-HRM-ATT-12, LVE-08, FR-XM-26), on the
/// deciding transaction. Approved: the minutes approved are those asked for (pre-approval) or worked (post-approval) within the daily cap and what the
/// monthly cap of the day type leaves in the calendar month (caps enforced on approval); when nothing is left the approval is refused and the approver
/// rejects. Approved overtime the policy gives as comp-off is converted at once (post-approval) or once the day shows the minutes worked
/// (pre-approval). A day locked since it was asked refuses the approval. Rejected: the entry is rejected. Returned or withdrawn: a claim goes back to
/// worked out (it can be claimed again), a pre-approval ends rejected.
/// </summary>
internal sealed class OvertimeHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public const string NotPending = "errors.hrm.attendance.overtime.not_pending";
    public const string NoRule = "errors.hrm.attendance.overtime.no_rule";
    public const string CapReached = "errors.hrm.attendance.overtime.cap_reached";
    public const string NothingToApprove = "errors.hrm.attendance.overtime.nothing_to_approve";

    public string DocumentType => AttendanceConsequenceEvents.OvertimeDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false);
        var row = await db.Set<OvertimeEntry>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
        if (row.Status != "submitted")
        {
            throw new WorkflowException(NotPending);
        }

        var now = clock.UtcNow;
        if (outcome.Outcome == WorkflowOutcomes.Approved)
        {
            // One decision at a time per employment, so two approvals never both fit under the monthly cap.
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.overtime:" + row.EmploymentId.ToString("D")}, 0))", cancellationToken).ConfigureAwait(false);
            if (await services.GetRequiredService<IAttendancePeriodLock>().IsLockedAsync(row.EmploymentId, row.WorkDate, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException(AttendanceConsequenceService.PeriodLocked);
            }

            var policy = row.PolicyVersionId is { } versionId ? await new AttendanceConsequences(db, services, services.GetRequiredService<IAttendancePeriodLock>()).PolicyAsync(versionId, cancellationToken).ConfigureAwait(false) : null;
            var rule = policy?.OvertimeFor(row.DayType) ?? throw new WorkflowException(NoRule);
            if (row.RequestedMinutes is null && row.Minutes <= 0)
            {
                throw new WorkflowException(NothingToApprove); // the day's overtime went away since it was claimed: reject the claim
            }

            var (first, last) = LatenessAndOvertime.Month(row.WorkDate);
            var approved = await db.Set<OvertimeEntry>().AsNoTracking()
                .Where(x => x.EmploymentId == row.EmploymentId && x.Id != row.Id && x.DayType == row.DayType && x.WorkDate >= first && x.WorkDate <= last
                    && (x.Status == "approved" || x.Status == "paid" || x.Status == "converted_to_comp_off"))
                .SumAsync(x => x.ApprovedMinutes ?? 0, cancellationToken).ConfigureAwait(false);
            var allowed = LatenessAndOvertime.Approvable(row.Minutes, row.RequestedMinutes, rule, approved);
            if (allowed.Minutes <= 0)
            {
                throw new WorkflowException(CapReached);
            }

            row.ApprovedMinutes = allowed.Minutes;
            row.Status = "approved";
            EmploymentApplier.Bump(row, now, actor.UserId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var worked = row.RequestedMinutes is null ? row.ApprovedMinutes.Value : Math.Min(row.Minutes, row.ApprovedMinutes.Value);
            if (rule.Compensation == OvertimeRule.CompOff && worked > 0 && (row.RequestedMinutes is null || row.AttendanceDayId is not null))
            {
                AttendanceConsequences.Convert(db, row, worked, rule, now, actor.UserId);
            }
        }
        else if (outcome.Outcome == WorkflowOutcomes.Rejected || row.RequestedMinutes is not null)
        {
            row.Status = "rejected";
            EmploymentApplier.Bump(row, now, actor.UserId);
        }
        else
        {
            row.Status = "calculated";
            row.WorkflowInstanceId = null;
            EmploymentApplier.Bump(row, now, actor.UserId);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The end of an approval of a penalty waiver (document type <c>hrm.attendance_penalty_waiver</c>, slice S1.29b Build item 8; FR-HRM-ATT-13,
/// FR-XM-32, FR-XM-26), on the deciding transaction. Approved: the penalty becomes waived (the record stays, with the reason and the time); its
/// deducted leave is given back by a reversal and a pay deduction no longer reaches payroll. A penalty the engine cancelled meanwhile stays
/// cancelled (nothing is left to waive). A day locked since it was asked, or leave of a closed year, refuses the approval. Otherwise the request
/// ends and the penalty stays applied (a new waiver may be asked).
/// </summary>
internal sealed class PenaltyWaiverHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public const string LeaveYearClosed = "errors.hrm.attendance.penalty.leave_year_closed";

    public string DocumentType => AttendanceConsequenceEvents.WaiverDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false);
        var row = await db.Set<AttendancePenalty>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
        if (row.Status != "applied")
        {
            return; // cancelled by a newer result of its day while the waiver waited: nothing is left to waive
        }

        var now = clock.UtcNow;
        if (outcome.Outcome == WorkflowOutcomes.Approved)
        {
            if (await services.GetRequiredService<IAttendancePeriodLock>().IsLockedAsync(row.EmploymentId, row.WorkDate, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException(AttendanceConsequenceService.PeriodLocked);
            }

            if (!await LeavePenaltyUsage.ReversibleAsync(db, row.LeaveEntryId, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException(LeaveYearClosed);
            }

            row.LeaveReversalId = await LeavePenaltyUsage.ReverseAsync(db, row.LeaveEntryId, "attendance_penalty_waived", now, actor.UserId, cancellationToken).ConfigureAwait(false);
            row.Status = "waived";
            row.WaivedAt = now;
        }
        else
        {
            row.WorkflowInstanceId = null;
        }

        EmploymentApplier.Bump(row, now, actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Approved overtime for payroll (contract <see cref="IOvertimeInputs"/>, slice S1.29 Build item 11): payable minutes, multiplier and the lock of each day.</summary>
internal sealed class OvertimeInputReader(HrmDbContext db, IAttendancePeriodLock locks) : IOvertimeInputs
{
    public async Task<IReadOnlyList<OvertimeInput>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employmentIds);
        var ids = employmentIds.Distinct().ToList();
        var rows = await (from x in db.Set<OvertimeEntry>().AsNoTracking()
                          join d in db.Set<AttendanceDay>().AsNoTracking() on new { Id = x.AttendanceDayId ?? Guid.Empty, x.WorkDate } equals new { d.Id, d.WorkDate } into days
                          from d in days.DefaultIfEmpty()
                          where ids.Contains(x.EmploymentId) && x.WorkDate >= firstDay && x.WorkDate <= lastDay && (x.Status == "approved" || x.Status == "paid")
                          orderby x.EmploymentId, x.WorkDate, x.Id
                          select new { x.Id, x.EmploymentId, x.WorkDate, PeriodId = d == null ? (Guid?)null : d.PeriodId, x.DayType, x.Minutes, x.ApprovedMinutes, x.RateMultiplier, x.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var inputs = new List<OvertimeInput>(rows.Count);
        foreach (var x in rows)
        {
            var payable = Math.Min(x.Minutes, x.ApprovedMinutes ?? 0);
            if (payable > 0)
            {
                inputs.Add(new OvertimeInput(x.Id, x.EmploymentId, x.WorkDate, x.PeriodId, x.DayType, payable, x.RateMultiplier, x.Status,
                    await locks.IsLockedAsync(x.EmploymentId, x.WorkDate, cancellationToken).ConfigureAwait(false)));
            }
        }

        return inputs;
    }
}

/// <summary>Lateness pay deductions for payroll (contract <see cref="ILatenessDeductions"/>, slice S1.29 Build item 7): applied penalties of action pay deduction.</summary>
internal sealed class LatenessDeductionReader(HrmDbContext db, IAttendancePeriodLock locks) : ILatenessDeductions
{
    public async Task<IReadOnlyList<LatenessDeduction>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employmentIds);
        var ids = employmentIds.Distinct().ToList();
        var rows = await db.Set<AttendancePenalty>().AsNoTracking()
            .Where(p => ids.Contains(p.EmploymentId) && p.WorkDate >= firstDay && p.WorkDate <= lastDay && p.Status == "applied" && p.Action == PenaltyRule.PayDeduction)
            .OrderBy(p => p.EmploymentId).ThenBy(p => p.WorkDate).ThenBy(p => p.Kind)
            .Select(p => new { p.Id, p.EmploymentId, p.WorkDate, p.PeriodId, p.Kind, p.Minutes, p.Occurrence })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var deductions = new List<LatenessDeduction>(rows.Count);
        foreach (var p in rows)
        {
            deductions.Add(new LatenessDeduction(p.Id, p.EmploymentId, p.WorkDate, p.PeriodId, p.Kind, p.Minutes, p.Occurrence,
                await locks.IsLockedAsync(p.EmploymentId, p.WorkDate, cancellationToken).ConfigureAwait(false)));
        }

        return deductions;
    }
}
