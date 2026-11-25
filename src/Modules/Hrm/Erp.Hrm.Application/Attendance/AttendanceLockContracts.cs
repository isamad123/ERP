using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.Hrm.Application.Attendance;

/// <summary>HR's request to lock a pay period before payroll (slice S1.31 Build item 3): the reason, and whether days not processed and open exceptions are accepted.</summary>
public sealed class PeriodLockRequestInput
{
    public string? Reason { get; init; }

    public bool AcceptOpenItems { get; init; }
}

/// <summary>A request to cancel the run that locked a period and reopen it (slice S1.31 Build item 4, CR-095), with the reason.</summary>
public sealed class PeriodReopenRequestInput
{
    public string? Reason { get; init; }
}

/// <summary>A corrected day of an adjustment: the date and, for kind <c>attendance</c>, the result and minutes; for kind <c>overtime</c>, the overtime minutes.</summary>
public sealed class AdjustedDayInput
{
    public DateOnly? Date { get; init; }

    public string? Result { get; init; }

    public int? WorkedMinutes { get; init; }

    public int? LateMinutes { get; init; }

    public int? EarlyMinutes { get; init; }

    public int? OvertimeMinutes { get; init; }
}

/// <summary>
/// A retroactive adjustment of locked dates (slice S1.31 Build items 5 and 6): the employment, the kind (<c>attendance</c>, <c>leave_cancellation</c>,
/// <c>overtime</c>), the days (attendance and overtime) or the approved leave request to cancel, and the reason. The same input previews the impact.
/// </summary>
public sealed class AttendanceAdjustmentRequestInput
{
    public Guid? EmploymentId { get; init; }

    public string? Kind { get; init; }

    public Guid? LeaveRequestId { get; init; }

