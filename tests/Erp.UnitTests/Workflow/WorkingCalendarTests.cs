using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Kernel.Domain.Workflow;

namespace Erp.UnitTests.Workflow;

/// <summary>
/// Approval time limits in working days (FR-XM-32; slice S1.10 rulings Q9 and Q16, S1.10b rulings P3 and P4): the day a task
/// arrives does not count; it is late from the start of the day after its N-th working day, in the company's time zone.
/// </summary>
public sealed partial class WorkingCalendarTests
{
    private static readonly TimeZoneInfo Karachi = WorkingCalendar.Zone("Asia/Karachi");
    private static readonly HashSet<DayOfWeek> Weekend = [DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Erp.sln not found");
        }
    }

    [Fact]
    public void FR_XM_32_P3_the_ruling_example_thursday_with_weekend_and_monday_holiday_is_late_from_wednesday_midnight()
    {
        var arrived = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.FromHours(5)); // Thursday 15:00 in Karachi
        var due = WorkingCalendar.DueAt(arrived, 2, Karachi, Weekend, new HashSet<DateOnly> { new(2026, 11, 23) });
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.FromHours(5)), due); // Friday and Tuesday count
        Assert.Equal(TimeSpan.Zero, due.Offset); // stored as UTC
    }

    [Fact]
    public void FR_XM_32_Q9_without_holidays_two_working_days_from_thursday_end_on_monday()
    {
        var arrived = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(5)), WorkingCalendar.DueAt(arrived, 2, Karachi, Weekend, new HashSet<DateOnly>()));
    }

    [Fact]
    public void FR_XM_32_P3_the_day_is_the_company_time_zone_day_not_the_utc_day()
    {
        // 20:00 UTC on Thursday is already Friday 01:00 in Karachi: Friday is the arrival day and does not count.
        var arrived = new DateTimeOffset(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.FromHours(5)), WorkingCalendar.DueAt(arrived, 2, Karachi, Weekend, new HashSet<DateOnly>()));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), WorkingCalendar.DueAt(arrived, 2, TimeZoneInfo.Utc, Weekend, new HashSet<DateOnly>()));
    }

    [Fact]
    public void FR_XM_32_Q16_off_days_are_the_setting_value_sunday_only_counts_saturday()
    {
        var arrived = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(5)); // Friday
        var due = WorkingCalendar.DueAt(arrived, 2, Karachi, WorkingCalendar.ParseOffDays(["sunday"]), new HashSet<DateOnly>());
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(5)), due); // Saturday and Monday count
    }

    [Fact]
    public void FR_XM_32_a_week_of_only_off_days_still_gives_a_time_limit()
    {
        var all = WorkingCalendar.ParseOffDays(WorkingCalendar.WeekdayNames.Keys);
        Assert.Equal(7, all.Count);
        var arrived = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.FromHours(5)), WorkingCalendar.DueAt(arrived, 2, Karachi, all, new HashSet<DateOnly>()));
    }

    [Fact]
    public void FR_XM_32_P4_only_public_and_religious_holidays_stop_the_clock_and_unknown_day_names_are_ignored()
    {
        Assert.Equal(["public", "religious"], WorkingCalendar.CountedHolidayKinds);
        Assert.Equal([DayOfWeek.Friday], WorkingCalendar.ParseOffDays(["friday", "funday"]));
    }

    [Fact]
    public void FR_XM_32_a_day_that_starts_after_a_daylight_saving_jump_starts_at_its_first_existing_minute()
    {
        // Asuncion (Paraguay) moved its clocks forward at midnight; the day starts at 01:00 local time.
        var zone = WorkingCalendar.Zone("America/Asuncion");
        var start = WorkingCalendar.StartOfDay(new DateOnly(2023, 10, 1), zone);
        Assert.Equal(new DateOnly(2023, 10, 1), WorkingCalendar.LocalDate(start, zone));
        Assert.Equal(new DateTimeOffset(2023, 10, 1, 4, 0, 0, TimeSpan.Zero), start); // 01:00 local, the first minute of the day
        Assert.Equal(new DateOnly(2023, 9, 30), WorkingCalendar.LocalDate(start.AddMinutes(-1), zone));
        Assert.True(start < WorkingCalendar.StartOfDay(new DateOnly(2023, 10, 2), zone));
        Assert.Equal(TimeZoneInfo.Utc, WorkingCalendar.Zone("Not/AZone"));
    }

    [GeneratedRegex("\"((?:errors\\.delegation|workflow\\.history)\\.[a-z_]+)\"")]
    private static partial Regex DelegationKey();

    [Fact]
    public void S110b_every_delegation_message_key_has_english_and_urdu_text()
    {
        var folder = Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Workflow/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "workflow.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "workflow.ur.json")))!;
        var used = Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => DelegationKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(used.Count >= 14, $"only {used.Count} keys found");
        Assert.DoesNotContain(used, k => !en.ContainsKey(k) || !ur.ContainsKey(k));

        // Every history event the engine writes has a text (the inbox screen shows the history).
        var events = Directory.GetFiles(Path.Combine(Root, "src/Platform/Erp.Kernel.Infrastructure/Workflow"), "WorkflowEngine*.cs")
            .SelectMany(f => HistoryEvent().Matches(File.ReadAllText(f)).Select(m => "workflow.history." + m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(events.Count > 15, $"only {events.Count} events found");
        Assert.DoesNotContain(events, k => !en.ContainsKey(k) || !ur.ContainsKey(k));
    }

    [GeneratedRegex("HistoryAsync\\([^,]+, [^,]+, \"([a-z_]+)\"")]
    private static partial Regex HistoryEvent();
}
