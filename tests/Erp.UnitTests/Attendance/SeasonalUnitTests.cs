using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>
/// Hijri dates, optional-holiday texts and the rules of the <c>seasonal_timing</c> policy (slice S1.28b Build items 6 to 8): FR-HRM-ATT-15
/// (Umm al-Qura with the company's -1, 0, +1 correction) and FR-HRM-ATT-16 (Ramzan and seasonal timing by date range or Hijri month).
/// </summary>
public sealed partial class SeasonalUnitTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void FR_HRM_ATT_15_a_gregorian_day_has_its_umm_al_qura_date_and_the_offset_moves_it_by_one_day()
    {
        var start = new DateOnly(2025, 3, 1);
        Assert.Equal(new HijriDate(1446, 9, 1), HijriDates.Of(start));
        Assert.Equal(8, HijriDates.Of(start, -1)!.Value.Month);
        Assert.Equal(new HijriDate(1446, 9, 2), HijriDates.Of(start, 1));
        Assert.True(HijriDates.ValidOffset(-1) && HijriDates.ValidOffset(0) && HijriDates.ValidOffset(1));
        Assert.False(HijriDates.ValidOffset(2));
        Assert.False(HijriDates.ValidOffset(-2));
    }

    [Fact]
    public void FR_HRM_ATT_15_a_day_the_calendar_does_not_cover_has_no_hijri_date()
    {
        Assert.Null(HijriDates.Of(new DateOnly(1800, 1, 1)));
        Assert.Null(HijriDates.Of(new DateOnly(2100, 1, 1)));
    }

    [Fact]
    public void FR_HRM_ATT_16_a_season_by_dates_or_by_hijri_month_covers_only_its_own_days()
    {
        var (rules, errors) = SeasonalTiming.Parse(Json("""
            {"seasons":[{"name":"Summer","from":"2026-06-01","to":"2026-08-31","startTime":"07:00","endTime":"14:00"},{"name":"Ramzan","hijriMonth":9,"shortenPercent":25}]}
            """));
        Assert.Empty(errors);
        Assert.Equal(2, rules.Count);
        Assert.Equal("Summer", SeasonalTiming.Find(rules, new DateOnly(2026, 7, 4), 0)!.Name);
        Assert.Null(SeasonalTiming.Find(rules, new DateOnly(2026, 9, 1), 0));
        Assert.Equal("Ramzan", SeasonalTiming.Find(rules, new DateOnly(2025, 3, 10), 0)!.Name);
        Assert.Null(SeasonalTiming.Find(rules, new DateOnly(2025, 2, 28), 0));
        Assert.Equal("Ramzan", SeasonalTiming.Find(rules, new DateOnly(2025, 3, 1), 0)!.Name);
        Assert.Null(SeasonalTiming.Find(rules, new DateOnly(2025, 3, 1), -1)); // the moon was sighted a day later
        Assert.Equal("Ramzan", SeasonalTiming.Find(rules, new DateOnly(2025, 2, 28), 1)!.Name);
    }

    [Fact]
    public void FR_HRM_ATT_16_new_times_a_percentage_and_a_break_change_the_shift_without_inventing_numbers()
    {
        var nine = new TimeOnly(9, 0);
        var five = new TimeOnly(17, 0);
        var times = new SeasonalRule("Ramzan", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 30), null, new TimeOnly(10, 0), new TimeOnly(15, 0), 30, null);
        var applied = SeasonalTiming.Apply(times, nine, five, false, 60);
        Assert.Equal((new TimeOnly(10, 0), new TimeOnly(15, 0), 30), (applied.Start, applied.End, applied.BreakMinutes));

        var percent = new SeasonalRule("Ramzan", null, null, 9, null, null, null, 25);
        var shorter = SeasonalTiming.Apply(percent, nine, five, false, 60);
        Assert.Equal((nine, new TimeOnly(15, 0), 60), (shorter.Start, shorter.End, shorter.BreakMinutes));

        // A night shift keeps crossing midnight when it is shortened; a flexible shift without times changes only its break.
        var night = SeasonalTiming.Apply(percent, new TimeOnly(22, 0), new TimeOnly(6, 0), true, 0);
        Assert.Equal(new TimeOnly(4, 0), night.End);
        Assert.True(night.CrossesMidnight);
        var flexible = SeasonalTiming.Apply(new SeasonalRule("x", null, null, 9, null, null, 15, null), null, null, false, 60);
        Assert.Equal((null, null, 15), (flexible.Start, flexible.End, flexible.BreakMinutes));
    }

    [Fact]
    public void FR_HRM_ATT_16_incomplete_seasons_are_refused_with_a_key_and_the_path()
    {
        string[] Keys(string season) => SeasonalTiming.Parse(Json("{\"seasons\":[" + season + "]}")).Errors.Values.SelectMany(v => v).ToArray();
        Assert.Contains(SeasonalTiming.RangeRequired, Keys("""{"name":"a","shortenPercent":10}"""));
        Assert.Contains(SeasonalTiming.RangeOrder, Keys("""{"name":"a","from":"2026-03-10","to":"2026-03-01","shortenPercent":10}"""));
        Assert.Contains(SeasonalTiming.RangeAmbiguous, Keys("""{"name":"a","hijriMonth":9,"from":"2026-03-01","to":"2026-03-02","shortenPercent":10}"""));
        Assert.Contains(SeasonalTiming.TimesPair, Keys("""{"name":"a","hijriMonth":9,"startTime":"10:00"}"""));
        Assert.Contains(SeasonalTiming.TimesOrPercent, Keys("""{"name":"a","hijriMonth":9,"startTime":"10:00","endTime":"15:00","shortenPercent":10}"""));
        Assert.Contains(SeasonalTiming.EffectRequired, Keys("""{"name":"a","hijriMonth":9}"""));
        Assert.Contains(SeasonalTiming.TimeFormat, Keys("""{"name":"a","hijriMonth":9,"startTime":"10am","endTime":"15:00"}"""));
        Assert.Contains(SeasonalTiming.DateFormat, Keys("""{"name":"a","from":"1 March","to":"2026-03-02","shortenPercent":10}"""));
        Assert.Contains(SeasonalTiming.SameTimes, Keys("""{"name":"a","hijriMonth":9,"startTime":"10:00","endTime":"10:00"}"""));
        Assert.Contains("seasons[0].from", SeasonalTiming.Parse(Json("""{"seasons":[{"name":"a","shortenPercent":10}]}""")).Errors.Keys);
        Assert.Empty(SeasonalTiming.Parse(Json("{}")).Errors);
    }

    [Fact]
    public void FR_HRM_ATT_15_FR_HRM_ATT_16_the_holiday_and_timing_permissions_settings_and_texts_exist_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var declared = manifest.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.All(HolidayPermissions.HandWritten, c => Assert.Contains(c, declared));

        var shifts = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(shifts, "shifts.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(shifts, "shifts.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(HolidayPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        var policies = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Policies/Localization");
        var pen = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(policies, "policies.en.json")))!;
        var pur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(policies, "policies.ur.json")))!;
        Assert.Equal(pen.Keys.Order(StringComparer.Ordinal), pur.Keys.Order(StringComparer.Ordinal));
        foreach (var key in new[]
        {
            SeasonalTiming.RangeRequired, SeasonalTiming.RangeOrder, SeasonalTiming.RangeAmbiguous, SeasonalTiming.EffectRequired, SeasonalTiming.TimesPair,
            SeasonalTiming.TimesOrPercent, SeasonalTiming.TimeFormat, SeasonalTiming.DateFormat, SeasonalTiming.SameTimes,
        })
        {
            Assert.True(pen.ContainsKey(key), key);
        }

        var sources = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("Generated", StringComparison.Ordinal));
        var used = sources.SelectMany(f => HolidayKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));

        var settings = File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Settings/settings.yaml"));
        Assert.Contains("hrm.att.hijri_offset_days", settings, StringComparison.Ordinal);
        Assert.Contains("hrm.att.optional_holidays_per_year", settings, StringComparison.Ordinal);
        Assert.Contains("hrm.att.optional_holiday_cutoff_days", settings, StringComparison.Ordinal);
    }

    [GeneratedRegex("\"(errors\\.hrm\\.holiday\\.[a-z_]+)\"")]
    private static partial Regex HolidayKey();
}
