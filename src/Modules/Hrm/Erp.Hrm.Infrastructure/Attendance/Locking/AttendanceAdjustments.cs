using System.Data.Common;
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
using Erp.Hrm.Infrastructure.Leave;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>A day of an adjustment as planned: its date, whether its period is locked, the period, what was locked and what it should be.</summary>
internal sealed record PlannedDay(DateOnly Date, bool Locked, Guid? PeriodId, Guid? PayPeriodId, AdjustedDaySummary Old, AdjustedDaySummary New);

/// <summary>An adjustment as planned (slice S1.31 Build items 5 and 6): the days, the locked period and its pay period, the target, the leave cancelled, or why it cannot be asked.</summary>
internal sealed record AdjustmentPlan(
    string Kind, List<PlannedDay> Days, AttendancePeriod? Period, PayPeriod? Target, LeaveRequest? Leave, LeaveType? LeaveType, LeaveLedger? Usage, List<DateOnly> OpenLeaveDays, string? Refusal);

/// <summary>
/// Plans a retroactive adjustment from the locked records (slice S1.31 Build items 5 and 6; FR-XM-26, GHR-04): each day's locked result as payroll read it
/// (the current day result, the counted leave days, the payable overtime) and the corrected result, the locked period of the dates (one, and locked), and
/// the next open payroll period of its pay group. For a leave cancellation the days are the request's days in locked periods; its days in open periods
/// are cancelled as usual on approval.
/// </summary>
internal static class AdjustmentPlanner
{
    public const string NotLocked = "errors.hrm.attendance.adjustment.not_locked";
    public const string TwoPeriods = "errors.hrm.attendance.adjustment.two_periods";
    public const string NoOpenPeriod = "errors.hrm.attendance.adjustment.no_open_period";
    public const string LeaveNotFound = "errors.hrm.attendance.adjustment.leave_not_found";
    public const string LeaveNotApproved = "errors.hrm.attendance.adjustment.leave_not_approved";

    private static readonly string[] Counted = ["approved", "cancel_requested"];

    public static async Task<AdjustmentPlan> PlanAsync(HrmDbContext db, Employment e, AttendanceAdjustmentRequestInput input, CancellationToken ct)
    {
        var kind = input.Kind!;
        LeaveRequest? leave = null;
        LeaveType? type = null;
        LeaveLedger? usage = null;
        var shares = new Dictionary<DateOnly, (decimal Unpaid, decimal Paid)>();
        var openLeaveDays = new List<DateOnly>();
        List<DateOnly> dates;
        if (kind == "leave_cancellation")
        {
            leave = await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == input.LeaveRequestId && r.EmploymentId == e.Id, ct).ConfigureAwait(false);
            if (leave is null)
            {
                return Refused(kind, LeaveNotFound);
            }

            if (leave.Status != "approved")
            {
                return Refused(kind, LeaveNotApproved);
            }

            type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == leave.LeaveTypeId, ct).ConfigureAwait(false);
            usage = await LeaveUsage.FindAsync(db, leave.Id, ct).ConfigureAwait(false);
            var days = await db.Set<LeaveRequestDay>().AsNoTracking().Where(d => d.LeaveRequestId == leave.Id).OrderBy(d => d.LeaveDate).ToListAsync(ct).ConfigureAwait(false);
            dates = [];
            foreach (var d in days)
            {
                if (await LockedPeriodOfAsync(db, e.Id, d.LeaveDate, ct).ConfigureAwait(false) is { Status: "locked" })
                {
                    dates.Add(d.LeaveDate);
                    shares[d.LeaveDate] = (!d.IsPaid && type.IsUnpaidDeduction ? d.Quantity : 0m, d.IsPaid ? d.Quantity : 0m);
                }
                else
                {
                    openLeaveDays.Add(d.LeaveDate);
                }
            }

