using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Payroll;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The end of HR's lock request (document type <c>hrm.attendance_period_lock</c>, slice S1.31 Build item 3, ruling Q1; FR-HRM-ATT-17, FR-XM-26, FR-XM-32), on
/// the deciding transaction. Approved: the period is locked as HR's lock by the requester with the reason and the approval (the counts accepted are
/// those of the decision; a lock that would now meet days not processed or open exceptions the request did not accept is refused, and the approver
/// rejects); a period payroll locked meanwhile stays as it is. Returned: the request stays open. Rejected, withdrawn or cancelled: the request is cleared.
/// </summary>
internal sealed class PeriodLockHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public string DocumentType => AttendanceLockEvents.LockDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        if (outcome.Outcome == WorkflowOutcomes.Returned)
        {
            return;
        }

        await using var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.attendance_periods where id = {outcome.DocumentId} for update", cancellationToken).ConfigureAwait(false);
        var period = await db.Set<AttendancePeriod>().FirstOrDefaultAsync(p => p.Id == outcome.DocumentId && p.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
        if (period.LockWorkflowInstanceId != outcome.InstanceId || period.Status == "locked")
        {
            return; // an older request, or payroll locked the period meanwhile: nothing left to do
        }

        if (outcome.Outcome == WorkflowOutcomes.Approved)
        {
            var result = await services.GetRequiredService<IAttendancePeriodLock>().LockAsync(
                new PeriodLockRequest(period.PayPeriodId, null, period.LockAcceptOpen == true, period.LockRequestedBy ?? actor.UserId, period.LockReason, outcome.InstanceId),
                transaction, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == PeriodLockOutcome.Refused)
            {
                throw new WorkflowException(result.ErrorKey ?? AttendancePeriodLocks.OpenItems,
                    [string.Create(System.Globalization.CultureInfo.InvariantCulture, $"unprocessedDays={result.UnprocessedDays}"), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"openExceptions={result.OpenExceptions}")]);
            }

            return;
        }

        period.LockWorkflowInstanceId = null;
        period.LockRequestedBy = null;
        period.LockReason = null;
        period.LockAcceptOpen = null;
        EmploymentApplier.Bump(period, clock.UtcNow, actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The end of a request to cancel the locking run and reopen (document type <c>hrm.attendance_period_reopen</c>, slice S1.31 Build item 4, CR-095;
/// FR-HRM-ATT-21, FR-XM-26, FR-XM-32), on the deciding transaction. Approved: refused when the period is no longer locked by a run, the run reached
/// locked or a finalized run uses the period; otherwise the run is cancelled (never left as a stale draft) and the period reopens, publishing
/// <c>hrm.attendance.reopened</c> with the affected employments. Returned: stays open. Rejected, withdrawn or cancelled: the request is cleared.
/// </summary>
internal sealed class PeriodReopenHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public string DocumentType => AttendanceLockEvents.ReopenDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        if (outcome.Outcome == WorkflowOutcomes.Returned)
        {
            return;
        }

        var now = clock.UtcNow;
        PeriodReopenApproval approval;
        Guid payPeriodId;
        string reason;
        await using (var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.attendance_periods where id = {outcome.DocumentId} for update", cancellationToken).ConfigureAwait(false);
            var period = await db.Set<AttendancePeriod>().FirstOrDefaultAsync(p => p.Id == outcome.DocumentId && p.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
                ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
            if (period.ReopenWorkflowInstanceId != outcome.InstanceId || period.Status != "locked")
            {
                return;
            }

            if (outcome.Outcome != WorkflowOutcomes.Approved)
            {
                period.ReopenWorkflowInstanceId = null;
                period.ReopenRequestedBy = null;
                period.ReopenReason = null;
                EmploymentApplier.Bump(period, now, actor.UserId);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            approval = new PeriodReopenApproval(period.ReopenRequestedBy ?? Guid.Empty, outcome.DecidedBy, outcome.InstanceId);
            var locks = services.GetRequiredService<AttendancePeriodLocks>();
            if (await locks.ReopenRefusalAsync(db, period, approval, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                throw new WorkflowException(refused);
            }

            reason = period.ReopenReason ?? string.Empty;
            payPeriodId = period.PayPeriodId;
            if (!await services.GetRequiredService<IPayRunStates>().CancelBeforeLockAsync(db, period.LockedByRunId!.Value, reason, outcome.DecidedBy, now, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException(AttendancePeriodLocks.RunFinal);
            }
        }

        var reopened = await services.GetRequiredService<IAttendancePeriodLock>().ReopenAsync(payPeriodId, reason, approval, transaction, cancellationToken).ConfigureAwait(false);
        if (reopened.Outcome != PeriodLockOutcome.Reopened)
        {
            throw new WorkflowException(reopened.ErrorKey ?? AttendancePeriodLocks.NotLocked);
        }
    }
}

/// <summary>
/// <c>attendance_locked</c> of the payroll readiness checklist (slice S1.31, FR-XM-26; registry <c>pay-readiness.json</c>): the attendance of the pay
/// period is locked. Before that it fails and names the employees of the group whose days are not all processed (so HR sees whom the lock would accept).
/// </summary>
internal sealed class AttendanceLockedCheck(HrmDbContext db, BuildingBlocks.Application.Security.IDataScopeFilter scope) : IPayReadinessCheck
{
    public string Code => "attendance_locked";

    public async Task<ReadinessOutcome> CheckAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await db.Set<AttendancePeriod>().AsNoTracking().AnyAsync(p => p.PayPeriodId == context.PeriodId && p.Status == "locked", cancellationToken).ConfigureAwait(false))
        {
            return new ReadinessOutcome(ReadinessStatus.Passed);
        }

        var inGroup = await PayReadinessQueries.InGroupAsync(db, scope, context, cancellationToken).ConfigureAwait(false);
        var days = db.Set<AttendanceDay>().AsNoTracking();
        var count = await inGroup.CountAsync(cancellationToken).ConfigureAwait(false);
        var first = await inGroup.OrderBy(e => !days.Any(d => d.EmploymentId == e.Id && d.WorkDate >= context.Start && d.WorkDate <= context.End && d.SupersededAt == null)).ThenBy(e => e.EmployeeNo)
            .Take(PayReadinessQueries.ListLimit).Select(e => new ReadinessEmployee(e.Id, e.EmployeeNo)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ReadinessOutcome(ReadinessStatus.Failed, count, first);
    }
}
