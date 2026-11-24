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
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The attendance engine's endpoints (slice S1.29a). Processing on demand works out an employment's days now in one transaction (the same
/// engine as the job); reads give the day results with their explanation (keys and values, translated at display), as they are now or as
/// they were known at a moment (GHR-03: a day row is valid from its creation until it is replaced); the explanation of one day adds its
/// history, its exceptions and what processing now would give, or why the day is not processed ("no attendance policy applies; ask HR").
/// The exception queue lists the open exceptions in the caller's scope and, for those who may read them, the device punches that map to no
/// employee yet (S1.27b). An employment outside the caller's scope does not exist for them.
/// </summary>
internal sealed class AttendanceDayService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    AttendanceDayProcessor processor) : IAttendanceDayService
{
    public const string RangeRequired = "errors.hrm.attendance.process_range_required";
    public const string RangeOrder = "errors.hrm.attendance.process_range_order";
    public const string RangeTooLong = "errors.hrm.attendance.process_range_too_long";
    public const string Future = "errors.hrm.attendance.process_future";

    private const int DefaultDays = 31;
    private const int MostRows = 500;
    private static readonly string[] Statuses = ["open", "regularized", "accepted", "rejected", "closed"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> ProcessAsync(Guid employmentId, ProcessDaysRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceEnginePermissions.Process, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var e = await EmploymentAsync(employmentId, "process", cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return PeopleResult.NotFound;
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(e.CompanyId, now, cancellationToken).ConfigureAwait(false);
        if (Range(request.From, request.To, today) is { } refusal)
        {
            return refusal;
        }

        IReadOnlyList<DayRun> runs = [];
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            runs = await processor.ProcessAsync(e, request.From!.Value, request.To!.Value, now, actor.UserId, save: true, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            employmentId = e.Id,
            days = runs.Select(r => new { date = r.Date, status = r.Status, reason = r.Reason, dayId = r.DayId, result = r.Result }).ToList(),
        });
    }

    public async Task<PeopleResult> DaysAsync(Guid employmentId, DateOnly? firstDay, DateOnly? lastDay, DateTimeOffset? asOf, bool history, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceEnginePermissions.DaysRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var e = await EmploymentAsync(employmentId, "read", cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return PeopleResult.NotFound;
        }

        var today = await applier.TodayAsync(e.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var last = lastDay ?? today;
        var first = firstDay ?? last.AddDays(1 - DefaultDays);
        if (last < first)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["to"] = [RangeOrder] });
        }

