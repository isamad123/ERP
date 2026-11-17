namespace Erp.Kernel.Domain.Workflow;

/// <summary>
/// Time limits of approval tasks in working days (FR-XM-32; slice S1.10 ruling Q9, S1.10b rulings P3 and P4). Whole days
/// in the company's time zone: the day a task arrives does not count; the task is late from the start of the day after
/// its N-th working day. Weekly off days and the company's holidays are not working days.
/// Example: arrives Thursday 15:00, two days, Saturday and Sunday off, Monday a holiday: Friday and Tuesday count, so the
/// task is late from Wednesday 00:00.
/// </summary>
public static class WorkingCalendar
{
    /// <summary>The working days after which nothing more is counted (a guard against a calendar with no working day).</summary>
    public const int MaxDaysSearched = 3660;

    /// <summary>Weekday names of the <c>org.weekly_off_days</c> setting.</summary>
    public static IReadOnlyDictionary<string, DayOfWeek> WeekdayNames { get; } = new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
    {
        ["monday"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday,
    };

    /// <summary>The holiday kinds that stop the clock (ruling P4): optional and restricted holidays are taken by some staff only.</summary>
    public static IReadOnlyList<string> CountedHolidayKinds { get; } = ["public", "religious"];

    /// <summary>
    /// The moment (UTC) a task that arrived at <paramref name="arrived"/> becomes late after <paramref name="workingDays"/>
    /// working days. A week of only off days is read as no off days, so a task always gets a limit.
    /// </summary>
    public static DateTimeOffset DueAt(DateTimeOffset arrived, int workingDays, TimeZoneInfo zone, IReadOnlySet<DayOfWeek> offDays, IReadOnlySet<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(offDays);
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentOutOfRangeException.ThrowIfLessThan(workingDays, 1);
        var off = offDays.Count >= 7 ? new HashSet<DayOfWeek>() : offDays;
        var day = LocalDate(arrived, zone);
        var counted = 0;
        for (var i = 0; i < MaxDaysSearched && counted < workingDays; i++)
        {
            day = day.AddDays(1);
            if (!off.Contains(day.DayOfWeek) && !holidays.Contains(day))
            {
                counted++;
            }
        }

        return StartOfDay(day.AddDays(1), zone);
    }

    /// <summary>The calendar date of a moment in the time zone.</summary>
    public static DateOnly LocalDate(DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);
    }

    /// <summary>
    /// The first moment (UTC) of a calendar date in the time zone. Where the clock skips midnight (a daylight-saving change),
    /// the day starts at the first minute that exists; where midnight happens twice, at the first. The answer is checked
    /// by converting back, because the platform's "invalid time" answer is not reliable at the exact moment of a change.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var candidates = new[] { local.AddDays(-1), local, local.AddDays(1) }
            .Select(zone.GetUtcOffset).Distinct()
            .Select(offset => new DateTimeOffset(local, offset).ToUniversalTime())
            .Order().ToList();
        foreach (var candidate in candidates)
        {
            if (TimeZoneInfo.ConvertTime(candidate, zone).DateTime == local)
            {
                return candidate;
            }
        }

        for (var moment = candidates[0]; moment < candidates[0].AddHours(26); moment = moment.AddMinutes(1))
        {
            if (LocalDate(moment, zone) == date)
            {
                return moment;
            }
        }

        return candidates[0];
    }

    /// <summary>Reads the off days of the setting; unknown names are ignored.</summary>
    public static HashSet<DayOfWeek> ParseOffDays(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var result = new HashSet<DayOfWeek>();
        foreach (var name in names)
        {
            if (WeekdayNames.TryGetValue(name, out var day))
            {
                result.Add(day);
            }
        }

        return result;
    }

    /// <summary>The time zone of an IANA (or Windows) id; UTC when the id is unknown.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // fall through to UTC
            }
        }

        return TimeZoneInfo.Utc;
    }
}
