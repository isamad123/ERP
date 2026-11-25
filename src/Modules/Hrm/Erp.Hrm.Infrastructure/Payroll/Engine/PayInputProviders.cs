using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>
/// Attendance and leave from locked data (slice S1.35 Build item 1, FR-HRM-PAY-07, FR-XM-26): the current day results of the period (worked, absent
/// days and hours worked), approved unpaid leave by day (<see cref="ILeaveDeductions"/>, dated so the pro-rata counts the days) and leave paid out
/// (<see cref="ILeaveEncashments"/>). The engine locks the period before it asks, so these no longer change.
/// </summary>
internal sealed class AttendanceLeaveInputProvider(HrmDbContext db, ILeaveDeductions unpaid, ILeaveEncashments encashments) : IPayInputProvider
{
    public const string ProviderCode = "attendance";

    public static PayInputNameSet Names { get; } = new(ProviderCode,
    [
        PayInputNames.WorkedDays, PayInputNames.AbsentDays, PayInputNames.WorkedHours, PayInputNames.UnpaidLeaveDays, PayInputNames.UnpaidLeaveHours,
        PayInputNames.LeaveEncashmentDays, PayInputNames.LeaveEncashmentHours,
    ]);

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.EmploymentIds.ToList();
        var items = new List<PayInputItem>();
        var days = await db.Set<AttendanceDay>().AsNoTracking()
            .Where(d => ids.Contains(d.EmploymentId) && d.WorkDate >= request.PeriodStart && d.WorkDate <= request.PeriodEnd && d.SupersededAt == null)
            .Select(d => new { d.EmploymentId, d.WorkDate, d.Result, d.WorkedMinutes })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // One dated item per day, so a run counts only the days the employee is in its pay group (review S1.35a finding 3).
        foreach (var d in days.OrderBy(d => d.EmploymentId).ThenBy(d => d.WorkDate))
        {
            if (Worked(d.Result) is var worked and > 0m)
            {
                items.Add(new PayInputItem(d.EmploymentId, ProviderCode, "hrm.attendance_days", null, PayInputNames.WorkedDays, worked, d.WorkDate));
            }

            if (d.Result == "absent")
            {
                items.Add(new PayInputItem(d.EmploymentId, ProviderCode, "hrm.attendance_days", null, PayInputNames.AbsentDays, 1m, d.WorkDate));
            }

            if (d.WorkedMinutes > 0)
            {
                items.Add(new PayInputItem(d.EmploymentId, ProviderCode, "hrm.attendance_days", null, PayInputNames.WorkedHours, PayRounding.Internal(d.WorkedMinutes / 60m), d.WorkDate));
            }
        }

        foreach (var d in await unpaid.ForAsync(ids, request.PeriodStart, request.PeriodEnd, cancellationToken).ConfigureAwait(false))
        {
            items.Add(d.Hours is { } hours && d.Days == 0m
                ? new PayInputItem(d.EmploymentId, ProviderCode, "hrm.leave_requests", d.LeaveRequestId, PayInputNames.UnpaidLeaveHours, hours, d.Date)
                : new PayInputItem(d.EmploymentId, ProviderCode, "hrm.leave_requests", d.LeaveRequestId, PayInputNames.UnpaidLeaveDays, d.Days, d.Date));
        }

        foreach (var e in await encashments.ForAsync(ids, request.PeriodStart, request.PeriodEnd, cancellationToken).ConfigureAwait(false))
        {
            var name = e.Unit == "hours" ? PayInputNames.LeaveEncashmentHours : PayInputNames.LeaveEncashmentDays;
            items.Add(new PayInputItem(e.EmploymentId, ProviderCode, "hrm.leave_ledger", e.EntryId, name, e.Units, e.EntryDate));
        }

        return items;
    }

    public Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A day's share of worked days: present, late and on duty count one, a half day one half (CR-093 S1.35a detail).</summary>
    public static decimal Worked(string? result) => result switch
    {
        "present" or "late" or "on_duty" => 1m,
        "half_day" => 0.5m,
        _ => 0m,
    };
}

/// <summary>Approved payable overtime (<see cref="IOvertimeInputs"/>, S1.29b): hours weighted by the policy's multiplier, and the minutes.</summary>
internal sealed class OvertimeInputProvider(IOvertimeInputs overtime) : IPayInputProvider
{
    public const string ProviderCode = "overtime";

    public static PayInputNameSet Names { get; } = new(ProviderCode, [PayInputNames.OvertimeHours, PayInputNames.OvertimeMinutes]);

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = new List<PayInputItem>();
        foreach (var o in await overtime.ForAsync(request.EmploymentIds, request.PeriodStart, request.PeriodEnd, cancellationToken).ConfigureAwait(false))
        {
            items.Add(new PayInputItem(o.EmploymentId, ProviderCode, "hrm.overtime_entries", o.EntryId, PayInputNames.OvertimeHours, PayRounding.Internal(o.Minutes * o.Multiplier / 60m), o.WorkDate));
            items.Add(new PayInputItem(o.EmploymentId, ProviderCode, "hrm.overtime_entries", o.EntryId, PayInputNames.OvertimeMinutes, o.Minutes, o.WorkDate));
        }

        return items;
    }

    public Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Lateness and early-leaving penalties whose action is a pay deduction (<see cref="ILatenessDeductions"/>, S1.29b): count and minutes.</summary>
