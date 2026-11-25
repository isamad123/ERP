using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>An HR context on the deciding connection and transaction, so the leave rows commit (or roll back) with the decision.</summary>
internal static class LeaveOnTransaction
{
    public static async Task<HrmDbContext> OpenAsync(IServiceProvider services, DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }
}

/// <summary>
/// The end of the approval of a leave request (document type <c>hrm.leave_request</c>; FR-HRM-LVE-05, FR-XM-32: the kernel never lets the
/// requester or the employee decide), on the deciding transaction. Approved: under the employment-and-type lock the request is checked again
/// (no day in a locked period, the leave year still open, the balance still within the policy's limit), the usage is posted, a change cancels
/// the request it replaces by reversal, and the attendance days are worked out again. Rejected: rejected. Returned: back to draft (the requester
/// corrects it and submits it again). Withdrawn or cancelled by an administrator: cancelled. A refusal is a <see cref="WorkflowException"/>, so
/// the decision is rolled back and the approver sees why.
/// </summary>
internal sealed class LeaveRequestHandler(IServiceProvider services, IClock clock, ICurrentActor actor, ISettings settings) : IWorkflowOutcomeHandler
{
    public string DocumentType => LeaveWorkflowValues.RequestType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var r = await db.Set<LeaveRequest>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.leave.request.not_found");
        var now = clock.UtcNow;
        var by = actor.UserId;

