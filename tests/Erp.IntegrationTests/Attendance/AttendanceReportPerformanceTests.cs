#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// Query performance of the attendance reports (slice S1.30b Build item 8; FR-HRM-ATT-18, ATT-19, NFR page target of 2 seconds): a customer of its own with
/// 10,000 employees, a month of day results for each of them (300,000 rows), five days of punches and an open exception each, written as copies of rows the real
/// engine made, then every report asked for as an administrator (data scope all) the way the screen asks: the page, the totals and the count. Each report
/// answers in under two seconds.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceReportPerformanceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const int Employees = 10_000;
    private const string Reports = "/api/hrm/attendance/reports";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForLoadAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task S130b_FR_HRM_ATT_19_FR_HRM_ATT_18_every_report_and_the_dashboard_answer_in_under_two_seconds_for_10000_employees_and_a_month_of_days()
    {
        var o = await ReportOrg.CreateAsync(_h);
        var shift = await o.ShiftAsync("09:00", "18:00");
        var day = o.Today.AddDays(-4);
        var (full, _) = await o.HireAsync();
        var (forgot, _) = await o.HireAsync();
        await o.RosterAsync(full, shift, day);
        await o.RosterAsync(forgot, shift, day);
        await o.PunchAsync(full, (ReportOrg.At(day, "09:02"), "in"), (ReportOrg.At(day, "18:03"), "out"));
        await o.PunchAsync(forgot, (ReportOrg.At(day, "09:01"), "in"));
        await o.ProcessAsync(full, day, day);
        await o.ProcessAsync(forgot, day, day);

        var first = new DateOnly(day.Year, day.Month, 1);
        var days = DateTime.DaysInMonth(day.Year, day.Month);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        await CloneAsync(o, full, forgot, first, days, tag);
        var loaded = stopwatch.Elapsed;

        var (fromIso, toIso) = (ReportOrg.Iso(first), ReportOrg.Iso(first.AddDays(days - 1)));
        var filters = $"companyId={o.Company}&from={fromIso}&to={toIso}";
        var timings = new List<string> { $"load {loaded.TotalSeconds:0.0}s" };
        foreach (var code in new[] { "muster_daily", "muster_monthly", "absenteeism", "late_early", "punch_log", "device_sync", "exception_ageing" })
        {
            await _h.GetAsync(o.Hr, $"{Reports}/{code}?{filters}&limit=1", Ct); // the first call of a query shape compiles it
            var watch = Stopwatch.StartNew();
            var answer = await _h.GetAsync(o.Hr, $"{Reports}/{code}?{filters}&limit=100", Ct);
            watch.Stop();
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
            timings.Add($"{code} {watch.ElapsedMilliseconds} ms ({answer.Json.GetProperty("total").GetInt64()} rows)");
            Assert.True(watch.ElapsedMilliseconds < 2000, $"{code} took {watch.ElapsedMilliseconds} ms for {Employees} employees; {string.Join("; ", timings)}");
        }

        await _h.GetAsync(o.Hr, $"/api/hrm/attendance/dashboard?companyId={o.Company}", Ct);
        var dashboard = Stopwatch.StartNew();
        var counts = await _h.GetAsync(o.Hr, $"/api/hrm/attendance/dashboard?companyId={o.Company}", Ct);
        dashboard.Stop();
        Assert.Equal(HttpStatusCode.OK, counts.Status);
        Assert.True(counts.Json.GetProperty("totals").GetProperty("headcount").GetInt32() >= Employees);
        timings.Add($"dashboard {dashboard.ElapsedMilliseconds} ms");
        Assert.True(dashboard.ElapsedMilliseconds < 2000, $"the dashboard took {dashboard.ElapsedMilliseconds} ms; {string.Join("; ", timings)}");
        TestContext.Current.SendDiagnosticMessage("S1.30b load: " + string.Join("; ", timings));
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "s130b-load.txt"), string.Join("\n", timings), Ct);
    }

    /// <summary>
    /// Copies of real rows: 10,000 people with a person, employment and assignment each, a result for each day of the month, five days of punches and an open
    /// exception. The rows are copies of those the engine and the services wrote (so every column is as the application writes it), with new keys.
    /// </summary>
    private async Task CloneAsync(ReportOrg o, Guid full, Guid forgot, DateOnly first, int days, string tag)
    {
        var firstIso = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var employment = $"(select id from hrm.employments where id = '{full}')";
        var statements = new[]
        {
            $"""
            insert into hrm.persons
            select r.*
            from hrm.persons t, generate_series(1, {Employees}) g,
                 lateral jsonb_populate_record(null::hrm.persons, to_jsonb(t) || jsonb_build_object('id', md5('{tag}p' || g)::uuid, 'person_no', 'LP{tag}' || g)) r
            where t.id = (select person_id from hrm.employments where id = '{full}')
            """,
            $"""
            insert into hrm.employments
            select r.*
            from hrm.employments t, generate_series(1, {Employees}) g,
                 lateral jsonb_populate_record(null::hrm.employments, to_jsonb(t) || jsonb_build_object('id', md5('{tag}e' || g)::uuid, 'person_id', md5('{tag}p' || g)::uuid, 'employee_no', 'LE{tag}' || g, 'user_ref', null, 'previous_employment_id', null)) r
            where t.id = '{full}'
            """,
            $"""
            insert into hrm.employment_assignments
            select r.*
            from hrm.employment_assignments t, generate_series(1, {Employees}) g,
                 lateral jsonb_populate_record(null::hrm.employment_assignments, to_jsonb(t) || jsonb_build_object('id', md5('{tag}a' || g)::uuid, 'employment_id', md5('{tag}e' || g)::uuid)) r
            where t.id = (select id from hrm.employment_assignments where employment_id = '{full}' order by valid_from limit 1)
            """,
            $"""
            insert into hrm.attendance_days
            select x.*
            from hrm.attendance_days t, generate_series(1, {Employees}) g, generate_series(0, {days - 1}) d,
                 lateral (select (array['present','present','present','late','absent','half_day','on_leave','present','weekly_off','present'])[1 + ((g + d) % 10)] as result) r,
                 lateral jsonb_populate_record(null::hrm.attendance_days, to_jsonb(t) || jsonb_build_object(
                    'id', md5('{tag}d' || g || '-' || d)::uuid, 'employment_id', md5('{tag}e' || g)::uuid, 'work_date', date '{firstIso}' + d,
                    'result', r.result, 'late_minutes', case when r.result = 'late' then 15 else 0 end)) x
            where t.employment_id = '{full}' and t.superseded_at is null
            """,
            $"""
            insert into hrm.attendance_events
            select x.*
            from hrm.attendance_events t, generate_series(1, {Employees}) g, generate_series(0, 4) d,
                 lateral jsonb_populate_record(null::hrm.attendance_events, to_jsonb(t) || jsonb_build_object(
                    'id', md5('{tag}v' || g || '-' || d)::uuid, 'employment_id', md5('{tag}e' || g)::uuid, 'event_date', date '{firstIso}' + d,
                    'event_at', to_char(date '{firstIso}' + d + time '04:00' + (g % 30) * interval '1 minute', 'YYYY-MM-DD"T"HH24:MI:SS"+00"'),
                    'idempotency_key', 'LP{tag}' || g || '-' || d, 'supersedes_event_id', null)) x
            where t.id = (select id from hrm.attendance_events where employment_id = '{full}' and direction = 'in' limit 1)
            """,
            $"""
            insert into hrm.attendance_exceptions
            select x.*
            from hrm.attendance_exceptions t, generate_series(1, {Employees}) g,
                 lateral jsonb_populate_record(null::hrm.attendance_exceptions, to_jsonb(t) || jsonb_build_object(
                    'id', md5('{tag}x' || g)::uuid, 'employment_id', md5('{tag}e' || g)::uuid)) x
            where t.employment_id = '{forgot}' and t.status = 'open'
            """,
            "analyze hrm.persons, hrm.employments, hrm.employment_assignments, hrm.attendance_days, hrm.attendance_events, hrm.attendance_exceptions",
        };
        await using var connection = await _h.OwnerAsync(null, Ct);
        foreach (var sql in statements)
        {
            await using var command = new Npgsql.NpgsqlCommand(sql, connection) { CommandTimeout = 900 };
            var watch = Stopwatch.StartNew();
            await command.ExecuteNonQueryAsync(Ct);
            TestContext.Current.SendDiagnosticMessage($"S1.30b load step {watch.Elapsed.TotalSeconds:0.0}s: {sql.Trim()[..50].ReplaceLineEndings(" ")}");
        }

        Assert.Equal(1, await _h.ScalarAsync<long>("select 1 where " + employment + " is not null", Ct));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from hrm.employments where company_id = $1", Ct, o.Company) >= Employees);
    }
}