        if (last.DayNumber - first.DayNumber + 1 > AttendanceEngine.MostDays)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["to"] = [RangeTooLong] });
        }

        var rows = db.Set<AttendanceDay>().AsNoTracking().Where(d => d.EmploymentId == e.Id && d.WorkDate >= first && d.WorkDate <= last);
        if (asOf is { } at)
        {
            rows = rows.Where(d => d.CreatedAt <= at && (d.SupersededAt == null || d.SupersededAt > at));
        }
        else if (!history)
        {
            rows = rows.Where(d => d.SupersededAt == null);
        }

        var list = await rows.OrderBy(d => d.WorkDate).ThenBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { employmentId = e.Id, from = first, to = last, asOf, history, days = list.Select(Day).ToList() });
    }

    public async Task<PeopleResult> ExplainAsync(Guid employmentId, DateOnly workDate, CancellationToken cancellationToken)
    {
        var date = workDate;
        if (!await permissions.HasPermissionAsync(AttendanceEnginePermissions.DaysRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var e = await EmploymentAsync(employmentId, "read", cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return PeopleResult.NotFound;
        }

        var versions = await db.Set<AttendanceDay>().AsNoTracking().Where(d => d.EmploymentId == e.Id && d.WorkDate == date)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var exceptions = await db.Set<AttendanceException>().AsNoTracking().Where(x => x.EmploymentId == e.Id && x.WorkDate == date)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var today = await applier.TodayAsync(e.CompanyId, now, cancellationToken).ConfigureAwait(false);
        object? preview = null;
        if (date <= today)
        {
            var run = (await processor.ProcessAsync(e, date, date, now, actor.UserId, save: false, cancellationToken).ConfigureAwait(false))[0];
            preview = run.Outcome is { } o
                ? new
                {
                    status = run.Status, reason = run.Reason, result = o.Result, dayType = o.DayType, shiftId = o.ShiftId, firstIn = o.FirstIn, lastOut = o.LastOut, workedMinutes = o.WorkedMinutes,
                    lateMinutes = o.LateMinutes, earlyMinutes = o.EarlyMinutes, leaveRequestId = o.LeaveRequestId, dutyRequestId = o.DutyRequestId,
                    explanation = JsonDocument.Parse(o.Explanation.ToJson().ToJsonString()).RootElement, exceptions = o.Exceptions,
                }
                : new { status = run.Status, reason = run.Reason, result = run.Result, dayType = (string?)null, shiftId = (Guid?)null, firstIn = (DateTimeOffset?)null, lastOut = (DateTimeOffset?)null, workedMinutes = 0, lateMinutes = 0, earlyMinutes = 0, leaveRequestId = (Guid?)null, dutyRequestId = (Guid?)null, explanation = (JsonElement?)null, exceptions = (IReadOnlyList<string>)[] };
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            employmentId = e.Id,
            date,
            current = versions.Where(v => v.SupersededAt == null).Select(Day).FirstOrDefault(),
            versions = versions.Select(Day).ToList(),
            exceptions = exceptions.Select(x => new { id = x.Id, kind = x.Kind, status = x.Status, attendanceDayId = x.AttendanceDayId, createdAt = x.CreatedAt, updatedAt = x.UpdatedAt, rowVersion = x.RowVersion }).ToList(),
            preview,
        });
    }

    public async Task<PeopleResult> ExceptionsAsync(Guid? companyId, string? status, string? kind, DateOnly? firstDay, DateOnly? lastDay, int? limit, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceEnginePermissions.ExceptionsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var wanted = status ?? "open";
        if (!Statuses.Contains(wanted, StringComparer.Ordinal))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["status"] = [PersonService.UnknownCode] });
        }

        var take = Math.Clamp(limit ?? 100, 1, MostRows);
        var query = db.Set<AttendanceException>().AsNoTracking().Where(x => x.Status == wanted);
        query = companyId is { } c ? query.Where(x => x.CompanyId == c) : query;
        query = kind is { Length: > 0 } k ? query.Where(x => x.Kind == k) : query;
        query = firstDay is { } f ? query.Where(x => x.WorkDate >= f) : query;
        query = lastDay is { } t ? query.Where(x => x.WorkDate <= t) : query;
        query = await scope.ApplyAsync(query, AttendanceExceptionAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var rows = await (from x in query
                          join e in db.Set<Employment>().AsNoTracking() on x.EmploymentId equals e.Id
                          orderby x.WorkDate descending, x.EmploymentId, x.Kind, x.Id
                          select new { x.Id, x.CompanyId, x.EmploymentId, e.EmployeeNo, x.WorkDate, x.Kind, x.Status, x.AttendanceDayId, x.CreatedAt, x.RowVersion })
            .Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Device punches of unknown device users (S1.27b) belong to the same queue (Build item 4), for those who may see them.
        object? unmapped = null;
        if ((kind is null || kind == "unmapped") && wanted == "open" && await permissions.HasPermissionAsync(AttendanceEnginePermissions.UnmappedRead, cancellationToken).ConfigureAwait(false))
        {
            var punches = db.Set<UnmappedPunch>().AsNoTracking().Where(u => u.Status == "open");
            punches = companyId is { } uc ? punches.Where(u => u.CompanyId == uc) : punches;
            punches = await scope.ApplyAsync(punches, UnmappedPunchAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
            var count = await punches.CountAsync(cancellationToken).ConfigureAwait(false);
            var first = await punches.OrderByDescending(u => u.PunchedAt).ThenBy(u => u.Id).Take(take)
                .Select(u => new { id = u.Id, companyId = u.CompanyId, deviceId = u.DeviceId, deviceUserNo = u.DeviceUserNo, punchedAt = u.PunchedAt, direction = u.Direction, kind = "unmapped" })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            unmapped = new { count, items = first };
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            status = wanted,
            items = rows.Take(take).Select(r => new
            {
                id = r.Id, companyId = r.CompanyId, employmentId = r.EmploymentId, employeeNo = r.EmployeeNo, workDate = r.WorkDate, kind = r.Kind, status = r.Status,
                attendanceDayId = r.AttendanceDayId, createdAt = r.CreatedAt, rowVersion = r.RowVersion,
            }).ToList(),
            more = rows.Count > take,
            unmapped,
        });
    }

    private async Task<Employment?> EmploymentAsync(Guid employmentId, string action, CancellationToken ct)
    {
        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, ct).ConfigureAwait(false);
        return e is not null && await scope.AllowsAsync(new AttendanceDay { CompanyId = e.CompanyId, EmploymentId = e.Id }, AttendanceDayAccess.Scope, action, Source, ct).ConfigureAwait(false)
            ? e
            : null;
    }

    private static PeopleResult? Range(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (from is null)
        {
            errors["from"] = [RangeRequired];
        }

        if (to is null)
        {
            errors["to"] = [RangeRequired];
        }
        else if (from is { } f && to < f)
        {
            errors["to"] = [RangeOrder];
        }
        else if (from is { } g && to.Value.DayNumber - g.DayNumber + 1 > AttendanceEngine.MostDays)
        {
            errors["to"] = [RangeTooLong];
        }
        else if (to > today)
        {
            errors["to"] = [Future];
        }

        return errors.Count > 0 ? PeopleResult.Fields(errors) : null;
    }

    private static object Day(AttendanceDay d) => new
    {
        id = d.Id, workDate = d.WorkDate, dayType = d.DayType, result = d.Result, shiftId = d.ShiftId, firstIn = d.FirstIn, lastOut = d.LastOut,
        workedMinutes = d.WorkedMinutes, lateMinutes = d.LateMinutes, earlyMinutes = d.EarlyMinutes, overtimeMinutes = d.OvertimeMinutes,
        leaveRequestId = d.LeaveRequestId, dutyRequestId = d.DutyRequestId, explanation = d.Explanation.RootElement, policyVersionId = d.PolicyVersionId,
        periodId = d.PeriodId, processedAt = d.ProcessedAt, supersededAt = d.SupersededAt,
    };
}
