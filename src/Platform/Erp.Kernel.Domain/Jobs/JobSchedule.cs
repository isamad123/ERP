using System.Globalization;
using System.Text.Json;

namespace Erp.Kernel.Domain.Jobs;

/// <summary>
/// When a job is due (FR-KRN-JOB-01; slice S1.11 ruling Q3): simple choices instead of cron text, in the customer's time
/// zone. Stored as JSON in <c>job.definitions.schedule</c>:
/// <list type="bullet">
/// <item><c>{"every_minutes": 5}</c>: every N minutes (1 to 1440), counted from midnight UTC;</item>
/// <item><c>{"hourly": {"minute": 10}}</c>: every hour at that minute;</item>
/// <item><c>{"daily": {"at": "02:30"}}</c>: every day at a local time;</item>
/// <item><c>{"weekly": {"days": ["monday", "thursday"], "at": "07:00"}}</c>;</item>
/// <item><c>{"monthly": {"day": 1, "at": "03:00"}}</c> or <c>{"monthly": {"day": "last", "at": "23:00"}}</c>: a day past the
/// month's end is its last day.</item>
/// </list>
/// Optional <c>"working_days_only": true</c> skips slots on a weekly off day or holiday, and <c>"calendar_company_id"</c> names
/// the company whose calendar is used (ruling P5; without one: the customer's weekly off days, no holidays).
/// A local time that does not exist (a clock change) has no slot that day.
/// </summary>
public sealed record JobSchedule(string Kind, int EveryMinutes, TimeOnly At, IReadOnlySet<DayOfWeek> Days, int? DayOfMonth, bool WorkingDaysOnly, Guid? CalendarCompanyId)
{
    public const string EveryMinutesKind = "every_minutes";
    public const string HourlyKind = "hourly";
    public const string DailyKind = "daily";
    public const string WeeklyKind = "weekly";
    public const string MonthlyKind = "monthly";