    public IReadOnlyList<AdjustedDayInput>? Days { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The attendance period lock console (slice S1.31 Build items 3, 4 and 7; FR-HRM-ATT-17, ATT-21, FR-XM-26, FR-XM-32): the pay periods of a company's pay
/// groups with their attendance state, who locked and why, open adjustments and late punches; HR's lock request after the cut-off date and the request to
/// cancel the locking run and reopen, both decided through the workflow by someone other than the requester.
/// </summary>
public interface IAttendanceLockService
{
    Task<PeopleResult> ListAsync(Guid? companyId, Guid? payGroupId, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid payPeriodId, CancellationToken cancellationToken);

    Task<PeopleResult> RequestLockAsync(Guid payPeriodId, PeriodLockRequestInput request, CancellationToken cancellationToken);

    Task<PeopleResult> RequestReopenAsync(Guid payPeriodId, PeriodReopenRequestInput request, CancellationToken cancellationToken);

    Task<PeopleResult> LateEventsAsync(Guid payPeriodId, CancellationToken cancellationToken);
}

/// <summary>
/// Retroactive adjustments (slice S1.31 Build items 5 and 6; FR-XM-26, GHR-04, FR-XM-32): the impact of a change to locked dates before it is asked for,
/// the request (approved through the workflow, never by the requester or the employee), the list and one adjustment.
/// </summary>
public interface IAttendanceAdjustmentService
{
    Task<PeopleResult> ImpactAsync(AttendanceAdjustmentRequestInput request, CancellationToken cancellationToken);

    Task<PeopleResult> SubmitAsync(AttendanceAdjustmentRequestInput request, CancellationToken cancellationToken);

    Task<PeopleResult> ListAsync(Guid? companyId, Guid? employmentId, string? status, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid adjustmentId, CancellationToken cancellationToken);
}

/// <summary>Pure rules of the lock and its adjustments (slice S1.31): checks of the inputs as field errors, the summaries kept as JSON.</summary>
public static class AttendanceLockRules
{
    public const string Required = "errors.hrm.attendance.lock.required";
    public const string ReasonTooLong = "errors.hrm.attendance.lock.reason_too_long";
    public const string KindUnknown = "errors.hrm.attendance.adjustment.kind";
    public const string DaysRequired = "errors.hrm.attendance.adjustment.days_required";
    public const string DayTwice = "errors.hrm.attendance.adjustment.day_twice";
    public const string TooManyDays = "errors.hrm.attendance.adjustment.too_many_days";
    public const string ResultUnknown = "errors.hrm.attendance.adjustment.result";
    public const string MinutesOutOfRange = "errors.hrm.attendance.adjustment.minutes";
    public const string LeaveRequestRequired = "errors.hrm.attendance.adjustment.leave_request_required";
    public const string LeaveRequestNotAllowed = "errors.hrm.attendance.adjustment.leave_request_not_allowed";

    public const int ReasonLength = 1000;

    /// <summary>The most days one adjustment covers (one pay period).</summary>
    public const int MostDays = 62;

    public const int MostMinutes = 1440;

    public static readonly IReadOnlyList<string> Kinds = ["attendance", "leave_cancellation", "overtime"];

    /// <summary>The day results of <c>hrm.attendance_days</c> a correction may state.</summary>
    public static readonly IReadOnlyList<string> Results = ["present", "absent", "late", "half_day", "on_leave", "holiday", "weekly_off", "on_duty", "incomplete"];

    public static Dictionary<string, string[]> CheckReason(string? reason, Dictionary<string, string[]>? errors = null)
    {
        errors ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = [Required];
        }
        else if (reason.Trim().Length > ReasonLength)
        {
            errors["reason"] = [ReasonTooLong];
        }

        return errors;
    }

    /// <summary>The checks of an adjustment request that need no data: kind, days (unique, at most a pay period, results and minutes in range), the leave request and the reason.</summary>
    public static Dictionary<string, string[]> CheckAdjustment(AttendanceAdjustmentRequestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = CheckReason(input.Reason);
        if (input.Kind is not { } kind || !Kinds.Contains(kind, StringComparer.Ordinal))
        {
            errors["kind"] = [input.Kind is null ? Required : KindUnknown];
            return errors;
        }

        if (kind == "leave_cancellation" && input.LeaveRequestId is null)
        {
            errors["leaveRequestId"] = [LeaveRequestRequired];
        }
        else if (kind != "leave_cancellation" && input.LeaveRequestId is not null)
        {
            errors["leaveRequestId"] = [LeaveRequestNotAllowed];
        }

        var days = input.Days ?? [];
        if (kind != "leave_cancellation" && days.Count == 0)
        {
            errors["days"] = [DaysRequired];
        }
        else if (days.Count > MostDays)
        {
            errors["days"] = [TooManyDays];
        }

        var seen = new HashSet<DateOnly>();
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var at = string.Create(CultureInfo.InvariantCulture, $"days[{i}]");
            if (d.Date is not { } date)
            {
                errors[at + ".date"] = [Required];
                continue;
            }

            if (!seen.Add(date))
            {
                errors[at + ".date"] = [DayTwice];
            }

            if (kind == "overtime" && d.OvertimeMinutes is null)
            {
                errors[at + ".overtimeMinutes"] = [Required];
            }

            if (kind == "attendance" && d.Result is null)
            {
                errors[at + ".result"] = [Required];
            }

            if (d.Result is { } result && !Results.Contains(result, StringComparer.Ordinal))
            {
                errors[at + ".result"] = [ResultUnknown];
            }

            foreach (var (name, value) in new[] { ("workedMinutes", d.WorkedMinutes), ("lateMinutes", d.LateMinutes), ("earlyMinutes", d.EarlyMinutes), ("overtimeMinutes", d.OvertimeMinutes) })
            {
                if (value is < 0 or > MostMinutes)
                {
                    errors[at + "." + name] = [MinutesOutOfRange];
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// The corrected day: an <c>attendance</c> correction states the result and the minutes it gives (the rest stays); an <c>overtime</c> correction the payable
    /// overtime minutes; a <c>leave_cancellation</c> removes the cancelled request's share of the day's unpaid and paid leave (and states a result only when given).
    /// </summary>
    public static AdjustedDaySummary Corrected(string kind, AdjustedDaySummary old, AdjustedDayInput? input, decimal unpaidShare = 0m, decimal paidShare = 0m)
    {
        ArgumentNullException.ThrowIfNull(old);
        return kind switch
        {
            "attendance" => old with
            {
                Result = input?.Result ?? old.Result,
                WorkedMinutes = input?.WorkedMinutes ?? old.WorkedMinutes,
                LateMinutes = input?.LateMinutes ?? old.LateMinutes,
                EarlyMinutes = input?.EarlyMinutes ?? old.EarlyMinutes,
                OvertimeMinutes = input?.OvertimeMinutes ?? old.OvertimeMinutes,
            },
            "overtime" => old with { OvertimeMinutes = input?.OvertimeMinutes ?? old.OvertimeMinutes },
            _ => old with { Result = input?.Result ?? old.Result, UnpaidLeave = Math.Max(0m, old.UnpaidLeave - unpaidShare), PaidLeave = Math.Max(0m, old.PaidLeave - paidShare) },
        };
    }

    /// <summary>The summary column: <c>{"days":[{date, result, workedMinutes, lateMinutes, earlyMinutes, overtimeMinutes, unpaidLeave, paidLeave}]}</c>.</summary>
    public static JsonDocument ToJson(IEnumerable<(DateOnly Date, AdjustedDaySummary Day)> days)
    {
        var array = new JsonArray();
        foreach (var (date, d) in days.OrderBy(x => x.Date))
        {
            array.Add(new JsonObject
            {
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["result"] = d.Result,
                ["workedMinutes"] = d.WorkedMinutes,
                ["lateMinutes"] = d.LateMinutes,
                ["earlyMinutes"] = d.EarlyMinutes,
                ["overtimeMinutes"] = d.OvertimeMinutes,
                ["unpaidLeave"] = d.UnpaidLeave,
                ["paidLeave"] = d.PaidLeave,
            });
        }

        return JsonDocument.Parse(new JsonObject { ["days"] = array }.ToJsonString());
    }

    /// <summary>The days of a summary column (see <see cref="ToJson"/>).</summary>
    public static IReadOnlyList<(DateOnly Date, AdjustedDaySummary Day)> FromJson(JsonDocument summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var list = new List<(DateOnly, AdjustedDaySummary)>();
        if (!summary.RootElement.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var d in days.EnumerateArray())
        {
            list.Add((DateOnly.Parse(d.GetProperty("date").GetString()!, CultureInfo.InvariantCulture), new AdjustedDaySummary(
                d.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                d.GetProperty("workedMinutes").GetInt32(), d.GetProperty("lateMinutes").GetInt32(), d.GetProperty("earlyMinutes").GetInt32(),
                d.GetProperty("overtimeMinutes").GetInt32(), d.GetProperty("unpaidLeave").GetDecimal(), d.GetProperty("paidLeave").GetDecimal())));
        }

        return list;
    }

    /// <summary>One entry of an adjustment's status history: the status, when and who.</summary>
    public static JsonObject HistoryEntry(string status, DateTimeOffset at, Guid by, string? comment = null) => new()
    {
        ["status"] = status,
        ["at"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        ["by"] = by.ToString("D"),
        ["comment"] = comment,
    };

    /// <summary>The history with one more entry (the history is only added to, FR-XM-06).</summary>
    public static JsonDocument Append(JsonDocument? history, JsonObject entry)
    {
        var array = history is null ? [] : JsonNode.Parse(history.RootElement.GetRawText())!.AsArray();
        array.Add(entry);
        return JsonDocument.Parse(array.ToJsonString());
    }
}
