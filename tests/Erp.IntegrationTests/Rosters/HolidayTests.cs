#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Rosters;

/// <summary>
/// Holidays with Hijri dates, optional holidays and seasonal (Ramzan) timing (slice S1.28b, Build items 6 to 8) through the real API pipeline,
/// the real access engine and a real PostgreSQL: FR-HRM-ATT-15 (a holiday shows its Hijri date and the company's offset moves it; an optional
/// holiday chosen beyond the limit or after the cut-off is refused) and FR-HRM-ATT-16 (a Ramzan range for one group changes the effective
/// shift of that group only, with the reason shown).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class HolidayTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private const string Shifts = "/api/hrm/shifts";
    private const string Rosters = "/api/hrm/rosters";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Admin, Guid Planner, Guid Settings);

    private async Task<Org> OrgAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "hr_admin");
        var planner = await _h.UserAsync(Ct);
        string[] all =
        [
            "hrm.shifts.read", "hrm.shifts.create", "hrm.rosters.read", "hrm.rosters.create", "hrm.rosters.update", "hrm.rosters.publish", "hrm.roster_lines.read",
            "hrm.employments.read", "hrm.employment_assignments.read", "hrm.holidays.read", "hrm.optional_holidays.choose", "hrm.optional_holiday_choices.read", "hrm.shift_effective.read",
        ];
        await _h.AssignAsync(planner, await _h.RoleAsync(Ct, all, [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        return new Org(company, await _h.PlaceAsync(company, Ct), admin, planner, await _h.UserAsync(Ct, null, "tenant_admin"));
    }

    private async Task SettingAsync(Org o, string key, object value)
    {
        var set = await _h.PutAsync(o.Settings, "/api/cfg/settings/" + key, new { layer = "company", scopeId = o.Company, value, reason = "test" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    /// <summary>A calendar with the holidays (date, kind, English name) set as the company's holiday calendar; returns the calendar and the holiday ids by date.</summary>
    private async Task<(Guid Calendar, Dictionary<string, Guid> Holidays)> CalendarAsync(Org o, params (string Date, string Kind, string Name)[] holidays)
    {
        var calendar = Guid.CreateVersion7();
        var by = o.Admin;
        await _h.ExecAsync("""
            insert into ref.calendars (id, code, name_i18n, source, is_active, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, '{"en":"Test calendar"}'::jsonb, 'tenant', true, now(), $3, now(), $3, 1)
            """, Ct, calendar, "CAL" + Guid.NewGuid().ToString("N")[..10], by);
        var ids = new Dictionary<string, Guid>();
        foreach (var (date, kind, name) in holidays)
        {
            var id = Guid.CreateVersion7();
            ids[date] = id;
            await _h.ExecAsync("""
                insert into ref.holidays (id, calendar_id, holiday_date, name_i18n, kind, is_tentative, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3::date, jsonb_build_object('en', $4::text), $5, false, now(), $6, now(), $6, 1)
                """, Ct, id, calendar, date, name, kind, by);
        }

        await _h.ExecAsync("update org.companies set holiday_calendar_id = $2 where id = $1", Ct, o.Company, calendar);
        return (calendar, ids);
    }

    private async Task<Guid> HireAsync(Org o, (Guid OrgUnit, Guid Branch)? place = null, string joining = "2025-01-06", Guid? user = null)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Admin, Employments, EmploymentTestSupport.HireBody(o.Company, place ?? o.Place, person: person, joining: joining), Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var id = answer.Json.GetProperty("id").GetGuid();
        if (user is { } u)
        {
            await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, u);
        }

        return id;
    }

    private async Task<Guid> EmployeeUserAsync(Guid employment)
    {
        var user = await _h.UserAsync(Ct, null, "employee");
        await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, employment, user);
        return user;
    }

    private async Task<Guid> ShiftAsync(Org o, string type = "fixed", string start = "08:00", string end = "16:00", object? segments = null)
    {
        var code = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var answer = await _h.PostAsync(o.Planner, Shifts, new
        {
            companyId = o.Company, code, nameI18n = new { en = "Shift " + code }, shiftType = type, startTime = start, endTime = end, crossesMidnight = false, minFullDayMinutes = 420, segments,
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    /// <summary>A published roster of the period with the shift for the people.</summary>
    private async Task<Guid> PublishedRosterAsync(Org o, string from, string to, Guid shift, params Guid[] people)
    {
        var created = await _h.PostAsync(o.Planner, Rosters, new { companyId = o.Company, name = "Season " + Guid.NewGuid().ToString("N")[..6], periodStart = from, periodEnd = to }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var roster = created.Json.GetProperty("id").GetGuid();
        var assigned = await _h.PostAsync(o.Planner, $"{Rosters}/{roster}/assign", new { shiftId = shift, employmentIds = people }, Ct);
        Assert.True(assigned.Status == HttpStatusCode.OK, assigned.Body);
        var current = await _h.GetAsync(o.Planner, $"{Rosters}/{roster}", Ct);
        var published = await _h.PostAsync(o.Planner, $"{Rosters}/{roster}/publish", new { rowVersion = current.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
        return roster;
    }

    private async Task SeasonalAsync(Org o, object[] seasons, Guid? unit, string from = "2025-01-01")
    {
        var (policy, _) = await _h.ActivePolicyAsync(o.Admin, o.Company, "seasonal_timing", new { seasons }, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), Ct);
        object condition = unit is { } u
            ? new { attribute = "unit", @operator = "equals", values = new object[] { u } }
            : new { attribute = "employee_type", @operator = "in", values = new[] { "permanent", "probation", "contract" } };
        var set = await _h.ActivateSetAsync(o.Admin, o.Company, "seasonal_timing", DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), Ct, (policy, 1, [condition]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    private async Task<JsonElement> EffectiveAsync(Guid user, Guid employment, string date)
    {
        var answer = await _h.GetAsync(user, $"{Shifts}/effective?employment={employment}&date={date}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json;
    }

    private static string[] Reasons(JsonElement effective) => [.. effective.GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("key").GetString()!)];

    // ------------------------------------------------------------------ FR-HRM-ATT-15: Hijri dates

    [Fact]
    public async Task FR_HRM_ATT_15_a_holiday_shows_its_hijri_date_and_the_company_offset_moves_it()
    {
        var o = await OrgAsync();
        var (_, ids) = await CalendarAsync(o, ("2025-03-01", "religious", "Start of Ramzan"), ("2025-05-01", "public", "Labour Day"));

        var plain = await _h.GetAsync(o.Planner, $"/api/hrm/holidays?companyId={o.Company}&year=2025", Ct);
        Assert.True(plain.Status == HttpStatusCode.OK, plain.Body);
        Assert.Equal(0, plain.Json.GetProperty("hijriOffsetDays").GetInt32());
        var first = plain.Json.GetProperty("holidays")[0];
        Assert.Equal("2025-03-01", first.GetProperty("date").GetString());
        Assert.Equal(ids["2025-03-01"], first.GetProperty("id").GetGuid());
        Assert.Equal((1446, 9, 1), (first.GetProperty("hijri").GetProperty("year").GetInt32(), first.GetProperty("hijri").GetProperty("month").GetInt32(), first.GetProperty("hijri").GetProperty("day").GetInt32()));

        // Offset -1 shows the Hijri date of the day before (Shaban 1446); the holiday itself does not move.
        await SettingAsync(o, "hrm.att.hijri_offset_days", -1);
        var earlier = (await _h.GetAsync(o.Planner, $"/api/hrm/holidays?companyId={o.Company}&year=2025", Ct)).Json.GetProperty("holidays")[0];
        Assert.Equal("2025-03-01", earlier.GetProperty("date").GetString());
        Assert.Equal(8, earlier.GetProperty("hijri").GetProperty("month").GetInt32());
        await SettingAsync(o, "hrm.att.hijri_offset_days", 1);
        var later = (await _h.GetAsync(o.Planner, $"/api/hrm/holidays?companyId={o.Company}&year=2025", Ct)).Json.GetProperty("holidays")[0];
        Assert.Equal((9, 2), (later.GetProperty("hijri").GetProperty("month").GetInt32(), later.GetProperty("hijri").GetProperty("day").GetInt32()));

        // An offset outside -1..+1 is not accepted by the setting.
        var refused = await _h.PutAsync(o.Settings, "/api/cfg/settings/hrm.att.hijri_offset_days", new { layer = "company", scopeId = o.Company, value = 2, reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);

        // Permission and a wrong year.
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"/api/hrm/holidays?companyId={o.Company}&year=2025", Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(o.Planner, $"/api/hrm/holidays?companyId={o.Company}&year=1700", Ct)).Status);
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-15: optional holidays

    [Fact]
    public async Task FR_HRM_ATT_15_an_optional_holiday_chosen_beyond_the_limit_or_after_the_cutoff_is_refused()
    {
        var o = await OrgAsync();
        var today = _h.Today;
        string D(int days) => today.AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (_, ids) = await CalendarAsync(o, (D(40), "optional", "Optional one"), (D(41), "optional", "Optional two"), (D(42), "optional", "Optional three"), (D(43), "public", "Mandatory"), (D(3), "optional", "Soon"));
        var employment = await HireAsync(o);
        var year = today.AddDays(40).Year;
        var soonYear = today.AddDays(3).Year;

        // No limit set: nothing can be chosen (never a default number).
        var none = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year, holidayIds = new[] { ids[D(40)] } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.Status);
        Assert.Contains("errors.hrm.holiday.limit_exceeded", none.Body, StringComparison.Ordinal);

        await SettingAsync(o, "hrm.att.optional_holidays_per_year", 2);
        var ok = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year, holidayIds = new[] { ids[D(40)], ids[D(41)] } }, Ct);
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body);
        Assert.Equal(2, ok.Json.GetProperty("optionalChosen").GetInt32());
        Assert.Equal(2, ok.Json.GetProperty("optionalLimit").GetInt32());
        Assert.Equal(2, ok.Json.GetProperty("holidays").EnumerateArray().Count(h => h.GetProperty("chosen").GetBoolean()));

        // A third is beyond the limit; a mandatory holiday and a holiday of another year are not optional choices.
        var third = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year, holidayIds = new[] { ids[D(40)], ids[D(41)], ids[D(42)] } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, third.Status);
        Assert.Contains("errors.hrm.holiday.limit_exceeded", third.Body, StringComparison.Ordinal);
        var mandatory = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year, holidayIds = new[] { ids[D(43)] } }, Ct);
        Assert.Contains("errors.hrm.holiday.not_optional", mandatory.Body, StringComparison.Ordinal);
        var wrongYear = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year = year + 1, holidayIds = new[] { ids[D(40)] } }, Ct);
        Assert.Contains("errors.hrm.holiday.not_in_calendar", wrongYear.Body, StringComparison.Ordinal);
        Assert.Equal(2, await _h.ScalarAsync<long>("select count(*) from hrm.optional_holiday_choices where employment_id = $1", Ct, employment));

        // Swapping one for another is one call; the set replaces the earlier one.
        var swapped = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year, holidayIds = new[] { ids[D(40)], ids[D(42)] } }, Ct);
        Assert.True(swapped.Status == HttpStatusCode.OK, swapped.Body);
        Assert.Equal(1, await _h.ScalarAsync<long>("select count(*) from hrm.optional_holiday_choices where employment_id = $1 and holiday_id = $2", Ct, employment, ids[D(42)]));
        Assert.Equal(0, await _h.ScalarAsync<long>("select count(*) from hrm.optional_holiday_choices where employment_id = $1 and holiday_id = $2", Ct, employment, ids[D(41)]));

        // The cut-off: with 7 days a holiday three days away can no longer be chosen; with 0 it still can.
        await SettingAsync(o, "hrm.att.optional_holiday_cutoff_days", 7);
        var late = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year = soonYear, holidayIds = new[] { ids[D(3)] } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, late.Status);
        Assert.Contains("errors.hrm.holiday.cutoff_passed", late.Body, StringComparison.Ordinal);
        var list = await _h.GetAsync(o.Planner, $"{Employments}/{employment}/holidays?year={soonYear}", Ct);
        var soon = list.Json.GetProperty("holidays").EnumerateArray().First(h => h.GetProperty("holiday").GetProperty("id").GetGuid() == ids[D(3)]);
        Assert.False(soon.GetProperty("open").GetBoolean());
        await SettingAsync(o, "hrm.att.optional_holiday_cutoff_days", 0);
        var allowed = await _h.PutAsync(o.Planner, $"{Employments}/{employment}/optional-holidays", new { year = soonYear, holidayIds = new[] { ids[D(3)] } }, Ct);
        Assert.True(allowed.Status == HttpStatusCode.OK, allowed.Body);
    }

    [Fact]
    public async Task FR_HRM_ATT_15_an_employee_chooses_only_for_their_own_employment()
    {
        var o = await OrgAsync();
        var day = _h.Today.AddDays(60);
        var iso = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (_, ids) = await CalendarAsync(o, (iso, "optional", "Optional day"));
        await SettingAsync(o, "hrm.att.optional_holidays_per_year", 1);
        var mine = await HireAsync(o);
        var other = await HireAsync(o);
        var user = await EmployeeUserAsync(mine);

        var own = await _h.PutAsync(user, $"{Employments}/{mine}/optional-holidays", new { year = day.Year, holidayIds = new[] { ids[iso] } }, Ct);
        Assert.True(own.Status == HttpStatusCode.OK, own.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(user, $"{Employments}/{other}/optional-holidays", new { year = day.Year, holidayIds = new[] { ids[iso] } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"{Employments}/{other}/holidays?year={day.Year}", Ct)).Status);
        Assert.Equal(1, await _h.ScalarAsync<long>("select count(*) from hrm.optional_holiday_choices where employment_id = $1", Ct, mine));
        Assert.Equal(0, await _h.ScalarAsync<long>("select count(*) from hrm.optional_holiday_choices where employment_id = $1", Ct, other));
    }

    // ------------------------------------------------------------------ FR-HRM-ATT-16: seasonal timing and the effective shift

    [Fact]
    public async Task FR_HRM_ATT_16_a_ramzan_range_for_one_group_changes_the_effective_shift_for_them_only_with_the_reason_shown()
    {
        var o = await OrgAsync();
        var factory = (OrgUnit: (Guid)(await _h.RowAsync("org.org_units", o.Company, null, Ct))["id"]!, o.Place.Branch);
        var lahore = await HireAsync(o, factory);
        var head = await HireAsync(o);
        var shift = await ShiftAsync(o, "fixed", "09:00", "17:00");
        await PublishedRosterAsync(o, "2026-03-02", "2026-03-15", shift, lahore, head);
        await SeasonalAsync(o, [new { name = "Ramzan", from = "2026-03-05", to = "2026-03-11", startTime = "10:00", endTime = "15:00", breakMinutes = 30 }], factory.OrgUnit);

        // Inside the range the Lahore factory works the Ramzan times; the shift row is untouched and the reason is shown.
        var inside = await EffectiveAsync(o.Planner, lahore, "2026-03-06");
        Assert.Equal("working", inside.GetProperty("dayType").GetString());
        Assert.Equal("roster", inside.GetProperty("source").GetString());
        Assert.StartsWith("10:00", inside.GetProperty("start").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("15:00", inside.GetProperty("end").GetString(), StringComparison.Ordinal);
        Assert.Equal(30, inside.GetProperty("breakMinutes").GetInt32());
        Assert.StartsWith("09:00", inside.GetProperty("baseStart").GetString(), StringComparison.Ordinal);
        Assert.Equal("Ramzan", inside.GetProperty("season").GetProperty("seasonName").GetString());
        Assert.Contains("hrm.shift.effective.reason.seasonal", Reasons(inside));
        Assert.Contains("hrm.shift.effective.reason.roster_shift", Reasons(inside));

        // Outside the range, and for a person outside the group, the shift's own times apply.
        Assert.StartsWith("09:00", (await EffectiveAsync(o.Planner, lahore, "2026-03-12")).GetProperty("start").GetString(), StringComparison.Ordinal);
        var other = await EffectiveAsync(o.Planner, head, "2026-03-06");
        Assert.StartsWith("09:00", other.GetProperty("start").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("season").ValueKind);
        Assert.DoesNotContain("hrm.shift.effective.reason.seasonal", Reasons(other));
        Assert.Equal(1, await _h.ScalarAsync<long>("select count(*) from hrm.shifts where id = $1 and start_time = '09:00' and end_time = '17:00'", Ct, shift));
    }

    [Fact]
    public async Task FR_HRM_ATT_16_a_hijri_month_and_a_percentage_shortening_apply_to_everyone_the_policy_covers()
    {
        var o = await OrgAsync();
        var person = await HireAsync(o);
        var shift = await ShiftAsync(o, "fixed", "09:00", "17:00");
        await PublishedRosterAsync(o, "2025-02-24", "2025-03-09", shift, person);
        await SeasonalAsync(o, [new { name = "Ramzan", hijriMonth = 9, shortenPercent = 25 }], null);

        // 1 March 2025 is 1 Ramadan 1446: eight hours become six, the end moves earlier.
        var ramzan = await EffectiveAsync(o.Planner, person, "2025-03-03");
        Assert.StartsWith("09:00", ramzan.GetProperty("start").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("15:00", ramzan.GetProperty("end").GetString(), StringComparison.Ordinal);
        Assert.Equal(25, ramzan.GetProperty("season").GetProperty("shortenPercent").GetInt32());
        Assert.StartsWith("17:00", (await EffectiveAsync(o.Planner, person, "2025-02-25")).GetProperty("end").GetString(), StringComparison.Ordinal);

        // The company's moon-sighting correction moves the Hijri month: with +1 the last day of Shaban is still Shaban... and with -1 Ramzan starts a day later.
        await SettingAsync(o, "hrm.att.hijri_offset_days", -1);
        Assert.StartsWith("17:00", (await EffectiveAsync(o.Planner, person, "2025-03-01")).GetProperty("end").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("15:00", (await EffectiveAsync(o.Planner, person, "2025-03-02")).GetProperty("end").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_ATT_16_the_effective_answer_says_weekly_off_holiday_and_nothing_without_guessing()
    {
        var o = await OrgAsync();
        var person = await HireAsync(o, joining: "2026-01-05");
        var (_, _) = await CalendarAsync(o, ("2026-03-23", "public", "Pakistan Day"), ("2026-03-24", "optional", "Optional day"));
        var (policy, _) = await _h.ActivePolicyAsync(o.Admin, o.Company, "shift", new { weeklyOffKind = "fixed", offDays = new[] { 7 } }, new DateOnly(2026, 1, 1), Ct);
        var set = await _h.ActivateSetAsync(o.Admin, o.Company, "shift", new DateOnly(2026, 1, 1), Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);

        // Sunday 22 March 2026 is the weekly off of the policy (no roster); Monday 23 is a public holiday; Tuesday 24 is an optional one nobody chose.
        var sunday = await EffectiveAsync(o.Planner, person, "2026-03-22");
        Assert.Equal("weekly_off", sunday.GetProperty("dayType").GetString());
        Assert.Equal("weekly_off_policy", sunday.GetProperty("source").GetString());
        Assert.Contains("hrm.shift.effective.reason.weekly_off_policy", Reasons(sunday));
        var holiday = await EffectiveAsync(o.Planner, person, "2026-03-23");
        Assert.Equal("holiday", holiday.GetProperty("dayType").GetString());
        Assert.Equal("Pakistan Day", holiday.GetProperty("holiday").GetProperty("name").GetProperty("en").GetString());
        var optional = await EffectiveAsync(o.Planner, person, "2026-03-24");
        Assert.Equal("none", optional.GetProperty("dayType").GetString());
        Assert.Contains("hrm.shift.effective.reason.no_roster", Reasons(optional));
        Assert.Equal("none", (await EffectiveAsync(o.Planner, person, "2025-12-01")).GetProperty("dayType").GetString());

        // A person the caller cannot see is not found; no permission is refused.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(o.Planner, $"{Shifts}/effective?employment={Guid.NewGuid()}&date=2026-03-22", Ct)).Status);
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Shifts}/effective?employment={person}&date=2026-03-22", Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_ATT_16_a_seasonal_timing_policy_with_an_incomplete_season_is_refused_with_a_key()
    {
        var o = await OrgAsync();
        async Task<string> TryAsync(object season)
        {
            var created = await _h.PostAsync(o.Admin, "/api/hrm/policies", new { companyId = o.Company, policyType = "seasonal_timing", code = "X" + Guid.NewGuid().ToString("N")[..8], name = new { en = "Bad" }, parameters = new { seasons = new[] { season } } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, created.Status);
            return created.Body;
        }

        Assert.Contains("errors.hrm.seasonal.range_required", await TryAsync(new { name = "R", startTime = "10:00", endTime = "15:00" }), StringComparison.Ordinal);
        Assert.Contains("errors.hrm.seasonal.times_pair", await TryAsync(new { name = "R", hijriMonth = 9, startTime = "10:00" }), StringComparison.Ordinal);
        Assert.Contains("errors.hrm.seasonal.times_or_percent", await TryAsync(new { name = "R", hijriMonth = 9, startTime = "10:00", endTime = "15:00", shortenPercent = 10 }), StringComparison.Ordinal);
        Assert.Contains("errors.hrm.seasonal.range_order", await TryAsync(new { name = "R", from = "2026-03-10", to = "2026-03-01", shortenPercent = 10 }), StringComparison.Ordinal);
        Assert.Contains("errors.hrm.seasonal.effect_required", await TryAsync(new { name = "R", hijriMonth = 9 }), StringComparison.Ordinal);
    }
}
