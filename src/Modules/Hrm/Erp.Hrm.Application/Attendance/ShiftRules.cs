using System.Globalization;
using System.Text.Json;

namespace Erp.Hrm.Application.Attendance;

/// <summary>One part of a shift: a stretch of work or a break, as clock times (slice S1.28a, FR-HRM-ATT-06).</summary>
public sealed record ShiftSegment(TimeOnly Start, TimeOnly End, string Kind)
{
    public const string Work = "work";
    public const string Break = "break";
}

/// <summary>What the shift rules look at: the type, the times, the segments and the minutes that make a full day.</summary>
public sealed record ShiftSpec(
    string ShiftType,
    TimeOnly? Start,
    TimeOnly? End,
    TimeOnly? CoreStart,
    TimeOnly? CoreEnd,
    bool CrossesMidnight,
    IReadOnlyList<ShiftSegment> Segments,
    int MinFullDayMinutes);

/// <summary>
/// Pure rules of a shift (slice S1.28a Build item 1, FR-HRM-ATT-06). No statutory working-time number is coded (AS-4): the only limits are
/// those of a clock day. Times are kept as clock times; a shift that crosses midnight ends on the next day.
/// </summary>
public static class ShiftRules
{
    public const string TimesRequired = "errors.hrm.shift.times_required";
    public const string EndNotAfterStart = "errors.hrm.shift.end_not_after_start";
    public const string MidnightMismatch = "errors.hrm.shift.midnight_mismatch";
    public const string CoreRequired = "errors.hrm.shift.core_required";
    public const string CoreOutside = "errors.hrm.shift.core_outside";
    public const string SegmentsRequired = "errors.hrm.shift.segments_required";
    public const string SegmentsInvalid = "errors.hrm.shift.segments_invalid";
    public const string ContinuousLength = "errors.hrm.shift.continuous_length";
    public const string FullDayRange = "errors.hrm.shift.full_day_range";
    public const string TypeInvalid = "errors.hrm.shift.type_invalid";

    public const int DayMinutes = 1440;

    /// <summary>The length of the shift from its start to its end in minutes; an end equal to the start is a full 24 hours (continuous shifts only).</summary>
    public static int SpanMinutes(TimeOnly start, TimeOnly end, bool crossesMidnight)
    {
        var s = Minutes(start);
        var e = Minutes(end);
        var span = crossesMidnight ? e + DayMinutes - s : e - s;
        return crossesMidnight && span > DayMinutes ? span - DayMinutes : span;
    }

    /// <summary>
    /// Checks a shift and returns field names (as the API names them) with localization keys; empty when the shift is acceptable. Types:
    /// fixed and rotating need start and end; flexible adds core hours inside them; split needs at least two work segments; night and
    /// continuous need start and end (a continuous shift is exactly 24 hours, so its end equals its start).
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(ShiftSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string key) => errors[field] = [key];

        if (!Contracts.Attendance.ShiftTypes.All.Contains(spec.ShiftType, StringComparer.Ordinal))
        {
            Fail("shiftType", TypeInvalid);
            return errors;
        }

        if (spec.Start is not { } start || spec.End is not { } end)
        {
            Fail("startTime", TimesRequired);
            return errors;
        }

        var continuous = spec.ShiftType == Contracts.Attendance.ShiftTypes.Continuous;
        if (continuous)
        {
            if (start != end || !spec.CrossesMidnight && start != TimeOnly.MinValue)
            {
                Fail("endTime", ContinuousLength);
                return errors;
            }
        }
        else if (spec.CrossesMidnight ? end >= start : end <= start)
        {
            Fail(spec.CrossesMidnight ? "crossesMidnight" : "endTime", spec.CrossesMidnight ? MidnightMismatch : EndNotAfterStart);
            return errors;
        }

        var span = continuous ? DayMinutes : SpanMinutes(start, end, spec.CrossesMidnight);
        if (spec.MinFullDayMinutes < 1 || spec.MinFullDayMinutes > span)
        {
            Fail("minFullDayMinutes", FullDayRange);
        }

        if (spec.ShiftType == Contracts.Attendance.ShiftTypes.Flexible)
        {
            if (spec.CoreStart is not { } cs || spec.CoreEnd is not { } ce)
            {
                Fail("coreStart", CoreRequired);
            }
            else
            {
                var s = Offset(cs, start, spec.CrossesMidnight);
                var e = Offset(ce, start, spec.CrossesMidnight);
                if (s < 0 || e <= s || e > span)
                {
                    Fail("coreStart", CoreOutside);
                }
            }
        }

        var work = spec.Segments.Count(g => g.Kind == ShiftSegment.Work);
        if (spec.ShiftType == Contracts.Attendance.ShiftTypes.Split && work < 2)
        {
            Fail("segments", SegmentsRequired);
        }
        else if (!SegmentsFit(spec.Segments, start, span, spec.CrossesMidnight || continuous))
        {
            Fail("segments", SegmentsInvalid);
        }

        return errors;
    }

    /// <summary>Segments lie inside the shift, in order and without overlap.</summary>
    private static bool SegmentsFit(IReadOnlyList<ShiftSegment> segments, TimeOnly start, int span, bool wraps)
    {
        var previousEnd = 0;
        foreach (var g in segments)
        {
            var s = Offset(g.Start, start, wraps);
            var length = Offset(g.End, g.Start, wraps);
            var e = s + length;
            if (s < previousEnd || s < 0 || length <= 0 || e > span || g.Kind is not (ShiftSegment.Work or ShiftSegment.Break))
            {
                return false;
            }

            previousEnd = e;
        }

        return true;
    }

    /// <summary>Minutes from <paramref name="origin"/> to <paramref name="time"/>, wrapping past midnight only when the shift does.</summary>
    private static int Offset(TimeOnly time, TimeOnly origin, bool wraps)
    {
        var d = Minutes(time) - Minutes(origin);
        return d < 0 && wraps ? d + DayMinutes : d;
    }

    private static int Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>Reads the stored or posted segments (<c>[{"start":"09:00","end":"13:00","kind":"work"}]</c>); null when the shape is wrong.</summary>
    public static IReadOnlyList<ShiftSegment>? ParseSegments(JsonElement? json)
    {
        if (json is not { } element || element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<ShiftSegment>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("end", out var e) || e.ValueKind != JsonValueKind.String
                || !TimeOnly.TryParseExact(s.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !TimeOnly.TryParseExact(e.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                return null;
            }

            var kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : ShiftSegment.Work;
            list.Add(new ShiftSegment(start, end, kind));
        }

        return list;
    }

    /// <summary>The segments as the stored JSON.</summary>
    public static object[] SegmentsJson(IReadOnlyList<ShiftSegment> segments) =>
        [.. segments.Select(g => new { start = g.Start.ToString("HH:mm", CultureInfo.InvariantCulture), end = g.End.ToString("HH:mm", CultureInfo.InvariantCulture), kind = g.Kind })];
}