        // A request returned for correction is a draft on an open (returned) request: withdrawing it, or an administrator's cancel, ends it (review S1.32b finding 3).
        if (r.Status == "draft" && r.WorkflowInstanceId == outcome.InstanceId && outcome.Outcome == WorkflowOutcomes.Cancelled)
        {
            await LeaveUsage.SetStatusAsync(db, r, "cancelled", now, by, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (r.Status != "submitted")
        {
            throw new WorkflowException("errors.hrm.leave.request.not_pending");
        }

        if (outcome.Outcome != WorkflowOutcomes.Approved)
        {
            var status = outcome.Outcome switch
            {
                WorkflowOutcomes.Rejected => "rejected",
                WorkflowOutcomes.Returned => "draft",
                _ => "cancelled",
            };
            await LeaveUsage.SetStatusAsync(db, r, status, now, by, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == r.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        var e = await db.Set<Employment>().AsNoTracking().FirstAsync(x => x.Id == r.EmploymentId, cancellationToken).ConfigureAwait(false);
        var old = r.ModifiesRequestId is { } m ? await db.Set<LeaveRequest>().FirstOrDefaultAsync(x => x.Id == m, cancellationToken).ConfigureAwait(false) : null;
        await LeaveLedgerStore.LockTypesAsync(db, r.EmploymentId, [r.LeaveTypeId, old?.LeaveTypeId], cancellationToken).ConfigureAwait(false);

        if (old is not null && old.Status != "approved")
        {
            throw new WorkflowException(LeaveRequestService.NotApproved);
        }

        var locks = services.GetRequiredService<IAttendancePeriodLock>();
        var days = await db.Set<LeaveRequestDay>().AsNoTracking().Where(d => d.LeaveRequestId == r.Id).Select(d => d.LeaveDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var d in days)
        {
            if (await locks.IsLockedAsync(r.EmploymentId, d, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException(LeaveLedgerService.PeriodLocked);
            }
        }

        // A type that needs a document has one at the decision too (review S1.32b finding 9: files can change while the request waits).
        if (type.RequiresDocument && (await services.GetRequiredService<IRecordFiles>().CurrentAsync(LeaveWorkflowValues.OwnerType, r.Id, transaction, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            throw new WorkflowException(LeaveRequestService.DocumentRequired);
        }

        var policies = new LeavePolicyReader(PolicyResolver.For(db, services));
        var policy = await policies.AtAsync(r.EmploymentId, r.StartDate, cancellationToken).ConfigureAwait(false);
        var rules = policy?.For(type.Code) ?? throw new WorkflowException(LeaveLedgerService.NoPolicy);
        var year = await new LeaveYearSetting(settings).YearAsync(r.CompanyId, rules, r.StartDate, cancellationToken).ConfigureAwait(false);
        if (await LeaveYearEnds.ClosedAsync(db, r.CompanyId, r.LeaveTypeId, year.Year, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowException(LeaveRequestPlanner.YearClosed);
        }

        var plan = new LeavePlan { Rules = rules, Year = year };
        await LeaveRequestPlanner.BalanceAsync(db, plan, e, type, r.StartDate, LeaveUsage.Quantity(r, type), r.Id, old, cancellationToken).ConfigureAwait(false);
        if (plan.Conflict is { } refused)
        {
            throw new WorkflowException(refused);
        }

        if (old is not null)
        {
            if (await LeaveCancellation.RefusalAsync(db, locks, old, cancellationToken).ConfigureAwait(false) is { Error: { } oldRefused })
            {
                throw new WorkflowException(oldRefused);
            }

            if (await LeaveUsage.ReversalAsync(db, old, r.LeaveTypeId == old.LeaveTypeId ? r.StartDate : null, "changed by " + r.RequestNo, now, by, cancellationToken).ConfigureAwait(false) is { } reversal)
            {
                db.Add(reversal);
            }

            await LeaveUsage.SetStatusAsync(db, old, "cancelled", now, by, cancellationToken).ConfigureAwait(false);
            LeaveUsage.Requeue(db, old, "leave_changed", now, by);
        }

        db.Add(LeaveUsage.Usage(r, type, year.Year, policy.Resolution.Policy?.VersionId, now, by));
        await LeaveUsage.SetStatusAsync(db, r, "approved", now, by, cancellationToken).ConfigureAwait(false);
        LeaveUsage.Requeue(db, r, "leave_approved", now, by);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            throw new WorkflowException(pg.ConstraintName == "ck_leave_request_days_daily_total" ? LeaveRequestPlanner.Overlap : "errors.data.rejected");
        }
    }
}

/// <summary>
/// The end of the approval of a cancellation of approved leave (document type <c>hrm.leave_cancellation</c>; FR-HRM-LVE-06, approval where the
/// policy requires it). Approved: the usage reversed and the request cancelled (refused when a day is now in a locked period). Returned: it waits for
/// the employee to send it again or withdraw it. Rejected, withdrawn or cancelled: the leave stays approved.
/// </summary>
internal sealed class LeaveCancellationHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public string DocumentType => LeaveWorkflowValues.CancellationType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var r = await db.Set<LeaveRequest>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.leave.request.not_found");
        if (r.Status != "cancel_requested")
        {
            throw new WorkflowException("errors.hrm.leave.request.not_pending");
        }

        var now = clock.UtcNow;
        await LeaveLedgerStore.LockAsync(db, r.EmploymentId, r.LeaveTypeId, cancellationToken).ConfigureAwait(false);

        // Returned: the cancellation waits for the employee to send it again (cancel again) or withdraw it; the leave stays taken (review S1.32b finding 3).
        if (outcome.Outcome == WorkflowOutcomes.Returned)
        {
            return;
        }

        if (outcome.Outcome != WorkflowOutcomes.Approved)
        {
            await LeaveUsage.SetStatusAsync(db, r, "approved", now, actor.UserId, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await LeaveCancellation.RefusalAsync(db, services.GetRequiredService<IAttendancePeriodLock>(), r, cancellationToken).ConfigureAwait(false) is { Error: { } refused })
        {
            throw new WorkflowException(refused);
        }

        await LeaveCancellation.ApplyAsync(db, r, outcome.Comment ?? "cancellation approved", now, actor.UserId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Files on a leave request (FR-HRM-LVE-16; owner type <c>hrm.leave_requests</c>, slice S1.11c1 ruling Q23, CR-114). Read: the employee, the person who
/// applied, the people with an open approval task of the request, and readers of leave requests whose data scope covers it. Add or replace: the
/// employee or the person who applied while it is a draft, or a writer of leave requests in scope while it is a draft or waiting for approval. Sensitive
/// files (medical certificates): the employee, the person who applied, the open-task approvers and the writers in scope (HR), not every reader.
/// </summary>
internal sealed class LeaveRequestAttachmentOwner(HrmDbContext db, ICurrentActor actor, IPermissionChecker permissions, IDataScopeFilter scope, IWorkflowQueries approvals) : IAttachmentOwner
{
    public string OwnerType => LeaveWorkflowValues.OwnerType;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var r = await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ownerId, cancellationToken).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var employee = await db.Set<Employment>().AsNoTracking().Where(x => x.Id == r.EmploymentId).Select(x => x.UserRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var party = employee == actor.UserId || r.CreatedBy == actor.UserId;
        var source = new DbContextScopeSource(db);
        var reader = await permissions.HasPermissionAsync(LeavePermissions.RequestsRead, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(r, LeaveRequestAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false);
        var open = r.Status is "draft" or "submitted";
        var hr = await permissions.HasPermissionAsync(LeavePermissions.RequestsUpdate, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(r, LeaveRequestAccess.Scope, "update", source, cancellationToken).ConfigureAwait(false);
        var approver = !party && (await approvals.OpenApprovalsOfAsync(actor.UserId, null, cancellationToken).ConfigureAwait(false)).TasksToDecide
            .Any(t => t.DocumentId == r.Id && t.DocumentType is LeaveWorkflowValues.RequestType or LeaveWorkflowValues.CancellationType);
        var read = party || reader || approver;

        // The parties change files only while the request is a draft (review S1.32b finding 9); HR writers while it is open. Sensitive files (medical
        // certificates) are for the parties, the open-task approvers and HR writers in scope, not every reader of the team (review S1.32b finding 8).
        return new AttachmentOwnerAccess(r.CompanyId, read, (party && r.Status == "draft") || (hr && open), party || approver || hr);
    }
}