    /// <summary>The days searched for a slot before giving up (a calendar with no working day).</summary>
    private const int MaxDaysSearched = 800;

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.Ordinal)
    {
        ["monday"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday,
    };

    /// <summary>Reads a stored schedule. Throws <see cref="FormatException"/> with a reason when it is not one of the shapes.</summary>
    public static JobSchedule Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("a schedule is an object");
        }

        var working = json.TryGetProperty("working_days_only", out var w) && w.ValueKind == JsonValueKind.True;
        Guid? company = json.TryGetProperty("calendar_company_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetGuid() : null;
        var kinds = json.EnumerateObject().Select(p => p.Name).Where(n => n is EveryMinutesKind or HourlyKind or DailyKind or WeeklyKind or MonthlyKind).ToList();
        var unknown = json.EnumerateObject().Select(p => p.Name).Where(n => !kinds.Contains(n) && n is not "working_days_only" and not "calendar_company_id").ToList();
        if (kinds.Count != 1 || unknown.Count > 0)
        {
            throw new FormatException("a schedule has exactly one of every_minutes, hourly, daily, weekly or monthly" + (unknown.Count > 0 ? "; unknown: " + string.Join(", ", unknown) : string.Empty));
        }

        var kind = kinds[0];
        var body = json.GetProperty(kind);
        switch (kind)
        {
            case EveryMinutesKind:
                var every = body.ValueKind == JsonValueKind.Number && body.TryGetInt32(out var n) ? n : 0;
                if (every is < 1 or > 1440)
                {
                    throw new FormatException("every_minutes is between 1 and 1440");
                }

                return new JobSchedule(kind, every, TimeOnly.MinValue, new HashSet<DayOfWeek>(), null, working, company);
            case HourlyKind:
                var minute = body.TryGetProperty("minute", out var m) && m.TryGetInt32(out var mm) ? mm : -1;
                if (minute is < 0 or > 59)
                {
                    throw new FormatException("hourly.minute is between 0 and 59");
                }

                return new JobSchedule(kind, 60, new TimeOnly(0, minute), new HashSet<DayOfWeek>(), null, working, company);
            case DailyKind:
                return new JobSchedule(kind, 0, ReadAt(body), new HashSet<DayOfWeek>(), null, working, company);
            case WeeklyKind:
                var days = body.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(x => WeekdayNames.TryGetValue(x.GetString() ?? string.Empty, out var day) ? day : throw new FormatException("unknown weekday " + x.GetString())).ToHashSet()
                    : [];
                if (days.Count == 0)
                {
                    throw new FormatException("weekly.days names at least one weekday");
                }

                return new JobSchedule(kind, 0, ReadAt(body), days, null, working, company);
            default:
                int day;
                if (body.TryGetProperty("day", out var dm) && dm.ValueKind == JsonValueKind.String && dm.GetString() == "last")
                {
                    day = 31;
                }
                else if (body.TryGetProperty("day", out dm) && dm.TryGetInt32(out var dd) && dd is >= 1 and <= 31)
                {
                    day = dd;
                }
                else
                {
                    throw new FormatException("monthly.day is 1 to 31 or \"last\"");
                }

                return new JobSchedule(kind, 0, ReadAt(body), new HashSet<DayOfWeek>(), day, working, company);
        }
    }

    private static TimeOnly ReadAt(JsonElement body) =>
        body.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String
            && TimeOnly.TryParseExact(at.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new FormatException("\"at\" is a local time HH:mm");

    /// <summary>
    /// The latest slot at or before <paramref name="now"/> (UTC), or null when none falls in the searched range. A run
    /// is planned for this slot only (ruling P7: after a pause, only the most recent missed slot runs).
    /// </summary>
    public DateTimeOffset? Latest(DateTimeOffset now, TimeZoneInfo zone, Func<DateOnly, bool> isWorkingDay)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(isWorkingDay);
        if (Kind == EveryMinutesKind)
        {
            var minutes = (long)Math.Floor((now.UtcDateTime - DateTime.UnixEpoch).TotalMinutes);
            for (var slot = minutes - (minutes % EveryMinutes); slot > minutes - MaxDaysSearched * 1440L; slot -= EveryMinutes)
            {
                var at = new DateTimeOffset(DateTime.UnixEpoch.AddMinutes(slot), TimeSpan.Zero);
                if (Allowed(at, zone, isWorkingDay))
                {
                    return at;
                }
            }

            return null;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        if (Kind == HourlyKind)
        {
            var hour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, At.Minute, 0, DateTimeKind.Unspecified);
            for (var i = 0; i < MaxDaysSearched * 24; i++, hour = hour.AddHours(-1))
            {
                if (ToUtc(hour, zone) is { } at && at <= now && Allowed(at, zone, isWorkingDay))
                {
                    return at;
                }
            }

            return null;
        }

        var day = DateOnly.FromDateTime(localNow.DateTime);
        for (var i = 0; i < MaxDaysSearched; i++, day = day.AddDays(-1))
        {
            if (SlotOn(day, zone, isWorkingDay) is { } at && at <= now)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>The first slot after <paramref name="after"/> (UTC), for the job centre's "next run", or null when none is found.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone, Func<DateOnly, bool> isWorkingDay)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(isWorkingDay);
        if (Kind == EveryMinutesKind)
        {
            var minutes = (long)Math.Floor((after.UtcDateTime - DateTime.UnixEpoch).TotalMinutes);
            for (var slot = minutes - (minutes % EveryMinutes) + EveryMinutes; slot < minutes + MaxDaysSearched * 1440L; slot += EveryMinutes)
            {
                var at = new DateTimeOffset(DateTime.UnixEpoch.AddMinutes(slot), TimeSpan.Zero);
                if (at > after && Allowed(at, zone, isWorkingDay))
                {
                    return at;
                }
            }

            return null;
        }

        var local = TimeZoneInfo.ConvertTime(after, zone);
        if (Kind == HourlyKind)
        {
            var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, At.Minute, 0, DateTimeKind.Unspecified);
            for (var i = 0; i < MaxDaysSearched * 24; i++, hour = hour.AddHours(1))
            {
                if (ToUtc(hour, zone) is { } at && at > after && Allowed(at, zone, isWorkingDay))
                {
                    return at;
                }
            }

            return null;
        }

        var day = DateOnly.FromDateTime(local.DateTime);
        for (var i = 0; i < MaxDaysSearched; i++, day = day.AddDays(1))
        {
            if (SlotOn(day, zone, isWorkingDay) is { } at && at > after)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>The slot on a local day for daily, weekly and monthly schedules, or null when the day has none.</summary>
    private DateTimeOffset? SlotOn(DateOnly day, TimeZoneInfo zone, Func<DateOnly, bool> isWorkingDay)
    {
        var due = Kind switch
        {
            DailyKind => true,
            WeeklyKind => Days.Contains(day.DayOfWeek),
            _ => day.Day == Math.Min(DayOfMonth ?? 1, DateTime.DaysInMonth(day.Year, day.Month)),
        };
        if (!due || (WorkingDaysOnly && !isWorkingDay(day)))
        {
            return null;
        }

        return ToUtc(day.ToDateTime(At, DateTimeKind.Unspecified), zone);
    }

    private bool Allowed(DateTimeOffset at, TimeZoneInfo zone, Func<DateOnly, bool> isWorkingDay) =>
        !WorkingDaysOnly || isWorkingDay(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime));

    private static DateTimeOffset? ToUtc(DateTime local, TimeZoneInfo zone) =>
        zone.IsInvalidTime(local) ? null : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
}
