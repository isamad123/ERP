#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.Rosters;

/// <summary>
/// Shifts, weekly-off patterns, the shift policy, rosters and their publication (slice S1.28a, Build items 1 to 5) through the real API
/// pipeline, the real access engine and a real PostgreSQL: FR-HRM-ATT-06 (six shift types, versions from a date, weekly-off patterns
/// across a month boundary) and FR-HRM-ATT-07 (a roster for 40 people built by group, copied forward, checked, published once per
/// person; a change after publication needs a reason and tells again).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class RosterTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Shifts = "/api/hrm/shifts";
    private const string Rosters = "/api/hrm/rosters";
    private const string Employments = "/api/hrm/employments";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> PersonAsync() => (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Admin, Guid Planner);

    /// <summary>A company of its own, a tenant administrator (hires, policies) and a rostering planner with exactly the S1.28a permissions.</summary>
    private async Task<Org> OrgAsync(string[]? permissions = null)
    {
        var company = await _h.CompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "hr_admin");
        var planner = await _h.UserAsync(Ct);
        string[] all =
        [
            "hrm.shifts.read", "hrm.shifts.create", "hrm.shifts.update", "hrm.rosters.read", "hrm.rosters.create", "hrm.rosters.update", "hrm.rosters.publish", "hrm.rosters.amend",
            "hrm.roster_lines.read", "hrm.shift_patterns.read", "hrm.employments.read", "hrm.employment_assignments.read",
        ];
        await _h.AssignAsync(planner, await _h.RoleAsync(Ct, permissions ?? all, [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        return new Org(company, await _h.PlaceAsync(company, Ct), admin, planner);
    }

    private async Task<Guid> HireAsync(Org o, string joining = "2026-01-05", Guid? userRef = null)
    {
        var answer = await _h.PostAsync(o.Admin, Employments, EmploymentTestSupport.HireBody(o.Company, o.Place, person: await PersonAsync(), joining: joining), Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var id = answer.Json.GetProperty("id").GetGuid();
        if (userRef is { } user)
        {
            await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, user);
        }

        return id;
    }

    private async Task<(Guid Id, long Version)> ShiftAsync(Org o, string type = "fixed", string start = "08:00", string end = "16:00", bool crosses = false, int full = 420, string? code = null, object? extra = null)
    {
        code ??= "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var body = new Dictionary<string, object?>
        {
            ["companyId"] = o.Company, ["code"] = code, ["nameI18n"] = new { en = "Shift " + code, ur = "شفٹ " + code }, ["shiftType"] = type,
            ["startTime"] = start, ["endTime"] = end, ["crossesMidnight"] = crosses, ["minFullDayMinutes"] = full,
        };
        foreach (var (k, v) in (extra is null ? [] : JsonSerializer.SerializeToElement(extra, JsonSerializerOptions.Web).EnumerateObject().Select(p => (p.Name, (object?)p.Value))))
        {
            body[k] = v;
        }

        var answer = await _h.PostAsync(o.Planner, Shifts, body, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Json.GetProperty("rowVersion").GetInt64());
    }

    /// <summary>A shift policy for every employee of the company from long ago (assignment rule: all employee types).</summary>
    private async Task ShiftPolicyAsync(Org o, object parameters, DateOnly? from = null)
    {
        var start = from ?? DateOnly.FromDayNumber(DateOnly.FromDateTime(new DateTime(2026, 1, 1)).DayNumber);
        var (policy, _) = await _h.ActivePolicyAsync(o.Admin, o.Company, "shift", parameters, start, Ct);
        var set = await _h.ActivateSetAsync(o.Admin, o.Company, "shift", start, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent", "probation", "contract" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    private async Task<(Guid Id, long Version)> RosterAsync(Org o, string from, string to, Guid? unit = null, string name = "March")
    {
        var answer = await _h.PostAsync(o.Planner, Rosters, new { companyId = o.Company, name, orgUnitId = unit, periodStart = from, periodEnd = to }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Json.GetProperty("rowVersion").GetInt64());
    }

    private Task<long> CountAsync(string sql, params object?[] values) => _h.ScalarAsync<long>(sql, Ct, values);

    private async Task<JsonElement> ChecksAsync(Org o, Guid roster)
    {
        var checks = await _h.GetAsync(o.Planner, $"{Rosters}/{roster}/checks", Ct);
        Assert.True(checks.Status == HttpStatusCode.OK, checks.Body);
        return checks.Json;
    }

    private static string[] Codes(JsonElement checks) => [.. checks.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("code").GetString()!)];

    // ------------------------------------------------------------------ shifts (FR-HRM-ATT-06)

    [Fact]
    public async Task FR_HRM_ATT_06_each_of_the_six_shift_types_is_saved_and_read_back()
    {
        var o = await OrgAsync();
        var fixedShift = await ShiftAsync(o);
        var flexible = await ShiftAsync(o, "flexible", "07:00", "19:00", false, 480, extra: new { coreStart = "10:00", coreEnd = "15:00" });
        var rotating = await ShiftAsync(o, "rotating", "06:00", "14:00", false, 420);
        var split = await ShiftAsync(o, "split", "08:00", "20:00", false, 480, extra: new { segments = new[] { new { start = "08:00", end = "12:00", kind = "work" }, new { start = "16:00", end = "20:00", kind = "work" } } });
        var night = await ShiftAsync(o, "night", "22:00", "06:00", true, 420);
        var continuous = await ShiftAsync(o, "continuous", "08:00", "08:00", true, 1440);

        foreach (var (id, type) in new[] { (fixedShift.Id, "fixed"), (flexible.Id, "flexible"), (rotating.Id, "rotating"), (split.Id, "split"), (night.Id, "night"), (continuous.Id, "continuous") })
        {
            var read = await _h.GetAsync(o.Planner, $"{Shifts}/{id}", Ct);
            Assert.Equal(HttpStatusCode.OK, read.Status);
            Assert.Equal(type, read.Json.GetProperty("shiftType").GetString());
        }

        var back = (await _h.GetAsync(o.Planner, $"{Shifts}/{split.Id}", Ct)).Json;
        Assert.Equal(2, back.GetProperty("segments").GetArrayLength());
        Assert.StartsWith("10:00", (await _h.GetAsync(o.Planner, $"{Shifts}/{flexible.Id}", Ct)).Json.GetProperty("coreStart").GetString(), StringComparison.Ordinal);
        Assert.True((await _h.GetAsync(o.Planner, $"{Shifts}/{night.Id}", Ct)).Json.GetProperty("crossesMidnight").GetBoolean());
    }

    [Fact]
    public async Task FR_HRM_ATT_06_wrong_times_repeated_codes_and_missing_permissions_are_refused()
    {
        var o = await OrgAsync();
        var bad = await _h.PostAsync(o.Planner, Shifts, new { companyId = o.Company, code = "BAD", nameI18n = new { en = "Bad" }, shiftType = "fixed", startTime = "17:00", endTime = "09:00", crossesMidnight = false, minFullDayMinutes = 400 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal("errors.hrm.shift.end_not_after_start", bad.Json.GetProperty("errors").GetProperty("endTime")[0].GetString());
        var code = "DUP" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await ShiftAsync(o, code: code);
        var again = await _h.PostAsync(o.Planner, Shifts, new { companyId = o.Company, code = code.ToLowerInvariant(), nameI18n = new { en = "Again" }, shiftType = "fixed", startTime = "08:00", endTime = "16:00", crossesMidnight = false, minFullDayMinutes = 400 }, Ct);
        Assert.Equal("errors.hrm.shift.code_taken", again.Json.GetProperty("errors").GetProperty("code")[0].GetString());

        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.shifts.read"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        var denied = await _h.PostAsync(reader, Shifts, new { companyId = o.Company, code = "NOPE", nameI18n = new { en = "No" }, shiftType = "fixed", startTime = "08:00", endTime = "16:00", crossesMidnight = false, minFullDayMinutes = 400 }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal(0, await CountAsync("select count(*) from hrm.shifts where company_id = $1 and code = 'NOPE'", o.Company));
    }

    [Fact]
    public async Task FR_HRM_ATT_06_a_change_from_a_date_is_a_new_version_and_the_old_row_is_kept()
    {
        var o = await OrgAsync();
        var (id, version) = await ShiftAsync(o, code: "VER" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());
        var noDate = await _h.PutAsync(o.Planner, $"{Shifts}/{id}", new { startTime = "09:00", endTime = "17:00", rowVersion = version }, Ct);
        Assert.Equal("errors.hrm.shift.effective_from_required", noDate.Json.GetProperty("errors").GetProperty("effectiveFrom")[0].GetString());

        var changed = await _h.PutAsync(o.Planner, $"{Shifts}/{id}", new { startTime = "09:00", endTime = "17:00", effectiveFrom = "2026-06-01", rowVersion = version }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var next = changed.Json.GetProperty("id").GetGuid();
        Assert.NotEqual(id, next);
        Assert.Equal(id, changed.Json.GetProperty("previousShiftId").GetGuid());
        Assert.Equal("2026-06-01", changed.Json.GetProperty("validFrom").GetString());
        var old = (await _h.GetAsync(o.Planner, $"{Shifts}/{id}", Ct)).Json;
        Assert.Equal("2026-05-31", old.GetProperty("validTo").GetString());
        Assert.StartsWith("08:00", old.GetProperty("startTime").GetString(), StringComparison.Ordinal);

        // The old row is history: it cannot be changed again, a name change of the version in force is made in place.
        var stale = await _h.PutAsync(o.Planner, $"{Shifts}/{id}", new { endTime = "18:00", effectiveFrom = "2026-08-01", rowVersion = old.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var rename = await _h.PutAsync(o.Planner, $"{Shifts}/{next}", new { nameI18n = new { en = "Renamed" }, rowVersion = changed.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(rename.Status == HttpStatusCode.OK, rename.Body);
        Assert.Equal(next, rename.Json.GetProperty("id").GetGuid());
        var earlier = await _h.PutAsync(o.Planner, $"{Shifts}/{next}", new { endTime = "18:00", effectiveFrom = "2026-05-01", rowVersion = rename.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal("errors.hrm.shift.effective_from_order", earlier.Json.GetProperty("errors").GetProperty("effectiveFrom")[0].GetString());
        Assert.Equal(2, await CountAsync("select count(*) from hrm.shifts where company_id = $1 and lower(code) = lower((select code from hrm.shifts where id = $2))", o.Company, id));
    }

    // ------------------------------------------------------------------ weekly off and the shift policy (FR-HRM-ATT-06)

    [Fact]
    public async Task FR_HRM_ATT_06_a_rotating_weekly_off_pattern_gives_the_right_days_across_a_month_boundary()
    {
        var o = await OrgAsync();
        var employment = await HireAsync(o);
        var none = await _h.GetAsync(o.Planner, $"{Employments}/{employment}/weekly-off?from=2026-02-27&to=2026-03-16", Ct);
        Assert.Equal(HttpStatusCode.OK, none.Status);
        Assert.Empty(none.Json.GetProperty("offDays").EnumerateArray()); // no policy: no weekly off, never a default
        Assert.Equal("no_rule_matched", none.Json.GetProperty("reason").GetString());

        await ShiftPolicyAsync(o, new { weeklyOffKind = "rotating", cycleDays = 6, offDays = new[] { 5, 6 }, anchorDate = "2026-02-27", graceMinutes = 10 });
        var answer = await _h.GetAsync(o.Planner, $"{Employments}/{employment}/weekly-off?from=2026-02-27&to=2026-03-16", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        Assert.Equal(["2026-03-03", "2026-03-04", "2026-03-09", "2026-03-10", "2026-03-15", "2026-03-16"], answer.Json.GetProperty("offDays").EnumerateArray().Select(d => d.GetString()!));
        Assert.Equal(10, answer.Json.GetProperty("graceMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, answer.Json.GetProperty("minimumRestMinutes").ValueKind);
    }

    [Fact]
    public async Task FR_HRM_ATT_06_the_shift_policy_refuses_a_bad_shape_and_an_incomplete_pattern_stops_a_roster_assignment()
    {
        var o = await OrgAsync();
        var bad = await _h.PostAsync(o.Admin, PolicyTestSupport.Policies, new { companyId = o.Company, policyType = "shift", code = "BADSHIFT", name = new { en = "Bad" }, parameters = new { weeklyOffKind = "monthly" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        // A rotating pattern without its anchor passes the schema (shape only) and is refused when it is used.
        var employment = await HireAsync(o);
        await ShiftPolicyAsync(o, new { weeklyOffKind = "rotating", cycleDays = 6, offDays = new[] { 5 } });
        var shift = await ShiftAsync(o);
        var roster = await RosterAsync(o, "2026-03-02", "2026-03-08");
        var assign = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, employmentIds = new[] { employment } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, assign.Status);
        Assert.Equal("errors.hrm.shift_pattern.anchor_required", assign.Json.GetProperty("errors").GetProperty("shiftPolicy")[0].GetString());
        Assert.Equal(0, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1", roster.Id));
    }

    // ------------------------------------------------------------------ rosters (FR-HRM-ATT-07)

    [Fact]
    public async Task FR_HRM_ATT_07_a_roster_for_40_people_is_built_by_group_copied_forward_published_once_per_person_and_changed_only_with_a_reason()
    {
        var o = await OrgAsync();
        await ShiftPolicyAsync(o, new { weeklyOffKind = "fixed", offDays = new[] { 6, 7 } });
        var people = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            var user = await _h.UserAsync(Ct);
            people.Add(await HireAsync(o, userRef: user));
        }

        var shift = await ShiftAsync(o);
        var roster = await RosterAsync(o, "2026-03-02", "2026-03-29", o.Place.OrgUnit);
        var assigned = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, everyone = true }, Ct);
        Assert.True(assigned.Status == HttpStatusCode.OK, assigned.Body);
        Assert.Equal(40, assigned.Json.GetProperty("people").GetInt32());
        Assert.Equal(40 * 28, assigned.Json.GetProperty("created").GetInt32());
        Assert.Equal(40 * 20, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and day_type = 'working' and shift_id = $2", roster.Id, shift.Id));
        Assert.Equal(40 * 8, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and day_type = 'weekly_off' and shift_id is null", roster.Id));

        // Assigning again keeps what is there; "replace" rewrites it.
        var kept = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, everyone = true }, Ct);
        Assert.Equal(0, kept.Json.GetProperty("created").GetInt32());
        Assert.Equal(40 * 28, kept.Json.GetProperty("kept").GetInt32());

        // Copy forward: the same people, four weeks later, the weekly off days where the pattern puts them.
        var copied = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/copy-forward", new { name = "April" }, Ct);
        Assert.True(copied.Status == HttpStatusCode.Created, copied.Body);
        var copy = copied.Json.GetProperty("id").GetGuid();
        Assert.Equal("2026-03-30", copied.Json.GetProperty("periodStart").GetString());
        Assert.Equal("2026-04-26", copied.Json.GetProperty("periodEnd").GetString());
        Assert.Equal(roster.Id, copied.Json.GetProperty("copiedFromRosterId").GetGuid());
        Assert.Equal(40 * 28, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1", copy));
        Assert.Equal(40 * 8, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and day_type = 'weekly_off'", copy));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and extract(isodow from work_date) in (6, 7) and day_type = 'working'", copy));

        // Publication: the checks pass, every person is told once, the lines are published.
        var checks = await ChecksAsync(o, roster.Id);
        Assert.Equal(0, checks.GetProperty("errors").GetInt32());
        var published = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/publish", new { rowVersion = roster.Version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
        Assert.Equal("published", published.Json.GetProperty("status").GetString());
        Assert.Equal(40, published.Json.GetProperty("people").GetInt32());
        Assert.Equal(40 * 28, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and roster_status = 'published'", roster.Id));
        Assert.Equal(40, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.roster.published' and aggregate_id = $1", roster.Id));
        Assert.Equal(40, await CountAsync("select count(distinct payload->>'employment_id') from hrm.outbox_messages where event_type = 'hrm.roster.published' and aggregate_id = $1", roster.Id));
        Assert.Equal(40, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.roster.published' and aggregate_id = $1 and payload->>'employee_user_id' is not null", roster.Id));
        var again = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/publish", new { rowVersion = published.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal(40, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.roster.published' and aggregate_id = $1", roster.Id));

        // After publication a normal edit is refused, a change needs a reason and tells that person again.
        var someone = people[7];
        var changeDay = new DateOnly(2026, 3, 11);
        var noReason = await _h.PutAsync(o.Planner, $"{Rosters}/{roster.Id}/days", new { changes = new[] { new { employmentId = someone, workDate = Iso(changeDay), dayType = "weekly_off" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
        Assert.Equal("errors.hrm.roster.reason_required", noReason.Json.GetProperty("errors").GetProperty("changes[0].reason")[0].GetString());
        var assignLate = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, employmentIds = new[] { someone }, replace = true }, Ct);
        Assert.Equal("errors.hrm.roster.not_draft", assignLate.Json.GetProperty("title").GetString());
        var changed = await _h.PutAsync(o.Planner, $"{Rosters}/{roster.Id}/days", new { reason = "Swapped with a colleague", changes = new[] { new { employmentId = someone, workDate = Iso(changeDay), dayType = "weekly_off" } } }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.Equal("weekly_off", await _h.ScalarAsync<string>("select day_type from hrm.roster_lines where roster_id = $1 and employment_id = $2 and work_date = $3", Ct, roster.Id, someone, changeDay));
        Assert.Equal("Swapped with a colleague", await _h.ScalarAsync<string>("select reason from hrm.roster_lines where roster_id = $1 and employment_id = $2 and work_date = $3", Ct, roster.Id, someone, changeDay));
        Assert.Equal(41, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.roster.published' and aggregate_id = $1", roster.Id));
        Assert.Equal(2, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.roster.published' and aggregate_id = $1 and payload->>'employment_id' = $2", roster.Id, someone.ToString()));
        var removed = await _h.PutAsync(o.Planner, $"{Rosters}/{roster.Id}/days", new { reason = "x", changes = new[] { new { employmentId = someone, workDate = Iso(changeDay), remove = true } } }, Ct);
        Assert.Equal("errors.hrm.roster.remove_published", removed.Json.GetProperty("errors").GetProperty("changes[0].remove")[0].GetString());
    }

    [Fact]
    public async Task FR_HRM_ATT_07_a_group_by_grade_or_custom_field_is_resolved_to_individual_lines_and_the_unit_group_includes_sub_units()
    {
        var o = await OrgAsync();
        var inUnit = await HireAsync(o);
        var other = await HireAsync(o);
        await _h.ExecAsync("update hrm.employments set custom = '{\"crew\":\"A\"}' where id = $1", Ct, inUnit);
        var shift = await ShiftAsync(o);
        var roster = await RosterAsync(o, "2026-03-02", "2026-03-04");
        var bad = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id }, Ct);
        Assert.Equal("errors.hrm.roster.target_required", bad.Json.GetProperty("errors").GetProperty("employmentIds")[0].GetString());
        var crew = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, customField = new { key = "crew", value = "A" } }, Ct);
        Assert.True(crew.Status == HttpStatusCode.OK, crew.Body);
        Assert.Equal(1, crew.Json.GetProperty("people").GetInt32());
        Assert.Equal(3, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and employment_id = $2", roster.Id, inUnit));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and employment_id = $2", roster.Id, other));
        var unit = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, orgUnitId = o.Place.OrgUnit }, Ct);
        Assert.Equal(2, unit.Json.GetProperty("people").GetInt32());
        Assert.Equal(3, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and employment_id = $2", roster.Id, other));
        var foreign = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = shift.Id, employmentIds = new[] { Guid.NewGuid() } }, Ct);
        Assert.Equal("errors.hrm.roster.people_unknown", foreign.Json.GetProperty("errors").GetProperty("employmentIds")[0].GetString());
    }

    [Fact]
    public async Task FR_HRM_ATT_07_a_shift_changed_from_a_date_is_the_one_assigned_for_the_days_after_it()
    {
        var o = await OrgAsync();
        var employment = await HireAsync(o);
        var (id, version) = await ShiftAsync(o);
        var changed = await _h.PutAsync(o.Planner, $"{Shifts}/{id}", new { startTime = "09:00", endTime = "17:00", effectiveFrom = "2026-03-04", rowVersion = version }, Ct);
        var next = changed.Json.GetProperty("id").GetGuid();
        var roster = await RosterAsync(o, "2026-03-02", "2026-03-06");
        var assigned = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/assign", new { shiftId = id, employmentIds = new[] { employment } }, Ct);
        Assert.True(assigned.Status == HttpStatusCode.OK, assigned.Body);
        Assert.Equal(2, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and shift_id = $2", roster.Id, id));
        Assert.Equal(3, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1 and shift_id = $2 and work_date >= '2026-03-04'", roster.Id, next));
    }

    [Fact]
    public async Task FR_HRM_ATT_07_the_checks_report_two_shifts_a_short_rest_work_on_a_rest_day_and_days_outside_employment_together()
    {
        var o = await OrgAsync();
        var employment = await HireAsync(o, joining: "2026-03-03");
        var day = await ShiftAsync(o);
        var night = await ShiftAsync(o, "night", "22:00", "06:00", true, 420);
        var first = await RosterAsync(o, "2026-03-02", "2026-03-08", name: "Week 10");
        var second = await RosterAsync(o, "2026-03-02", "2026-03-08", name: "Week 10 again");

        // Night on the 4th and the day shift on the 5th leaves 2 hours of rest, but no minimum is set yet, so nothing is reported.
        await ChangeAsync(o, first.Id, (employment, "2026-03-04", "working", night.Id, null), (employment, "2026-03-05", "working", day.Id, null));
        Assert.Empty(Codes(await ChecksAsync(o, first.Id)));

        // A second roster gives the same person a shift on the 5th, a rest day and a day before the employment began.
        await ShiftPolicyAsync(o, new { weeklyOffKind = "fixed", offDays = new[] { 6 }, minRestMinutes = 660 });
        await ChangeAsync(o, second.Id, (employment, "2026-03-05", "working", night.Id, null));
        await ChangeAsync(o, first.Id, (employment, "2026-03-07", "working", day.Id, null), (employment, "2026-03-02", "working", day.Id, null));
        var checks = await ChecksAsync(o, first.Id);
        var messages = checks.GetProperty("messages").EnumerateArray().Select(m => (Code: m.GetProperty("code").GetString()!, Severity: m.GetProperty("severity").GetString()!, Date: m.GetProperty("date").GetString()!)).ToList();
        Assert.Contains(("double_assignment", "error", "2026-03-05"), messages);
        Assert.Contains(("short_rest", "warning", "2026-03-05"), messages);
        Assert.Contains(("rest_day_work", "warning", "2026-03-07"), messages);
        Assert.Contains(("outside_employment", "error", "2026-03-02"), messages);
        Assert.Equal(2, checks.GetProperty("errors").GetInt32());
        Assert.Equal(2, checks.GetProperty("warnings").GetInt32());

        // Publishing is refused while there are errors, and says which.
        var refused = await _h.PostAsync(o.Planner, $"{Rosters}/{first.Id}/publish", new { rowVersion = first.Version }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.hrm.roster.checks_failed", refused.Json.GetProperty("title").GetString());
        Assert.Equal(2, refused.Json.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("severity").GetString() == "error"));
        Assert.Equal("draft", await _h.ScalarAsync<string>("select status from hrm.rosters where id = $1", Ct, first.Id));

        // A reason on the rest day clears that warning.
        await ChangeAsync(o, first.Id, (employment, "2026-03-07", "working", day.Id, "Plant shutdown cover"));
        Assert.DoesNotContain("rest_day_work", Codes(await ChecksAsync(o, first.Id)));
    }

    private async Task ChangeAsync(Org o, Guid roster, params (Guid Employment, string Date, string Type, Guid? Shift, string? Reason)[] changes)
    {
        var answer = await _h.PutAsync(o.Planner, $"{Rosters}/{roster}/days", new { changes = changes.Select(c => new { employmentId = c.Employment, workDate = c.Date, dayType = c.Type, shiftId = c.Shift, reason = c.Reason }).ToArray() }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
    }

    [Fact]
    public async Task FR_HRM_ATT_07_the_database_refuses_a_change_to_a_published_day_without_a_reason_and_any_delete()
    {
        var o = await OrgAsync();
        var employment = await HireAsync(o);
        var shift = await ShiftAsync(o);
        var roster = await RosterAsync(o, "2026-03-02", "2026-03-03");
        await ChangeAsync(o, roster.Id, (employment, "2026-03-02", "working", shift.Id, null));
        var published = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/publish", new { rowVersion = roster.Version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
        var update = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.roster_lines set day_type = 'weekly_off', shift_id = null where roster_id = $1", Ct, roster.Id));
        Assert.Equal("ck_roster_lines_published", update.ConstraintName);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("delete from hrm.roster_lines where roster_id = $1", Ct, roster.Id));
        Assert.Equal("ck_roster_lines_published", delete.ConstraintName);
        var insert = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(
            "insert into hrm.roster_lines (id, company_id, roster_id, employment_id, work_date, day_type, roster_status, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4, '2026-03-03', 'weekly_off', 'published', now(), $5, now(), $5)",
            Ct, Guid.CreateVersion7(), o.Company, roster.Id, employment, o.Planner));
        Assert.Equal("ck_roster_lines_published", insert.ConstraintName);
        // The shift rule of a day: a working day names a shift.
        var working = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(
            "insert into hrm.roster_lines (id, company_id, roster_id, employment_id, work_date, day_type, roster_status, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4, '2026-03-03', 'working', 'draft', now(), $5, now(), $5)",
            Ct, Guid.CreateVersion7(), o.Company, roster.Id, employment, o.Planner));
        Assert.Equal("ck_roster_lines_shift", working.ConstraintName);
    }

    [Fact]
    public async Task FR_HRM_ATT_07_permissions_data_scope_and_the_draft_rule_guard_every_roster_write()
    {
        var o = await OrgAsync();
        var shift = await ShiftAsync(o);
        var roster = await RosterAsync(o, "2026-03-02", "2026-03-08");
        var employment = await HireAsync(o);

        var viewer = await _h.UserAsync(Ct);
        await _h.AssignAsync(viewer, await _h.RoleAsync(Ct, ["hrm.rosters.read", "hrm.roster_lines.read", "hrm.rosters.create", "hrm.rosters.update"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(viewer, $"{Rosters}/{roster.Id}/publish", new { rowVersion = roster.Version }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(o.Planner, $"{Rosters}/{Guid.NewGuid()}/publish", new { rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/publish", new { }, Ct)).Status);
        Assert.Equal("errors.hrm.roster.nothing_to_publish", (await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/publish", new { rowVersion = roster.Version }, Ct)).Json.GetProperty("title").GetString());

        await ChangeAsync(o, roster.Id, (employment, "2026-03-02", "working", shift.Id, null));
        var published = await _h.PostAsync(o.Planner, $"{Rosters}/{roster.Id}/publish", new { rowVersion = roster.Version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);

        // Changing a published roster needs the amend permission as well as a reason.
        var noAmend = await _h.PutAsync(viewer, $"{Rosters}/{roster.Id}/days", new { reason = "swap", changes = new[] { new { employmentId = employment, workDate = "2026-03-02", dayType = "weekly_off" } } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, noAmend.Status);
        var rename = await _h.PutAsync(o.Planner, $"{Rosters}/{roster.Id}", new { name = "Renamed", rowVersion = published.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal("errors.hrm.roster.not_draft", rename.Json.GetProperty("title").GetString());

        // A planner who may not read the person's employment cannot roster them: the answer is the one for an unknown person.
        var narrow = await _h.UserAsync(Ct);
        await _h.AssignAsync(narrow, await _h.RoleAsync(Ct, ["hrm.rosters.read", "hrm.rosters.create", "hrm.rosters.update", "hrm.employments.read"], [("*", "all"), ("hrm.employments", "own")]), null, _h.Today.AddDays(-1), null, Ct);
        var draft = await RosterAsync(o, "2026-04-01", "2026-04-07", name: "April");
        var hidden = await _h.PostAsync(narrow, $"{Rosters}/{draft.Id}/assign", new { shiftId = shift.Id, employmentIds = new[] { employment } }, Ct);
        Assert.Equal("errors.hrm.roster.people_unknown", hidden.Json.GetProperty("errors").GetProperty("employmentIds")[0].GetString());
        Assert.Equal(0, await CountAsync("select count(*) from hrm.roster_lines where roster_id = $1", draft.Id));
    }
}
