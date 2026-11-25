using System.Text.Json;
using Erp.Hrm.Application.Policies;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// The parameters of the <c>attendance</c> policy type (slice S1.29 Build item 1, FR-HRM-ATT-09, ruling Q1). Every number is the customer's
/// (no default, ruling Q2, AS-4, R-002): grace minutes for coming late and leaving early (BR-05), the lateness and early leaving that make a
/// half day (optional rules), the minutes worked for a full day (optional: each shift's own <c>min_full_day_minutes</c> otherwise) and below
/// which a day is an absence, the break mode, the rounding of worked minutes, the window around a shift in which a punch belongs to it
/// (also the overnight cut, ruling Q3) and whether work on a weekly off or a holiday is recorded. S1.29b (CR-089 S1.29b detail) adds the
/// late-coming and early-leaving rules (<see cref="LatePenalty"/>, <see cref="EarlyPenalty"/>: how many a month are allowed, then leave, a pay
/// deduction or a warning; FR-HRM-ATT-13) and the overtime rule of each day type (<see cref="Overtime"/>: multiplier, caps, pre- or
/// post-approval, pay or comp-off; FR-HRM-ATT-12). None of them has a default; no statutory working-time limit or rate is entered (AS-4, R-002).
/// </summary>
public sealed record AttendancePolicy(
    int LateGraceMinutes,
    int EarlyGraceMinutes,
    int? LateHalfDayAfterMinutes,
    int? EarlyHalfDayAfterMinutes,
    int? FullDayMinutes,
    int AbsentBelowMinutes,
    string BreakMode,
    int? BreakMinutes,
    string RoundingMode,
    int? RoundingStepMinutes,
    int ShiftWindowMinutes,
    string WeeklyOffWork,
    string HolidayWork,
    IReadOnlyDictionary<string, bool>? DutyCountsAsPresent = null,
    PenaltyRule? LatePenalty = null,
    PenaltyRule? EarlyPenalty = null,
    IReadOnlyDictionary<string, OvertimeRule>? Overtime = null)
{
    public const string PolicyType = "attendance";

    public const string BreakNone = "none";
    public const string BreakFixed = "fixed";
    public const string BreakByPunches = "by_punches";

    public const string RoundNone = "none";
    public const string RoundUp = "up";
    public const string RoundDown = "down";
    public const string RoundNearest = "nearest";

    public const string Record = "record";
    public const string Ignore = "ignore";

    /// <summary>
    /// The duty kinds of ATT-14 and whether each counts as present when the policy says nothing (slice S1.30 Q1, CR-090): on duty, official visit and
    /// field duty do; work from home is the customer's choice, so it does not count until the policy says it does.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> DutyKinds { get; } = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["on_duty"] = true,
        ["official_visit"] = true,
        ["work_from_home"] = false,
        ["field_duty"] = true,
    };

    /// <summary>The policy's property name of the flag of a duty kind (<c>onDutyCountsAsPresent</c> and so on).</summary>
    public static string DutyFlagName(string kind) => kind switch
    {
        "on_duty" => "onDutyCountsAsPresent",
        "official_visit" => "officialVisitCountsAsPresent",
        "work_from_home" => "workFromHomeCountsAsPresent",
        "field_duty" => "fieldDutyCountsAsPresent",
        _ => kind,
    };

    public const string BreakMinutesRequired = "errors.hrm.policy.attendance.break_minutes_required";
    public const string BreakMinutesUnused = "errors.hrm.policy.attendance.break_minutes_unused";
    public const string RoundingStepRequired = "errors.hrm.policy.attendance.rounding_step_required";
    public const string RoundingStepUnused = "errors.hrm.policy.attendance.rounding_step_unused";
    public const string AbsentAboveFullDay = "errors.hrm.policy.attendance.absent_above_full_day";
    public const string HalfDayWithinGrace = "errors.hrm.policy.attendance.half_day_within_grace";
    public const string LeaveTypeRequired = "errors.hrm.policy.attendance.penalty_leave_type_required";
    public const string LeaveUnitsRequired = "errors.hrm.policy.attendance.penalty_leave_units_required";
    public const string PenaltyLeaveUnused = "errors.hrm.policy.attendance.penalty_leave_unused";
    public const string CompOffMinutesRequired = "errors.hrm.policy.attendance.comp_off_day_minutes_required";
    public const string CompOffUnused = "errors.hrm.policy.attendance.comp_off_unused";
    public const string MonthlyBelowDaily = "errors.hrm.policy.attendance.monthly_cap_below_daily";
    public const string CountBeforeShiftRequired = "errors.hrm.policy.attendance.count_before_shift_required";
    public const string CountBeforeShiftUnused = "errors.hrm.policy.attendance.count_before_shift_unused";
    public const string RestWorkIgnored = "errors.hrm.policy.attendance.overtime_rest_work_ignored";

    /// <summary>Every error key of the check (the text files must have each, in English and Urdu).</summary>
    public static IReadOnlyList<string> ErrorKeys { get; } =
    [
        BreakMinutesRequired, BreakMinutesUnused, RoundingStepRequired, RoundingStepUnused, AbsentAboveFullDay, HalfDayWithinGrace, LeaveTypeRequired, LeaveUnitsRequired,
        PenaltyLeaveUnused, CompOffMinutesRequired, CompOffUnused, MonthlyBelowDaily, CountBeforeShiftRequired, CountBeforeShiftUnused, RestWorkIgnored,
    ];

    /// <summary>
    /// Reads the parameters of a version (the schema was checked when the version was saved); null when a required value is missing, so a
    /// broken version is never read as zeros.
    /// </summary>
    public static AttendancePolicy? Read(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || Check(p).Count > 0)
        {
            return null;
        }

        var lateGrace = Int(p, "lateGraceMinutes");
        var earlyGrace = Int(p, "earlyGraceMinutes");
        var absent = Int(p, "absentBelowMinutes");
        var window = Int(p, "shiftWindowMinutes");
        var breakMode = Text(p, "breakMode");
        var rounding = Text(p, "roundingMode");
        var weeklyOff = Text(p, "weeklyOffWork");
        var holiday = Text(p, "holidayWork");
        if (lateGrace is null || earlyGrace is null || absent is null || window is null || breakMode is null || rounding is null || weeklyOff is null || holiday is null)
        {
            return null;
        }

        return new AttendancePolicy(lateGrace.Value, earlyGrace.Value, Int(p, "lateHalfDayAfterMinutes"), Int(p, "earlyHalfDayAfterMinutes"), Int(p, "fullDayMinutes"),
            absent.Value, breakMode, Int(p, "breakMinutes"), rounding, Int(p, "roundingStepMinutes"), window.Value, weeklyOff, holiday,
            DutyKinds.Keys.ToDictionary(k => k, k => Flag(p, DutyFlagName(k)) ?? DutyKinds[k], StringComparer.Ordinal),
            Penalty(p, "latePenalty"), Penalty(p, "earlyPenalty"), OvertimeRules(p));
    }

    /// <summary>The overtime rule of a day type (<c>working</c>, <c>weekly_off</c>, <c>holiday</c>), or null when the policy records no overtime on it.</summary>
    public OvertimeRule? OvertimeFor(string dayType) => Overtime is { } rules && rules.TryGetValue(dayType, out var rule) ? rule : null;

    /// <summary>The policy's property of a day type's overtime rule (<c>working</c>, <c>weeklyOff</c>, <c>holiday</c>).</summary>
    public static string OvertimeProperty(string dayType) => dayType switch
    {
        "weekly_off" => "weeklyOff",
        _ => dayType,
    };

    /// <summary>The day types in the order the policy lists them.</summary>
    public static IReadOnlyList<string> OvertimeDayTypes { get; } = ["working", "weekly_off", "holiday"];

    private static PenaltyRule? Penalty(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object || Int(r, "allowedPerMonth") is not { } allowed || Text(r, "action") is not { } action)
        {
            return null;
        }

        return new PenaltyRule(allowed, action, Text(r, "leaveTypeCode"), Decimal(r, "leaveUnits"));
    }

    private static Dictionary<string, OvertimeRule>? OvertimeRules(JsonElement p)
    {
        if (!p.TryGetProperty("overtime", out var o) || o.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rules = new Dictionary<string, OvertimeRule>(StringComparer.Ordinal);
        foreach (var dayType in OvertimeDayTypes)
        {
            if (o.TryGetProperty(OvertimeProperty(dayType), out var r) && r.ValueKind == JsonValueKind.Object && Decimal(r, "multiplier") is { } multiplier
                && Text(r, "approval") is { } approval && Text(r, "compensation") is { } compensation)
            {
                rules[dayType] = new OvertimeRule(multiplier, approval, Int(r, "dailyCapMinutes"), Int(r, "monthlyCapMinutes"), Int(r, "minimumMinutes"),
                    Flag(r, "countBeforeShift") ?? false, compensation, Int(r, "compOffDayMinutes"), Text(r, "compOffLeaveTypeCode"));
            }
        }

        return rules;
    }

    /// <summary>Whether an approved duty of this kind counts as present (CR-090: a flag per kind in the policy; ATT-14).</summary>
    public bool CountsAsPresent(string kind) =>
        DutyCountsAsPresent is { } flags && flags.TryGetValue(kind, out var set) ? set : DutyKinds.TryGetValue(kind, out var fallback) && fallback;

    /// <summary>What the schema cannot say: a value used only with another choice, and limits that must be in order. Empty when valid.</summary>
    public static IReadOnlyDictionary<string, string[]> Check(JsonElement p)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (p.ValueKind != JsonValueKind.Object)
        {
            return errors;
        }

        var breakMode = Text(p, "breakMode");
        var breakMinutes = Int(p, "breakMinutes");
        if (breakMode == BreakFixed && breakMinutes is null)
        {
            errors["breakMinutes"] = [BreakMinutesRequired];
        }
        else if (breakMode is not null && breakMode != BreakFixed && breakMinutes is not null)
        {
            errors["breakMinutes"] = [BreakMinutesUnused];
        }

        var rounding = Text(p, "roundingMode");
        var step = Int(p, "roundingStepMinutes");
        if (rounding is not null && rounding != RoundNone && step is null)
        {
            errors["roundingStepMinutes"] = [RoundingStepRequired];
        }
        else if (rounding == RoundNone && step is not null)
        {
            errors["roundingStepMinutes"] = [RoundingStepUnused];
        }

        if (Int(p, "fullDayMinutes") is { } full && Int(p, "absentBelowMinutes") is { } absent && absent > full)
        {
            errors["absentBelowMinutes"] = [AbsentAboveFullDay];
        }

        if (Int(p, "lateHalfDayAfterMinutes") is { } lateHalf && Int(p, "lateGraceMinutes") is { } lateGrace && lateHalf <= lateGrace)
        {
            errors["lateHalfDayAfterMinutes"] = [HalfDayWithinGrace];
        }

        if (Int(p, "earlyHalfDayAfterMinutes") is { } earlyHalf && Int(p, "earlyGraceMinutes") is { } earlyGrace && earlyHalf <= earlyGrace)
        {
            errors["earlyHalfDayAfterMinutes"] = [HalfDayWithinGrace];
        }

        foreach (var name in new[] { "latePenalty", "earlyPenalty" })
        {
            if (p.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.Object)
            {
                var leave = Text(r, "action") == PenaltyRule.Leave;
                if (leave && Text(r, "leaveTypeCode") is null)
                {
                    errors[name + ".leaveTypeCode"] = [LeaveTypeRequired];
                }

                if (leave && Decimal(r, "leaveUnits") is null)
                {
                    errors[name + ".leaveUnits"] = [LeaveUnitsRequired];
                }

                if (!leave && (r.TryGetProperty("leaveTypeCode", out _) || r.TryGetProperty("leaveUnits", out _)))
                {
                    errors[name + ".action"] = [PenaltyLeaveUnused];
                }
            }
        }

        if (p.TryGetProperty("overtime", out var overtime) && overtime.ValueKind == JsonValueKind.Object)
        {
            foreach (var dayType in OvertimeDayTypes)
            {
                if (!overtime.TryGetProperty(OvertimeProperty(dayType), out var r) || r.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var at = "overtime." + OvertimeProperty(dayType) + ".";
                var compOff = Text(r, "compensation") == OvertimeRule.CompOff;
                if (compOff && Int(r, "compOffDayMinutes") is null)
                {
                    errors[at + "compOffDayMinutes"] = [CompOffMinutesRequired];
                }

                if (!compOff && (r.TryGetProperty("compOffDayMinutes", out _) || r.TryGetProperty("compOffLeaveTypeCode", out _)))
                {
                    errors[at + "compensation"] = [CompOffUnused];
                }

                if (Int(r, "dailyCapMinutes") is { } daily && Int(r, "monthlyCapMinutes") is { } monthly && monthly < daily)
                {
                    errors[at + "monthlyCapMinutes"] = [MonthlyBelowDaily];
                }

                // A rest-day rule would never apply when the policy ignores work on that day (review S1.29b finding 5).
                if ((dayType == "weekly_off" && Text(p, "weeklyOffWork") == Ignore) || (dayType == "holiday" && Text(p, "holidayWork") == Ignore))
                {
                    errors[at + "multiplier"] = [RestWorkIgnored];
                }

                // Whether time before the shift counts is asked only of a working day (the customer's choice, never assumed).
                if (dayType == "working" && Flag(r, "countBeforeShift") is null)
                {
                    errors[at + "countBeforeShift"] = [CountBeforeShiftRequired];
                }
                else if (dayType != "working" && r.TryGetProperty("countBeforeShift", out _))
                {
                    errors[at + "countBeforeShift"] = [CountBeforeShiftUnused];
                }
            }
        }

        return errors;
    }

    /// <summary>Worked minutes rounded by the policy (mode and step, ruling Q6); never below zero.</summary>
    public int Round(int minutes)
    {
        if (RoundingMode == RoundNone || RoundingStepMinutes is not { } step || step <= 1 || minutes <= 0)
        {
            return Math.Max(0, minutes);
        }

        var down = minutes / step * step;
        return RoundingMode switch
        {
            RoundUp => down == minutes ? minutes : down + step,
            RoundDown => down,
            _ => minutes - down >= step - (step / 2) ? down + step : down, // nearest; a half step goes up
        };
    }

    private static bool? Flag(JsonElement p, string name) => p.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;

    private static decimal? Decimal(JsonElement p, string name) => p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var v) ? v : null;

    private static int? Int(JsonElement p, string name) => p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static string? Text(JsonElement p, string name) => p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

