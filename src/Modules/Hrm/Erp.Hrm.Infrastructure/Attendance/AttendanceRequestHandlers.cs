using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The end of an approval of a regularization request (document type <c>hrm.regularization</c>, slice S1.30a Build item 1; FR-HRM-ATT-11), on the
/// deciding transaction. Approved: the request becomes approved and the correction punches are written through <see cref="IAttendanceEventStore"/>
/// in the same transaction (a new event of source <c>regularization</c> that supersedes the person's original punch of that direction on that day,
/// or stands alone when there was none; the original is never touched), and that day is re-queued, so the engine marks the exception regularized.
/// A day that has been locked since the request was made refuses the approval (FR-XM-26). Rejected, returned or cancelled: the request ends and the
/// exception stays open.
/// </summary>
internal sealed class RegularizationHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public string DocumentType => AttendanceRequestEvents.RegularizationDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false);
        var row = await db.Set<RegularizationRequest>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
        if (row.Status != "submitted")
        {
            throw new WorkflowException("errors.hrm.attendance.request.not_pending");
        }

        var now = clock.UtcNow;
        row.FileIds = [.. await services.GetRequiredService<IRecordFiles>().CurrentAsync(AttendanceRequestEvents.RegularizationOwnerType, row.Id, transaction, cancellationToken).ConfigureAwait(false)]; // the proof as it stood when decided (R-161)
        if (outcome.Outcome == WorkflowOutcomes.Approved)
        {
            if (await services.GetRequiredService<IAttendancePeriodLock>().IsLockedAsync(row.EmploymentId, row.WorkDate, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkflowException(AttendanceRequestService.PeriodLocked);
            }

            await WriteCorrectionsAsync(db, row, now, cancellationToken).ConfigureAwait(false);
            row.Status = "approved";
        }
        else
        {
            row.Status = outcome.Outcome == WorkflowOutcomes.Rejected ? "rejected" : "cancelled";
        }

        row.RowVersion++;
        row.UpdatedAt = now;
        row.UpdatedBy = actor.UserId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteCorrectionsAsync(HrmDbContext db, RegularizationRequest row, DateTimeOffset now, CancellationToken ct)
    {
        var companyZone = await new EmploymentApplier(db, services.GetRequiredService<ISettings>(), PolicyResolver.For(db, services)).ZoneAsync(row.CompanyId, ct).ConfigureAwait(false);
        var inputs = new List<AttendanceEventInput>();
        var days = new List<DateOnly> { row.WorkDate };
        foreach (var (direction, at) in new[] { (AttendanceDirections.In, row.RequestedIn), (AttendanceDirections.Out, row.RequestedOut) })
        {
            if (at is not { } time)
            {
                continue;
            }

            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, companyZone).DateTime);
            days.Add(day);
            // The person's own punch of this direction on that day that counts today (not a duplicate, not already corrected) is the one corrected.
            var original = await (from e in db.Set<AttendanceEvent>().AsNoTracking()
                                  where e.EmploymentId == row.EmploymentId && e.EventDate == day && e.Direction == direction && !e.IsDuplicate
                                      && !db.Set<AttendanceEvent>().Any(x => x.SupersedesEventId == e.Id)
                                  select new { e.Id, e.EventAt }).ToListAsync(ct).ConfigureAwait(false);
            var chosen = direction == AttendanceDirections.In ? original.OrderBy(o => o.EventAt).FirstOrDefault() : original.OrderByDescending(o => o.EventAt).FirstOrDefault();
            inputs.Add(new AttendanceEventInput(row.EmploymentId, time, direction, AttendanceSources.Regularization,
                "regularization:" + row.Id.ToString("D") + ":" + direction, null, null, chosen?.Id));
        }

        var store = new AttendanceEventStore(db, clock, actor, new AttendanceSettingReader(services.GetRequiredService<ISettings>()),
            new EmploymentApplier(db, services.GetRequiredService<ISettings>(), PolicyResolver.For(db, services)));
        var before = clock.UtcNow;
        foreach (var result in await store.AppendAsync(inputs, ct).ConfigureAwait(false))
        {
            if (result.Status == AttendanceEventOutcome.Refused)
            {
                throw new WorkflowException(result.Error!);
            }
        }

        // The store re-queues a day that was already processed; a day not processed yet is asked for here, so the engine always sees the correction.
        if (!await db.Set<OutboxMessage>().AsNoTracking().AnyAsync(m => m.AggregateId == row.EmploymentId && m.EventType == AttendanceEngineEvents.DaysRequeued && m.OccurredAt >= before, ct).ConfigureAwait(false))
        {
            AttendanceReprocessing.Add(db, row.EmploymentId, days.Min(), days.Max(), "regularization", now, actor.UserId);
        }
    }
}

