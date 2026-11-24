using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Policies;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// One season of a <c>seasonal_timing</c> policy (slice S1.28b Build item 8, FR-HRM-ATT-16): a Gregorian date range or a Hijri month, and what
/// changes in the shift on those days: new start and end times, or a percentage shortening of the day, and optionally the break minutes.
/// </summary>
public sealed record SeasonalRule(
    string Name,
    DateOnly? From,
    DateOnly? To,
    int? HijriMonth,
    TimeOnly? Start,
    TimeOnly? End,
    int? BreakMinutes,
    int? ShortenPercent)
{
    /// <summary>True when the day falls in this season; <paramref name="hijriOffsetDays"/> is the company's moon-sighting correction.</summary>
    public bool Covers(DateOnly date, int hijriOffsetDays)
    {
        if (HijriMonth is { } month)
        {
            return HijriDates.Of(date, hijriOffsetDays)?.Month == month;
        }

        return From is { } from && To is { } to && date >= from && date <= to;
    }
}

/// <summary>The shift times of one day after a season was applied.</summary>
public sealed record TimingResult(TimeOnly? Start, TimeOnly? End, int? BreakMinutes, bool CrossesMidnight);

/// <summary>
/// Reads and checks the parameters of the <c>seasonal_timing</c> policy type and applies a season to a shift's times. No date range, month or
/// percentage is stored in code: the customer enters its own Ramzan dates (AS-4, R-002). The first season in the list that covers a day wins.
/// </summary>
public static class SeasonalTiming
{
    public const string PolicyType = "seasonal_timing";

    public const string RangeRequired = "errors.hrm.seasonal.range_required";
    public const string RangeOrder = "errors.hrm.seasonal.range_order";
    public const string RangeAmbiguous = "errors.hrm.seasonal.range_ambiguous";
    public const string EffectRequired = "errors.hrm.seasonal.effect_required";
    public const string TimesPair = "errors.hrm.seasonal.times_pair";
    public const string TimesOrPercent = "errors.hrm.seasonal.times_or_percent";
    public const string TimeFormat = "errors.hrm.seasonal.time_format";
    public const string DateFormat = "errors.hrm.seasonal.date_format";
    public const string SameTimes = "errors.hrm.seasonal.same_times";

    /// <summary>Reads the seasons; the errors are keyed by path (<c>seasons[0].from</c>).</summary>
    public static (IReadOnlyList<SeasonalRule> Rules, IReadOnlyDictionary<string, string[]> Errors) Parse(JsonElement parameters)
    {
        var rules = new List<SeasonalRule>();
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Fail(string path, string key)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            if (!list.Contains(key))
            {
                list.Add(key);
            }
        }

        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in seasons.EnumerateArray())
            {
                var path = "seasons[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var from = Date(item, "from", path, Fail);
                var to = Date(item, "to", path, Fail);
                var month = Int(item, "hijriMonth");
                var start = Time(item, "startTime", path, Fail);
                var end = Time(item, "endTime", path, Fail);
                var brk = Int(item, "breakMinutes");
                var percent = Int(item, "shortenPercent");

                if (month is not null && (from is not null || to is not null))
                {
                    Fail(path + ".hijriMonth", RangeAmbiguous);
                }
                else if (month is null && (from is null || to is null))
                {
                    Fail(path + ".from", RangeRequired);
                }
                else if (from is { } f && to is { } t && t < f)
                {
                    Fail(path + ".to", RangeOrder);
                }

                if ((start is null) != (end is null))
                {
                    Fail(path + ".endTime", TimesPair);
                }
                else if (start is { } s && end is { } e && s == e)
                {
                    Fail(path + ".endTime", SameTimes);
                }

                if (start is not null && percent is not null)
                {
                    Fail(path + ".shortenPercent", TimesOrPercent);
                }

                if (start is null && percent is null && brk is null)
                {
                    Fail(path + ".startTime", EffectRequired);
                }

                rules.Add(new SeasonalRule(Text(item, "name") ?? string.Empty, from, to, month, start, end, brk, percent));
            }
        }

        return (rules, errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
    }

    /// <summary>The first season that covers the day, or null.</summary>
    public static SeasonalRule? Find(IReadOnlyList<SeasonalRule> rules, DateOnly date, int hijriOffsetDays)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.FirstOrDefault(r => r.Covers(date, hijriOffsetDays));
    }

    /// <summary>
    /// The shift's times after the season: new times replace the shift's, a percentage shortens the span between start and end (the end moves
    /// earlier; whole minutes, rounded down), a break value replaces the shift's break. A shift with no start and end (a flexible one) is
    /// changed only in its break.
    /// </summary>
    public static TimingResult Apply(SeasonalRule rule, TimeOnly? start, TimeOnly? end, bool crossesMidnight, int? breakMinutes)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var brk = rule.BreakMinutes ?? breakMinutes;
        if (rule.Start is { } s && rule.End is { } e)
        {
            return new TimingResult(s, e, brk, e <= s);
        }

        if (rule.ShortenPercent is { } percent && start is { } from && end is { } to)
        {
            var span = Span(from, to, crossesMidnight);
            var shortened = span * (100 - percent) / 100;
            var newEnd = from.AddMinutes(shortened);
            return new TimingResult(from, newEnd, brk, crossesMidnight && newEnd <= from);
        }

        return new TimingResult(start, end, brk, crossesMidnight);
    }

    private static int Span(TimeOnly from, TimeOnly to, bool crossesMidnight)
    {
        var minutes = (to.Hour * 60 + to.Minute) - (from.Hour * 60 + from.Minute);
        return minutes <= 0 && crossesMidnight ? minutes + 1440 : Math.Max(minutes, 0);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static DateOnly? Date(JsonElement e, string name, string path, Action<string, string> fail)
    {
        var text = Text(e, name);
        if (text is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        fail(path + "." + name, DateFormat);
        return null;
    }

    private static TimeOnly? Time(JsonElement e, string name, string path, Action<string, string> fail)
    {
        var text = Text(e, name);
        if (text is null)
        {
            return null;
        }

        if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t;
        }

        fail(path + "." + name, TimeFormat);
        return null;
    }
}

/// <summary>The policy framework's check of a <c>seasonal_timing</c> policy beyond its schema (registered with the type catalogue).</summary>
public sealed class SeasonalTimingCheck : IPolicyParameterCheck
{
    public string PolicyType => SeasonalTiming.PolicyType;

    public IReadOnlyDictionary<string, string[]> Check(JsonElement parameters) => SeasonalTiming.Parse(parameters).Errors;
}