            if (dates.Count == 0)
            {
                return Refused(kind, NotLocked) with { Leave = leave }; // nothing of it is locked: cancel it as usual
            }
        }
        else
        {
            dates = [.. (input.Days ?? []).Select(d => d.Date!.Value).Order()];
        }

        var inputs = (input.Days ?? []).Where(d => d.Date is not null).GroupBy(d => d.Date!.Value).ToDictionary(g => g.Key, g => g.First());
        var first = dates[0];
        var last = dates[^1];
        var results = await db.Set<AttendanceDay>().AsNoTracking().Where(d => d.EmploymentId == e.Id && d.WorkDate >= first && d.WorkDate <= last && d.SupersededAt == null)
            .ToDictionaryAsync(d => d.WorkDate, ct).ConfigureAwait(false);
        var leaveDays = await (from d in db.Set<LeaveRequestDay>().AsNoTracking()
                               join r in db.Set<LeaveRequest>().AsNoTracking() on d.LeaveRequestId equals r.Id
                               join t in db.Set<LeaveType>().AsNoTracking() on r.LeaveTypeId equals t.Id
                               where d.EmploymentId == e.Id && d.LeaveDate >= first && d.LeaveDate <= last && Counted.Contains(d.RequestStatus) && d.Quantity > 0m
                               select new { d.LeaveDate, Unpaid = !d.IsPaid && t.IsUnpaidDeduction ? d.Quantity : 0m, Paid = d.IsPaid ? d.Quantity : 0m })
            .ToListAsync(ct).ConfigureAwait(false);
        var overtime = await db.Set<OvertimeEntry>().AsNoTracking()
            .Where(x => x.EmploymentId == e.Id && x.WorkDate >= first && x.WorkDate <= last && (x.Status == "approved" || x.Status == "paid" || x.Status == "converted_to_comp_off"))
            .Select(x => new { x.WorkDate, Payable = Math.Min(x.Minutes, x.ApprovedMinutes ?? 0) - (x.CompOffMinutes ?? 0) }).ToListAsync(ct).ConfigureAwait(false);

        var planned = new List<PlannedDay>(dates.Count);
        AttendancePeriod? period = null;
        string? refusal = null;
        foreach (var date in dates)
        {
            var p = await LockedPeriodOfAsync(db, e.Id, date, ct).ConfigureAwait(false);
            results.TryGetValue(date, out var day);
            var old = new AdjustedDaySummary(day?.Result, day?.WorkedMinutes ?? 0, day?.LateMinutes ?? 0, day?.EarlyMinutes ?? 0,
                Math.Max(0, overtime.Where(o => o.WorkDate == date).Sum(o => o.Payable)), leaveDays.Where(l => l.LeaveDate == date).Sum(l => l.Unpaid), leaveDays.Where(l => l.LeaveDate == date).Sum(l => l.Paid));
            shares.TryGetValue(date, out var share);
            planned.Add(new PlannedDay(date, p is { Status: "locked" }, p?.Id, p?.PayPeriodId, old, AttendanceLockRules.Corrected(kind, old, inputs.GetValueOrDefault(date), share.Unpaid, share.Paid)));
            if (p is not { Status: "locked" })
            {
                refusal ??= NotLocked;
            }
            else if (period is null)
            {
                period = p;
            }
            else if (period.Id != p.Id)
            {
                refusal ??= TwoPeriods;
            }
        }

        var target = period is null ? null : await AttendancePeriodQueries.NextOpenAsync(db, period.PayGroupId, period.PeriodEnd, ct).ConfigureAwait(false);
        if (refusal is null && target is null)
        {
            refusal = NoOpenPeriod;
        }

        return new AdjustmentPlan(kind, planned, period, target, leave, type, usage, openLeaveDays, refusal);
    }

    /// <summary>The attendance period of an employment's date: its pay group's period covering the date (null when there is none).</summary>
    public static async Task<AttendancePeriod?> LockedPeriodOfAsync(HrmDbContext db, Guid employmentId, DateOnly date, CancellationToken ct)
    {
        var group = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employmentId && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date))
            .OrderByDescending(a => a.ValidFrom).Select(a => a.PayGroupId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return group is { } g
            ? await db.Set<AttendancePeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.PayGroupId == g && p.PeriodStart <= date && p.PeriodEnd >= date, ct).ConfigureAwait(false)
            : null;
    }

    private static AdjustmentPlan Refused(string kind, string refusal) => new(kind, [], null, null, null, null, null, [], refusal);
}

/// <summary>
/// Retroactive adjustments (slice S1.31 Build items 5 and 6; FR-XM-26, FR-HRM-ATT-17, LVE-07, GHR-04, FR-XM-32). A change to a locked date is asked for as an
/// adjustment, never by editing the locked period: the impact is shown first (the days with what was locked and what it should be, the leave given back
/// and the balance after, the overtime and penalties of the days, and the pay periods touched); the request records both summaries and the next open
/// payroll period, and goes to the workflow (document type <c>hrm.attendance_adjustment</c>), where neither the requester nor the employee approves.
/// </summary>
internal sealed class AttendanceAdjustmentService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    IWorkflowService workflow) : IAttendanceAdjustmentService
{
    public const string Waiting = "errors.hrm.attendance.adjustment.waiting";
    public const string EmploymentClosed = "errors.hrm.attendance.request.employment_closed";

