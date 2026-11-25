using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// Regularization and duty requests (slice S1.30a Build items 1 to 3; FR-HRM-ATT-11, ATT-14, GHR-09, FR-XM-26, FR-XM-32). A request is made for the
/// caller's own employment or, when the data scope of their role allows, for another person's (a manager's team, HR's company): the row's
/// <c>created_by</c> is the acting user and the person it is for is the employment, so an approval card shows both. A date in a locked attendance
/// period is refused (a retroactive adjustment is S1.31's). The request is decided through the workflow engine (document types
/// <c>hrm.regularization</c> and <c>hrm.duty_request</c>); the requester and the person it is for never approve it. Proof files are not part of this
/// release (see R-161).
/// </summary>
internal sealed class AttendanceRequestService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    IAttendancePeriodLock locks,
    IWorkflowService workflow) : IAttendanceRequestService
{
    public const string PeriodLocked = "errors.hrm.attendance.request.period_locked";
    public const string ExceptionMismatch = "errors.hrm.attendance.request.exception_mismatch";
    public const string ExceptionMissing = "errors.hrm.attendance.request.exception_missing";
    public const string AlreadyPending = "errors.hrm.attendance.request.already_pending";
    public const string Overlap = "errors.hrm.attendance.request.overlap";
    public const string EmploymentClosed = "errors.hrm.attendance.request.employment_closed";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];
    private const int MostLockedDaysChecked = 366;

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> RequestRegularizationAsync(RegularizationRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceRequestPermissions.RegularizationCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        // The day and the person come from the exception when one is named (it is read in the caller's scope below).
        AttendanceException? exception = null;
        var employmentId = request.EmploymentId;
        var workDate = request.WorkDate;
        if (request.ExceptionId is { } exceptionId)
        {
            exception = await db.Set<AttendanceException>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == exceptionId, cancellationToken).ConfigureAwait(false);
            if (exception is null || (employmentId is { } given && given != exception.EmploymentId) || (workDate is { } day && day != exception.WorkDate))
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["exceptionId"] = [ExceptionMismatch] });
            }

            employmentId = exception.EmploymentId;
            workDate = exception.WorkDate;
        }
        else if (workDate is null)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["workDate"] = [ExceptionMissing] });
        }

        var (employment, failure) = await EmploymentAsync(employmentId, e => scope.AllowsAsync(new RegularizationRequest { CompanyId = e.CompanyId, EmploymentId = e.Id }, RegularizationRequestAccess.Scope, "create", Source, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return failure!;
        }

        if (exception is not null && exception.Status != "open")
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["exceptionId"] = [ExceptionMismatch] });
        }

        var now = clock.UtcNow;
        var zone = await applier.ZoneAsync(employment.CompanyId, cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var requestedIn = request.RequestedIn ?? (request.RequestedInTime is { } inTime ? AttendanceRequestRules.LocalInstant(workDate!.Value, inTime, request.RequestedInDayOffset, zone) : null);
        var requestedOut = request.RequestedOut ?? (request.RequestedOutTime is { } outTime ? AttendanceRequestRules.LocalInstant(workDate!.Value, outTime, request.RequestedOutDayOffset, zone) : null);
        var errors = AttendanceRequestRules.CheckRegularization(workDate!.Value, today, requestedIn, requestedOut, request.Reason, zone);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        if (await locks.IsLockedAsync(employment.Id, workDate.Value, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(PeriodLocked);
        }

        var row = new RegularizationRequest
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            ExceptionId = exception?.Id,
            WorkDate = workDate.Value,
            RequestedIn = requestedIn?.ToUniversalTime(),
            RequestedOut = requestedOut?.ToUniversalTime(),
            Reason = request.Reason!.Trim(),
            FileIds = [],
            Status = "submitted",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = JsonSerializer.SerializeToElement(employment.CompanyId.ToString()),
            ["work_date"] = JsonSerializer.SerializeToElement(Iso(row.WorkDate)),
            ["employee_no"] = JsonSerializer.SerializeToElement(employment.EmployeeNo),
            ["requested_in"] = JsonSerializer.SerializeToElement(row.RequestedIn is { } i ? AttendanceRequestRules.LocalTime(i, zone) : string.Empty),
            ["requested_out"] = JsonSerializer.SerializeToElement(row.RequestedOut is { } o ? AttendanceRequestRules.LocalTime(o, zone) : string.Empty),
        };
        return await SaveAndStartAsync(row, row.Id, i => { row.WorkflowInstanceId = i; row.RowVersion++; }, () => row.RowVersion, employment, AttendanceRequestEvents.RegularizationDocumentType, values,
            async () =>
            {
                var pending = await db.Set<RegularizationRequest>().AsNoTracking().AnyAsync(r => r.EmploymentId == employment.Id && r.WorkDate == row.WorkDate && r.Status == "submitted", cancellationToken).ConfigureAwait(false);
                return pending ? PeopleResult.Conflict(AlreadyPending) : null;
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RequestDutyAsync(DutyRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceRequestPermissions.DutyCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, failure) = await EmploymentAsync(request.EmploymentId, e => scope.AllowsAsync(new DutyRequest { CompanyId = e.CompanyId, EmploymentId = e.Id }, DutyRequestAccess.Scope, "create", Source, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return failure!;
        }

        var errors = AttendanceRequestRules.CheckDuty(request.Kind, request.StartDate, request.EndDate, request.StartTime, request.EndTime, request.Purpose);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        var zone = await applier.ZoneAsync(employment.CompanyId, cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var (start, end) = AttendanceRequestRules.DutyInstants(request.StartDate!.Value, request.EndDate!.Value, request.StartTime, request.EndTime, zone);
        var (first, last) = AttendanceRequestRules.DutyDays(start, end, zone);
        var lastChecked = last < today ? last : today;
        for (var day = first; day <= lastChecked && day.DayNumber - first.DayNumber < MostLockedDaysChecked; day = day.AddDays(1))
        {
            if (await locks.IsLockedAsync(employment.Id, day, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Conflict(PeriodLocked);
            }
        }

        var row = new DutyRequest
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            Kind = request.Kind!,
            StartAt = start.ToUniversalTime(),
            EndAt = end.ToUniversalTime(),
            Purpose = request.Purpose!.Trim(),
            Status = "submitted",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = JsonSerializer.SerializeToElement(employment.CompanyId.ToString()),
            ["kind"] = JsonSerializer.SerializeToElement(row.Kind),
            ["start_date"] = JsonSerializer.SerializeToElement(Iso(first)),
            ["end_date"] = JsonSerializer.SerializeToElement(Iso(last)),
            ["employee_no"] = JsonSerializer.SerializeToElement(employment.EmployeeNo),
        };
        return await SaveAndStartAsync(row, row.Id, i => { row.WorkflowInstanceId = i; row.RowVersion++; }, () => row.RowVersion, employment, AttendanceRequestEvents.DutyDocumentType, values,
            async () =>
            {
                var overlap = await db.Set<DutyRequest>().AsNoTracking().AnyAsync(r => r.EmploymentId == employment.Id && (r.Status == "submitted" || r.Status == "approved") && r.StartAt < row.EndAt && r.EndAt > row.StartAt, cancellationToken).ConfigureAwait(false);
                return overlap ? PeopleResult.Conflict(Overlap) : null;
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person the request is for: the employment named, else the caller's own (exactly one current employment), inside the caller's data scope.</summary>
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
                return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentId"] = [AttendanceRequestRules.Required] }));
            }

            employment = own[0];
        }

        if (employment is null || !await allows(employment).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return CurrentStatuses.Contains(employment.Status) || employment.Status == "terminated" ? (employment, null) : (null, PeopleResult.Conflict(EmploymentClosed));
    }

    private async Task<PeopleResult> SaveAndStartAsync(object row, Guid rowId, Action<Guid> attach, Func<long> version, Employment employment, string documentType, Dictionary<string, JsonElement> values, Func<Task<PeopleResult?>> refuse, CancellationToken ct)
    {
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            PeopleResult? refused = null;
            Guid instance = Guid.Empty;
            await strategy.ExecuteAsync(async () =>
            {
                refused = null;
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance_request:" + employment.Id.ToString("D")}, 0))", ct).ConfigureAwait(false);
                refused = await refuse().ConfigureAwait(false);
                if (refused is not null)
                {
                    return;
                }

                db.Add(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                values["employment"] = JsonSerializer.SerializeToElement(employment.Id.ToString()); // the manager step follows the subject's employment (R-305)
                if (employment.UserRef is { } user)
                {
                    values["employee"] = JsonSerializer.SerializeToElement(user.ToString());
                }

                var started = await workflow.StartAsync(new WorkflowStartRequest(documentType, rowId, employment.CompanyId, employment.UserRef is { } subject ? [subject] : [], values),
                    db.Database.CurrentTransaction!.GetDbTransaction(), ct).ConfigureAwait(false);
                instance = started.InstanceId;
                attach(started.InstanceId);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (refused is not null)
            {
                return refused;
            }

            return new PeopleResult(PeopleOutcome.Pending, Body: new
            {
                id = rowId,
                employmentId = employment.Id,
                status = "submitted",
                instanceId = instance,
                onBehalf = employment.UserRef is { } u && u != actor.UserId,
                rowVersion = version(),
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

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
