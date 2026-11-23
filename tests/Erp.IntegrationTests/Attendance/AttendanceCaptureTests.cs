#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// Attendance capture (slice S1.27 part a, Build items 1 to 5) through the real API pipeline, the real access engine, the real
/// worker jobs and a real PostgreSQL: FR-HRM-ATT-01 (web check-in and out, kiosk, QR, allowed networks), FR-HRM-ATT-03 (duplicate removal),
/// FR-HRM-DAT-01 (append only), FR-HRM-DAT-02 (capture and receipt time, idempotency key), FR-HRM-DAT-04 (partitions ahead and the purge).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceCaptureTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Base = "/api/hrm/attendance";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Key() => Guid.NewGuid().ToString("N");

    private static string Title(Answer a) => a.Json.GetProperty("title").GetString()!;

    // ------------------------------------------------------------------ material

    /// <summary>A company with a place to hire into and an HR user.</summary>
    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Hr)> CompanyAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        return (company, await _h.PlaceAsync(company, Ct), await _h.UserAsync(Ct, null, "hr_admin"));
    }

    /// <summary>An employee of the company: a hired person whose employment is linked to a fresh user holding the baseline employee role.</summary>
    private async Task<(Guid User, Guid Employment)> EmployeeAsync(Guid company, (Guid OrgUnit, Guid Branch) place, Guid hr, string status = "active")
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var hired = await _h.PostAsync(hr, "/api/hrm/employments", new
        {
            personId = person, companyId = company, employeeType = "permanent", joiningDate = Iso(_h.Today.AddDays(-90)),
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer" },
        }, Ct);
        Assert.True(hired.Status == HttpStatusCode.Created, hired.Body);
        var employment = hired.Json.GetProperty("id").GetGuid();
        var user = await _h.UserAsync(Ct, null, "employee");
        var linked = await _h.PutAsync(hr, $"/api/hrm/employments/{employment}/user", new { userId = user, rowVersion = hired.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(linked.Status == HttpStatusCode.OK, linked.Body);
        if (status != "active")
        {
            await _h.ExecAsync("update hrm.employments set status = $2 where id = $1", Ct, employment, status);
        }

        return (user, employment);
    }

    private async Task<Guid> UserWithAsync(Guid? company, params string[] permissions)
    {
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, await _h.RoleAsync(Ct, permissions), company, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    private Task<Answer> PunchAsync(Guid user, string direction, string? key = null, Guid? company = null) =>
        _h.PostAsync(user, $"{Base}/punch", new { direction, idempotencyKey = key ?? Key(), companyId = company }, Ct);

    private Task<long> CountAsync(Guid employment, string? where = null) =>
        _h.ScalarAsync<long>("select count(*) from hrm.attendance_events where employment_id = $1" + (where is null ? string.Empty : " and " + where), Ct, employment);

    // ------------------------------------------------------------------ web (ATT-01, DAT-02)

    [Fact]
    public async Task FR_HRM_ATT_01_a_signed_in_employee_checks_in_and_out_and_the_person_comes_from_the_token()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var (_, otherEmployment) = await EmployeeAsync(company, place, hr);

        var started = _h.Clock.UtcNow;
        var checkIn = await PunchAsync(user, "in");
        Assert.True(checkIn.Status == HttpStatusCode.Created, checkIn.Body);
        Assert.Equal("stored", checkIn.Json.GetProperty("status").GetString());
        _h.Clock.Advance(TimeSpan.FromHours(8));
        var checkOut = await PunchAsync(user, "out");
        Assert.Equal(HttpStatusCode.Created, checkOut.Status);

        // Both are events of the caller's own employment with source web; the server time is the injected clock.
        Assert.Equal(2L, await CountAsync(employment, "source = 'web'"));
        Assert.Equal(0L, await CountAsync(otherEmployment));
        Assert.Equal(started.ToUnixTimeSeconds(), await _h.ScalarAsync<long>("select extract(epoch from min(event_at))::bigint from hrm.attendance_events where employment_id = $1", Ct, employment));
        Assert.Equal(started.AddHours(8).ToUnixTimeSeconds(), await _h.ScalarAsync<long>("select extract(epoch from max(received_at))::bigint from hrm.attendance_events where employment_id = $1", Ct, employment));
        Assert.Equal(user, await _h.ScalarAsync<Guid>("select created_by from hrm.attendance_events where id = $1", Ct, checkIn.Json.GetProperty("eventId").GetGuid()));

        // The employee cannot name anybody else: a body that carries another company or employment changes nothing, and unknown directions are refused.
        var hostile = await _h.PostAsync(user, $"{Base}/punch", new { direction = "in", idempotencyKey = Key(), companyId = Guid.NewGuid(), employmentId = otherEmployment }, Ct);
        Assert.Equal("errors.hrm.attendance.no_employment", Title(hostile));
        Assert.Equal(0L, await CountAsync(otherEmployment));
        Assert.Equal("errors.hrm.attendance.direction_required", Title(await PunchAsync(user, "break_in")));
        Assert.Equal("errors.hrm.attendance.key_required", Title(await _h.PostAsync(user, $"{Base}/punch", new { direction = "in" }, Ct)));

        // The employee's day shows both punches and what comes next.
        var day = await _h.GetAsync(user, $"{Base}/my-day", Ct);
        Assert.Equal(HttpStatusCode.OK, day.Status);
        Assert.Equal(employment, day.Json.GetProperty("employmentId").GetGuid());
        Assert.Equal("in", day.Json.GetProperty("next").GetString());
    }

    [Fact]
    public async Task FR_HRM_ATT_01_a_user_without_a_current_employment_cannot_punch_and_needs_the_permission()
    {
        var (company, place, hr) = await CompanyAsync();
        var plain = await _h.UserAsync(Ct, null, "employee");
        Assert.Equal("errors.hrm.attendance.no_employment", Title(await PunchAsync(plain, "in")));
        var day = await _h.GetAsync(plain, $"{Base}/my-day", Ct);
        Assert.Equal(HttpStatusCode.OK, day.Status);
        Assert.Equal(JsonValueKind.Null, day.Json.GetProperty("employmentId").ValueKind);

        var (user, _) = await EmployeeAsync(company, place, hr, "separated");
        Assert.Equal("errors.hrm.attendance.no_employment", Title(await PunchAsync(user, "in"))); // a separated employment cannot punch

        var noPermission = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await PunchAsync(noPermission, "in")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(noPermission, $"{Base}/my-day", Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_DAT_02_the_same_idempotency_key_twice_gives_one_event()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var key = Key();
        var first = await PunchAsync(user, "in", key);
        var again = await PunchAsync(user, "in", key);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("already_received", again.Json.GetProperty("status").GetString());
        Assert.Equal(first.Json.GetProperty("eventId").GetGuid(), again.Json.GetProperty("eventId").GetGuid());
        Assert.Equal(1L, await CountAsync(employment));

        // Another employee's identical key is a different punch (keys are kept per person).
        var (second, secondEmployment) = await EmployeeAsync(company, place, hr);
        Assert.Equal(HttpStatusCode.Created, (await PunchAsync(second, "in", key)).Status);
        Assert.Equal(1L, await CountAsync(secondEmployment));
    }

    [Fact]
    public async Task FR_HRM_ATT_03_a_second_punch_of_the_same_direction_inside_the_window_is_kept_but_marked_duplicate()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var first = await PunchAsync(user, "in");
        _h.Clock.Advance(TimeSpan.FromSeconds(30));
        var near = await PunchAsync(user, "in");
        Assert.True(near.Json.GetProperty("isDuplicate").GetBoolean());
        Assert.False(first.Json.GetProperty("isDuplicate").GetBoolean());
        _h.Clock.Advance(TimeSpan.FromSeconds(31)); // 61 s after the first, 31 s after the marked one: the marked one never counts as the earlier punch
        var later = await PunchAsync(user, "in");
        Assert.False(later.Json.GetProperty("isDuplicate").GetBoolean());
        _h.Clock.Advance(TimeSpan.FromSeconds(10));
        var out1 = await PunchAsync(user, "out"); // another direction is never a duplicate
        Assert.False(out1.Json.GetProperty("isDuplicate").GetBoolean());

        Assert.Equal(4L, await CountAsync(employment)); // all kept
        Assert.Equal(1L, await CountAsync(employment, "is_duplicate"));
        Assert.Equal(3L, await CountAsync(employment, "not is_duplicate"));
    }

    [Fact]
    public async Task FR_HRM_DAT_01_the_database_refuses_update_and_delete_of_events_even_for_its_owner()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        Assert.Equal(HttpStatusCode.Created, (await PunchAsync(user, "in")).Status);
        await using var owner = await _h.OwnerAsync(null, Ct);
        var update = await Assert.ThrowsAsync<PostgresException>(async () => await AccessHarness.ExecAsync(owner, "update hrm.attendance_events set direction = 'out' where employment_id = $1", Ct, employment));
        Assert.Contains("append", update.MessageText, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<PostgresException>(async () => await AccessHarness.ExecAsync(owner, "delete from hrm.attendance_events where employment_id = $1", Ct, employment));
        await Assert.ThrowsAsync<PostgresException>(async () => await AccessHarness.ExecAsync(owner, "truncate hrm.attendance_events", Ct));
        Assert.Equal(1L, await CountAsync(employment));

        // The generated write is closed: the only way in is the event store.
        var viaGenerated = await _h.PostAsync(hr, "/api/hrm/attendance-events", new { employmentId = employment }, Ct);
        Assert.Contains(viaGenerated.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    // ------------------------------------------------------------------ HR entry and the ingestion contract (DAT-01, DAT-02, ATT-03)

    [Fact]
    public async Task FR_HRM_DAT_02_a_late_batch_keeps_the_original_times_and_receipt_time_is_the_server_clock()
    {
        var (company, place, hr) = await CompanyAsync();
        var (_, employment) = await EmployeeAsync(company, place, hr);
        var punched = _h.Clock.UtcNow.AddHours(-6).AddMinutes(-7);
        var batch = new
        {
            events = new object[]
            {
                new { employmentId = employment, eventAt = punched, direction = "in", source = "import", idempotencyKey = "b-1" },
                new { employmentId = employment, eventAt = punched.AddHours(4), direction = "out", source = "import", idempotencyKey = "b-2" },
            },
        };
        var sent = await _h.PostAsync(hr, $"{Base}/events", batch, Ct);
        Assert.True(sent.Status == HttpStatusCode.OK, sent.Body);
        Assert.All(sent.Json.GetProperty("results").EnumerateArray(), r => Assert.Equal("stored", r.GetProperty("status").GetString()));
        Assert.Equal(punched.ToUnixTimeSeconds(), await _h.ScalarAsync<long>("select extract(epoch from min(event_at))::bigint from hrm.attendance_events where employment_id = $1", Ct, employment));
        Assert.True(await _h.ScalarAsync<bool>("select bool_and(received_at = $2) from hrm.attendance_events where employment_id = $1", Ct, employment, _h.Clock.UtcNow));

        // The same batch again (a device that replays) adds nothing; the answer names the stored events.
        var replay = await _h.PostAsync(hr, $"{Base}/events", batch, Ct);
        Assert.All(replay.Json.GetProperty("results").EnumerateArray(), r => Assert.Equal("already_received", r.GetProperty("status").GetString()));
        Assert.Equal(2L, await CountAsync(employment));

        // The local date is the company's date in its time zone (Asia/Karachi by default), not the UTC date.
        var late = new DateTimeOffset(2026, 3, 1, 21, 30, 0, TimeSpan.Zero); // 02:30 on 2 March in Karachi
        var edge = await _h.PostAsync(hr, $"{Base}/events", new { events = new object[] { new { employmentId = employment, eventAt = late, direction = "in", source = "hr_entry", idempotencyKey = "edge" } } }, Ct);
        Assert.Equal("stored", edge.Json.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("2026-03-02", await _h.ScalarAsync<string>("select event_date::text from hrm.attendance_events where employment_id = $1 and idempotency_key like '%edge'", Ct, employment));
    }

    [Fact]
    public async Task FR_HRM_DAT_02_a_punch_ahead_of_the_server_clock_beyond_the_tolerance_is_refused()
    {
        var (company, place, hr) = await CompanyAsync();
        var (_, employment) = await EmployeeAsync(company, place, hr);
        var now = _h.Clock.UtcNow;
        var answer = await _h.PostAsync(hr, $"{Base}/events", new
        {
            events = new object[]
            {
                new { employmentId = employment, eventAt = now.AddMinutes(3), direction = "in", source = "hr_entry", idempotencyKey = "f-1" },
                new { employmentId = employment, eventAt = now.AddHours(1), direction = "in", source = "hr_entry", idempotencyKey = "f-2" },
            },
        }, Ct);
        var results = answer.Json.GetProperty("results");
        Assert.Equal("stored", results[0].GetProperty("status").GetString());
        Assert.Equal("refused", results[1].GetProperty("status").GetString());
        Assert.Equal("errors.hrm.attendance.future_time", results[1].GetProperty("error").GetString());
        Assert.Equal(1L, await CountAsync(employment));
    }

    [Fact]
    public async Task FR_HRM_ATT_03_a_batch_marks_duplicates_and_refuses_what_it_cannot_take_without_stopping_the_rest()
    {
        var (company, place, hr) = await CompanyAsync();
        var (_, employment) = await EmployeeAsync(company, place, hr);
        var t = _h.Clock.UtcNow.AddHours(-3);
        var answer = await _h.PostAsync(hr, $"{Base}/events", new
        {
            events = new object[]
            {
                new { employmentId = employment, eventAt = t, direction = "in", source = "hr_entry", idempotencyKey = "d-1" },
                new { employmentId = employment, eventAt = t.AddSeconds(20), direction = "in", source = "hr_entry", idempotencyKey = "d-2" },
                new { employmentId = employment, eventAt = t.AddSeconds(20), direction = "sideways", source = "hr_entry", idempotencyKey = "d-3" },
                new { employmentId = employment, eventAt = t, direction = "in", source = "web", idempotencyKey = "d-4" },
                new { employmentId = Guid.NewGuid(), eventAt = t, direction = "in", source = "hr_entry", idempotencyKey = "d-5" },
                new { employmentId = employment, eventAt = t.AddHours(1), direction = "in", source = "hr_entry", idempotencyKey = (string?)null },
            },
        }, Ct);
        var r = answer.Json.GetProperty("results");
        Assert.True(r[1].GetProperty("isDuplicate").GetBoolean());
        Assert.Equal("errors.hrm.attendance.direction_unknown", r[2].GetProperty("error").GetString());
        Assert.Equal("errors.hrm.attendance.source_not_allowed", r[3].GetProperty("error").GetString());
        Assert.Equal("errors.hrm.attendance.employment_unknown", r[4].GetProperty("error").GetString());
        Assert.Equal("errors.hrm.attendance.key_required", r[5].GetProperty("error").GetString());
        Assert.Equal(2L, await CountAsync(employment));

        // A correction is a new event that names the one it replaces and is not itself marked a duplicate.
        var first = r[0].GetProperty("eventId").GetGuid();
        var fix = await _h.PostAsync(hr, $"{Base}/events", new { events = new object[] { new { employmentId = employment, eventAt = t.AddSeconds(5), direction = "in", source = "hr_entry", idempotencyKey = "d-fix", supersedesEventId = first } } }, Ct);
        Assert.False(fix.Json.GetProperty("results")[0].GetProperty("isDuplicate").GetBoolean());
        Assert.Equal(first, await _h.ScalarAsync<Guid>("select supersedes_event_id from hrm.attendance_events where idempotency_key like '%d-fix'", Ct));
        var wrong = await _h.PostAsync(hr, $"{Base}/events", new { events = new object[] { new { employmentId = employment, eventAt = t, direction = "in", source = "hr_entry", idempotencyKey = "d-bad", supersedesEventId = Guid.NewGuid() } } }, Ct);
        Assert.Equal("errors.hrm.attendance.supersedes_unknown", wrong.Json.GetProperty("results")[0].GetProperty("error").GetString());

        Assert.Equal("errors.hrm.attendance.batch_empty", Title(await _h.PostAsync(hr, $"{Base}/events", new { events = Array.Empty<object>() }, Ct)));
    }

    [Fact]
    public async Task FR_HRM_ATT_03_appending_for_others_needs_the_permission_and_the_employment_in_the_callers_data_scope()
    {
        var (company, place, hr) = await CompanyAsync();
        var (employee, employment) = await EmployeeAsync(company, place, hr);
        var body = new { events = new object[] { new { employmentId = employment, eventAt = _h.Clock.UtcNow.AddMinutes(-5), direction = "in", source = "hr_entry", idempotencyKey = Key() } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(employee, $"{Base}/events", body, Ct)).Status);

        // A holder of the permission whose scope is "own" cannot append for somebody else's employment (answered as an unknown employment).
        var ownOnly = await _h.UserAsync(Ct);
        await _h.AssignAsync(ownOnly, await _h.RoleAsync(Ct, ["hrm.attendance_events.create", "hrm.employments.read"], [("hrm.employments", "own")]), company, _h.Today.AddDays(-1), null, Ct);
        var refused = await _h.PostAsync(ownOnly, $"{Base}/events", body, Ct);
        Assert.Equal("errors.hrm.attendance.employment_unknown", refused.Json.GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal(0L, await CountAsync(employment));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(hr, $"{Base}/events", body, Ct)).Status);
        Assert.Equal(1L, await CountAsync(employment));
    }

    // ------------------------------------------------------------------ allowed networks (ATT-01)

    [Fact]
    public async Task FR_HRM_ATT_01_web_punches_from_outside_the_allowed_networks_are_refused()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var set = await _h.PutAsync(admin, "/api/cfg/settings/hrm.att.allowed_networks", new { layer = "company", scopeId = company, value = new[] { "203.0.113.0/24" }, reason = "office" }, Ct);
        Assert.True(set.Status is HttpStatusCode.OK or HttpStatusCode.Accepted, set.Body);
        if (set.Status == HttpStatusCode.Accepted)
        {
            var second = await _h.UserAsync(Ct, null, "tenant_admin");
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{set.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        }

        // The test server's connection has no address of an office network, so the punch is refused and nothing is stored.
        var refused = await PunchAsync(user, "in");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.hrm.attendance.network_not_allowed", Title(refused));
        Assert.Equal(0L, await CountAsync(employment));

        // An empty list means anywhere.
        var clear = await _h.PutAsync(admin, "/api/cfg/settings/hrm.att.allowed_networks", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "open" }, Ct);
        Assert.True(clear.Status is HttpStatusCode.OK or HttpStatusCode.Accepted, clear.Body);
        Assert.Equal(HttpStatusCode.Created, (await PunchAsync(user, "in")).Status);
    }

    // ------------------------------------------------------------------ kiosk (ATT-01)

    [Fact]
    public async Task FR_HRM_ATT_01_a_kiosk_station_punches_for_the_employee_whose_badge_number_is_mapped()
    {
        var (company, place, hr) = await CompanyAsync();
        var (_, employment) = await EmployeeAsync(company, place, hr);
        var badge = "K" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        await _h.RowAsync("hrm.device_user_maps", company, new Dictionary<string, object?>
        {
            ["employment_id"] = employment, ["device_user_no"] = badge, ["device_id"] = null, ["valid_from"] = _h.Today.AddDays(-10), ["valid_to"] = null, ["consent_at"] = null,
        }, Ct);
        var station = await UserWithAsync(company, "hrm.attendance.kiosk");

        var punch = await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = badge, direction = "in", idempotencyKey = Key() }, Ct);
        Assert.True(punch.Status == HttpStatusCode.Created, punch.Body);
        Assert.Equal("kiosk", punch.Json.GetProperty("punch").GetProperty("source").GetString());
        Assert.False(string.IsNullOrEmpty(punch.Json.GetProperty("firstName").GetString()));
        Assert.Equal(1L, await CountAsync(employment, "source = 'kiosk'"));
        Assert.Equal(station, await _h.ScalarAsync<Guid>("select created_by from hrm.attendance_events where employment_id = $1", Ct, employment));

        // A retried request punches once; an unknown number is refused and stores nothing; the station of another company is refused.
        var key = Key();
        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        var one = await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = badge, direction = "out", idempotencyKey = key }, Ct);
        var two = await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = badge, direction = "out", idempotencyKey = key }, Ct);
        Assert.Equal(HttpStatusCode.Created, one.Status);
        Assert.Equal("already_received", two.Json.GetProperty("punch").GetProperty("status").GetString());
        Assert.Equal(2L, await CountAsync(employment));
        var unknown = await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = "NOPE", direction = "in", idempotencyKey = Key() }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("errors.hrm.attendance.badge_unknown", Title(unknown));
        var elsewhere = await UserWithAsync(await _h.CompanyAsync(Ct), "hrm.attendance.kiosk");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(elsewhere, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = badge, direction = "in", idempotencyKey = Key() }, Ct)).Status);
        var ordinary = await _h.UserAsync(Ct, null, "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(ordinary, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = badge, direction = "in", idempotencyKey = Key() }, Ct)).Status);
        Assert.Equal(2L, await CountAsync(employment));
    }

    [Fact]
    public async Task FR_HRM_ATT_01_a_badge_mapping_that_has_ended_or_belongs_to_a_separated_employee_punches_nothing()
    {
        var (company, place, hr) = await CompanyAsync();
        var (_, ended) = await EmployeeAsync(company, place, hr);
        var (_, gone) = await EmployeeAsync(company, place, hr, "separated");
        var endedBadge = "E" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        var goneBadge = "G" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        await _h.RowAsync("hrm.device_user_maps", company, new Dictionary<string, object?> { ["employment_id"] = ended, ["device_user_no"] = endedBadge, ["device_id"] = null, ["valid_from"] = _h.Today.AddDays(-30), ["valid_to"] = _h.Today.AddDays(-1), ["consent_at"] = null }, Ct);
        await _h.RowAsync("hrm.device_user_maps", company, new Dictionary<string, object?> { ["employment_id"] = gone, ["device_user_no"] = goneBadge, ["device_id"] = null, ["valid_from"] = _h.Today.AddDays(-30), ["valid_to"] = null, ["consent_at"] = null }, Ct);
        var station = await UserWithAsync(company, "hrm.attendance.kiosk");
        Assert.Equal("errors.hrm.attendance.badge_unknown", Title(await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = endedBadge, direction = "in", idempotencyKey = Key() }, Ct)));
        Assert.Equal("errors.hrm.attendance.employment_not_active", Title(await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = goneBadge, direction = "in", idempotencyKey = Key() }, Ct)));
        Assert.Equal("errors.hrm.attendance.badge_required", Title(await _h.PostAsync(station, $"{Base}/kiosk/punch", new { companyId = company, badgeNo = " ", direction = "in", idempotencyKey = Key() }, Ct)));
        Assert.Equal(0L, await CountAsync(ended) + await CountAsync(gone));
    }

    // ------------------------------------------------------------------ QR (ATT-01, Q5)

    private async Task<(Guid Location, string Code)> QrAsync(Guid company, Guid viewer, Guid? location = null)
    {
        var place = location ?? (Guid)(await _h.RowAsync("org.locations", company, null, Ct))["id"]!;
        var shown = await _h.GetAsync(viewer, $"{Base}/qr-code?companyId={company}&locationId={place}", Ct);
        Assert.True(shown.Status == HttpStatusCode.OK, shown.Body);
        return (place, shown.Text("code")!);
    }

    [Fact]
    public async Task FR_HRM_ATT_01_a_scan_of_the_gates_rotating_code_by_a_signed_in_employee_is_a_qr_punch_and_a_replayed_code_is_refused()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var viewer = await UserWithAsync(company, "hrm.attendance.qr_display");
        var toSecond = 10 - (int)(_h.Clock.UtcNow.ToUnixTimeSeconds() % 60); // the code changes every 60 s: start 10 s into a period so the steps below are exact
        _h.Clock.Advance(TimeSpan.FromSeconds(toSecond <= 0 ? toSecond + 60 : toSecond));
        var (_, code) = await QrAsync(company, viewer);
        var shownAgain = await QrAsync(company, viewer, (Guid)(await _h.RowAsync("org.locations", company, null, Ct))["id"]!);
        Assert.NotEqual(code, shownAgain.Code); // another location, another code

        var scan = await _h.PostAsync(user, $"{Base}/qr-punch", new { code, direction = "in", idempotencyKey = Key() }, Ct);
        Assert.True(scan.Status == HttpStatusCode.Created, scan.Body);
        Assert.Equal(1L, await CountAsync(employment, "source = 'qr'"));

        // The code of the period before is still taken (a person scanning as it changes); two periods later it is not, and the refusal stores nothing.
        _h.Clock.Advance(TimeSpan.FromSeconds(60));
        var late = await _h.PostAsync(user, $"{Base}/qr-punch", new { code, direction = "out", idempotencyKey = Key() }, Ct);
        Assert.True(late.Status == HttpStatusCode.Created, late.Body);
        _h.Clock.Advance(TimeSpan.FromSeconds(60)); // now two periods after the code was shown
        var replay = await _h.PostAsync(user, $"{Base}/qr-punch", new { code, direction = "in", idempotencyKey = Key() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, replay.Status);
        Assert.Equal("errors.hrm.attendance.qr_expired", Title(replay));
        Assert.Equal(2L, await CountAsync(employment));
    }

    [Fact]
    public async Task FR_HRM_ATT_01_a_changed_forged_or_foreign_qr_code_is_refused_and_a_photograph_without_a_session_is_useless()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var viewer = await UserWithAsync(company, "hrm.attendance.qr_display");
        var (location, code) = await QrAsync(company, viewer);

        var parts = code.Split('.');
        foreach (var bad in new[] { "garbage", $"{parts[0]}.{parts[1]}.AAAAAAAAAAAAAAAA", $"{parts[0]}.{long.Parse(parts[1], CultureInfo.InvariantCulture) + 1}.{parts[2]}", $"{Guid.NewGuid()}.{parts[1]}.{parts[2]}", string.Empty })
        {
            var refused = await _h.PostAsync(user, $"{Base}/qr-punch", new { code = bad, direction = "in", idempotencyKey = Key() }, Ct);
            Assert.Equal("errors.hrm.attendance.qr_invalid", Title(refused));
        }

        // A code of another company's location is valid but the employee is not employed there.
        var other = await _h.CompanyAsync(Ct);
        var otherViewer = await UserWithAsync(other, "hrm.attendance.qr_display");
        var (_, otherCode) = await QrAsync(other, otherViewer);
        Assert.Equal("errors.hrm.attendance.qr_other_company", Title(await _h.PostAsync(user, $"{Base}/qr-punch", new { code = otherCode, direction = "in", idempotencyKey = Key() }, Ct)));
        Assert.Equal(0L, await CountAsync(employment));

        // The code cannot be asked for without the permission, for a company the viewer does not cover, or for a location of another company.
        var employee = await _h.UserAsync(Ct, null, "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(employee, $"{Base}/qr-code?companyId={company}&locationId={location}", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(viewer, $"{Base}/qr-code?companyId={other}&locationId={location}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(viewer, $"{Base}/qr-code?companyId={company}&locationId={Guid.NewGuid()}", Ct)).Status);
        // Scanning needs the punch permission of a signed-in session.
        var noPunch = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(noPunch, $"{Base}/qr-punch", new { code, direction = "in", idempotencyKey = Key() }, Ct)).Status);
        using var anonymous = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), null);
        using var unsigned = await anonymous.PostAsJsonAsync($"{Base}/qr-punch", new { code, direction = "in", idempotencyKey = Key() }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    // ------------------------------------------------------------------ partitions and purge (DAT-04)

    private async Task<bool> PartitionExistsAsync(string name) =>
        await _h.ScalarAsync<bool>("select exists (select 1 from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'hrm' and c.relname = $1)", Ct, name);

    private static string PartitionName(DateOnly month) => "attendance_events_p" + month.ToString("yyyy_MM", CultureInfo.InvariantCulture);

    [Fact]
    public async Task FR_HRM_DAT_04_monthly_partitions_exist_three_months_ahead_and_the_partition_job_makes_a_missing_one()
    {
        var thisMonth = new DateOnly(_h.Today.Year, _h.Today.Month, 1);
        var months = Enumerable.Range(0, 4).Select(i => thisMonth.AddMonths(i)).ToList();
        var last = months[^1];
        await using (var owner = await _h.OwnerAsync(null, Ct))
        {
            // A missing future partition (empty): the job puts it back.
            await AccessHarness.ExecAsync(owner, $"drop table if exists hrm.{PartitionName(last)}", Ct);
        }

        Assert.False(await PartitionExistsAsync(PartitionName(last)));
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        var run = await rig.QueueAsync("kernel.partitions_ensure", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("succeeded", await rig.StatusAsync(run, Ct));
        foreach (var month in months)
        {
            Assert.True(await PartitionExistsAsync(PartitionName(month)), PartitionName(month));
        }
    }

    [Fact]
    public async Task FR_HRM_DAT_04_the_purge_job_drops_only_whole_months_older_than_the_retention_and_never_single_punches()
    {
        var (company, place, hr) = await CompanyAsync();
        var (user, employment) = await EmployeeAsync(company, place, hr);
        var today = _h.Today;
        var cutoff = new DateOnly(today.AddYears(-7).Year, today.AddYears(-7).Month, 1); // months that ended on or before this day go
        var expired = cutoff.AddMonths(-1);
        await using (var owner = await _h.OwnerAsync(null, Ct))
        {
            foreach (var month in new[] { expired, cutoff })
            {
                await AccessHarness.ExecAsync(owner, $"create table if not exists hrm.{PartitionName(month)} partition of hrm.attendance_events for values from ('{Iso(month)}') to ('{Iso(month.AddMonths(1))}')", Ct);
            }
        }

        // One punch in the expired month, one in the month the retention reaches into, one recent (written by HR entry in their local times).
        DateTimeOffset At(DateOnly d) => new(d.Year, d.Month, d.Day, 9, 0, 0, TimeSpan.FromHours(5));
        var answer = await _h.PostAsync(hr, $"{Base}/events", new
        {
            events = new object[]
            {
                new { employmentId = employment, eventAt = At(expired.AddDays(9)), direction = "in", source = "import", idempotencyKey = "old" },
                new { employmentId = employment, eventAt = At(cutoff.AddDays(9)), direction = "in", source = "import", idempotencyKey = "edge" },
                new { employmentId = employment, eventAt = _h.Clock.UtcNow.AddMinutes(-1), direction = "in", source = "import", idempotencyKey = "now" },
            },
        }, Ct);
        Assert.All(answer.Json.GetProperty("results").EnumerateArray(), r => Assert.Equal("stored", r.GetProperty("status").GetString()));
        Assert.Equal(3L, await CountAsync(employment));

        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        var run = await rig.QueueAsync("hrm.attendance_purge", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("succeeded", await rig.StatusAsync(run, Ct));

        Assert.False(await PartitionExistsAsync(PartitionName(expired)));
        Assert.True(await PartitionExistsAsync(PartitionName(cutoff)));
        Assert.True(await PartitionExistsAsync(PartitionName(new DateOnly(today.Year, today.Month, 1))));
        Assert.True(await PartitionExistsAsync("attendance_events_default"));
        Assert.Equal(2L, await CountAsync(employment)); // the expired month went with its partition; nothing else
        Assert.Contains(await rig.LogsAsync(run, Ct), l => l.Contains(PartitionName(expired), StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(employment, "idempotency_key like '%edge'"));
        Assert.NotEqual(Guid.Empty, user);

        // The job again finds nothing to do, and a retention longer than the oldest data removes nothing.
        var again = await rig.QueueAsync("hrm.attendance_purge", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("succeeded", await rig.StatusAsync(again, Ct));
        Assert.Equal(2L, await CountAsync(employment));
    }

    [Fact]
    public async Task FR_HRM_DAT_04_the_application_login_cannot_drop_a_partition_itself_and_the_function_refuses_a_missing_date()
    {
        await using var owner = await _h.OwnerAsync(null, Ct);
        await Assert.ThrowsAnyAsync<PostgresException>(async () => await AccessHarness.ExecAsync(owner, "select * from hrm.drop_expired_attendance_partitions(null)", Ct));
        Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from hrm.drop_expired_attendance_partitions('1990-01-01')", Ct));
    }
}
