using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// Leave requests (slice S1.32 Build items 7 to 9; FR-HRM-LVE-04 to -06, -16, FR-XM-26, FR-XM-32, GHR-09). An employee applies for their own leave
/// (the employment is the token user's, never one the request names); HR applies for an employee in their scope and is recorded as the one who
/// applied. A request is worked out and checked by <see cref="LeaveRequestPlanner"/>, numbered by the kernel numbering service when it is saved
/// (gap-free, in its transaction), and sent for approval on the route of <c>hrm.leave_request</c> (line manager, then HR where the policy says;
/// the kernel never lets the requester or the employee approve it). A type that needs a document is submitted once a file is attached to the draft
/// (the file store, owner <c>hrm.leave_requests</c>); the request keeps the ids of its files. Approved leave is cancelled at once, or after the
/// approval of <c>hrm.leave_cancellation</c> when the policy says so, and changed by a new request linked to it; the balance is restored by reversal.
/// Every write of an employment's leave of one type runs under the employment-and-type lock (ruling Q8).
/// </summary>
internal sealed class LeaveRequestService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ICustomFieldValidator customFields,
    INumberAllocator numbers,
    IWorkflowService workflow,
    IRecordFiles files,
    LeaveRequestPlanner planner,
    EmploymentApplier applier) : ILeaveRequestService
{
    public const string NumberType = "hrm.leave_request_no";
    public const string NotYours = "errors.hrm.leave.request.no_employment";
    public const string NotCurrent = "errors.hrm.employment.not_current";
    public const string DocumentRequired = "errors.hrm.leave.request.document_required";
    public const string NotDraft = "errors.hrm.leave.request.not_draft";
    public const string NotApproved = "errors.hrm.leave.request.not_approved";
    public const string Withdraw = "errors.hrm.leave.request.withdraw";
    public const string ChangeOpen = "errors.hrm.leave.request.change_open";
    public const string NotCancellable = "errors.hrm.leave.request.not_cancellable";

    private const int ReasonLength = 500;
    private static readonly string[] Current = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> PreviewAsync(LeaveRequestInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (e, type, ask, refusal) = await ReadInputAsync(input, null, "create", cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var today = await applier.TodayAsync(e!.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var plan = await planner.PlanAsync(e, type!, ask!, null, null, today, e.UserRef == actor.UserId, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            employmentId = e.Id,
            allowed = !plan.Refused,
            error = plan.Conflict,
            errors = plan.Errors.Count == 0 ? null : plan.Errors,
            details = plan.Details,
            documentRequired = type!.RequiresDocument,
            plan = plan.View(type),
        });
    }

    public async Task<PeopleResult> CreateAsync(LeaveRequestInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (e, type, ask, refusal) = await ReadInputAsync(input, null, "create", cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        return await SaveNewAsync(e!, type!, ask!, input, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> ChangeAsync(Guid requestId, LeaveRequestInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var old = await VisibleAsync(requestId, "update", cancellationToken).ConfigureAwait(false);
        if (old is null)
        {
            return PeopleResult.NotFound;
        }

        if (old.Status != "approved")
        {
            return PeopleResult.Conflict(NotApproved);
        }

        // The change is for the same employment; only the leave itself changes.
        var (e, type, ask, refusal) = await ReadInputAsync(input with { EmploymentId = old.EmploymentId, LeaveTypeId = input.LeaveTypeId ?? old.LeaveTypeId }, old.EmploymentId, "update", cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (await db.Set<LeaveRequest>().AsNoTracking().AnyAsync(r => r.ModifiesRequestId == old.Id && (r.Status == "draft" || r.Status == "submitted"), cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(ChangeOpen);
        }

        return await SaveNewAsync(e!, type!, ask!, input, old, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid requestId, LeaveRequestInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await VisibleAsync(requestId, "update", cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return PeopleResult.NotFound;
        }

        if (row.Status != "draft")
        {
            return PeopleResult.Conflict(NotDraft);
        }

        var (e, type, ask, refusal) = await ReadInputAsync(input with
        {
            EmploymentId = row.EmploymentId,
            LeaveTypeId = input.LeaveTypeId ?? row.LeaveTypeId,
            StartDate = input.StartDate ?? row.StartDate,
            EndDate = input.EndDate ?? (input.StartDate is null ? row.EndDate : null),
            StartPart = input.StartPart ?? (input.StartDate is null ? row.StartPart : null),
            EndPart = input.EndPart ?? (input.StartDate is null ? row.EndPart : null),
            Hours = input.Hours ?? (input.StartDate is null ? row.Hours : null),
        }, row.EmploymentId, "update", cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var modifies = row.ModifiesRequestId is { } m ? await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == m, cancellationToken).ConfigureAwait(false) : null;
        return await InTransactionAsync(async () =>
        {
            await LeaveLedgerStore.LockAsync(db, e!.Id, type!.Id, cancellationToken).ConfigureAwait(false);
            var tracked = await db.Set<LeaveRequest>().FirstAsync(r => r.Id == row.Id, cancellationToken).ConfigureAwait(false);
            if (tracked.Status != "draft" || (input.RowVersion is { } v && v != tracked.RowVersion))
            {
                return PeopleResult.Conflict(tracked.Status != "draft" ? NotDraft : PersonService.Concurrency);
            }

            var today = await applier.TodayAsync(e.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var plan = await planner.PlanAsync(e, type, ask!, tracked.Id, modifies, today, e.UserRef == actor.UserId, cancellationToken).ConfigureAwait(false);
            if (plan.Refused)
            {
                return Refused(plan, type);
            }

            var custom = await CustomAsync(input.Custom, tracked.Custom, e.CompanyId, tracked.Id, true, cancellationToken).ConfigureAwait(false);
            if (custom?.Error is { } bad)
            {
                return bad;
            }

            await using var hold = custom?.Check;
            if (hold is not null)
            {
                tracked.Custom = hold.Values;
            }

            var now = clock.UtcNow;
            tracked.LeaveTypeId = type.Id;
            tracked.StartDate = ask!.Start;
            tracked.EndDate = ask.End;
            tracked.StartPart = ask.StartPart;
            tracked.EndPart = ask.EndPart;
            tracked.Hours = type.Counting == "hours" ? ask.Hours : null;
            tracked.Days = DaysOf(plan, type);
            tracked.Reason = Reason(input.Reason) ?? tracked.Reason;
            tracked.BranchId = plan.BranchId ?? tracked.BranchId;
            EmploymentApplier.Bump(tracked, now, actor.UserId);
            db.RemoveRange(await db.Set<LeaveRequestDay>().Where(d => d.LeaveRequestId == tracked.Id).ToListAsync(cancellationToken).ConfigureAwait(false)); // audited (review S1.32b finding 10)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            AddDays(tracked, type, plan, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: View(tracked, plan, type));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> SubmitAsync(Guid requestId, LeaveSubmitInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await VisibleAsync(requestId, "update", cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return PeopleResult.NotFound;
        }

        if (row.Status != "draft")
        {
            return PeopleResult.Conflict(NotDraft);
        }

        var e = await db.Set<Employment>().AsNoTracking().FirstAsync(x => x.Id == row.EmploymentId, cancellationToken).ConfigureAwait(false);
        var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == row.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        if (!Current.Contains(e.Status, StringComparer.Ordinal))
        {
            return PeopleResult.Conflict(NotCurrent);
        }

        if (!type.IsActive || type.IsDeleted)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["leaveTypeId"] = [LeaveLedgerService.TypeRetired] });
        }

        var modifies = row.ModifiesRequestId is { } m ? await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == m, cancellationToken).ConfigureAwait(false) : null;
        if (modifies is not null && modifies.Status != "approved")
        {
            return PeopleResult.Conflict(NotApproved);
        }

        return await InTransactionAsync(async () =>
        {
            await LeaveLedgerStore.LockAsync(db, e.Id, type.Id, cancellationToken).ConfigureAwait(false);
            var tracked = await db.Set<LeaveRequest>().FirstAsync(r => r.Id == row.Id, cancellationToken).ConfigureAwait(false);
            if (tracked.Status != "draft" || (input.RowVersion is { } v && v != tracked.RowVersion))
            {
                return PeopleResult.Conflict(tracked.Status != "draft" ? NotDraft : PersonService.Concurrency);
            }

            var today = await applier.TodayAsync(e.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var ask = new LeaveAsk(tracked.StartDate, tracked.EndDate, tracked.StartPart, tracked.EndPart, tracked.Hours);
            var plan = await planner.PlanAsync(e, type, ask, tracked.Id, modifies, today, e.UserRef == actor.UserId, cancellationToken).ConfigureAwait(false);
            if (plan.Refused)
            {
                return Refused(plan, type);
            }

            // The days are counted again on the day it is sent (the plan may have changed since the draft was saved).
            var now = clock.UtcNow;
            tracked.Days = DaysOf(plan, type);
            db.RemoveRange(await db.Set<LeaveRequestDay>().Where(d => d.LeaveRequestId == tracked.Id).ToListAsync(cancellationToken).ConfigureAwait(false)); // audited (review S1.32b finding 10)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            AddDays(tracked, type, plan, now);
            return await SendAsync(tracked, e, type, plan, now, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> CancelAsync(Guid requestId, LeaveCancelInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsCancel, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await VisibleAsync(requestId, "cancel", cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return PeopleResult.NotFound;
        }

        var reason = Reason(input.Reason);
        if (reason is null || reason.Length > ReasonLength)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["reason"] = [reason is null ? LeaveLedgerService.ReasonRequired : PersonService.TooLong] });
        }

        // A request on its route is withdrawn through the approvals (the kernel closes its tasks and tells this module).
        if (row.Status == "submitted" || (row.Status == "draft" && row.WorkflowInstanceId is not null))
        {
            return PeopleResult.Conflict(Withdraw, new Dictionary<string, object?> { ["instanceId"] = row.WorkflowInstanceId });
        }

        if (row.Status is not ("draft" or "approved" or "cancel_requested"))
        {
            return PeopleResult.Conflict(NotCancellable);
        }

        var e = await db.Set<Employment>().AsNoTracking().FirstAsync(x => x.Id == row.EmploymentId, cancellationToken).ConfigureAwait(false);
        var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == row.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        return await InTransactionAsync(async () =>
        {
            await LeaveLedgerStore.LockAsync(db, e.Id, type.Id, cancellationToken).ConfigureAwait(false);
            var tracked = await db.Set<LeaveRequest>().FirstAsync(r => r.Id == row.Id, cancellationToken).ConfigureAwait(false);
            if (tracked.Status != row.Status || (input.RowVersion is { } v && v != tracked.RowVersion))
            {
                return PeopleResult.Conflict(PersonService.Concurrency);
            }

            var now = clock.UtcNow;

            // A cancellation returned for correction is sent back to its route (review S1.32b finding 3); one still running is waiting already.
            if (tracked.Status == "cancel_requested")
            {
                var returnedRules = (await planner.Policies.AtAsync(e.Id, tracked.StartDate, cancellationToken).ConfigureAwait(false))?.For(type.Code);
                var again = LeaveWorkflowValues.Of(tracked, e, type, returnedRules, e.UserRef != actor.UserId,
                    await LeaveWorkflowValues.LineManagerAsync(db, e.Id, await applier.TodayAsync(e.CompanyId, now, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
                again["reason"] = JsonSerializer.SerializeToElement(reason);
                var resent = await workflow.ResubmitAsync(LeaveWorkflowValues.CancellationType, tracked.Id, again, db.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Pending, Body: new { id = tracked.Id, status = tracked.Status, cancellationInstanceId = resent.InstanceId, rowVersion = tracked.RowVersion });
            }

            // Approved leave with a change waiting is not cancelled under it: the change is withdrawn first (review S1.32b finding 12).
            if (tracked.Status == "approved" && await db.Set<LeaveRequest>().AsNoTracking().AnyAsync(r => r.ModifiesRequestId == tracked.Id && (r.Status == "draft" || r.Status == "submitted"), cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Conflict(ChangeOpen);
            }

            if (tracked.Status == "draft")
            {
                await LeaveUsage.SetStatusAsync(db, tracked, "cancelled", now, actor.UserId, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Done, Body: new { id = tracked.Id, status = tracked.Status, rowVersion = tracked.RowVersion });
            }

            if (await LeaveCancellation.RefusalAsync(db, planner.Locks, tracked, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var rules = (await planner.Policies.AtAsync(e.Id, tracked.StartDate, cancellationToken).ConfigureAwait(false))?.For(type.Code);
            if (rules?.CancellationApproval == true)
            {
                await LeaveUsage.SetStatusAsync(db, tracked, "cancel_requested", now, actor.UserId, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var today = await applier.TodayAsync(e.CompanyId, now, cancellationToken).ConfigureAwait(false);
                var values = LeaveWorkflowValues.Of(tracked, e, type, rules, e.UserRef != actor.UserId, await LeaveWorkflowValues.LineManagerAsync(db, e.Id, today, cancellationToken).ConfigureAwait(false));
                values["reason"] = JsonSerializer.SerializeToElement(reason);
                var started = await workflow.StartAsync(new WorkflowStartRequest(LeaveWorkflowValues.CancellationType, tracked.Id, tracked.CompanyId,
                    e.UserRef is { } subject ? [subject] : [], values), db.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Pending, Body: new { id = tracked.Id, status = tracked.Status, cancellationInstanceId = started.InstanceId, rowVersion = tracked.RowVersion });
            }

            await LeaveCancellation.ApplyAsync(db, tracked, reason, now, actor.UserId, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { id = tracked.Id, status = tracked.Status, rowVersion = tracked.RowVersion });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> DetailsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await VisibleAsync(requestId, "read", cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return PeopleResult.NotFound;
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == row.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        var e = await db.Set<Employment>().AsNoTracking().FirstAsync(x => x.Id == row.EmploymentId, cancellationToken).ConfigureAwait(false);
        var days = await db.Set<LeaveRequestDay>().AsNoTracking().Where(d => d.LeaveRequestId == row.Id).OrderBy(d => d.LeaveDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.SourceType == LeaveUsage.Source && x.SourceId == row.Id).OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = await files.CurrentAsync(LeaveWorkflowValues.OwnerType, row.Id, null, cancellationToken).ConfigureAwait(false);
        LeavePlan? impact = null;
        if (row.Status is "submitted" or "draft")
        {
            var modifies = row.ModifiesRequestId is { } replaced ? await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == replaced, cancellationToken).ConfigureAwait(false) : null;
            impact = await planner.ImpactAsync(e, type, row, modifies, cancellationToken).ConfigureAwait(false);
        }

        var changes = await db.Set<LeaveRequest>().AsNoTracking().Where(r => r.ModifiesRequestId == row.Id).OrderBy(r => r.CreatedAt).Select(r => new { id = r.Id, requestNo = r.RequestNo, status = r.Status }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            id = row.Id,
            requestNo = row.RequestNo,
            companyId = row.CompanyId,
            employmentId = row.EmploymentId,
            employeeNo = e.EmployeeNo,
            employeeName = await db.Set<Person>().AsNoTracking().Where(p => p.Id == e.PersonId).Select(p => p.FirstName + (p.LastName == null ? "" : " " + p.LastName)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            leaveTypeId = type.Id,
            leaveTypeCode = type.Code,
            unit = type.Unit,
            counting = type.Counting,
            isPaid = type.IsPaid,
            status = row.Status,
            startDate = row.StartDate,
            endDate = row.EndDate,
            startPart = row.StartPart,
            endPart = row.EndPart,
            hours = row.Hours,
            days = row.Days,
            reason = row.Reason,
            fileIds = row.FileIds,
            currentFileIds = current,
            documentRequired = type.RequiresDocument,
            modifiesRequestId = row.ModifiesRequestId,
            changes,
            workflowInstanceId = row.WorkflowInstanceId,
            appliedBy = row.CreatedBy,
            onBehalf = e.UserRef != row.CreatedBy,
            impact = impact is null ? null : new { leaveYear = impact.Year?.Year, balance = impact.Balance, pending = impact.Pending, after = impact.After, lowest = impact.Lowest },
            dayList = days.Select(d => new { date = d.LeaveDate, quantity = d.Quantity, kind = d.DayKind, isPaid = d.IsPaid, status = d.RequestStatus }).ToList(),
            entries = entries.Select(x => LeaveLedgerService.Entry(x, null)).ToList(),
            rowVersion = row.RowVersion,
        });
    }

    /// <summary>Saves a new request (a draft, or sent at once) under the lock; a change names the request it replaces.</summary>
    private async Task<PeopleResult> SaveNewAsync(Employment e, LeaveType type, LeaveAsk ask, LeaveRequestInput input, LeaveRequest? modifies, CancellationToken ct)
    {
        var submit = input.Submit ?? true;
        if (submit && type.RequiresDocument)
        {
            // A new request has no files yet: it is saved as a draft, the document attached to it, then submitted (FR-HRM-LVE-16).
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["fileIds"] = [DocumentRequired] });
        }

        var reason = Reason(input.Reason);
        if (reason is { Length: > ReasonLength })
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["reason"] = [PersonService.TooLong] });
        }

        var id = Guid.CreateVersion7(clock.UtcNow);
        return await InTransactionAsync(async () =>
        {
            await LeaveLedgerStore.LockTypesAsync(db, e.Id, [type.Id, modifies?.LeaveTypeId], ct).ConfigureAwait(false);

            // One change of approved leave at a time, checked under the lock (review S1.32b finding 12).
            if (modifies is not null && await db.Set<LeaveRequest>().AsNoTracking().AnyAsync(r => r.ModifiesRequestId == modifies.Id && (r.Status == "draft" || r.Status == "submitted"), ct).ConfigureAwait(false))
            {
                return PeopleResult.Conflict(ChangeOpen);
            }

            var today = await applier.TodayAsync(e.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
            var plan = await planner.PlanAsync(e, type, ask, null, modifies, today, e.UserRef == actor.UserId, ct).ConfigureAwait(false);
            if (plan.Refused)
            {
                return Refused(plan, type);
            }

            var custom = await CustomAsync(input.Custom, null, e.CompanyId, null, false, ct).ConfigureAwait(false);
            if (custom?.Error is { } bad)
            {
                return bad;
            }

            await using var hold = custom?.Check;
            var branch = await db.Set<EmploymentAssignment>().AsNoTracking()
                .Where(a => a.EmploymentId == e.Id && a.ValidFrom <= ask.Start && (a.ValidTo == null || a.ValidTo > ask.Start))
                .OrderByDescending(a => a.ValidFrom).Select(a => (Guid?)a.BranchId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var now = clock.UtcNow;
            var row = new LeaveRequest
            {
                Id = id, CompanyId = e.CompanyId, BranchId = branch, EmploymentId = e.Id, LeaveTypeId = type.Id, StartDate = ask.Start, EndDate = ask.End, StartPart = ask.StartPart,
                EndPart = ask.EndPart, Hours = type.Counting == "hours" ? ask.Hours : null, Days = DaysOf(plan, type), Reason = reason, FileIds = [], ModifiesRequestId = modifies?.Id,
                Status = "draft", Custom = custom?.Check?.Values ?? JsonDocument.Parse("{}"), RequestNo = string.Empty,
                CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
            };
            if (!await scope.AllowsAsync(row, LeaveRequestAccess.Scope, modifies is null ? "create" : "update", Source, ct).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            // Numbered when saved (FR-KRN-NUM, FR-XM-06): gap-free in this transaction; a rollback gives the number back.
            row.RequestNo = await numbers.AssignAsync(new NumberRequest(NumberType, row.CompanyId, row.BranchId, row.StartDate), db.Database.CurrentTransaction!.GetDbTransaction(), ct).ConfigureAwait(false);
            db.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // the request first: its days refer to it
            AddDays(row, type, plan, now);
            if (!submit)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Created, Body: View(row, plan, type));
            }

            return await SendAsync(row, e, type, plan, now, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a request to its route (or back to it after a return), with its current files; the days now hold the calendar.</summary>
    private async Task<PeopleResult> SendAsync(LeaveRequest row, Employment e, LeaveType type, LeavePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var current = await files.CurrentAsync(LeaveWorkflowValues.OwnerType, row.Id, db.Database.CurrentTransaction!.GetDbTransaction(), ct).ConfigureAwait(false);
        if (type.RequiresDocument && current.Count == 0)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["fileIds"] = [DocumentRequired] });
        }

        row.FileIds = [.. current];
        var returned = row.WorkflowInstanceId is not null;
        row.Status = "submitted";
        if (db.Entry(row).State != EntityState.Added)
        {
            EmploymentApplier.Bump(row, now, actor.UserId);
        }

        foreach (var d in db.ChangeTracker.Entries<LeaveRequestDay>().Where(x => x.Entity.LeaveRequestId == row.Id).Select(x => x.Entity))
        {
            d.RequestStatus = "submitted";
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var today = await applier.TodayAsync(e.CompanyId, now, ct).ConfigureAwait(false);
        var values = LeaveWorkflowValues.Of(row, e, type, plan.Rules, e.UserRef != actor.UserId, await LeaveWorkflowValues.LineManagerAsync(db, e.Id, today, ct).ConfigureAwait(false));
        var tx = db.Database.CurrentTransaction!.GetDbTransaction();
        var started = returned
            ? await workflow.ResubmitAsync(LeaveWorkflowValues.RequestType, row.Id, values, tx, ct).ConfigureAwait(false)
            : await workflow.StartAsync(new WorkflowStartRequest(LeaveWorkflowValues.RequestType, row.Id, row.CompanyId, e.UserRef is { } subject ? [subject] : [], values), tx, ct).ConfigureAwait(false);
        if (row.WorkflowInstanceId != started.InstanceId)
        {
            row.WorkflowInstanceId = started.InstanceId;
            row.RowVersion++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new PeopleResult(PeopleOutcome.Pending, Body: View(row, plan, type));
    }

    /// <summary>The request's days as rows: the counted days (leave and sandwich), each with the type's paid flag and the request's status.</summary>
    private void AddDays(LeaveRequest row, LeaveType type, LeavePlan plan, DateTimeOffset now)
    {
        foreach (var d in plan.Count!.Days.Where(d => d.Quantity > 0m))
        {
            db.Add(new LeaveRequestDay
            {
                Id = Guid.CreateVersion7(now), CompanyId = row.CompanyId, LeaveRequestId = row.Id, EmploymentId = row.EmploymentId, LeaveDate = d.Date, Quantity = d.Quantity,
                IsPaid = type.IsPaid, RequestStatus = row.Status, DayKind = d.Kind == LeaveDayRules.Sandwich ? LeaveDayRules.Sandwich : LeaveDayRules.Leave,
                CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
            });
        }
    }

    /// <summary>The request's <c>days</c>: the total for a type counted in days; for hours, their share of the day's working hours (never zero).</summary>
    private static decimal DaysOf(LeavePlan plan, LeaveType type)
    {
        var count = plan.Count!;
        return type.Counting != "hours" || plan.DayHours is not { } work || work <= 0m ? count.Total : Math.Max(0.0001m, decimal.Round(count.Total / work, 4));
    }

    private static object View(LeaveRequest r, LeavePlan plan, LeaveType type) => new
    {
        id = r.Id,
        requestNo = r.RequestNo,
        status = r.Status,
        employmentId = r.EmploymentId,
        modifiesRequestId = r.ModifiesRequestId,
        workflowInstanceId = r.WorkflowInstanceId,
        rowVersion = r.RowVersion,
        plan = plan.View(type),
    };

    private static PeopleResult Refused(LeavePlan plan, LeaveType type) =>
        plan.Errors.Count > 0
            ? new PeopleResult(PeopleOutcome.Invalid, "errors.data.invalid_request", FieldErrors: plan.Errors, Extensions: plan.Details)
            : PeopleResult.Conflict(plan.Conflict!, new Dictionary<string, object?>(plan.Details ?? []) { ["plan"] = plan.View(type) });

    /// <summary>
    /// The employment, type and leave asked for. Without an employment it is the signed-in user's own in the type's company; with one, the caller's
    /// data scope for the action must cover it (an employment outside it does not exist for them).
    /// </summary>
    private async Task<(Employment? E, LeaveType? Type, LeaveAsk? Ask, PeopleResult? Refusal)> ReadInputAsync(LeaveRequestInput input, Guid? fixedEmployment, string action, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (input.LeaveTypeId is null)
        {
            errors["leaveTypeId"] = [PersonService.Required];
        }

        if (input.StartDate is null)
        {
            errors["startDate"] = [PersonService.Required];
        }

        if (errors.Count > 0)
        {
            return (null, null, null, PeopleResult.Fields(errors));
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.LeaveTypeId && !t.IsDeleted, ct).ConfigureAwait(false);
        Employment? e;
        if (input.EmploymentId is { } employmentId)
        {
            e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, ct).ConfigureAwait(false);
            if (e is null || (fixedEmployment is null && !await scope.AllowsAsync(new LeaveRequest { CompanyId = e.CompanyId, EmploymentId = e.Id }, LeaveRequestAccess.Scope, action, Source, ct).ConfigureAwait(false)))
            {
                return (null, null, null, PeopleResult.NotFound);
            }
        }
        else
        {
            e = type is null ? null : await db.Set<Employment>().AsNoTracking()
                .Where(x => x.UserRef == actor.UserId && x.CompanyId == type.CompanyId && Current.Contains(x.Status)).OrderBy(x => x.EmployeeNo).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (e is null)
            {
                return (null, null, null, type is null ? PeopleResult.Fields(new Dictionary<string, string[]> { ["leaveTypeId"] = [LeaveLedgerService.TypeUnknown] }) : PeopleResult.Conflict(NotYours));
            }
        }

        if (!Current.Contains(e.Status, StringComparer.Ordinal))
        {
            return (null, null, null, PeopleResult.Conflict(NotCurrent));
        }

        if (type is null || type.CompanyId != e.CompanyId)
        {
            return (null, null, null, PeopleResult.Fields(new Dictionary<string, string[]> { ["leaveTypeId"] = [LeaveLedgerService.TypeUnknown] }));
        }

        if (!type.IsActive)
        {
            return (null, null, null, PeopleResult.Fields(new Dictionary<string, string[]> { ["leaveTypeId"] = [LeaveLedgerService.TypeRetired] }));
        }

        var start = input.StartDate!.Value;
        var ask = new LeaveAsk(start, input.EndDate ?? start, input.StartPart ?? LeaveParts.Full, input.EndPart ?? (input.EndDate is null ? input.StartPart ?? LeaveParts.Full : LeaveParts.Full), input.Hours);
        return (e, type, ask, null);
    }

    /// <summary>A request the caller may see (or act on) by its data scope; null otherwise.</summary>
    private async Task<LeaveRequest?> VisibleAsync(Guid id, string action, CancellationToken ct)
    {
        var row = await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return row is not null && await scope.AllowsAsync(row, LeaveRequestAccess.Scope, action, Source, ct).ConfigureAwait(false) ? row : null;
    }

    /// <summary>Custom values (S1.15b): field security first, then the active definitions; the check holds the locks of unique fields until the save.</summary>
    private async Task<(CustomCheck? Check, PeopleResult? Error)?> CustomAsync(JsonElement? requested, JsonDocument? stored, Guid company, Guid? rowId, bool update, CancellationToken ct)
    {
        if (requested is null && !update)
        {
            requested = JsonDocument.Parse("{}").RootElement;
        }
        else if (requested is null)
        {
            return null;
        }

        var access = await fields.ForAsync(LeaveRequestAccess.Fields, update ? "update" : "create", company, ct).ConfigureAwait(false);
        var denied = new List<string>();
        var values = FieldShaping.MergeCustom(requested, stored, access, denied);
        if (denied.Count > 0)
        {
            return (null, new PeopleResult(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal)));
        }

        var check = await customFields.CheckAsync(new CustomWrite(LeaveWorkflowValues.OwnerType, rowId, company, values, stored, key => access.CanEdit(FieldAccessMap.CustomPrefix + key), update), ct).ConfigureAwait(false);
        if (check.Errors.Count > 0)
        {
            await check.DisposeAsync().ConfigureAwait(false);
            return (null, PeopleResult.Fields(check.Errors.ToDictionary(x => x.Key, x => new[] { x.Value }, StringComparer.Ordinal)));
        }

        return (check, null);
    }

    private static string? Reason(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>One transaction, committed only on success; database and workflow refusals become answers.</summary>
    private async Task<PeopleResult> InTransactionAsync(Func<Task<PeopleResult>> work, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var result = await work().ConfigureAwait(false);
            if (result.Outcome is PeopleOutcome.Created or PeopleOutcome.Done or PeopleOutcome.Pending)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }

            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return pg.ConstraintName == "ck_leave_request_days_daily_total" ? PeopleResult.Conflict(LeaveRequestPlanner.Overlap) : PersonService.Refusal(pg);
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (NumberingException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey);
        }
        catch (PostgresException pg) when (pg.SqlState is "40P01" or "40001")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
    }
}

/// <summary>Cancelling approved leave (FR-HRM-LVE-06, FR-XM-26): refused when a day of it is in a locked period or its leave year is closed.</summary>
internal static class LeaveCancellation
{
    public static async Task<PeopleResult?> RefusalAsync(HrmDbContext db, Kernel.Contracts.Attendance.IAttendancePeriodLock locks, LeaveRequest r, CancellationToken ct)
    {
        var days = await db.Set<LeaveRequestDay>().AsNoTracking().Where(d => d.LeaveRequestId == r.Id).Select(d => d.LeaveDate).ToListAsync(ct).ConfigureAwait(false);
        foreach (var d in days)
        {
            if (await locks.IsLockedAsync(r.EmploymentId, d, ct).ConfigureAwait(false))
            {
                // FR-XM-26 (S1.31): the cancellation becomes a retroactive adjustment (POST /api/hrm/attendance/adjustments, kind leave_cancellation).
                return PeopleResult.Conflict(LeaveLedgerService.PeriodLocked, new Dictionary<string, object?> { ["date"] = d, ["adjustmentKind"] = "leave_cancellation" });
            }
        }

        var usage = await LeaveUsage.FindAsync(db, r.Id, ct).ConfigureAwait(false);
        return usage is not null && await LeaveYearEnds.ClosedAsync(db, r.CompanyId, r.LeaveTypeId, usage.LeaveYear, ct).ConfigureAwait(false)
            ? PeopleResult.Conflict(LeaveRequestPlanner.YearClosed, new Dictionary<string, object?> { ["leaveYear"] = usage.LeaveYear })
            : null;
    }

    /// <summary>Cancels the leave: the usage reversed, the request and its days cancelled, the days worked out again by attendance.</summary>
    public static async Task ApplyAsync(HrmDbContext db, LeaveRequest r, string reason, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        if (await LeaveUsage.ReversalAsync(db, r, null, reason, now, actor, ct).ConfigureAwait(false) is { } reversal)
        {
            db.Add(reversal);
        }

        await LeaveUsage.SetStatusAsync(db, r, "cancelled", now, actor, ct).ConfigureAwait(false);
        LeaveUsage.Requeue(db, r, "leave_cancelled", now, actor);
    }
}
