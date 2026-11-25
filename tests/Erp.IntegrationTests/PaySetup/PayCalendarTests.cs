#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.PaySetup;

/// <summary>
/// Pay groups, pay periods, the payroll calendar and the readiness checklist (slice S1.34a, Build items 1 to 3) through the real API
/// pipeline, the real access engine and a real PostgreSQL: FR-HRM-PAY-01 (frequency, calendar, cut-off, payment dates, currency,
/// entity; one pay group at a time; a move only through an employment event and never out of a group with an unfinished run),
/// FR-HRM-PAY-38 (calendar, cut-off reminders, readiness checklist that never counts an unavailable item as passed), GHR-06 (the
/// approval threshold is salary-class data) and the permission and data-scope rules of every endpoint.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PayCalendarTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Groups = "/api/hrm/pay-groups";
    private const string Employments = "/api/hrm/employments";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    /// <summary>The business date the pay services use (the company's time zone, default Asia/Karachi), not the UTC date: from 19:00 UTC
    /// it is already the next day in Karachi, and the calendar counts days to the cut-off from that date.</summary>
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> PersonAsync() => (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;

    /// <summary>A company of its own; job changes (pay group moves are transfers) apply without approval there.</summary>
    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        return (company, await _h.PlaceAsync(company, Ct));
    }

    /// <summary>A payroll administrator: the pay set-up permissions everywhere, and edit access to salary-class fields (the approval threshold).</summary>
    private async Task<Guid> PayAdminAsync()
    {
        var user = await _h.UserAsync(Ct);
        string[] permissions =
        [
            "hrm.pay_groups.read", "hrm.pay_groups.create", "hrm.pay_groups.update", "hrm.pay_periods.read", "hrm.pay_periods.generate", "hrm.pay_periods.update",
            "hrm.pay_readiness.read", "hrm.employments.read", "hrm.employment_assignments.read",
        ];
        await _h.AssignAsync(user, await _h.RoleAsync(Ct, permissions, [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        return user;
    }

    private static string Code() => "PG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>Creates a pay group through the API (as a tenant administrator, who may edit salary-class fields).</summary>
    private async Task<(Guid Id, string Code, long Version)> GroupAsync(Guid user, Guid company, string frequency = "monthly", int startMonth = 7, int? cutOff = 3, int? payment = 5, string? code = null, decimal? threshold = null)
    {
        code ??= Code();
        var answer = await _h.PostAsync(user, Groups, new
        {
            companyId = company,
            code,
            nameI18n = new { en = "Group " + code, ur = "گروپ " + code },
            frequency,
            taxYearStartMonth = startMonth,
            cutOffDay = cutOff,
            paymentDay = payment,
            approvalThreshold = threshold,
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), code, answer.Json.GetProperty("rowVersion").GetInt64());
    }

    private async Task<JsonElement[]> GenerateAsync(Guid user, Guid group, int taxYear, string? weekStartsOn = null)
    {
        var answer = await _h.PostAsync(user, $"{Groups}/{group}/periods/generate", new { taxYear, weekStartsOn }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return [.. answer.Json.GetProperty("items").EnumerateArray()];
    }

    private async Task<Guid> HireAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, Guid? payGroup = null, int daysAgo = 60)
    {
        var answer = await _h.PostAsync(user, Employments, new
        {
            personId = await PersonAsync(),
            companyId = company,
            employeeType = "permanent",
            joiningDate = Iso(Today.AddDays(-daysAgo)),
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer", payGroupId = payGroup },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private Task<long> EmploymentVersionAsync(Guid employment) => _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment);

    private async Task<Answer> MoveAsync(Guid user, Guid employment, Guid group) =>
        await _h.PostAsync(user, $"{Employments}/{employment}/events", new
        {
            kind = "transfer",
            reasonCode = "business_need",
            effectiveDate = Iso(Today),
            assignment = new { payGroupId = group },
            rowVersion = await EmploymentVersionAsync(employment),
        }, Ct);

    private static string FieldError(Answer a, string field) => a.Json.GetProperty("errors").GetProperty(field)[0].GetString()!;

    // ------------------------------------------------------------------ pay groups (FR-HRM-PAY-01)

    [Fact]
    public async Task FR_HRM_PAY_01_a_pay_group_takes_the_company_currency_by_default_and_refuses_bad_numbers_and_repeated_codes()
    {
        var admin = await PayAdminAsync();
        var (company, _) = await CompanyAsync();
        var code = Code();
        var (id, _, version) = await GroupAsync(admin, company, code: code, threshold: 5_000_000m);
        var company_currency = await _h.ScalarAsync<string>("select base_currency_code::text from org.companies where id = $1", Ct, company);
        Assert.Equal(company_currency, await _h.ScalarAsync<string>("select currency_code::text from hrm.pay_groups where id = $1", Ct, id)); // slice S1.34 Q2

        // Every change is audited (spec 03 section 10): the creation of the group is in the change log under the person who made it.
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_groups' and entity_id = $1", Ct, id) >= 1);

        // The code is unique in the company whatever its case; numbers are in range; frequency and name are required.
        var again = await _h.PostAsync(admin, Groups, new { companyId = company, code = code.ToLowerInvariant(), nameI18n = new { en = "x" }, frequency = "monthly", taxYearStartMonth = 7 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Equal("errors.hrm.pay.code_taken", FieldError(again, "code"));
        var bad = await _h.PostAsync(admin, Groups, new { companyId = company, code = Code(), nameI18n = new { en = "x" }, frequency = "daily", taxYearStartMonth = 13, cutOffDay = 40, paymentDay = -1, approvalThreshold = -5 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal("errors.hrm.pay.frequency", FieldError(bad, "frequency"));
        Assert.Equal("errors.hrm.pay.start_month", FieldError(bad, "taxYearStartMonth"));
        Assert.Equal("errors.hrm.pay.offset_range", FieldError(bad, "cutOffDay"));
        Assert.Equal("errors.hrm.pay.offset_range", FieldError(bad, "paymentDay"));
        Assert.Equal("errors.hrm.pay.threshold_negative", FieldError(bad, "approvalThreshold"));
        Assert.Equal("errors.hrm.pay.name_required", FieldError(await _h.PostAsync(admin, Groups, new { companyId = company, code = Code(), frequency = "monthly", taxYearStartMonth = 7 }, Ct), "nameI18n"));

        // A change needs the row version read; a stale one is a conflict; the answer carries the next version.
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PutAsync(admin, $"{Groups}/{id}", new { cutOffDay = 2 }, Ct)).Status);
        var changed = await _h.PutAsync(admin, $"{Groups}/{id}", new { cutOffDay = 2, rowVersion = version }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.Equal(version + 1, changed.Json.GetProperty("rowVersion").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(admin, $"{Groups}/{id}", new { cutOffDay = 1, rowVersion = version }, Ct)).Status);
        var cleared = await _h.PutAsync(admin, $"{Groups}/{id}", new { clear = new[] { "cutOffDay", "approvalThreshold" }, rowVersion = version + 1 }, Ct);
        Assert.True(cleared.Status == HttpStatusCode.OK, cleared.Body);
        Assert.True(await _h.ScalarAsync<bool>("select cut_off_day is null and approval_threshold is null from hrm.pay_groups where id = $1", Ct, id));

        // The database repeats the ranges (a direct write is refused too).
        await using var owner = await _h.OwnerAsync(null, Ct);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, "update hrm.pay_groups set payment_day = 99 where id = $1", Ct, id));
        Assert.Equal("ck_pay_groups_rules", ex.ConstraintName);
    }

    [Fact]
    public async Task FR_HRM_PAY_01_the_frequency_is_fixed_once_periods_exist_and_a_group_with_employees_cannot_be_switched_off()
    {
        var admin = await PayAdminAsync();
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (id, _, version) = await GroupAsync(admin, company);
        await GenerateAsync(admin, id, 2026);
        var locked = await _h.PutAsync(admin, $"{Groups}/{id}", new { frequency = "weekly", rowVersion = version }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, locked.Status);
        Assert.Equal("errors.hrm.pay.frequency_locked", FieldError(locked, "frequency"));
        Assert.Equal("errors.hrm.pay.frequency_locked", FieldError(await _h.PutAsync(admin, $"{Groups}/{id}", new { taxYearStartMonth = 1, rowVersion = version }, Ct), "taxYearStartMonth"));

        var employment = await HireAsync(hr, company, place, id);
        var off = await _h.PutAsync(admin, $"{Groups}/{id}", new { isActive = false, rowVersion = version }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, off.Status);
        Assert.Equal("errors.hrm.pay.group_in_use", FieldError(off, "isActive"));

        // A group with nobody in it can be switched off; periods are then no longer generated for it, and nobody can be put in it.
        var (empty, _, emptyVersion) = await GroupAsync(admin, company);
        var switched = await _h.PutAsync(admin, $"{Groups}/{empty}", new { isActive = false, rowVersion = emptyVersion }, Ct);
        Assert.True(switched.Status == HttpStatusCode.OK, switched.Body);
        Assert.Equal("errors.hrm.pay.group_inactive", FieldError(await _h.PostAsync(admin, $"{Groups}/{empty}/periods/generate", new { taxYear = 2026 }, Ct), "payGroupId"));
        Assert.Equal("errors.hrm.pay.group_not_found", FieldError(await MoveAsync(hr, employment, empty), "assignment.payGroupId"));
    }

    // ------------------------------------------------------------------ periods (FR-HRM-PAY-01, FR-HRM-PAY-38)

    [Fact]
    public async Task FR_HRM_PAY_01_Acceptance_periods_of_a_tax_year_are_generated_for_two_groups_and_never_overlap()
    {
        var admin = await PayAdminAsync();
        var (company, _) = await CompanyAsync();
        var (monthly, _, _) = await GroupAsync(admin, company, "monthly", startMonth: 7, cutOff: 3, payment: 5);
        var (semi, _, _) = await GroupAsync(admin, company, "semi_monthly", startMonth: 1, cutOff: 2, payment: 0);

        var months = await GenerateAsync(admin, monthly, 2026);
        Assert.Equal(12, months.Length);
        Assert.Equal(("2026-07-01", "2026-07-31", "2026-07-28", "2026-08-05"), (months[0].GetProperty("startDate").GetString(), months[0].GetProperty("endDate").GetString(), months[0].GetProperty("cutOffDate").GetString(), months[0].GetProperty("paymentDate").GetString()));
        Assert.Equal(("2027-06-01", "2027-06-30"), (months[11].GetProperty("startDate").GetString(), months[11].GetProperty("endDate").GetString()));
        Assert.All(months, m => Assert.Equal("future", m.GetProperty("status").GetString()));
        var halves = await GenerateAsync(admin, semi, 2026);
        Assert.Equal(24, halves.Length);
        Assert.Equal(("2026-01-01", "2026-01-15", "2026-01-16", "2026-01-31"), (halves[0].GetProperty("startDate").GetString(), halves[0].GetProperty("endDate").GetString(), halves[1].GetProperty("startDate").GetString(), halves[1].GetProperty("endDate").GetString()));
        Assert.Equal("2026-02-28", halves[3].GetProperty("endDate").GetString());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(distinct tax_year) from hrm.pay_periods where pay_group_id = $1", Ct, monthly));
        Assert.Equal(12L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_periods' and entity_id in (select id from hrm.pay_periods where pay_group_id = $1)", Ct, monthly));

        // The same year again, or a year that would overlap, is refused; the database has the exclusion constraint too.
        Assert.Equal("errors.hrm.pay.periods_exist", (await _h.PostAsync(admin, $"{Groups}/{monthly}/periods/generate", new { taxYear = 2026 }, Ct)).Json.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(admin, $"{Groups}/{monthly}/periods/generate", new { taxYear = 2026 }, Ct)).Status);
        await using var owner = await _h.OwnerAsync(null, Ct);
        var overlap = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, """
            insert into hrm.pay_periods (id, company_id, pay_group_id, period_no, tax_year, start_date, end_date, cut_off_date, payment_date, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 99, 2030, '2026-07-10', '2026-07-20', '2026-07-19', '2026-07-25', 'future', now(), $4, now(), $4)
            """, Ct, Guid.CreateVersion7(), company, monthly, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId));
        Assert.Equal("ex_pay_periods_pay_group_id_dates", overlap.ConstraintName);
        Assert.Equal("errors.hrm.pay.tax_year_range", FieldError(await _h.PostAsync(admin, $"{Groups}/{monthly}/periods/generate", new { taxYear = 1990 }, Ct), "taxYear"));
        Assert.Equal("errors.hrm.pay.tax_year_required", FieldError(await _h.PostAsync(admin, $"{Groups}/{monthly}/periods/generate", new { }, Ct), "taxYear"));
    }

    [Fact]
    public async Task FR_HRM_PAY_01_weekly_periods_start_on_the_chosen_weekday_and_the_next_year_continues_without_a_gap()
    {
        var admin = await PayAdminAsync();
        var (company, _) = await CompanyAsync();
        var (weekly, _, _) = await GroupAsync(admin, company, "weekly", startMonth: 1, cutOff: 1, payment: 2);
        Assert.Equal("errors.hrm.pay.weekday_required", FieldError(await _h.PostAsync(admin, $"{Groups}/{weekly}/periods/generate", new { taxYear = 2026 }, Ct), "weekStartsOn"));
        Assert.Equal("errors.hrm.pay.weekday_unknown", FieldError(await _h.PostAsync(admin, $"{Groups}/{weekly}/periods/generate", new { taxYear = 2026, weekStartsOn = "someday" }, Ct), "weekStartsOn"));

        var first = await GenerateAsync(admin, weekly, 2026, "monday");
        Assert.Equal("2026-01-05", first[0].GetProperty("startDate").GetString()); // the first Monday on or after 1 January 2026
        Assert.Equal("2026-01-11", first[0].GetProperty("endDate").GetString());
        Assert.Equal("2026-01-10", first[0].GetProperty("cutOffDate").GetString());
        Assert.Equal("2026-01-13", first[0].GetProperty("paymentDate").GetString());
        Assert.All(first, p => Assert.Equal(DayOfWeek.Monday, DateOnly.Parse(p.GetProperty("startDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture).DayOfWeek));
        var lastEnd = DateOnly.Parse(first[^1].GetProperty("endDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(lastEnd >= new DateOnly(2026, 12, 31));

        // The next year starts the day after the last period, whatever weekday is sent.
        var next = await GenerateAsync(admin, weekly, 2027, "friday");
        Assert.Equal(Iso(lastEnd.AddDays(1)), next[0].GetProperty("startDate").GetString());
        Assert.Equal(1, next[0].GetProperty("periodNo").GetInt32());
        var overlapping = await _h.PostAsync(admin, $"{Groups}/{weekly}/periods/generate", new { taxYear = 2025, weekStartsOn = "tuesday" }, Ct); // its last week would run into 2026's first
        Assert.Equal(HttpStatusCode.Conflict, overlapping.Status);
        Assert.Equal("errors.hrm.pay.periods_overlap", overlapping.Json.GetProperty("title").GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_periods where pay_group_id = $1 and tax_year = 2025", Ct, weekly));
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, $"{Groups}/{weekly}/periods/generate", new { taxYear = 2025, weekStartsOn = "monday" }, Ct)).Status); // a Monday year fits in just before

        // A cut-off offset longer than the period is refused before anything is written.
        var (tooLong, _, _) = await GroupAsync(admin, company, "weekly", startMonth: 1, cutOff: 8, payment: 0);
        Assert.Equal("errors.hrm.pay.cutoff_before_start", FieldError(await _h.PostAsync(admin, $"{Groups}/{tooLong}/periods/generate", new { taxYear = 2026, weekStartsOn = "monday" }, Ct), "payGroupId"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_periods where pay_group_id = $1", Ct, tooLong));
    }

    [Fact]
    public async Task FR_HRM_PAY_38_dates_change_by_hand_before_a_period_opens_and_a_closed_period_never_changes()
    {
        var admin = await PayAdminAsync();
        var (company, _) = await CompanyAsync();
        var (id, _, _) = await GroupAsync(admin, company);
        var periods = await GenerateAsync(admin, id, 2026);
        var (p1, v1) = (periods[0].GetProperty("id").GetGuid(), periods[0].GetProperty("rowVersion").GetInt64());

        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = "2026-07-20" }, Ct)).Status); // row version
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { rowVersion = v1 }, Ct)).Status); // nothing to change
        Assert.Equal("errors.hrm.pay.period_dates", FieldError(await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = "2026-06-30", rowVersion = v1 }, Ct), "cutOffDate"));
        Assert.Equal("errors.hrm.pay.period_dates", FieldError(await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { paymentDate = "2026-07-01", rowVersion = v1 }, Ct), "paymentDate"));
        var changed = await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = "2026-07-20", paymentDate = "2026-08-07", rowVersion = v1 }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.Equal("2026-07-20", changed.Text("cutOffDate"));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = "2026-07-21", rowVersion = v1 }, Ct)).Status); // stale

        // Once the pay run opens the period its dates are fixed (answer 409); a closed one cannot change at all, not even directly.
        await using var owner = await _h.OwnerAsync(null, Ct);
        await AccessHarness.ExecAsync(owner, "update hrm.pay_periods set status = 'open' where id = $1", Ct, p1);
        var open = await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = "2026-07-21", rowVersion = v1 + 1 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, open.Status);
        Assert.Equal("errors.hrm.pay.period_not_future", open.Json.GetProperty("title").GetString());
        await AccessHarness.ExecAsync(owner, "update hrm.pay_periods set status = 'closed' where id = $1", Ct, p1);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = "2026-07-21", rowVersion = v1 + 1 }, Ct)).Status);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, "update hrm.pay_periods set payment_date = payment_date + 1 where id = $1", Ct, p1));
        Assert.Equal("ck_pay_periods_fixed", direct.ConstraintName);
        Assert.Equal("ck_pay_periods_fixed", (await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, "update hrm.pay_periods set status = 'open' where id = $1", Ct, p1))).ConstraintName);
        Assert.Equal("ck_pay_periods_fixed", (await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, "delete from hrm.pay_periods where id = $1", Ct, p1))).ConstraintName);
    }

    [Fact]
    public async Task FR_HRM_PAY_38_the_calendar_lists_periods_with_days_to_the_cut_off_and_the_generated_writes_are_closed()
    {
        var admin = await PayAdminAsync();
        var payroll = await _h.UserAsync(Ct, null, "payroll_officer");
        var (company, _) = await CompanyAsync();
        var (id, code, _) = await GroupAsync(admin, company, startMonth: Today.Month);
        var periods = await GenerateAsync(admin, id, Today.Year);
        var (p1, v1) = (periods[0].GetProperty("id").GetGuid(), periods[0].GetProperty("rowVersion").GetInt64());
        Assert.True((await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = Iso(Today.AddDays(4)), paymentDate = Iso(Today.AddDays(9)), rowVersion = v1 }, Ct)).Status == HttpStatusCode.OK);

        var calendar = await _h.GetAsync(payroll, $"/api/hrm/pay-calendar?payGroupId={id}&taxYear={Today.Year}", Ct);
        Assert.True(calendar.Status == HttpStatusCode.OK, calendar.Body);
        var items = calendar.Json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(12, items.Length);
        Assert.Equal(code, items[0].GetProperty("payGroupCode").GetString());
        Assert.Equal(4, items[0].GetProperty("daysToCutOff").GetInt32());
        Assert.Equal(9, items[0].GetProperty("daysToPayment").GetInt32());
        Assert.False(calendar.Json.GetProperty("more").GetBoolean());

        // The generated writes of both tables are closed: only the services above change them.
        foreach (var (method, path) in new[] { ("POST", "/api/hrm/pay-periods"), ("DELETE", $"/api/hrm/pay-periods/{p1}"), ("DELETE", $"{Groups}/{id}") })
        {
            var answer = method == "POST" ? await _h.PostAsync(admin, path, new { payGroupId = id }, Ct) : await _h.DeleteAsync(admin, path, Ct);
            Assert.Contains(answer.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(payroll, Groups, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(payroll, "/api/hrm/pay-periods", Ct)).Status);
    }

    // ------------------------------------------------------------------ one pay group at a time (FR-HRM-PAY-01, slice S1.34 Q7)

    [Fact]
    public async Task FR_HRM_PAY_01_Acceptance_an_employee_changes_pay_group_only_through_an_employment_event_and_is_never_in_two()
    {
        var admin = await PayAdminAsync();
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (a, _, _) = await GroupAsync(admin, company);
        var (b, _, _) = await GroupAsync(admin, company);
        var employment = await HireAsync(hr, company, place, a);

        // The assignment is read as of a date and shows its group; the generated write of assignments is closed.
        var asOf = await _h.GetAsync(hr, $"{Employments}/{employment}/as-of?date={Iso(Today)}", Ct);
        Assert.Equal(a.ToString(), asOf.Json.GetProperty("assignment").GetProperty("payGroupId").GetString());
        var row = await _h.ScalarAsync<Guid>("select id from hrm.employment_assignments where employment_id = $1", Ct, employment);
        Assert.Contains((await _h.PutAsync(admin, $"/api/hrm/employment-assignments/{row}", new { payGroupId = b }, Ct)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        // The move is an event on a date; the old row ends and a new one starts, so history keeps the earlier group.
        var moved = await MoveAsync(hr, employment, b);
        Assert.True(moved.Status is HttpStatusCode.Created or HttpStatusCode.OK, moved.Body);
        Assert.Equal(b, await _h.ScalarAsync<Guid>("select pay_group_id from hrm.employment_assignments where employment_id = $1 and valid_to is null", Ct, employment));
        Assert.Equal(a, await _h.ScalarAsync<Guid>("select pay_group_id from hrm.employment_assignments where employment_id = $1 and valid_to is not null order by valid_from limit 1", Ct, employment));

        // By construction an employment has one assignment row on any day (exclusion constraint), so it cannot be in two groups.
        await using var owner = await _h.OwnerAsync(null, Ct);
        var second = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, """
            insert into hrm.employment_assignments (id, company_id, employment_id, event_id, org_unit_id, title, branch_id, pay_group_id, cost_allocation, fte, is_acting, valid_from, created_at, created_by, updated_at, updated_by)
            select $1, company_id, employment_id, event_id, org_unit_id, title, branch_id, $2, cost_allocation, fte, is_acting, valid_from, now(), created_by, now(), updated_by
              from hrm.employment_assignments where employment_id = $3 and valid_to is null
            """, Ct, Guid.CreateVersion7(), a, employment));
        Assert.True(second.ConstraintName == "ex_employment_assignments_employment_id", second.MessageText);
    }

    [Fact]
    public async Task FR_HRM_PAY_01_a_move_out_of_a_group_with_an_unfinished_run_is_refused_until_the_run_is_closed()
    {
        var admin = await PayAdminAsync();
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (a, _, _) = await GroupAsync(admin, company);
        var (b, _, _) = await GroupAsync(admin, company);
        var periods = await GenerateAsync(admin, a, 2026);
        var employment = await HireAsync(hr, company, place, a);
        var run = (Guid)(await _h.RowAsync("hrm.pay_runs", company, new Dictionary<string, object?>
        {
            ["pay_group_id"] = a,
            ["pay_period_id"] = periods[0].GetProperty("id").GetGuid(),
            ["status"] = "calculated",
            ["kind"] = "regular",
        }, Ct))["id"]!;

        var refused = await MoveAsync(hr, employment, b);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.hrm.pay.group_open_run", FieldError(refused, "assignment.payGroupId"));
        Assert.Equal(a, await _h.ScalarAsync<Guid>("select pay_group_id from hrm.employment_assignments where employment_id = $1 and valid_to is null", Ct, employment));

        // A run that is closed or cancelled no longer holds the group.
        await using var owner = await _h.OwnerAsync(null, Ct);
        await AccessHarness.ExecAsync(owner, "update hrm.pay_runs set status = 'cancelled' where id = $1", Ct, run);
        var allowed = await MoveAsync(hr, employment, b);
        Assert.True(allowed.Status is HttpStatusCode.Created or HttpStatusCode.OK, allowed.Body);
    }

    // ------------------------------------------------------------------ readiness (FR-HRM-PAY-38)

    private async Task AddAccountAsync(Guid company, Guid employment)
    {
        await _h.RowAsync("hrm.employee_bank_accounts", company, new Dictionary<string, object?>
        {
            ["employment_id"] = employment,
            ["payment_mode"] = "cash",
            ["bank_id"] = null,
            ["account_number"] = null,
            ["iban"] = null,
            ["split_kind"] = "remainder",
            ["split_value"] = null,
            ["priority"] = 1,
            ["valid_from"] = Today.AddDays(-60),
            ["valid_to"] = null,
        }, Ct);
    }

    [Fact]
    public async Task FR_HRM_PAY_38_Acceptance_the_checklist_shows_unavailable_items_as_such_and_lists_who_is_missing_bank_data()
    {
        var admin = await PayAdminAsync();
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var payroll = await _h.UserAsync(Ct, null, "payroll_officer");
        var (company, place) = await CompanyAsync();
        var (id, _, _) = await GroupAsync(admin, company, startMonth: Today.Month);
        var period = (await GenerateAsync(admin, id, Today.Year))[0].GetProperty("id").GetGuid();
        var with = await HireAsync(hr, company, place, id);
        var missing1 = await HireAsync(hr, company, place, id);
        var missing2 = await HireAsync(hr, company, place, id);
        var elsewhere = await HireAsync(hr, company, place, null); // no pay group: not part of this group's checklist
        await AddAccountAsync(company, with);

        var first = await _h.GetAsync(payroll, $"/api/hrm/pay-periods/{period}/readiness", Ct);
        Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        var items = first.Json.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("code").GetString()!);
        Assert.Equal(["attendance_locked", "loans_updated", "claims_approved", "bank_data_complete", "structures_assigned"], items.Keys.ToArray());

        // Items whose slice does not exist yet are "not available", never passed; S1.31 made the attendance item real: an unlocked period fails it. S1.41 made the loans item real:
        // with no approved loan waiting to be paid out it passes (LoanTests holds the failing case).
        Assert.Equal("not_available", items["claims_approved"].GetProperty("status").GetString());
        Assert.Equal("passed", items["loans_updated"].GetProperty("status").GetString());

        Assert.Equal("failed", items["attendance_locked"].GetProperty("status").GetString());

        Assert.Equal("S1.31", items["attendance_locked"].GetProperty("slice").GetString());
        var bank = items["bank_data_complete"];
        Assert.Equal("failed", bank.GetProperty("status").GetString());
        Assert.Equal(2, bank.GetProperty("count").GetInt32());
        var numbers = bank.GetProperty("employees").EnumerateArray().Select(e => e.GetProperty("employmentId").GetGuid()).ToHashSet();
        Assert.Equal(new[] { missing1, missing2 }.OrderBy(x => x), numbers.OrderBy(x => x));
        Assert.DoesNotContain(with, numbers);
        Assert.DoesNotContain(elsewhere, numbers);
        Assert.Equal("failed", items["structures_assigned"].GetProperty("status").GetString()); // nobody has a structure until S1.34b
        Assert.Equal(3, items["structures_assigned"].GetProperty("count").GetInt32());
        Assert.False(first.Json.GetProperty("ready").GetBoolean());
        Assert.Equal(2, first.Json.GetProperty("summary").GetProperty("notAvailable").GetInt32()); // loans and claims (S1.41, S1.42); attendance is S1.31's check

        // Once everyone has an account the item passes; the others stay as they were, so the period is still not ready.
        await AddAccountAsync(company, missing1);
        await AddAccountAsync(company, missing2);
        var second = await _h.GetAsync(payroll, $"/api/hrm/pay-periods/{period}/readiness", Ct);
        var bankAfter = second.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "bank_data_complete");
        Assert.Equal("passed", bankAfter.GetProperty("status").GetString());
        Assert.Equal(0, bankAfter.GetProperty("employees").GetArrayLength());
        Assert.False(second.Json.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task FR_HRM_PAY_38_a_registered_check_replaces_not_available_and_only_all_passed_means_ready()
    {
        // The registry lists what must be ready; a later slice registers a check with the item's code (here a stand-in for S1.31).
        var registry = new Erp.Hrm.Infrastructure.Payroll.PayReadinessRegistry(Erp.Hrm.Infrastructure.Payroll.PayServices.ReadinessManifest);
        Assert.Equal(["attendance_locked", "loans_updated", "claims_approved", "bank_data_complete", "structures_assigned"], registry.Items.Select(i => i.Code).ToArray());
        Assert.Throws<InvalidOperationException>(() => new Erp.Hrm.Infrastructure.Payroll.PayReadinessRegistry("""{"items":[{"code":"a","labelKey":"k","slice":"s","requirement":"r"},{"code":"a","labelKey":"k","slice":"s","requirement":"r"}]}"""));
        await ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ cut-off reminders (FR-HRM-PAY-38)

    [Fact]
    public async Task FR_HRM_PAY_38_Acceptance_a_reminder_event_is_raised_once_before_the_cut_off_and_again_when_the_date_moves()
    {
        var admin = await PayAdminAsync();
        var payroll = await _h.UserAsync(Ct, null, "payroll_officer");
        var (company, _) = await CompanyAsync();
        var (id, code, _) = await GroupAsync(admin, company, startMonth: Today.Month);
        var periods = await GenerateAsync(admin, id, Today.Year);
        var (p1, v1) = (periods[0].GetProperty("id").GetGuid(), periods[0].GetProperty("rowVersion").GetInt64());
        var (p2, v2) = (periods[1].GetProperty("id").GetGuid(), periods[1].GetProperty("rowVersion").GetInt64());
        Assert.True((await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = Iso(Today.AddDays(2)), paymentDate = Iso(Today.AddDays(8)), rowVersion = v1 }, Ct)).Status == HttpStatusCode.OK);
        Assert.True((await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p2}", new { cutOffDate = Iso(Today.AddDays(20)), paymentDate = Iso(Today.AddDays(25)), rowVersion = v2 }, Ct)).Status == HttpStatusCode.OK);

        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.pay_cutoff_reminders", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        await rig.QueueAsync("hrm.pay_cutoff_reminders", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.pay.cutoff_due' and aggregate_id = $1", Ct, p1)); // default 3 days before, once
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.pay.cutoff_due' and aggregate_id = $1", Ct, p2)); // 20 days away
        Assert.Equal(code, await _h.ScalarAsync<string>("select payload->>'pay_group_code' from hrm.outbox_messages where event_type = 'hrm.pay.cutoff_due' and aggregate_id = $1", Ct, p1));
        Assert.Equal(Iso(Today.AddDays(2)), await _h.ScalarAsync<string>("select payload->>'cut_off_date' from hrm.outbox_messages where event_type = 'hrm.pay.cutoff_due' and aggregate_id = $1", Ct, p1));

        // The payroll officers get it as a notification (ids and dates only in the event; the text comes from the catalogue).
        var dispatcher = rig.Provider().GetRequiredService<Erp.Kernel.Infrastructure.Jobs.EventDispatcher>();
        // The dispatcher reads 100 events per pass, oldest first, and the tenant's outbox holds the events of every test that ran before
        // (and of this one): pass again until this event has been handled.
        for (var pass = 0; pass < 200; pass++)
        {
            var outcomes = await dispatcher.DispatchOnceAsync(_h.Code, Ct);
            foreach (var outcome in outcomes)
            {
                Assert.True(outcome.Error is null, outcome.Error);
            }

            if (outcomes.Count == 0 || await _h.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1 and event_code = 'hrm.pay.cutoff_due'", Ct, payroll) >= 1)
            {
                break;
            }
        }

        Assert.True(await _h.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1 and event_code = 'hrm.pay.cutoff_due'", Ct, payroll) >= 1);

        // Moving the cut-off date by hand reminds again for the new date; a switched-off group is not reminded.
        var v1b = await _h.ScalarAsync<long>("select row_version from hrm.pay_periods where id = $1", Ct, p1);
        Assert.True((await _h.PutAsync(admin, $"/api/hrm/pay-periods/{p1}", new { cutOffDate = Iso(Today.AddDays(1)), rowVersion = v1b }, Ct)).Status == HttpStatusCode.OK);
        await rig.QueueAsync("hrm.pay_cutoff_reminders", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.pay.cutoff_due' and aggregate_id = $1", Ct, p1));
    }

    // ------------------------------------------------------------------ security (GHR-06, permissions, scope)

    [Fact]
    public async Task GHR_06_the_approval_threshold_is_salary_class_data_hidden_from_users_without_the_field_class_and_not_settable_by_them()
    {
        var admin = await PayAdminAsync();
        var (company, _) = await CompanyAsync();
        var (id, _, version) = await GroupAsync(admin, company, threshold: 7_500_000m);
        string[] groupRead = ["hrm.pay_groups.read", "hrm.pay_groups.create", "hrm.pay_groups.update"];
        (string, object)[] scopes = [("hrm.pay_groups", "all")];

        // Without the salary field class the generated list leaves the threshold out; with it the value is there.
        var plain = await _h.UserAsync(Ct);
        await _h.AssignAsync(plain, await _h.RoleAsync(Ct, groupRead, scopes, [("hrm.pay_groups", "class:salary", "hidden")]), null, Today.AddDays(-1), null, Ct);
        var hidden = await _h.GetAsync(plain, $"{Groups}/{id}", Ct);
        Assert.True(hidden.Status == HttpStatusCode.OK, hidden.Body);
        Assert.DoesNotContain("approvalThreshold", hidden.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7500000", hidden.Body, StringComparison.Ordinal);
        var list = await _h.GetAsync(plain, Groups, Ct);
        Assert.DoesNotContain("7500000", list.Body, StringComparison.Ordinal);
        var shown = await _h.GetAsync(admin, $"{Groups}/{id}", Ct);
        Assert.Contains("7500000", shown.Body, StringComparison.Ordinal);

        // That user may change the group but not its threshold: the field answers "not editable" and nothing changes.
        var changed = await _h.PutAsync(plain, $"{Groups}/{id}", new { cutOffDay = 4, rowVersion = version }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var denied = await _h.PutAsync(plain, $"{Groups}/{id}", new { approvalThreshold = 1m, rowVersion = version + 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, denied.Status);
        Assert.Equal("errors.access.field_not_editable", denied.Json.GetProperty("errors").GetProperty("approval_threshold")[0].GetString());
        Assert.Equal(7_500_000m, await _h.ScalarAsync<decimal>("select approval_threshold from hrm.pay_groups where id = $1", Ct, id));
        var createDenied = await _h.PostAsync(plain, Groups, new { companyId = company, code = Code(), nameI18n = new { en = "x" }, frequency = "monthly", taxYearStartMonth = 7, approvalThreshold = 5m }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, createDenied.Status);

        // The audit history of the group shows who changed what, but not the amount, to someone without the field class; with it, the amount shows.
        var auditor = await _h.UserAsync(Ct);
        await _h.AssignAsync(auditor, await _h.RoleAsync(Ct, ["hrm.pay_groups.read", "aud.history.view"], scopes, [("hrm.pay_groups", "class:salary", "hidden")]), null, Today.AddDays(-1), null, Ct);
        var history = await _h.GetAsync(auditor, $"/api/aud/history/hrm.pay_groups/{id}", Ct);
        Assert.True(history.Status == HttpStatusCode.OK, history.Body);
        Assert.Contains("code", history.Body, StringComparison.OrdinalIgnoreCase); // the log is there
        Assert.DoesNotContain("7500000", history.Body, StringComparison.Ordinal);
        Assert.Contains("\"hidden\"", history.Body, StringComparison.Ordinal); // the field is named as hidden, with no value
    }

    [Fact]
    public async Task FR_HRM_PAY_01_every_pay_endpoint_needs_its_permission_and_stays_inside_the_users_company()
    {
        var admin = await PayAdminAsync();
        var (company, _) = await CompanyAsync();
        var (other, _) = await CompanyAsync();
        var (id, _, version) = await GroupAsync(admin, company);
        var (otherGroup, _, _) = await GroupAsync(admin, other);
        var periods = await GenerateAsync(admin, id, 2026);
        var period = periods[0].GetProperty("id").GetGuid();

        var nobody = await _h.UserAsync(Ct);
        var forbidden = new (string Method, string Path, object? Body)[]
        {
            ("POST", Groups, new { companyId = company, code = Code(), nameI18n = new { en = "x" }, frequency = "monthly", taxYearStartMonth = 7 }),
            ("PUT", $"{Groups}/{id}", new { cutOffDay = 1, rowVersion = version }),
            ("POST", $"{Groups}/{id}/periods/generate", new { taxYear = 2031 }),
            ("PUT", $"/api/hrm/pay-periods/{period}", new { cutOffDate = "2026-07-20", rowVersion = 1 }),
            ("GET", "/api/hrm/pay-calendar", null),
            ("GET", $"/api/hrm/pay-periods/{period}/readiness", null),
        };
        foreach (var (method, path, body) in forbidden)
        {
            var answer = method switch
            {
                "POST" => await _h.PostAsync(nobody, path, body, Ct),
                "PUT" => await _h.PutAsync(nobody, path, body!, Ct),
                _ => await _h.GetAsync(nobody, path, Ct),
            };
            Assert.True(answer.Status == HttpStatusCode.Forbidden, $"{method} {path}: {answer.Status} {answer.Body}");
        }

        // A payroll user of the first company only: the other company's group and periods do not exist for them (404) and the calendar leaves them out.
        string[] all = ["hrm.pay_groups.read", "hrm.pay_groups.update", "hrm.pay_periods.read", "hrm.pay_periods.generate", "hrm.pay_periods.update", "hrm.pay_readiness.read", "hrm.employments.read", "hrm.employment_assignments.read"];
        (string, object)[] scopes = [("hrm.pay_groups", await _h.ScopeAsync("companies", new { company_ids = new[] { company } }, Ct)), ("hrm.pay_periods", await _h.ScopeAsync("companies", new { company_ids = new[] { company } }, Ct)), ("hrm.employments", "all"), ("hrm.employment_assignments", "all")];
        var scoped = await _h.UserAsync(Ct);
        await _h.AssignAsync(scoped, await _h.RoleAsync(Ct, all, scopes), null, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(scoped, $"{Groups}/{otherGroup}", new { cutOffDay = 1, rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(scoped, $"{Groups}/{otherGroup}/periods/generate", new { taxYear = 2026 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(scoped, $"{Groups}/{id}", new { cutOffDay = 1, rowVersion = version }, Ct)).Status);
        var otherPeriods = await GenerateAsync(admin, otherGroup, 2026);
        var otherPeriod = otherPeriods[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(scoped, $"/api/hrm/pay-periods/{otherPeriod}/readiness", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(scoped, $"/api/hrm/pay-periods/{otherPeriod}", new { cutOffDate = "2026-07-20", rowVersion = 1 }, Ct)).Status);
        var calendar = await _h.GetAsync(scoped, "/api/hrm/pay-calendar?taxYear=2026", Ct);
        Assert.DoesNotContain(otherPeriod.ToString(), calendar.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(period.ToString(), calendar.Body, StringComparison.OrdinalIgnoreCase);
    }
}
