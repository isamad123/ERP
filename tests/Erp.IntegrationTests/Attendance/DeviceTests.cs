#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// Attendance devices (slice S1.27 part b, Build items 6 to 9) through the real API pipeline, the real access engine, the real worker and a real
/// PostgreSQL: FR-HRM-ATT-02 (registry, console status, credentials kept out of every answer), FR-HRM-ATT-03 (signed push, pull driver, duplicate removal,
/// unmapped punches), FR-HRM-ATT-20 (consent, removal on leaving, no template ever), FR-HRM-DAT-01 and DAT-02 (original times, one event per key).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class DeviceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Base = "/api/hrm/attendance";
    // Made at run time: no secret-like literal sits in the repository (the secret scan), and each run has its own value to look for in every answer, row and log.
    private static readonly string Secret = "dev-" + Guid.NewGuid().ToString("N");
    private static readonly string Other = "other-" + Guid.NewGuid().ToString("N");
    private AccessHarness _h = null!;
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    /// <summary>The company's date (Asia/Karachi) on the harness clock; the harness's own <c>Today</c> is the UTC date, a day behind after 19:00 UTC.</summary>
    private DateOnly CompanyToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);


    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------ material

    private sealed record World(Guid Company, Guid Location, Guid Hr, Guid Admin, Guid Employee, Guid Employment, string EmployeeNo);

    /// <summary>A company with the biometric switch on, a location, an HR user and an active employee.</summary>
    private async Task<World> WorldAsync(bool biometric = true)
    {
        var company = await _h.CompanyAsync(Ct);
        var place = await _h.PlaceAsync(company, Ct);
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var hired = await _h.PostAsync(hr, "/api/hrm/employments", new
        {
            personId = person, companyId = company, employeeType = "permanent", joiningDate = Iso(CompanyToday.AddDays(-90)),
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer" },
        }, Ct);
        Assert.True(hired.Status == HttpStatusCode.Created, hired.Body);
        var user = await _h.UserAsync(Ct, null, "employee");
        var location = (Guid)(await _h.RowAsync("org.locations", company, null, Ct))["id"]!;
        if (biometric)
        {
            await SwitchAsync(admin, company, true);
        }

        return new World(company, location, hr, admin, user, hired.Json.GetProperty("id").GetGuid(), hired.Json.GetProperty("employeeNo").GetString()!);
    }

    private async Task SwitchAsync(Guid admin, Guid company, bool on)
    {
        var set = await _h.PutAsync(admin, "/api/cfg/settings/hrm.att.biometric_enabled", new { layer = "company", scopeId = company, value = on, reason = "devices" }, Ct);
        Assert.True(set.Status is HttpStatusCode.OK or HttpStatusCode.Accepted, set.Body);
        if (set.Status == HttpStatusCode.Accepted)
        {
            var second = await _h.UserAsync(Ct, null, "tenant_admin");
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{set.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        }
    }

    private async Task<(Guid Id, long Version)> PushDeviceAsync(World w, string? serial = null, string? secret = null)
    {
        var made = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = serial ?? "SN" + Guid.NewGuid().ToString("N")[..10], provider = "generic_push", credentials = secret ?? Secret }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return (made.Json.GetProperty("id").GetGuid(), made.Json.GetProperty("rowVersion").GetInt64());
    }

    private async Task<Answer> MapAsync(World w, Guid? device, string number, string? consent = null, string? employeeNo = null)
    {
        var body = new Dictionary<string, object?> { ["companyId"] = w.Company, ["deviceUserNo"] = number, ["employeeNo"] = employeeNo ?? w.EmployeeNo };
        body["validFrom"] = Iso(CompanyToday.AddDays(-1)); // a map starts on the company's day by default; punches of the last hours may fall on the day before
        if (device is { } d)
        {
            body["deviceId"] = d;
        }

        if (consent is not null)
        {
            body["consentDate"] = consent;
        }

        return await _h.PostAsync(w.Hr, $"{Base}/device-maps", body, Ct);
    }

    /// <summary>Sends a push the way a gateway does: the exact body, signed with the secret, as a user holding the push permission.</summary>
    private async Task<(HttpStatusCode Status, string Body)> PushAsync(Guid user, Guid device, string body, string? secret = null, bool unsigned = false, long? stamp = null, string? header = null)
    {
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), _h.Token(user));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/devices/{device}/push") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header is not null)
        {
            request.Headers.Add("X-Erp-Signature", header);
        }
        else if (!unsigned)
        {
            request.Headers.Add("X-Erp-Signature", DeviceRules.SignatureHeader(secret ?? Secret, stamp ?? _h.Clock.UtcNow.ToUnixTimeSeconds(), body));
        }

        using var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Records(params (string User, DateTimeOffset At, string Direction)[] rows) =>
        JsonSerializer.Serialize(new { records = rows.Select(r => new { userNo = r.User, at = r.At.ToString("O", CultureInfo.InvariantCulture), direction = r.Direction }) });

    private Task<Guid> PusherAsync(Guid company) => UserWithAsync(company, "hrm.attendance_devices.push");

    private async Task<Guid> UserWithAsync(Guid? company, params string[] permissions)
    {
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, await _h.RoleAsync(Ct, permissions), company, CompanyToday.AddDays(-1), null, Ct);
        return user;
    }

    private Task<long> EventsAsync(Guid employment, string? where = null) =>
        _h.ScalarAsync<long>("select count(*) from hrm.attendance_events where employment_id = $1" + (where is null ? string.Empty : " and " + where), Ct, employment);

    // ------------------------------------------------------------------ registry and credentials (ATT-02)

    [Fact]
    public async Task FR_HRM_ATT_02_a_device_secret_is_kept_in_the_secret_store_and_never_appears_in_an_answer_the_row_the_change_log_or_a_job_log()
    {
        var w = await WorldAsync();
        var (id, _) = await PushDeviceAsync(w, "SN-SECRET-1");
        var pull = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = "SN-SECRET-2", provider = "zkteco", host = "127.0.0.1", port = 9, protocol = "tcp", credentials = Secret }, Ct);
        Assert.Equal(HttpStatusCode.Created, pull.Status);

        // Every answer that shows a device: the console list, the generated read, the created answer and an update.
        var answers = new List<string> { pull.Body, (await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct)).Body, (await _h.GetAsync(w.Hr, $"/api/hrm/attendance-devices/{id}", Ct)).Body, (await _h.GetAsync(w.Hr, "/api/hrm/attendance-devices", Ct)).Body };
        var update = await _h.PutAsync(w.Hr, $"{Base}/devices/{id}", new { model = "Model X", credentials = Secret + "-new", rowVersion = 1 }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.Status);
        answers.Add(update.Body);
        Assert.All(answers, a => Assert.DoesNotContain(Secret, a, StringComparison.OrdinalIgnoreCase));
        Assert.True(update.Json.GetProperty("hasCredentials").GetBoolean());

        // The row holds a reference only; the store holds the value; the audit trail and the logs hold neither.
        var reference = await _h.ScalarAsync<string>("select credentials_ref from hrm.attendance_devices where id = $1", Ct, id);
        Assert.DoesNotContain(Secret, reference, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Secret + "-new", await fx.Secrets.GetAsync(reference, Ct));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where changes::text ilike '%' || $1 || '%'", Ct, Secret));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.attendance_devices where connection::text ilike '%' || $1 || '%' or coalesce(last_error, '') ilike '%' || $1 || '%'", Ct, Secret));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.attendance_devices'", Ct) >= 2);

        // A job that reads the pull device fails (nothing listens on port 9) without putting the key in its log, its error or the device row.
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var run = await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(rig.Worker(), Ct);
        Assert.DoesNotContain(Secret, string.Join('\n', await rig.LogsAsync(run, Ct)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, await rig.DumpAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, await _h.ScalarAsync<string>("select coalesce(string_agg(last_error, ','), '') from hrm.attendance_devices", Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FR_HRM_ATT_20_a_company_that_has_not_switched_biometric_devices_on_cannot_register_one_or_map_a_device_user()
    {
        var w = await WorldAsync(biometric: false);
        var refused = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = "SN-OFF", provider = "generic_push", credentials = Secret }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.hrm.device.biometric_disabled", refused.Text("title"));
        await SwitchAsync(w.Admin, w.Company, true);
        var (device, _) = await PushDeviceAsync(w);
        await SwitchAsync(w.Admin, w.Company, false);
        var mapped = await MapAsync(w, device, "55", "2024-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, mapped.Status);
        Assert.Equal("errors.hrm.device.biometric_disabled", mapped.Text("title"));
        var pusher = await PusherAsync(w.Company);
        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(pusher, device, Records(("55", _h.Clock.UtcNow.AddHours(-1), "in")))).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.unmapped_punches where company_id = $1", Ct, w.Company));
    }

    [Fact]
    public async Task FR_HRM_ATT_02_registering_checks_the_serial_the_connection_and_the_location()
    {
        var w = await WorldAsync();
        await PushDeviceAsync(w, "SN-DUP");
        var dup = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = "SN-DUP", provider = "generic_push", credentials = Secret }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.Status);
        var bad = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = Guid.NewGuid(), serialNo = "SN-X", provider = "zkteco", host = "http://bad host/", port = 70000, protocol = "ftp" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("errors.hrm.device.location_unknown", bad.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.device.host_invalid", bad.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.device.port_invalid", bad.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.device.protocol_invalid", bad.Body, StringComparison.Ordinal);
        var noSecret = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = "SN-NOSEC", provider = "generic_push" }, Ct);
        Assert.Contains("errors.hrm.device.credentials_required", noSecret.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.device.provider_unknown", (await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = "SN-P", provider = "nope" }, Ct)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_ATT_02_every_device_endpoint_needs_its_permission()
    {
        var w = await WorldAsync();
        var (device, version) = await PushDeviceAsync(w);
        var nobody = await UserWithAsync(w.Company, "hrm.employments.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Base}/devices", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = "SN-Z", provider = "generic_push", credentials = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(nobody, $"{Base}/devices/{device}", new { model = "m", rowVersion = version }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Base}/devices/{device}/sync", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(nobody, device, Records())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Base}/device-maps", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Base}/device-maps", new { companyId = w.Company }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Base}/unmapped", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Base}/unmapped/{Guid.NewGuid()}/discard", new { rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Base}/punch-log", Ct)).Status);
        // The employee role has none of them either, and a user of another company's scope cannot see this company's device.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(w.Employee, $"{Base}/devices", Ct)).Status);
        var elsewhere = await UserWithAsync(await _h.CompanyAsync(Ct), "hrm.attendance_devices.read", "hrm.attendance_devices.update", "hrm.attendance_devices.sync", "hrm.attendance_devices.push");
        Assert.Empty((await _h.GetAsync(elsewhere, $"{Base}/devices", Ct)).Json.GetProperty("devices").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(elsewhere, $"{Base}/devices/{device}", new { model = "m", rowVersion = version }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PushAsync(elsewhere, device, Records())).Status);
    }

    // ------------------------------------------------------------------ signed push (ATT-03, DAT-01, DAT-02)

    [Fact]
    public async Task FR_HRM_ATT_03_a_signed_push_stores_each_punch_once_with_its_original_time_and_a_resend_or_late_batch_causes_no_duplicate()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, device, "17", "2024-01-01")).Status);
        var pusher = await PusherAsync(w.Company);
        var early = _h.Clock.UtcNow.AddHours(-9);
        var late = _h.Clock.UtcNow.AddHours(-1);
        var body = Records(("17", early, "in"), ("17", late, "out"));

        var first = await PushAsync(pusher, device, body);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        var result = JsonDocument.Parse(first.Body).RootElement;
        Assert.Equal(2, result.GetProperty("stored").GetInt32());
        Assert.Equal(2L, await EventsAsync(w.Employment, "source = 'biometric'"));
        Assert.Equal(early.ToUnixTimeSeconds(), await _h.ScalarAsync<long>("select extract(epoch from min(event_at))::bigint from hrm.attendance_events where employment_id = $1", Ct, w.Employment));
        Assert.True(await _h.ScalarAsync<bool>("select bool_and(received_at = $2 and device_id = $3 and idempotency_key like 'biometric:%') from hrm.attendance_events where employment_id = $1", Ct, w.Employment, _h.Clock.UtcNow, device));

        // The same batch again, from a later time, changes nothing: the keys are the same.
        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        var again = await PushAsync(pusher, device, body);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(2, JsonDocument.Parse(again.Body).RootElement.GetProperty("alreadyReceived").GetInt32());
        Assert.Equal(2L, await EventsAsync(w.Employment));

        // A repeat of the punch a few seconds later (a double press) is kept but never counted.
        var press = await PushAsync(pusher, device, Records(("17", late.AddSeconds(20), "out")));
        Assert.Equal(1, JsonDocument.Parse(press.Body).RootElement.GetProperty("duplicates").GetInt32());
        Assert.Equal(1L, await EventsAsync(w.Employment, "is_duplicate"));
        Assert.True(await _h.ScalarAsync<bool>("select last_seen_at is not null from hrm.attendance_devices where id = $1", Ct, device));
    }

    [Fact]
    public async Task FR_HRM_ATT_03_a_push_with_a_missing_wrong_old_or_foreign_signature_is_refused_before_anything_is_read()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        var (other, _) = await PushDeviceAsync(w, secret: Other);
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, device, "18", "2024-01-01")).Status);
        var pusher = await PusherAsync(w.Company);
        var body = Records(("18", _h.Clock.UtcNow.AddHours(-1), "in"));
        var now = _h.Clock.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, device, body, unsigned: true)).Status); // no signature
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, device, body, secret: "wrong")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, device, body, stamp: now - 3600)).Status); // an old request replayed
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, device, body, secret: Other)).Status); // another device's secret
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, device, body, header: "garbage")).Status);
        var signed = DeviceRules.SignatureHeader(Secret, now, body);
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, device, body.Replace("\"in\"", "\"out\"", StringComparison.Ordinal), header: signed)).Status); // a changed body
        Assert.Equal(HttpStatusCode.Forbidden, (await PushAsync(pusher, other, body, header: signed)).Status);
        Assert.Equal(0L, await EventsAsync(w.Employment));
        Assert.Equal(HttpStatusCode.OK, (await PushAsync(pusher, device, body)).Status);
        Assert.Equal(1L, await EventsAsync(w.Employment));
    }

    [Fact]
    public async Task FR_HRM_ATT_20_a_pushed_record_that_carries_anything_but_user_time_and_direction_is_refused_whole_and_never_stored_or_echoed()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, device, "19", "2024-01-01")).Status);
        var pusher = await PusherAsync(w.Company);
        var at = _h.Clock.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);
        foreach (var field in new[] { "template", "fingerprint", "photo", "name" })
        {
            var body = $$"""{"records":[{"userNo":"19","at":"{{at}}","direction":"in","{{field}}":"TUZUUjEwLUJJT01FVFJJQw=="}]}""";
            var answer = await PushAsync(pusher, device, body);
            Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
            Assert.DoesNotContain("TUZUUjEw", answer.Body, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(pusher, device, "not json")).Status);
        Assert.Equal(0L, await EventsAsync(w.Employment));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.unmapped_punches where company_id = $1", Ct, w.Company));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where changes::text ilike '%TUZUUjEw%'", Ct));
    }

    // ------------------------------------------------------------------ unmapped punches (ATT-03)

    [Fact]
    public async Task FR_HRM_ATT_03_a_punch_of_an_unmapped_device_user_waits_in_the_unmapped_list_and_comes_in_with_its_original_time_when_mapped()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        var pusher = await PusherAsync(w.Company);
        var punched = _h.Clock.UtcNow.AddHours(-3);
        var sent = await PushAsync(pusher, device, Records(("77", punched, "in")));
        Assert.Equal(1, JsonDocument.Parse(sent.Body).RootElement.GetProperty("unmapped").GetInt32());
        Assert.Equal(0L, await EventsAsync(w.Employment));
        // The same punch again does not make a second waiting row.
        await PushAsync(pusher, device, Records(("77", punched, "in")));
        var list = await _h.GetAsync(w.Hr, $"{Base}/unmapped?companyId={w.Company}", Ct);
        Assert.Single(list.Json.GetProperty("punches").EnumerateArray());
        Assert.Equal(1, (await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct)).Json.GetProperty("devices")[0].GetProperty("unmappedOpen").GetInt32());

        var mapped = await MapAsync(w, device, "77", "2024-01-01");
        Assert.Equal(HttpStatusCode.Created, mapped.Status);
        Assert.Equal(1, mapped.Json.GetProperty("replayedPunches").GetInt32());
        Assert.Equal(1L, await EventsAsync(w.Employment, "source = 'biometric'"));
        Assert.Equal(punched.ToUnixTimeSeconds(), await _h.ScalarAsync<long>("select extract(epoch from event_at)::bigint from hrm.attendance_events where employment_id = $1", Ct, w.Employment));
        Assert.Empty((await _h.GetAsync(w.Hr, $"{Base}/unmapped?companyId={w.Company}", Ct)).Json.GetProperty("punches").EnumerateArray());
        Assert.Equal("resolved", await _h.ScalarAsync<string>("select status from hrm.unmapped_punches where company_id = $1", Ct, w.Company));
    }

    [Fact]
    public async Task FR_HRM_ATT_03_an_unmapped_punch_can_be_discarded_once_and_only_by_someone_allowed()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        var pusher = await PusherAsync(w.Company);
        await PushAsync(pusher, device, Records(("88", _h.Clock.UtcNow.AddHours(-1), "in")));
        var row = (await _h.GetAsync(w.Hr, $"{Base}/unmapped?companyId={w.Company}", Ct)).Json.GetProperty("punches")[0];
        var id = row.GetProperty("id").GetGuid();
        var version = row.GetProperty("rowVersion").GetInt64();
        var reader = await UserWithAsync(w.Company, "hrm.unmapped_punches.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, $"{Base}/unmapped/{id}/discard", new { rowVersion = version }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(w.Hr, $"{Base}/unmapped/{id}/discard", new { rowVersion = version }, Ct)).Status);
        var twice = await _h.PostAsync(w.Hr, $"{Base}/unmapped/{id}/discard", new { rowVersion = version + 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.Status);
        Assert.Empty((await _h.GetAsync(w.Hr, $"{Base}/unmapped?companyId={w.Company}", Ct)).Json.GetProperty("punches").EnumerateArray());
    }

    // ------------------------------------------------------------------ consent and removal (ATT-20)

    [Fact]
    public async Task FR_HRM_ATT_20_mapping_a_device_user_needs_a_consent_date_not_in_the_future_while_biometric_is_on()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        var none = await MapAsync(w, device, "31");
        Assert.Equal(HttpStatusCode.BadRequest, none.Status);
        Assert.Contains("errors.hrm.device.consent_required", none.Body, StringComparison.Ordinal);
        var future = await MapAsync(w, device, "31", Iso(CompanyToday.AddDays(3)));
        Assert.Contains("errors.hrm.device.consent_in_future", future.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.device_user_maps where company_id = $1", Ct, w.Company));

        var ok = await MapAsync(w, device, "31", Iso(CompanyToday.AddDays(-2)));
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        Assert.Equal(Iso(CompanyToday.AddDays(-2)), await _h.ScalarAsync<string>("select consent_at::date::text from hrm.device_user_maps where company_id = $1", Ct, w.Company));
        Assert.Equal(HttpStatusCode.Conflict, (await MapAsync(w, device, "31", Iso(CompanyToday))).Status); // the number is taken
        // A badge or PIN number for all devices (the kiosk's) involves no biometrics and needs no consent.
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, null, "BADGE-1")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await MapAsync(w, device, "32", "2024-01-01", employeeNo: "NO-SUCH")).Status);
    }

    [Fact]
    public async Task FR_HRM_ATT_20_leaving_an_employment_ends_the_mapping_and_makes_a_removal_task_that_hr_confirms()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w, "SN-LEAVE");
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, null, "BADGE-2")).Status);
        var mapped = await MapAsync(w, device, "41", "2024-01-01");
        Assert.Equal(HttpStatusCode.Created, mapped.Status);
        var mapId = mapped.Json.GetProperty("map").GetProperty("id").GetGuid();

        // The employment ends on the 10th: the event of S1.20c/S1.25 reaches the dispatcher, which ends the mappings from the day after the last day.
        var lastDay = CompanyToday.AddDays(10);
        await _h.ExecAsync("""
            insert into hrm.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers)
            values ($1, now(), 'hrm.employee_separated', 'hrm.employment', $2, $3::jsonb, '{}'::jsonb)
            """, Ct, Guid.CreateVersion7(), w.Employment, JsonSerializer.Serialize(new { employmentId = w.Employment, personId = Guid.NewGuid(), companyId = w.Company, eventId = Guid.NewGuid(), reasonCode = "resign", separationDate = Iso(lastDay) }));
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
        // The outbox is shared with the other tests of this customer: dispatch until this event has been handled.
        for (var pass = 0; pass < 40 && await _h.ScalarAsync<bool>("select valid_to is null from hrm.device_user_maps where id = $1", Ct, mapId); pass++)
        {
            await dispatcher.DispatchOnceAsync(_h.Code, Ct);
        }


        Assert.Equal(Iso(lastDay.AddDays(1)), await _h.ScalarAsync<string>("select valid_to::text from hrm.device_user_maps where id = $1", Ct, mapId));
        Assert.Equal(Iso(lastDay.AddDays(1)), await _h.ScalarAsync<string>("select valid_to::text from hrm.device_user_maps where device_user_no = 'BADGE-2'", Ct));
        Assert.True(await _h.ScalarAsync<bool>("select removal_due_at is not null and removal_done_at is null from hrm.device_user_maps where id = $1", Ct, mapId));
        Assert.True(await _h.ScalarAsync<bool>("select removal_due_at is null from hrm.device_user_maps where device_user_no = 'BADGE-2'", Ct)); // a badge number has nothing on a device
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.device_user.removal_due' and aggregate_id = $1", Ct, mapId));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.device_user_maps' and entity_id = $1", Ct, mapId) >= 2); // the end is audited

        // The console lists the task; HR removes the user from the device and confirms; it cannot be confirmed twice.
        var pending = await _h.GetAsync(w.Hr, $"{Base}/device-maps?companyId={w.Company}&state=removal_pending", Ct);
        var row = Assert.Single(pending.Json.GetProperty("maps").EnumerateArray());
        Assert.Equal(mapId, row.GetProperty("id").GetGuid());
        var version = row.GetProperty("rowVersion").GetInt64();
        var viewer = await UserWithAsync(w.Company, "hrm.device_user_maps.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(viewer, $"{Base}/device-maps/{mapId}/removal-done", new { rowVersion = version }, Ct)).Status);
        var done = await _h.PostAsync(w.Hr, $"{Base}/device-maps/{mapId}/removal-done", new { rowVersion = version }, Ct);
        Assert.Equal(HttpStatusCode.OK, done.Status);
        Assert.Equal(JsonValueKind.String, done.Json.GetProperty("removalDoneAt").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(w.Hr, $"{Base}/device-maps/{mapId}/removal-done", new { rowVersion = version + 1 }, Ct)).Status);
        Assert.Empty((await _h.GetAsync(w.Hr, $"{Base}/device-maps?companyId={w.Company}&state=removal_pending", Ct)).Json.GetProperty("maps").EnumerateArray());
    }

    [Fact]
    public async Task FR_HRM_ATT_03_ending_a_mapping_by_hand_makes_the_same_removal_task_and_later_punches_of_the_number_are_unmapped()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        var mapped = await MapAsync(w, device, "51", "2024-01-01");
        var map = mapped.Json.GetProperty("map");
        var ended = await _h.PostAsync(w.Hr, $"{Base}/device-maps/{map.GetProperty("id").GetGuid()}/end", new { endDate = Iso(CompanyToday.AddDays(1)), rowVersion = map.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.OK, ended.Status);
        Assert.Equal(JsonValueKind.String, ended.Json.GetProperty("removalDueAt").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(w.Hr, $"{Base}/device-maps/{map.GetProperty("id").GetGuid()}/end", new { endDate = Iso(CompanyToday.AddDays(1)), rowVersion = ended.Json.GetProperty("rowVersion").GetInt64() }, Ct)).Status);
        var pusher = await PusherAsync(w.Company);
        _h.Clock.Advance(TimeSpan.FromDays(3));
        var after = await PushAsync(pusher, device, Records(("51", _h.Clock.UtcNow.AddMinutes(-5), "in")));
        Assert.Equal(1, JsonDocument.Parse(after.Body).RootElement.GetProperty("unmapped").GetInt32());
    }

    // ------------------------------------------------------------------ pull driver and the sync job (ATT-02, ATT-03)

    private async Task<Guid> PullDeviceAsync(World w, FakeZkDevice device, string serial, string key = "0")
    {
        var made = await _h.PostAsync(w.Hr, $"{Base}/devices", new { companyId = w.Company, locationId = w.Location, serialNo = serial, provider = "zkteco", host = "127.0.0.1", port = device.Port, protocol = "tcp", credentials = key }, Ct);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        return made.Json.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task FR_HRM_ATT_03_the_sync_job_reads_a_zkteco_device_with_its_key_keeps_original_times_removes_repeats_and_asks_for_no_user_or_template_data()
    {
        var w = await WorldAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
        var local = TimeZoneInfo.ConvertTime(_h.Clock.UtcNow.AddHours(-8), zone).DateTime;
        var local2 = local.AddHours(7);
        await using var zk = new FakeZkDevice(123456, [("61", local, 0), ("61", local2, 1), ("99", local, 0)]);
        var device = await PullDeviceAsync(w, zk, "SN-ZK-1", "123456");
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, device, "61", "2024-01-01")).Status);

        await _h.ExecAsync("update hrm.attendance_devices set created_at = created_at - interval '3 days' where id = $1", Ct, device); // an old device that is read now stays online
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        var run = await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("succeeded", await rig.StatusAsync(run, Ct));
        Assert.Equal(2L, await EventsAsync(w.Employment, "source = 'biometric'"));
        var times = await _h.ScalarAsync<string>("select string_agg(extract(epoch from event_at)::bigint::text || direction, ',' order by event_at) from hrm.attendance_events where employment_id = $1", Ct, w.Employment);
        var expected = $"{new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero).ToUnixTimeSeconds()}in,{new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local2, zone), TimeSpan.Zero).ToUnixTimeSeconds()}out";
        Assert.Equal(expected, times);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.unmapped_punches where device_user_no = '99'", Ct)); // the unknown user of the device waits
        Assert.True(await _h.ScalarAsync<bool>("select last_sync_at is not null and last_error is null and status = 'active' and sync_cursor_at is not null from hrm.attendance_devices where id = $1", Ct, device));

        // Only the commands that read attendance records were sent: no user list and no template request.
        lock (zk.Commands)
        {
            Assert.Empty(zk.Commands.Except(new ushort[] { 1000, 1102, 50, 13, 1502, 1001 }));
            Assert.Contains((ushort)13, zk.Commands);
            Assert.DoesNotContain((ushort)9, zk.Commands); // the template request of the protocol
            Assert.DoesNotContain((ushort)8, zk.Commands); // the user list of the protocol
        }

        // A second read of the same log (the device keeps what it was told to keep) stores nothing new.
        _h.Clock.Advance(TimeSpan.FromMinutes(6));
        var again = await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("succeeded", await rig.StatusAsync(again, Ct));
        Assert.Equal(2L, await EventsAsync(w.Employment));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.unmapped_punches where device_user_no = '99'", Ct));
    }

    [Fact]
    public async Task FR_HRM_ATT_02_a_device_that_cannot_be_read_shows_its_last_error_and_goes_offline_and_a_wrong_key_is_reported_without_the_key()
    {
        var w = await WorldAsync();
        await using var zk = new FakeZkDevice(777, [("1", DateTime.UtcNow.AddHours(-1), 0)]);
        var wrong = await PullDeviceAsync(w, zk, "SN-ZK-WRONG", "111"); // not the device's key
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        var row = (await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct)).Json.GetProperty("devices")[0];
        Assert.Equal("errors.hrm.device.auth_failed", row.GetProperty("lastError").GetString());
        Assert.DoesNotContain("111", row.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.False(row.GetProperty("online").GetBoolean());
        Assert.Equal("offline", row.GetProperty("status").GetString());
        Assert.Equal(wrong, row.GetProperty("id").GetGuid());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.attendance_events where company_id = $1", Ct, w.Company));
    }

    [Fact]
    public async Task FR_HRM_ATT_02_sync_now_asks_for_a_read_at_the_next_run_and_is_refused_for_a_pushing_device()
    {
        var w = await WorldAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
        await using var zk = new FakeZkDevice(null, [("71", TimeZoneInfo.ConvertTime(_h.Clock.UtcNow.AddHours(-2), zone).DateTime, 0)], recordSize16: true);
        var pull = await PullDeviceAsync(w, zk, "SN-ZK-NOW");
        var (push, _) = await PushDeviceAsync(w);
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, pull, "71", "2024-01-01")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(w.Hr, $"{Base}/devices/{push}/sync", null, Ct)).Status);

        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal(1L, await EventsAsync(w.Employment));
        // Not due again for five minutes... unless someone asks.
        lock (zk.Commands)
        {
            zk.Commands.Clear();
        }

        await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        lock (zk.Commands)
        {
            Assert.Empty(zk.Commands);
        }

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(w.Hr, $"{Base}/devices/{pull}/sync", null, Ct)).Status);
        await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        lock (zk.Commands)
        {
            Assert.Contains((ushort)13, zk.Commands);
        }

        Assert.Equal(JsonValueKind.Null, (await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct)).Json.GetProperty("devices").EnumerateArray().First(d => d.GetProperty("id").GetGuid() == pull).GetProperty("syncRequestedAt").ValueKind);
    }

    [Fact]
    public async Task FR_HRM_ATT_02_a_pushing_device_that_stops_sending_shows_offline_and_online_again_when_it_sends()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, device, "21", "2024-01-01")).Status);
        var pusher = await PusherAsync(w.Company);
        Assert.Equal(HttpStatusCode.OK, (await PushAsync(pusher, device, Records(("21", _h.Clock.UtcNow.AddMinutes(-2), "in")))).Status);
        JsonElement Row(Answer a) => a.Json.GetProperty("devices").EnumerateArray().First(d => d.GetProperty("id").GetGuid() == device);
        Assert.True(Row(await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct)).GetProperty("online").GetBoolean());

        _h.Clock.Advance(TimeSpan.FromMinutes(31));
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        await rig.QueueAsync("hrm.device_sync", ct: Ct);
        await rig.RunUntilIdleAsync(rig.Worker(), Ct);
        var silent = Row(await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct));
        Assert.False(silent.GetProperty("online").GetBoolean());
        Assert.Equal("offline", silent.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await PushAsync(pusher, device, Records(("21", _h.Clock.UtcNow.AddMinutes(-1), "out")))).Status);
        var back = Row(await _h.GetAsync(w.Hr, $"{Base}/devices?companyId={w.Company}", Ct));
        Assert.True(back.GetProperty("online").GetBoolean());
        Assert.Equal("active", back.GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------ the punch log (item 9)

    [Fact]
    public async Task FR_HRM_ATT_02_the_punch_log_filters_by_source_employee_and_date_and_pages()
    {
        var w = await WorldAsync();
        var (device, _) = await PushDeviceAsync(w);
        Assert.Equal(HttpStatusCode.Created, (await MapAsync(w, device, "91", "2024-01-01")).Status);
        var pusher = await PusherAsync(w.Company);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PushAsync(pusher, device, Records(("91", _h.Clock.UtcNow.AddHours(-10 + (i * 2)), i % 2 == 0 ? "in" : "out")))).Status);
        }

        var all = await _h.GetAsync(w.Hr, $"{Base}/punch-log?companyId={w.Company}&source=biometric&limit=2", Ct);
        Assert.Equal(2, all.Json.GetProperty("events").GetArrayLength());
        Assert.Equal(w.EmployeeNo, all.Json.GetProperty("events")[0].GetProperty("employeeNo").GetString());
        var next = all.Json.GetProperty("next").GetString()!;
        var page2 = await _h.GetAsync(w.Hr, $"{Base}/punch-log?companyId={w.Company}&source=biometric&limit=2&after={Uri.EscapeDataString(next)}", Ct);
        Assert.Equal(1, page2.Json.GetProperty("events").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page2.Json.GetProperty("next").ValueKind);
        Assert.Empty((await _h.GetAsync(w.Hr, $"{Base}/punch-log?companyId={w.Company}&source=web", Ct)).Json.GetProperty("events").EnumerateArray());
        Assert.Empty((await _h.GetAsync(w.Hr, $"{Base}/punch-log?companyId={w.Company}&employeeNo=NOBODY", Ct)).Json.GetProperty("events").EnumerateArray());
        Assert.Equal(3, (await _h.GetAsync(w.Hr, $"{Base}/punch-log?companyId={w.Company}&employeeNo={w.EmployeeNo}&from={Iso(CompanyToday.AddDays(-2))}&to={Iso(CompanyToday.AddDays(1))}", Ct)).Json.GetProperty("events").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(w.Hr, $"{Base}/punch-log?source=bogus", Ct)).Status);
        Assert.Equal(device, (await _h.GetAsync(w.Hr, $"{Base}/punch-log?companyId={w.Company}", Ct)).Json.GetProperty("events")[0].GetProperty("deviceId").GetGuid());
    }
}
