using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Leave;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// Overtime claims, overtime asked for before the day, and waivers of lateness penalties (slice S1.29b Build items 8 and 9; FR-HRM-ATT-12, ATT-13,
/// GHR-09, FR-XM-26, FR-XM-32). Each is made for the caller's own employment or, when the data scope of their role allows, for another person's; the
/// acting user is the requester. The workflow decides (document types <c>hrm.overtime</c> and <c>hrm.attendance_penalty_waiver</c>; the line manager
/// given in the field <c>line_manager</c>, so a person without a sign-in reaches their own manager (R-305), else the HR administrators); the
/// person concerned and the requester never approve. A date in a locked attendance period is refused.
/// </summary>
internal sealed class AttendanceConsequenceService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    IAttendancePeriodLock locks,
    IEffectiveShiftReader effective,
    IPolicyResolver resolver,
    IWorkflowService workflow) : IAttendanceConsequenceService
{
    public const string PeriodLocked = "errors.hrm.attendance.request.period_locked";
    public const string NotClaimable = "errors.hrm.attendance.overtime.not_claimable";
    public const string AlreadyAsked = "errors.hrm.attendance.overtime.already_asked";
    public const string NotPreApproval = "errors.hrm.attendance.overtime.not_pre_approval";
    public const string NoPolicy = "errors.hrm.attendance.overtime.no_policy";
    public const string NotWaivable = "errors.hrm.attendance.penalty.not_waivable";
    public const string WaiverPending = "errors.hrm.attendance.penalty.waiver_pending";
    public const string EmploymentClosed = "errors.hrm.attendance.request.employment_closed";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> ClaimOvertimeAsync(Guid entryId, OvertimeClaimInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceConsequencePermissions.OvertimeSubmit, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var entry = await db.Set<OvertimeEntry>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken).ConfigureAwait(false);
        if (entry is null || !await scope.AllowsAsync(entry, OvertimeEntryAccess.Scope, "submit", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var errors = AttendanceConsequenceRules.CheckReason(request.Reason);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == entry.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (await locks.IsLockedAsync(entry.EmploymentId, entry.WorkDate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(PeriodLocked);
        }

        return await StartAsync(employment, entry.Id, AttendanceConsequenceEvents.OvertimeDocumentType, OvertimeValues(employment, entry.WorkDate, entry.DayType, entry.Minutes, false),
            async () =>
            {
                var row = await db.Set<OvertimeEntry>().FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken).ConfigureAwait(false);
                if (row is null || row.Status != "calculated" || row.Minutes <= 0)
                {
                    return (PeopleResult.Conflict(NotClaimable), null);
                }

                row.Status = "submitted";
                row.Reason = request.Reason!.Trim();
                EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
                return (null, i => row.WorkflowInstanceId = i);
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RequestOvertimeAsync(OvertimeRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceConsequencePermissions.OvertimeSubmit, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, failure) = await EmploymentAsync(request.EmploymentId, e => scope.AllowsAsync(new OvertimeEntry { CompanyId = e.CompanyId, EmploymentId = e.Id }, OvertimeEntryAccess.Scope, "submit", Source, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return failure!;
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(employment.CompanyId, now, cancellationToken).ConfigureAwait(false);
        var errors = AttendanceConsequenceRules.CheckRequest(request.WorkDate, request.Minutes, request.Reason, today);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var date = request.WorkDate!.Value;
        if (await locks.IsLockedAsync(employment.Id, date, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(PeriodLocked);
        }

        // The day type is the plan of the day (S1.28b's effective shift); the rule is the attendance policy the day's shift names, else the resolver's.
        var plan = await effective.ReadAsync(employment.Id, date, cancellationToken).ConfigureAwait(false);
        var dayType = plan?.DayType ?? "working";
        var shiftPolicy = plan?.ShiftId is { } shiftId ? await db.Set<Shift>().AsNoTracking().Where(s => s.Id == shiftId).Select(s => s.PolicyId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : null;
        var answer = await new AttendancePolicyReader(db, resolver).AtAsync(employment.Id, date, shiftPolicy, cancellationToken).ConfigureAwait(false);
        if (answer.Policy is null)
        {
            return PeopleResult.Conflict(NoPolicy);
        }

        if (answer.Policy.OvertimeFor(dayType) is not { Approval: OvertimeRule.Pre } rule)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["workDate"] = [NotPreApproval] });
        }

        var row = new OvertimeEntry
        {
            Id = Guid.CreateVersion7(now), CompanyId = employment.CompanyId, EmploymentId = employment.Id, WorkDate = date, DayType = dayType, Minutes = 0,
            RequestedMinutes = request.Minutes, RateMultiplier = rule.Multiplier, PolicyVersionId = answer.Reference!.VersionId, Reason = request.Reason!.Trim(),
            Status = "submitted", CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
        };
        return await StartAsync(employment, row.Id, AttendanceConsequenceEvents.OvertimeDocumentType, OvertimeValues(employment, date, dayType, request.Minutes!.Value, true),
            async () =>
            {
                if (await db.Set<OvertimeEntry>().AsNoTracking().AnyAsync(x => x.EmploymentId == employment.Id && x.WorkDate == date && x.Status != "rejected", cancellationToken).ConfigureAwait(false))
                {
                    return (PeopleResult.Conflict(AlreadyAsked), null);
                }

                db.Add(row);
                return (null, i => row.WorkflowInstanceId = i);
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RequestWaiverAsync(Guid penaltyId, PenaltyWaiverInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceConsequencePermissions.PenaltyWaive, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var penalty = await db.Set<AttendancePenalty>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == penaltyId, cancellationToken).ConfigureAwait(false);
        if (penalty is null || !await scope.AllowsAsync(penalty, AttendancePenaltyAccess.Scope, "waive", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var errors = AttendanceConsequenceRules.CheckReason(request.Reason);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        if (await locks.IsLockedAsync(penalty.EmploymentId, penalty.WorkDate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(PeriodLocked);
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == penalty.EmploymentId, cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = Json(employment.CompanyId.ToString()),
            ["employee_no"] = Json(employment.EmployeeNo),
            ["work_date"] = Json(Iso(penalty.WorkDate)),
            ["kind"] = Json(penalty.Kind),
            ["action"] = Json(penalty.Action),
            ["minutes"] = Json(penalty.Minutes),
        };
        return await StartAsync(employment, penalty.Id, AttendanceConsequenceEvents.WaiverDocumentType, values,
            async () =>
            {
                var row = await db.Set<AttendancePenalty>().FirstOrDefaultAsync(x => x.Id == penaltyId, cancellationToken).ConfigureAwait(false);
                if (row is null || row.Status != "applied")
                {
                    return (PeopleResult.Conflict(NotWaivable), null);
                }

                if (row.WorkflowInstanceId is not null)
                {
                    return (PeopleResult.Conflict(WaiverPending), null);
                }

                row.WaiverReason = request.Reason!.Trim();
                EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
                return (null, i => row.WorkflowInstanceId = i);
            }, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, JsonElement> OvertimeValues(Employment e, DateOnly date, string dayType, int minutes, bool before) => new(StringComparer.Ordinal)
    {
        ["company"] = Json(e.CompanyId.ToString()),
        ["employee_no"] = Json(e.EmployeeNo),
        ["work_date"] = Json(Iso(date)),
        ["day_type"] = Json(dayType),
        ["minutes"] = Json(minutes),
        ["pre_approval"] = Json(before),
    };

    /// <summary>The person: the employment named, else the caller's own (exactly one current employment), inside the caller's data scope.</summary>
    private async Task<(Employment? Employment, PeopleResult? Failure)> EmploymentAsync(Guid? employmentId, Func<Employment, ValueTask<bool>> allows, CancellationToken ct)
    {
        Employment? employment;
        if (employmentId is { } id && id != Guid.Empty)
        {
            employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        }
        else
        {
            var own = await db.Set<Employment>().AsNoTracking().Where(e => e.UserRef == actor.UserId && CurrentStatuses.Contains(e.Status)).OrderBy(e => e.EmployeeNo).Take(2).ToListAsync(ct).ConfigureAwait(false);
            if (own.Count != 1)
            {
                return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentId"] = [AttendanceConsequenceRules.Required] }));
            }

            employment = own[0];
        }

        if (employment is null || !await allows(employment).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return CurrentStatuses.Contains(employment.Status) ? (employment, null) : (null, PeopleResult.Conflict(EmploymentClosed));
    }

    /// <summary>
    /// In one transaction under the employment's lock: <paramref name="prepare"/> checks and changes the row (or refuses), the workflow starts with the
    /// line manager and the employee as values, and the row takes the instance. The answer is 202 with the row and the instance.
    /// </summary>
    private async Task<PeopleResult> StartAsync(
        Employment employment, Guid documentId, string documentType, Dictionary<string, JsonElement> values, Func<Task<(PeopleResult? Refused, Action<Guid>? Attach)>> prepare, CancellationToken ct)
    {
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            PeopleResult? refused = null;
            var instance = Guid.Empty;
            await strategy.ExecuteAsync(async () =>
            {
                refused = null;
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance_request:" + employment.Id.ToString("D")}, 0))", ct).ConfigureAwait(false);
                var (no, attach) = await prepare().ConfigureAwait(false);
                if (no is not null)
                {
                    refused = no;
                    return;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                if (employment.UserRef is { } user)
                {
                    values["employee"] = Json(user.ToString());
                }

                var today = await applier.TodayAsync(employment.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
                if (await LeaveWorkflowValues.LineManagerAsync(db, employment.Id, today, ct).ConfigureAwait(false) is { } manager)
                {
                    values["line_manager"] = Json(manager.ToString());
                }

                var started = await workflow.StartAsync(new WorkflowStartRequest(documentType, documentId, employment.CompanyId, employment.UserRef is { } subject ? [subject] : [], values),
                    db.Database.CurrentTransaction!.GetDbTransaction(), ct).ConfigureAwait(false);
                instance = started.InstanceId;
                attach!(started.InstanceId);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (refused is not null)
            {
                return refused;
            }

            return new PeopleResult(PeopleOutcome.Pending, Body: new
            {
                id = documentId,
                employmentId = employment.Id,
                status = "submitted",
                instanceId = instance,
                onBehalf = employment.UserRef is not { } u || u != actor.UserId,
            });
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