/// <summary>
/// The end of an approval of a duty request (document type <c>hrm.duty_request</c>, slice S1.30a Build item 2; FR-HRM-ATT-14), on the deciding
/// transaction: approved makes the request approved (the engine reads it through <c>IApprovedDutyDays</c>) and re-queues the days it covers, only
/// those; rejected, returned or cancelled ends it.
/// </summary>
internal sealed class DutyRequestHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public string DocumentType => AttendanceRequestEvents.DutyDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false);
        var row = await db.Set<DutyRequest>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
        if (row.Status != "submitted")
        {
            throw new WorkflowException("errors.hrm.attendance.request.not_pending");
        }

        var now = clock.UtcNow;
        row.FileIds = [.. await services.GetRequiredService<IRecordFiles>().CurrentAsync(AttendanceRequestEvents.DutyOwnerType, row.Id, transaction, cancellationToken).ConfigureAwait(false)]; // the proof as it stood when decided (R-161)
        if (outcome.Outcome == WorkflowOutcomes.Approved)
        {
            row.Status = "approved";
            var zone = await new EmploymentApplier(db, services.GetRequiredService<ISettings>(), PolicyResolver.For(db, services)).ZoneAsync(row.CompanyId, cancellationToken).ConfigureAwait(false);
            var (first, last) = AttendanceRequestRules.DutyDays(row.StartAt, row.EndAt, zone);
            AttendanceReprocessing.Add(db, row.EmploymentId, first, last, "duty_request", now, actor.UserId);
        }
        else
        {
            row.Status = outcome.Outcome == WorkflowOutcomes.Rejected ? "rejected" : "cancelled";
        }

        row.RowVersion++;
        row.UpdatedAt = now;
        row.UpdatedBy = actor.UserId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Proof files of a regularization request (slice S1.30b, R-161; FR-KRN-DOC-01, DOC-03; owner type <c>hrm.regularization_requests</c>, CR-114). The files follow
/// the request: the person it is for and the person who made it, the people with an open approval task of it, and readers of regularization requests whose
/// data scope covers it (a manager's team, HR's company) may read; the parties and the people who may make requests for this person (data scope) may add or
/// delete while the request waits for a decision; sensitive files are for the same people. Nobody else, and nothing in another company's scope.
/// </summary>
internal sealed class RegularizationAttachmentOwner(HrmDbContext db, ICurrentActor actor, IPermissionChecker permissions, IDataScopeFilter scope, IWorkflowQueries approvals) : IAttachmentOwner
{
    public string OwnerType => AttendanceRequestEvents.RegularizationOwnerType;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var r = await db.Set<RegularizationRequest>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ownerId, cancellationToken).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var source = new DbContextScopeSource(db);
        var reader = await permissions.HasPermissionAsync(AttendanceRequestPermissions.RegularizationRead, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(r, RegularizationRequestAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false);
        var maker = await permissions.HasPermissionAsync(AttendanceRequestPermissions.RegularizationCreate, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(r, RegularizationRequestAccess.Scope, "create", source, cancellationToken).ConfigureAwait(false);
        var employee = await db.Set<Employment>().AsNoTracking().Where(x => x.Id == r.EmploymentId).Select(x => x.UserRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var party = employee == actor.UserId || r.CreatedBy == actor.UserId;
        var approver = !party && (await approvals.OpenApprovalsOfAsync(actor.UserId, null, cancellationToken).ConfigureAwait(false)).TasksToDecide
            .Any(t => t.DocumentId == r.Id && t.DocumentType == AttendanceRequestEvents.RegularizationDocumentType);
        return new AttachmentOwnerAccess(r.CompanyId, party || reader || approver, (party || maker) && r.Status == "submitted", party || approver || maker || reader);
    }
}

/// <summary>Proof files of a duty request (slice S1.30b, R-161; owner type <c>hrm.duty_requests</c>): the same rules as <see cref="RegularizationAttachmentOwner"/>.</summary>
internal sealed class DutyRequestAttachmentOwner(HrmDbContext db, ICurrentActor actor, IPermissionChecker permissions, IDataScopeFilter scope, IWorkflowQueries approvals) : IAttachmentOwner
{
    public string OwnerType => AttendanceRequestEvents.DutyOwnerType;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var r = await db.Set<DutyRequest>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ownerId, cancellationToken).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var source = new DbContextScopeSource(db);
        var reader = await permissions.HasPermissionAsync(AttendanceRequestPermissions.DutyRead, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(r, DutyRequestAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false);
        var maker = await permissions.HasPermissionAsync(AttendanceRequestPermissions.DutyCreate, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(r, DutyRequestAccess.Scope, "create", source, cancellationToken).ConfigureAwait(false);
        var employee = await db.Set<Employment>().AsNoTracking().Where(x => x.Id == r.EmploymentId).Select(x => x.UserRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var party = employee == actor.UserId || r.CreatedBy == actor.UserId;
        var approver = !party && (await approvals.OpenApprovalsOfAsync(actor.UserId, null, cancellationToken).ConfigureAwait(false)).TasksToDecide
            .Any(t => t.DocumentId == r.Id && t.DocumentType == AttendanceRequestEvents.DutyDocumentType);
        return new AttachmentOwnerAccess(r.CompanyId, party || reader || approver, (party || maker) && r.Status == "submitted", party || approver || maker || reader);
    }
}
