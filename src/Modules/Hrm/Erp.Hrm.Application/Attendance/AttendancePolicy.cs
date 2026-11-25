using System.Text.Json;
using Erp.Hrm.Application.Policies;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// The parameters of the <c>attendance</c> policy type (slice S1.29 Build item 1, FR-HRM-ATT-09, ruling Q1). Every number is the customer's
/// (no default, ruling Q2, AS-4, R-002): grace minutes for coming late and leaving early (BR-05), the lateness and early leaving that make a
/// half day (optional rules), the minutes worked for a full day (optional: each shift's own <c>min_full_day_minutes</c> otherwise) and below
/// which a day is an absence, the break mode, the rounding of worked minutes, the window around a shift in which a punch belongs to it
/// (also the overnight cut, ruling Q3) and whether work on a weekly off or a holiday is recorded.
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
    IReadOnlyDictionary<string, bool>? DutyCountsAsPresent = null)
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
            DutyKinds.Keys.ToDictionary(k => k, k => Flag(p, DutyFlagName(k)) ?? DutyKinds[k], StringComparer.Ordinal));
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

    private static int? Int(JsonElement p, string name) => p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static string? Text(JsonElement p, string name) => p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

/// <summary>The <c>attendance</c> type's check beyond its schema, registered beside the manifest (S1.32a pattern).</summary>
public sealed class AttendancePolicyCheck : IPolicyParameterCheck
{
    public string PolicyType => AttendancePolicy.PolicyType;

    public IReadOnlyDictionary<string, string[]> Check(JsonElement parameters) => AttendancePolicy.Check(parameters);
}
