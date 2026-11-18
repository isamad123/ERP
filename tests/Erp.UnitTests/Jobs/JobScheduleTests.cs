using System.Text.Json;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Jobs;
using Erp.Kernel.Infrastructure.Jobs;

namespace Erp.UnitTests.Jobs;

/// <summary>
/// Schedules of slice S1.11 (ruling Q3: simple choices in the customer's time zone, working days only, P5, P7) and the job
/// manifest check (Build item 1, FR-KRN-JOB-01).
/// </summary>
public sealed class JobScheduleTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private static JobSchedule Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JobSchedule.Parse(doc.RootElement);
    }

    private static bool Always(DateOnly day) => true;

    private static DateTimeOffset Utc(int y, int m, int d, int h, int min) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void S111a_FR_KRN_JOB_01_Q3_every_n_minutes_and_hourly_slots()
    {
        var every5 = Parse("""{"every_minutes": 5}""");
        Assert.Equal(Utc(2026, 11, 18, 10, 5), every5.Latest(Utc(2026, 11, 18, 10, 7), Karachi, Always));
        Assert.Equal(Utc(2026, 11, 18, 10, 10), every5.Next(Utc(2026, 11, 18, 10, 7), Karachi, Always));
        var hourly = Parse("""{"hourly": {"minute": 10}}""");
        Assert.Equal(Utc(2026, 11, 18, 9, 10), hourly.Latest(Utc(2026, 11, 18, 10, 5), Karachi, Always));
        Assert.Equal(Utc(2026, 11, 18, 10, 10), hourly.Next(Utc(2026, 11, 18, 10, 5), Karachi, Always));
    }

    [Fact]
    public void S111a_Q3_daily_weekly_and_monthly_slots_are_in_the_customers_time_zone()
    {
        // 02:30 in Karachi is 21:30 UTC the day before.
        var daily = Parse("""{"daily": {"at": "02:30"}}""");
        Assert.Equal(Utc(2026, 11, 17, 21, 30), daily.Latest(Utc(2026, 11, 18, 10, 0), Karachi, Always));
        Assert.Equal(Utc(2026, 11, 18, 21, 30), daily.Next(Utc(2026, 11, 18, 10, 0), Karachi, Always));

        // 2026-11-18 is a Wednesday.
        var weekly = Parse("""{"weekly": {"days": ["monday", "friday"], "at": "07:00"}}""");
        Assert.Equal(Utc(2026, 11, 16, 2, 0), weekly.Latest(Utc(2026, 11, 18, 10, 0), Karachi, Always));
        Assert.Equal(Utc(2026, 11, 20, 2, 0), weekly.Next(Utc(2026, 11, 18, 10, 0), Karachi, Always));

        var last = Parse("""{"monthly": {"day": "last", "at": "23:00"}}""");
        Assert.Equal(Utc(2027, 2, 28, 18, 0), last.Next(Utc(2027, 2, 1, 0, 0), Karachi, Always));
        var the31st = Parse("""{"monthly": {"day": 31, "at": "00:00"}}""");
        Assert.Equal(Utc(2026, 11, 29, 19, 0), the31st.Next(Utc(2026, 11, 18, 0, 0), Karachi, Always)); // 30 November 00:00 Karachi
    }

    [Fact]
    public void S111a_Q3_working_days_only_skips_off_days_and_holidays()
    {
        var off = new DateOnly(2026, 11, 19); // Thursday made a holiday
        bool Working(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != off;
        var daily = Parse("""{"daily": {"at": "09:00"}, "working_days_only": true}""");
        Assert.Equal(Utc(2026, 11, 20, 4, 0), daily.Next(Utc(2026, 11, 18, 10, 0), Karachi, Working)); // Friday
        Assert.Equal(Utc(2026, 11, 20, 4, 0), daily.Latest(Utc(2026, 11, 22, 10, 0), Karachi, Working)); // Sunday: Friday's slot
    }

    [Fact]
    public void S111a_P7_after_a_long_pause_only_the_latest_slot_is_due()
    {
        var hourly = Parse("""{"hourly": {"minute": 0}}""");
        Assert.Equal(Utc(2026, 11, 18, 10, 0), hourly.Latest(Utc(2026, 11, 18, 10, 30), Karachi, Always));
    }

    [Theory]
    [InlineData("""{"every_minutes": 0}""")]
    [InlineData("""{"hourly": {"minute": 60}}""")]
    [InlineData("""{"daily": {"at": "25:00"}}""")]
    [InlineData("""{"weekly": {"days": [], "at": "07:00"}}""")]
    [InlineData("""{"monthly": {"day": 32, "at": "07:00"}}""")]
    [InlineData("""{"daily": {"at": "07:00"}, "hourly": {"minute": 1}}""")]
    [InlineData("""{"cron": "* * * * *"}""")]
    public void S111a_Q3_schedules_that_are_not_one_of_the_simple_choices_are_refused(string json) =>
        Assert.Throws<FormatException>(() => Parse(json));

    [Fact]
    public void S111a_FR_KRN_JOB_01_the_kernel_manifest_is_valid_and_every_job_has_its_handler_and_texts_in_english_and_urdu()
    {
        var specs = JobManifest.Parse(JobManifest.KernelJson);
        Assert.Equal(10, specs.Count); // 8 of S1.11a, kernel.email_send and kernel.email_bounces of S1.11b2
        Assert.All(specs, s => Assert.Empty(JobManifest.Check(s)));
        var handlers = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddKernelJobHandlers()
            .Select(d => d.ImplementationType!).Select(t => t.Name).ToList();
        Assert.Equal(10, handlers.Count);

        // Every permission a manifest names exists in the kernel's permission catalogue (R-009).
        var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Platform/Erp.Kernel.Infrastructure/Access/permissions.g.json")))
            .RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()!)
            .Concat(JobPermissions.All.Select(p => p.Code)).ToHashSet(StringComparer.Ordinal);
        Assert.All(specs.SelectMany(s => s.Permissions.Append(s.RequestPermission!)), p => Assert.Contains(p, catalogue));
    }

    [Fact]
    public void S111a_FR_KRN_JOB_01_the_manifest_check_names_each_problem()
    {
        var bad = JobManifest.Parse("""
            {"module": "kernel", "jobs": [{"code": "other.job", "name": {"en": "x"}, "description": {"en": "x", "ur": "x"},
              "runs_as": "requester", "schedule": {"every_minutes": 1}, "retry": {"max_attempts": 0}, "timeout_seconds": 0,
              "runs_in": ["sometimes"], "entitlement": ""}]}
            """).Single();
        var problems = JobManifest.Check(bad);
        Assert.Contains(problems, p => p.StartsWith("code", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("name needs English and Urdu", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("cannot have a schedule", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("retry", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("timeout_seconds", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("runs_in", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("entitlement", StringComparison.Ordinal));
    }

    [Fact]
    public void S111a_the_job_texts_exist_in_english_and_urdu()
    {
        var folder = Path.Combine(RepositoryRoot(), "src/Platform/Erp.Kernel.Contracts/Jobs/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "jobs.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "jobs.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        foreach (var key in new[] { JobErrors.TimeLimit, JobErrors.WorkerStopped, JobErrors.RequesterLostPermission })
        {
            Assert.Contains(key, en.Keys);
        }

        Assert.All(JobPermissions.All, p => Assert.Contains(p.DescriptionKey, en.Keys));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