    public const int ListLimit = 200;

    private static readonly string[] Open = ["submitted", "approved"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> ImpactAsync(AttendanceAdjustmentRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.AdjustmentsSubmit, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, failure) = await EmploymentAsync(request.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return failure!;
        }

        var errors = AttendanceLockRules.CheckAdjustment(request);
        errors.Remove("reason"); // the impact is looked at before the reason is written
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var plan = await AdjustmentPlanner.PlanAsync(db, employment, request, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: await ImpactBodyAsync(employment, plan, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PeopleResult> SubmitAsync(AttendanceAdjustmentRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.AdjustmentsSubmit, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, failure) = await EmploymentAsync(request.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return failure!;
        }

        var errors = AttendanceLockRules.CheckAdjustment(request);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            PeopleResult? result = null;
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance_request:" + employment.Id.ToString("D")}, 0))", cancellationToken).ConfigureAwait(false);
                var plan = await AdjustmentPlanner.PlanAsync(db, employment, request, cancellationToken).ConfigureAwait(false);
                if (plan.Refusal is { } refused)
                {
                    result = PeopleResult.Conflict(refused);
                    return;
                }

                var from = plan.Days[0].Date;
                var to = plan.Days[^1].Date;
                var kind = plan.Kind;
                var leaveId = plan.Leave?.Id;
                if (await db.Set<AttendanceAdjustment>().AsNoTracking().AnyAsync(a => a.EmploymentId == employment.Id && a.Kind == kind && a.Status == "submitted"
                        && (leaveId != null ? a.LeaveRequestId == leaveId : a.FromDate <= to && a.ToDate >= from), cancellationToken).ConfigureAwait(false)
                    || (leaveId != null && await db.Set<AttendanceAdjustment>().AsNoTracking().AnyAsync(a => a.LeaveRequestId == leaveId && Open.Contains(a.Status), cancellationToken).ConfigureAwait(false)))
                {
                    result = PeopleResult.Conflict(Waiting);
                    return;
                }

                var now = clock.UtcNow;
                var row = new AttendanceAdjustment
                {
                    Id = Guid.CreateVersion7(now), CompanyId = employment.CompanyId, EmploymentId = employment.Id, PeriodId = plan.Period!.Id, FromDate = from, ToDate = to, Kind = kind,
                    LeaveRequestId = leaveId, OldSummary = AttendanceLockRules.ToJson(plan.Days.Select(d => (d.Date, d.Old))), NewSummary = AttendanceLockRules.ToJson(plan.Days.Select(d => (d.Date, d.New))),
                    Reason = request.Reason!.Trim(), TargetPayPeriodId = plan.Target!.Id, Status = "submitted",
                    StatusHistory = AttendanceLockRules.Append(null, AttendanceLockRules.HistoryEntry("submitted", now, actor.UserId)),
                    CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
                };
                db.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["company"] = AttendanceLockService.Json(employment.CompanyId.ToString()),
                    ["employee_no"] = AttendanceLockService.Json(employment.EmployeeNo),
                    ["kind"] = AttendanceLockService.Json(kind),
                    ["from_date"] = AttendanceLockService.Json(AttendanceLockService.Iso(from)),
                    ["to_date"] = AttendanceLockService.Json(AttendanceLockService.Iso(to)),
                    ["target_period_start"] = AttendanceLockService.Json(AttendanceLockService.Iso(plan.Target.StartDate)),
                    ["reason"] = AttendanceLockService.Json(row.Reason),
                };
                if (employment.UserRef is { } user)
                {
                    values["employee"] = AttendanceLockService.Json(user.ToString());
                }

                var today = await applier.TodayAsync(employment.CompanyId, now, cancellationToken).ConfigureAwait(false);
                if (await LeaveWorkflowValues.LineManagerAsync(db, employment.Id, today, cancellationToken).ConfigureAwait(false) is { } manager)
                {
                    values["line_manager"] = AttendanceLockService.Json(manager.ToString());
                }

                var started = await workflow.StartAsync(new WorkflowStartRequest(AttendanceLockEvents.AdjustmentDocumentType, row.Id, employment.CompanyId, employment.UserRef is { } subject ? [subject] : [], values),
                    db.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                row.WorkflowInstanceId = started.InstanceId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new PeopleResult(PeopleOutcome.Pending, Body: new
                {
                    id = row.Id, employmentId = employment.Id, kind, fromDate = from, toDate = to, status = row.Status, targetPayPeriodId = row.TargetPayPeriodId,
                    instanceId = started.InstanceId,
                });
            }).ConfigureAwait(false);
            return result!;
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