internal sealed class LatenessInputProvider(ILatenessDeductions lateness) : IPayInputProvider
{
    public const string ProviderCode = "lateness";

    public static PayInputNameSet Names { get; } = new(ProviderCode, [PayInputNames.LateDeductions, PayInputNames.LateDeductionMinutes]);

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = new List<PayInputItem>();
        foreach (var p in await lateness.ForAsync(request.EmploymentIds, request.PeriodStart, request.PeriodEnd, cancellationToken).ConfigureAwait(false))
        {
            items.Add(new PayInputItem(p.EmploymentId, ProviderCode, "hrm.attendance_penalties", p.PenaltyId, PayInputNames.LateDeductions, 1m, p.WorkDate));
            items.Add(new PayInputItem(p.EmploymentId, ProviderCode, "hrm.attendance_penalties", p.PenaltyId, PayInputNames.LateDeductionMinutes, p.Minutes, p.WorkDate));
        }

        return items;
    }

    public Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Approved retroactive attendance adjustments carried into the run's period (<see cref="IAttendanceAdjustments"/>, S1.31): the change of unpaid leave
/// days (given back is positive), of worked days and of payable overtime minutes, as named quantities a formula can pay. Marking them applied and
/// arrears are S1.39's (each is read only by its target period's run).
/// </summary>
internal sealed class AdjustmentInputProvider(IAttendanceAdjustments adjustments) : IPayInputProvider
{
    public const string ProviderCode = "adjustments";

    public static PayInputNameSet Names { get; } = new(ProviderCode, [PayInputNames.AdjustmentPaidDays, PayInputNames.AdjustmentWorkedDays, PayInputNames.AdjustmentOvertimeMinutes]);

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = new List<PayInputItem>();
        foreach (var a in await adjustments.ForPayPeriodAsync(request.EmploymentIds, request.PayPeriodId, cancellationToken).ConfigureAwait(false))
        {
            if (a.Status is not ("approved" or "applied"))
            {
                continue;
            }

            var paid = a.Days.Sum(d => d.Old.UnpaidLeave - d.New.UnpaidLeave);
            var worked = a.Days.Where(d => d.New.Result is not null).Sum(d => AttendanceLeaveInputProvider.Worked(d.New.Result) - AttendanceLeaveInputProvider.Worked(d.Old.Result));
            var overtime = a.Days.Sum(d => d.New.OvertimeMinutes - d.Old.OvertimeMinutes);
            items.Add(new PayInputItem(a.EmploymentId, ProviderCode, "hrm.attendance_adjustments", a.AdjustmentId, PayInputNames.AdjustmentPaidDays, paid));
            items.Add(new PayInputItem(a.EmploymentId, ProviderCode, "hrm.attendance_adjustments", a.AdjustmentId, PayInputNames.AdjustmentWorkedDays, worked));
            items.Add(new PayInputItem(a.EmploymentId, ProviderCode, "hrm.attendance_adjustments", a.AdjustmentId, PayInputNames.AdjustmentOvertimeMinutes, overtime));
        }

        return items;
    }

    public Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The one-time inputs of <c>hrm.pay_inputs</c> (bonuses, incentives, deductions, adjustments, imported rows; FR-HRM-PAY-07, -08): the pending inputs of
/// the period, and those this run already used (a later calculation of the same run reads them again). A saved result marks the inputs it used
/// <c>consumed</c> by the run (Build item 1).
/// </summary>
internal sealed class PayInputRowsProvider(HrmDbContext db, IClock clock, ICurrentActor actor) : IPayInputProvider
{
    public const string ProviderCode = "pay_inputs";

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.EmploymentIds.ToList();
        var rows = await db.Set<PayInput>().AsNoTracking()
            .Where(i => ids.Contains(i.EmploymentId) && i.PayPeriodId == request.PayPeriodId && i.SourceType != "expense_claim" // expense claims are read by their own provider (S1.42)
                        && (i.Status == "pending" || (i.Status == "consumed" && i.ConsumedByRunId == request.PayRunId)))
            .OrderBy(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(i => new PayInputItem(i.EmploymentId, ProviderCode, i.SourceType, i.Id, ComponentId: i.ComponentId, Amount: i.Amount, Units: i.Units))];
    }

    public async Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        var ids = consumed.Items.Where(c => c.Item.Provider == ProviderCode && c.Item.SourceId is not null).Select(c => c.Item.SourceId!.Value).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // The rows are taken for update: a cancel that came in after the batch read them makes the batch roll back (the employee is calculated
        // again on the next call), so a paid input is never left marked cancelled (review S1.35a finding 5).
        var now = clock.UtcNow;
        var rows = await db.Set<PayInput>().FromSqlInterpolated($"select * from hrm.pay_inputs where id = any({ids.ToArray()}) for update").ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count != ids.Count || rows.Any(r => !(r.Status == "pending" || (r.Status == "consumed" && r.ConsumedByRunId == consumed.PayRunId))))
        {
            throw new PayInputsChangedException();
        }

        foreach (var row in rows.Where(r => r.Status == "pending"))
        {
            row.Status = "consumed";
            row.ConsumedByRunId = consumed.PayRunId;
            Employments.EmploymentApplier.Bump(row, now, actor.UserId);
        }
    }
}

/// <summary>A one-time input changed (it was cancelled) between the batch's read and its save: the batch rolls back and the caller calculates again.</summary>
internal sealed class PayInputsChangedException() : Exception(PayRunService.InputsChanged);