/// <summary>
/// A late-coming or early-leaving rule of the attendance policy (slice S1.29 Build item 7, FR-HRM-ATT-13): <see cref="AllowedPerMonth"/> such days of a
/// calendar month carry no penalty; each further one gets <see cref="Action"/>: <c>leave</c> deducts <see cref="LeaveUnits"/> (in the leave type's unit)
/// of the leave type <see cref="LeaveTypeCode"/>, <c>pay_deduction</c> gives payroll an input (S1.35 works out the amount), <c>warning</c> only records.
/// </summary>
public sealed record PenaltyRule(int AllowedPerMonth, string Action, string? LeaveTypeCode, decimal? LeaveUnits)
{
    public const string Leave = "leave";
    public const string PayDeduction = "pay_deduction";
    public const string Warning = "warning";
}

/// <summary>
/// The overtime rule of one day type (slice S1.29 Build item 9, FR-HRM-ATT-12): the rate <see cref="Multiplier"/> payroll applies, the caps the approval
/// keeps to (none when not given: the customer enters any limit, AS-4), whether overtime is asked before the day (<c>pre</c>) or claimed after it
/// (<c>post</c>), the fewest minutes that count, whether time before a working day's shift counts, and whether approved overtime is paid or becomes
/// compensatory off (<see cref="CompOffDayMinutes"/> minutes earn one day; LVE-08).
/// </summary>
public sealed record OvertimeRule(
    decimal Multiplier, string Approval, int? DailyCapMinutes, int? MonthlyCapMinutes, int? MinimumMinutes, bool CountBeforeShift, string Compensation, int? CompOffDayMinutes,
    string? CompOffLeaveTypeCode)
{
    public const string Pre = "pre";
    public const string Post = "post";
    public const string Pay = "pay";
    public const string CompOff = "comp_off";
}

/// <summary>The <c>attendance</c> type's check beyond its schema, registered beside the manifest (S1.32a pattern).</summary>
public sealed class AttendancePolicyCheck : IPolicyParameterCheck
{
    public string PolicyType => AttendancePolicy.PolicyType;

    public IReadOnlyDictionary<string, string[]> Check(JsonElement parameters) => AttendancePolicy.Check(parameters);
}