    public async Task<PeopleResult> ListAsync(Guid? companyId, Guid? employmentId, string? status, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.AdjustmentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var rows = await scope.ApplyAsync(db.Set<AttendanceAdjustment>().AsNoTracking(), AttendanceAdjustmentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        if (companyId is { } c)
        {
            rows = rows.Where(a => a.CompanyId == c);
        }

        if (employmentId is { } e)
        {
            rows = rows.Where(a => a.EmploymentId == e);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            rows = rows.Where(a => a.Status == status);
        }

        var employments = db.Set<Employment>().AsNoTracking();
        var items = await rows.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(ListLimit)
            .Select(a => new
            {
                id = a.Id, companyId = a.CompanyId, employmentId = a.EmploymentId, employeeNo = employments.Where(x => x.Id == a.EmploymentId).Select(x => x.EmployeeNo).FirstOrDefault(),
                periodId = a.PeriodId, kind = a.Kind, fromDate = a.FromDate, toDate = a.ToDate, leaveRequestId = a.LeaveRequestId, reason = a.Reason,
                targetPayPeriodId = a.TargetPayPeriodId, status = a.Status, instanceId = a.WorkflowInstanceId, createdAt = a.CreatedAt, createdBy = a.CreatedBy,
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { items });
    }

    public async Task<PeopleResult> GetAsync(Guid adjustmentId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.AdjustmentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var a = await db.Set<AttendanceAdjustment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == adjustmentId, cancellationToken).ConfigureAwait(false);
        if (a is null || !await scope.AllowsAsync(a, AttendanceAdjustmentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var employeeNo = await db.Set<Employment>().AsNoTracking().Where(x => x.Id == a.EmploymentId).Select(x => x.EmployeeNo).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var periods = await db.Set<PayPeriod>().AsNoTracking()
            .Where(p => p.Id == a.TargetPayPeriodId || db.Set<AttendancePeriod>().Any(x => x.Id == a.PeriodId && x.PayPeriodId == p.Id))
            .Select(p => new { p.Id, p.StartDate, p.EndDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var old = AttendanceLockRules.FromJson(a.OldSummary);
        var corrected = AttendanceLockRules.FromJson(a.NewSummary).ToDictionary(x => x.Date, x => x.Day);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            id = a.Id, companyId = a.CompanyId, employmentId = a.EmploymentId, employeeNo, periodId = a.PeriodId, kind = a.Kind, fromDate = a.FromDate, toDate = a.ToDate,
            leaveRequestId = a.LeaveRequestId, reason = a.Reason, status = a.Status, instanceId = a.WorkflowInstanceId, leaveReversalId = a.LeaveReversalId, appliedInRunId = a.AppliedInRunId,
            targetPayPeriod = periods.Find(p => p.Id == a.TargetPayPeriodId),
            originalPayPeriod = periods.Find(p => p.Id != a.TargetPayPeriodId),
            days = old.Select(d => new { date = d.Date, old = d.Day, @new = corrected.GetValueOrDefault(d.Date) }),
            history = a.StatusHistory.RootElement, createdAt = a.CreatedAt, createdBy = a.CreatedBy, rowVersion = a.RowVersion,
        });
    }

    /// <summary>
    /// The impact of the change (GHR-04): the days with what was locked and what it should be, the leave given back with the balance before and after,
    /// the overtime and the penalties of the days, the pay periods touched (each locked one and the one it is carried into) and the modules affected.
    /// </summary>
    private async Task<object> ImpactBodyAsync(Employment e, AdjustmentPlan plan, CancellationToken ct)
    {
        var dates = plan.Days.Select(d => d.Date).ToList();
        var first = dates.Count > 0 ? dates.Min() : default;
        var last = dates.Count > 0 ? dates.Max() : default;
        var overtime = await db.Set<OvertimeEntry>().AsNoTracking().Where(x => x.EmploymentId == e.Id && dates.Contains(x.WorkDate)).OrderBy(x => x.WorkDate)
            .Select(x => new { id = x.Id, workDate = x.WorkDate, status = x.Status, minutes = x.Minutes, approvedMinutes = x.ApprovedMinutes }).ToListAsync(ct).ConfigureAwait(false);
        var penalties = await db.Set<AttendancePenalty>().AsNoTracking().Where(x => x.EmploymentId == e.Id && dates.Contains(x.WorkDate)).OrderBy(x => x.WorkDate)
            .Select(x => new { id = x.Id, workDate = x.WorkDate, kind = x.Kind, action = x.Action, status = x.Status }).ToListAsync(ct).ConfigureAwait(false);
        var leaveDays = await db.Set<LeaveRequestDay>().AsNoTracking().Where(d => d.EmploymentId == e.Id && dates.Contains(d.LeaveDate) && (d.RequestStatus == "approved" || d.RequestStatus == "cancel_requested"))
            .OrderBy(d => d.LeaveDate).Select(d => new { leaveRequestId = d.LeaveRequestId, leaveDate = d.LeaveDate, quantity = d.Quantity, isPaid = d.IsPaid }).ToListAsync(ct).ConfigureAwait(false);

        object? leave = null;
        if (plan.Leave is { } r && plan.LeaveType is { } t)
        {
            var given = plan.Usage is { } u ? -u.Quantity : 0m;
            var year = plan.Usage?.LeaveYear;
            var balance = year is { } y
                ? await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.EmploymentId == e.Id && x.LeaveTypeId == t.Id && x.LeaveYear == y).SumAsync(x => x.Quantity, ct).ConfigureAwait(false)
                : 0m;
            leave = new
            {
                leaveRequestId = r.Id, requestNo = r.RequestNo, leaveTypeId = t.Id, leaveTypeCode = t.Code, unit = t.Unit, leaveYear = year, givenBack = given, balanceBefore = balance,
                balanceAfter = balance + given, yearClosed = year is { } closedYear && await LeaveYearEnds.ClosedAsync(db, e.CompanyId, t.Id, closedYear, ct).ConfigureAwait(false),
                entryDate = plan.Target?.StartDate, daysCancelledAsUsual = plan.OpenLeaveDays,
            };
        }

        var touched = plan.Days.Where(d => d.PayPeriodId is not null).Select(d => d.PayPeriodId!.Value).Distinct().ToList();
        if (plan.Target is { } target)
        {
            touched.Add(target.Id);
        }

        var payPeriods = await db.Set<PayPeriod>().AsNoTracking().Where(p => touched.Contains(p.Id)).OrderBy(p => p.StartDate)
            .Select(p => new { p.Id, p.StartDate, p.EndDate, p.Status }).ToListAsync(ct).ConfigureAwait(false);
        var modules = new List<string> { "attendance" };
        if (plan.Days.Any(d => d.Old.UnpaidLeave != d.New.UnpaidLeave || d.Old.PaidLeave != d.New.PaidLeave) || leave is not null || leaveDays.Count > 0)
        {
            modules.Add("leave");
        }

        modules.Add("payroll");
        return new
        {
            employmentId = e.Id,
            employeeNo = e.EmployeeNo,
            kind = plan.Kind,
            fromDate = dates.Count > 0 ? first : (DateOnly?)null,
            toDate = dates.Count > 0 ? last : (DateOnly?)null,
            canSubmit = plan.Refusal is null,
            refusal = plan.Refusal,
            days = plan.Days.Select(d => new { date = d.Date, locked = d.Locked, periodId = d.PeriodId, payPeriodId = d.PayPeriodId, old = d.Old, @new = d.New }),
            leave,
            leaveDays,
            overtime,
            penalties,
            payPeriods = payPeriods.Select(p => new
            {
                payPeriodId = p.Id, startDate = p.StartDate, endDate = p.EndDate, payStatus = p.Status,
                role = plan.Target?.Id == p.Id ? "target" : "locked",
            }),
            modules,
        };
    }

    /// <summary>The person: the employment named, inside the caller's data scope for asking adjustments, and not a cancelled hire.</summary>
    private async Task<(Employment? Employment, PeopleResult? Failure)> EmploymentAsync(Guid? employmentId, CancellationToken ct)
    {
        if (employmentId is not { } id || id == Guid.Empty)
        {
            return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentId"] = [AttendanceLockRules.Required] }));
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(new AttendanceAdjustment { CompanyId = employment.CompanyId, EmploymentId = employment.Id }, AttendanceAdjustmentAccess.Scope, "submit", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return employment.Status == "cancelled" ? (null, PeopleResult.Conflict(EmploymentClosed)) : (employment, null);
    }
}

/// <summary>
/// The end of an adjustment's approval (document type <c>hrm.attendance_adjustment</c>, slice S1.31 Build item 5; FR-XM-26, FR-HRM-LVE-07, FR-XM-32), on the
/// deciding transaction. Approved: the target becomes the next open payroll period again (the one asked for may have locked meanwhile); a leave
/// cancellation gives the leave back by a usage reversal dated on the first day of that period (never inside the lock) and cancels the request, whose
/// days in open periods are cancelled and worked out again while its locked days stay as payroll read them; the adjustment is approved and
/// <c>hrm.attendance.adjustment_approved</c> is published. Returned: stays open (withdrawn and asked again if needed). Rejected: rejected. Withdrawn or
/// cancelled: withdrawn. Each move is added to the status history.
/// </summary>
internal sealed class AttendanceAdjustmentHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public const string NotPending = "errors.hrm.attendance.adjustment.not_pending";
    public const string LeaveYearClosed = "errors.hrm.attendance.adjustment.leave_year_closed";

    public string DocumentType => AttendanceLockEvents.AdjustmentDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        if (outcome.Outcome == WorkflowOutcomes.Returned)
        {
            return;
        }

        await using var db = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.attendance_adjustments where id = {outcome.DocumentId} for update", cancellationToken).ConfigureAwait(false);
        var row = await db.Set<AttendanceAdjustment>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.attendance.request.not_found");
        if (row.Status != "submitted")
        {
            throw new WorkflowException(NotPending);
        }

        var now = clock.UtcNow;
        if (outcome.Outcome != WorkflowOutcomes.Approved)
        {
            var ended = outcome.Outcome == WorkflowOutcomes.Rejected ? "rejected" : "withdrawn";
            row.Status = ended;
            row.StatusHistory = AttendanceLockRules.Append(row.StatusHistory, AttendanceLockRules.HistoryEntry(ended, now, outcome.DecidedBy, outcome.Comment));
            EmploymentApplier.Bump(row, now, actor.UserId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var period = await db.Set<AttendancePeriod>().AsNoTracking().FirstAsync(p => p.Id == row.PeriodId, cancellationToken).ConfigureAwait(false);
        var target = await AttendancePeriodQueries.NextOpenAsync(db, period.PayGroupId, period.PeriodEnd, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException(AdjustmentPlanner.NoOpenPeriod);
        row.TargetPayPeriodId = target.Id;
        if (row.Kind == "leave_cancellation")
        {
            row.LeaveReversalId = await CancelLeaveAsync(db, row, target.StartDate, now, cancellationToken).ConfigureAwait(false);
        }

        row.Status = "approved";
        row.StatusHistory = AttendanceLockRules.Append(row.StatusHistory, AttendanceLockRules.HistoryEntry("approved", now, outcome.DecidedBy, outcome.Comment));
        EmploymentApplier.Bump(row, now, actor.UserId);
        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = AttendanceLockEvents.AdjustmentApproved,
            AggregateType = AttendanceLockEvents.AggregateType,
            AggregateId = row.PeriodId,
            Payload = JsonSerializer.SerializeToDocument(new
            {
                adjustmentId = row.Id, employmentId = row.EmploymentId, companyId = row.CompanyId, kind = row.Kind,
                fromDate = AttendanceLockService.Iso(row.FromDate), toDate = AttendanceLockService.Iso(row.ToDate), periodId = row.PeriodId,
                originalPayPeriodId = period.PayPeriodId, targetPayPeriodId = row.TargetPayPeriodId,
            }, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor = actor.UserId }, JsonSerializerOptions.Web),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The leave cancellation of an approved adjustment: its usage reversed by an entry dated <paramref name="on"/> (the next open period's first day, same leave
    /// year), the request cancelled, its days in open periods cancelled and re-queued; its days in locked periods are not touched (the database refuses it).
    /// </summary>
    private static async Task<Guid?> CancelLeaveAsync(HrmDbContext db, AttendanceAdjustment row, DateOnly on, DateTimeOffset now, CancellationToken ct)
    {
        var r = await db.Set<LeaveRequest>().FirstAsync(x => x.Id == row.LeaveRequestId, ct).ConfigureAwait(false);
        await LeaveLedgerStore.LockAsync(db, r.EmploymentId, r.LeaveTypeId, ct).ConfigureAwait(false);
        if (r.Status != "approved")
        {
            throw new WorkflowException(AdjustmentPlanner.LeaveNotApproved);
        }

        Guid? reversalId = null;
        var usage = await LeaveUsage.FindAsync(db, r.Id, ct).ConfigureAwait(false);
        if (usage is not null && !await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.ReversesEntryId == usage.Id, ct).ConfigureAwait(false))
        {
            if (await LeaveYearEnds.ClosedAsync(db, r.CompanyId, r.LeaveTypeId, usage.LeaveYear, ct).ConfigureAwait(false))
            {
                throw new WorkflowException(LeaveYearClosed);
            }

            var reversal = new LeaveLedger
            {
                Id = Guid.CreateVersion7(now), CompanyId = usage.CompanyId, EmploymentId = usage.EmploymentId, LeaveTypeId = usage.LeaveTypeId, LeaveYear = usage.LeaveYear,
                EntryDate = on, Kind = "usage_reversal", Quantity = -usage.Quantity, SourceType = LeaveUsage.Source, SourceId = r.Id, PolicyVersionId = usage.PolicyVersionId,
                ReversesEntryId = usage.Id, Reason = "attendance_adjustment", CreatedAt = now, CreatedBy = row.CreatedBy,
            };
            db.Add(reversal);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // the entry exists before the adjustment names it
            reversalId = reversal.Id;
        }

        r.Status = "cancelled";
        EmploymentApplier.Bump(r, now, row.CreatedBy);
        var days = await db.Set<LeaveRequestDay>().Where(d => d.LeaveRequestId == r.Id && d.RequestStatus != "cancelled").ToListAsync(ct).ConfigureAwait(false);
        var open = new List<DateOnly>();
        foreach (var d in days)
        {
            if (await AdjustmentPlanner.LockedPeriodOfAsync(db, r.EmploymentId, d.LeaveDate, ct).ConfigureAwait(false) is not { Status: "locked" })
            {
                d.RequestStatus = "cancelled";
                EmploymentApplier.Bump(d, now, row.CreatedBy);
                open.Add(d.LeaveDate);
            }
        }

        if (open.Count > 0)
        {
            AttendanceReprocessing.Add(db, r.EmploymentId, open.Min(), open.Max(), "leave_cancelled", now, row.CreatedBy);
        }

        return reversalId;
    }
}

/// <summary>Approved retroactive adjustments for payroll (contract <see cref="IAttendanceAdjustments"/>, slice S1.31 Build item 5): approved or applied, by target pay period.</summary>
internal sealed class AttendanceAdjustmentReader(HrmDbContext db) : IAttendanceAdjustments
{
    public async Task<IReadOnlyList<AttendanceAdjustmentInput>> ForPayPeriodAsync(IReadOnlyCollection<Guid> employmentIds, Guid payPeriodId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employmentIds);
        var ids = employmentIds.Distinct().ToList();
        var rows = await (from a in db.Set<AttendanceAdjustment>().AsNoTracking()
                          join p in db.Set<AttendancePeriod>().AsNoTracking() on a.PeriodId equals p.Id
                          where ids.Contains(a.EmploymentId) && a.TargetPayPeriodId == payPeriodId && (a.Status == "approved" || a.Status == "applied")
                          orderby a.EmploymentId, a.FromDate, a.Id
                          select new { a, p.PayPeriodId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(x =>
        {
            var corrected = AttendanceLockRules.FromJson(x.a.NewSummary).ToDictionary(d => d.Date, d => d.Day);
            var days = AttendanceLockRules.FromJson(x.a.OldSummary).Select(d => new AdjustedDay(d.Date, d.Day, corrected.GetValueOrDefault(d.Date) ?? d.Day)).ToList();
            return new AttendanceAdjustmentInput(x.a.Id, x.a.EmploymentId, x.a.Kind, x.a.FromDate, x.a.ToDate, x.PayPeriodId, x.a.TargetPayPeriodId, days, x.a.LeaveRequestId, x.a.Status);
        })];
    }
}
