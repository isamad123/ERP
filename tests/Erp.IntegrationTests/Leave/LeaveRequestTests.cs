#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Contracts.Leave;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Leave;

/// <summary>
/// Leave requests and the rest of the leave engine of slice S1.32b (Build items 7 to 14) through the real API pipeline, the real access engine, the
/// real workflow engine, the real job worker and event dispatcher and a real PostgreSQL: FR-HRM-LVE-04 (full, half-day and hourly requests with the
/// balance impact before submitting), LVE-05 (the route: line manager, then HR where the policy says), LVE-06 (cancellation and change, balance
/// restored by reversal), LVE-07 (unpaid days to payroll), LVE-08 (comp-off earned once, expiring), LVE-11 (year-end preview, rollback, confirm once
/// with a second person), LVE-16 (a document before submitting), FR-XM-26 (locked days refused), FR-XM-30 (transfer carry-over), FR-XM-32 (nobody
/// approves their own request) and GHR-09 (HR applying for an employee is recorded). The company's date comes from the harness clock in Asia/Karachi.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class LeaveRequestTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Requests = "/api/hrm/leave-requests";
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The company's business date (default time zone Asia/Karachi), as the leave services use it.</summary>
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private int Prev => Today.Year - 1;

    /// <summary>
    /// A Monday at least three weeks ahead whose next four weeks stay in one calendar (leave) year: the week the tests plan their leave in.
    /// </summary>
    private DateOnly Base
    {
        get
        {
            var d = Today.AddDays(21);
            while (d.DayOfWeek != DayOfWeek.Monday)
            {
                d = d.AddDays(1);
            }

            if (d.AddDays(28).Year != d.Year)
            {
                d = new DateOnly(d.Year + 1, 1, 8);
                while (d.DayOfWeek != DayOfWeek.Monday)
                {
                    d = d.AddDays(1);
                }
            }

            return d;
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Admin, Guid SecondHr);

    private sealed record Staff(Guid Employment, Guid User, Guid Manager, Guid ManagerUser);

    private async Task<Org> OrgAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        return new Org(company, await _h.PlaceAsync(company, Ct), await _h.UserAsync(Ct, null, "hr_admin"), await _h.UserAsync(Ct, null, "hr_admin"));
    }

    private async Task<Guid> HireAsync(Org o, DateOnly joining, Guid? user = null, Guid? manager = null, Guid? person = null)
    {
        person ??= (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Admin, Employments, EmploymentTestSupport.HireBody(o.Company, o.Place, person: person, joining: Iso(joining), manager: manager), Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var id = answer.Json.GetProperty("id").GetGuid();
        if (user is { } u)
        {
            await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, u);
        }

        return id;
    }

    /// <summary>An employee with a user (role employee) and a line manager with a user (role line_manager), both joined two years before the planned week.</summary>
    private async Task<Staff> StaffAsync(Org o, string? gender = null)
    {
        var managerUser = await _h.UserAsync(Ct, null, "line_manager");
        var manager = await HireAsync(o, Base.AddYears(-2), managerUser);
        var user = await _h.UserAsync(Ct, null, "employee");
        Guid? person = null;
        if (gender is not null)
        {
            person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, new Dictionary<string, object?> { ["gender"] = gender }, Ct))["id"]!;
        }

        return new Staff(await HireAsync(o, Base.AddYears(-2), user, manager, person), user, manager, managerUser);
    }

    private async Task<Guid> TypeAsync(Org o, string code, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["companyId"] = o.Company, ["code"] = code, ["nameI18n"] = new { en = "Leave " + code, ur = "رخصت " + code }, ["isPaid"] = true, ["counting"] = "working_days",
            ["unit"] = "half_day", ["requiresDocument"] = false, ["isCompOff"] = false, ["isUnpaidDeduction"] = false,
        };
        foreach (var p in extra is null ? [] : JsonSerializer.SerializeToElement(extra, JsonSerializerOptions.Web).EnumerateObject())
        {
            body[p.Name] = p.Value;
        }

        var answer = await _h.PostAsync(o.Admin, "/api/hrm/leave-types", body, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private static Dictionary<string, object?> Rule(string code, object? extra = null)
    {
        var rule = new Dictionary<string, object?> { ["leaveType"] = code, ["entitlement"] = new { model = "manual" } };
        foreach (var p in extra is null ? [] : JsonSerializer.SerializeToElement(extra, JsonSerializerOptions.Web).EnumerateObject())
        {
            rule[p.Name] = p.Value;
        }

        return rule;
    }

    /// <summary>A leave policy for every permanent employee of the company from long before the horizon.</summary>
    private async Task<Guid> PolicyAsync(Org o, params object[] rules)
    {
        var from = new DateOnly(Prev - 3, 1, 1);
        var (policy, _) = await _h.ActivePolicyAsync(o.Admin, o.Company, "leave", new { types = rules }, from, Ct);
        var set = await _h.ActivateSetAsync(o.Admin, o.Company, "leave", from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return policy;
    }

    /// <summary>Saturday and Sunday off for every permanent employee (shift policy), and a holiday calendar with these days.</summary>
    private async Task WeekAsync(Org o, params DateOnly[] holidays)
    {
        var from = new DateOnly(Prev - 3, 1, 1);
        var (policy, _) = await _h.ActivePolicyAsync(o.Admin, o.Company, "shift", new { weeklyOffKind = "fixed", offDays = new[] { 6, 7 } }, from, Ct);
        var set = await _h.ActivateSetAsync(o.Admin, o.Company, "shift", from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        var calendar = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into ref.calendars (id, code, name_i18n, source, is_active, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, '{"en":"Leave test calendar"}'::jsonb, 'tenant', true, now(), $3, now(), $3, 1)
            """, Ct, calendar, "LCAL" + Guid.NewGuid().ToString("N")[..10], o.Admin);
        foreach (var day in holidays)
        {
            await _h.ExecAsync("""
                insert into ref.holidays (id, calendar_id, holiday_date, name_i18n, kind, is_tentative, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, '{"en":"Test holiday"}'::jsonb, 'public', false, now(), $4, now(), $4, 1)
                """, Ct, Guid.CreateVersion7(), calendar, day, o.Admin);
        }

        await _h.ExecAsync("update org.companies set holiday_calendar_id = $2 where id = $1", Ct, o.Company, calendar);
    }

    private async Task GrantAsync(Org o, Guid employment, Guid type, decimal quantity, DateOnly? on = null)
    {
        var answer = await _h.PostAsync(o.Admin, $"{Employments}/{employment}/leave-ledger", new { leaveTypeId = type, kind = "grant", quantity, entryDate = Iso(on ?? new DateOnly(Base.Year, 1, 1)), reason = "Granted for the test" }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
    }

    private static object Ask(Guid type, DateOnly start, DateOnly? end = null, string? startPart = null, string? endPart = null, decimal? hours = null, Guid? employment = null, bool? submit = null, string? reason = null) =>
        new { employmentId = employment, leaveTypeId = type, startDate = Iso(start), endDate = end is { } e ? Iso(e) : null, startPart, endPart, hours, submit, reason };

    private async Task<Guid> PendingTaskAsync(Guid request)
    {
        var instance = await _h.ScalarAsync<Guid>("select workflow_instance_id from hrm.leave_requests where id = $1", Ct, request);
        return await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at desc limit 1", Ct, instance);
    }

    private async Task ApproveAsync(Guid approver, Guid request, string? documentType = null)
    {
        var task = documentType is null
            ? await PendingTaskAsync(request)
            : await _h.ScalarAsync<Guid>("""
                select t.id from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id
                where i.document_type = $1 and i.document_id = $2 and t.status = 'pending' order by t.created_at desc limit 1
                """, Ct, documentType, request);
        var approved = await _h.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private Task<string> StatusAsync(Guid request) => _h.ScalarAsync<string>("select status from hrm.leave_requests where id = $1", Ct, request);

    private Task<decimal> SumAsync(Guid employment, Guid type, int? year = null) =>
        _h.ScalarAsync<decimal>("select coalesce(sum(quantity), 0) from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2 and leave_year = $3", Ct, employment, type, year ?? Base.Year);

    private Task<string> KindsAsync(Guid employment, Guid type) =>
        _h.ScalarAsync<string>("select coalesce(string_agg(kind || ':' || quantity::numeric(9,2)::text, ',' order by kind, quantity), '') from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2", Ct, employment, type);

    /// <summary>Runs code in a service scope bound to the harness's customer (for the in-process contracts payroll reads).</summary>
    private async Task<T> InCustomerScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ResolvedTenant tenant;
        await using (var cp = new NpgsqlConnection(fx.Options.ConnectionString))
        {
            await cp.OpenAsync(Ct);
            tenant = (await Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory.FindByCodeAsync(cp, _h.Code, Ct))!;
        }

        var dataSource = await _h.App.Services.GetRequiredService<ITenantDataSourceFactory>().GetAsync(tenant, Ct);
        var licence = await _h.App.Services.GetRequiredService<ILicenceResolver>().ResolveAsync(tenant, dataSource, Ct);
        await using var scope = _h.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, TenantAccessMode.Full, dataSource, licence);
        return await action(scope.ServiceProvider);
    }

    /// <summary>Delivers the outbox events through the real dispatcher (the worker's consumers) until none is left or a few passes ran.</summary>
    private async Task DispatchAsync(JobRig rig)
    {
        var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
        for (var pass = 0; pass < 5; pass++)
        {
            await dispatcher.DispatchOnceAsync(_h.Code, Ct);
        }
    }

    private static async Task<Guid> RunJobAsync(JobRig rig, string job, object parameters, Guid? company, bool expectOk = true)
    {
        var worker = rig.Worker();
        var run = await rig.QueueAsync(job, parameters, company: company, ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        if (expectOk)
        {
            Assert.True(await rig.StatusAsync(run, Ct) == "succeeded", string.Join("\n", await rig.LogsAsync(run, Ct)));
        }

        return run;
    }

    // ------------------------------------------------------------------ requests, sandwich and the route (FR-HRM-LVE-04, -05, FR-XM-32)

    [Fact]
    public async Task FR_HRM_LVE_04_a_half_day_request_across_a_holiday_and_a_weekend_counts_by_the_sandwich_mode_and_shows_the_balance_impact()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var tuesday = Base.AddDays(1);
        await WeekAsync(o, Base.AddDays(2)); // Wednesday is a holiday; Saturday and Sunday are off
        var both = await TypeAsync(o, "SANDB");
        var holidays = await TypeAsync(o, "SANDH");
        var off = await TypeAsync(o, "SANDO");
        var whole = await TypeAsync(o, "WHOLE", new { unit = "day" });
        await PolicyAsync(o, Rule("SANDB", new { sandwich = "both" }), Rule("SANDH", new { sandwich = "holidays" }), Rule("SANDO", new { sandwich = "off" }), Rule("WHOLE"));
        await GrantAsync(o, s.Employment, both, 10m);

        // Tuesday afternoon to the next Monday morning: Tue ½, Wed holiday, Thu, Fri, Sat and Sun off, Mon ½.
        async Task<JsonElement> PreviewAsync(Guid type)
        {
            var answer = await _h.PostAsync(s.User, $"{Requests}/preview", Ask(type, tuesday, Base.AddDays(7), "second_half", "first_half"), Ct);
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
            return answer.Json;
        }

        var all = await PreviewAsync(both);
        Assert.True(all.GetProperty("allowed").GetBoolean(), all.ToString());
        var plan = all.GetProperty("plan");
        Assert.Equal(6m, plan.GetProperty("total").GetDecimal());
        Assert.Equal("leave:0.5,sandwich:1,leave:1,leave:1,sandwich:1,sandwich:1,leave:0.5",
            string.Join(',', plan.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("kind").GetString() + ":" + d.GetProperty("quantity").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Contains("hrm.leave.day.sandwich_holiday", plan.ToString(), StringComparison.Ordinal);
        Assert.Equal(10m, plan.GetProperty("balance").GetDecimal());
        Assert.Equal(4m, plan.GetProperty("after").GetDecimal());
        Assert.Equal(4m, (await PreviewAsync(holidays)).GetProperty("plan").GetProperty("total").GetDecimal()); // the holiday counts, the weekend does not
        Assert.Equal(3m, (await PreviewAsync(off)).GetProperty("plan").GetProperty("total").GetDecimal());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_requests where employment_id = $1", Ct, s.Employment)); // a preview saves nothing

        // A leading or trailing off day never counts; a type taken in whole days refuses a half day.
        var trailing = await _h.PostAsync(s.User, $"{Requests}/preview", Ask(both, Base.AddDays(3), Base.AddDays(6)), Ct);
        Assert.Equal(2m, trailing.Json.GetProperty("plan").GetProperty("total").GetDecimal());
        var half = await _h.PostAsync(s.User, Requests, Ask(whole, Base, startPart: "first_half"), Ct);
        Assert.True(half.Status == HttpStatusCode.BadRequest, half.Body);
        Assert.Contains("errors.hrm.leave.request.half_not_allowed", half.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_LVE_05_FR_XM_32_the_line_manager_then_HR_approve_the_employee_never_approves_their_own_and_approval_moves_the_balance()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        await WeekAsync(o, Base.AddDays(2));
        var type = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, Rule("ANNUAL", new { sandwich = "both", hrApproval = true }));
        await GrantAsync(o, s.Employment, type, 10m);

        var asked = await _h.PostAsync(s.User, Requests, Ask(type, Base.AddDays(1), Base.AddDays(7), "second_half", "first_half", reason: "Family visit"), Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        Assert.Equal("submitted", await StatusAsync(id));
        Assert.Matches(@"^LV-\d{4}-\d{6}$", asked.Json.GetProperty("requestNo").GetString()!);
        Assert.Equal(10m, await SumAsync(s.Employment, type)); // nothing moves before approval
        Assert.Equal(3L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_request_days where leave_request_id = $1 and day_kind = 'sandwich' and request_status = 'submitted'", Ct, id));

        // The employee cannot decide their own request (the kernel refuses requester and subject); the line manager decides the first step.
        var task = await PendingTaskAsync(id);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(s.User, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal(s.ManagerUser, await _h.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, task));
        await ApproveAsync(s.ManagerUser, id);
        Assert.Equal("submitted", await StatusAsync(id)); // the policy sets HR approval: the second step waits
        await ApproveAsync(o.Admin, id);

        Assert.Equal("approved", await StatusAsync(id));
        Assert.Equal("grant:10.00,usage:-6.00", await KindsAsync(s.Employment, type));
        Assert.Equal(4m, await SumAsync(s.Employment, type));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_request_days where leave_request_id = $1 and request_status <> 'approved'", Ct, id));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.attendance.days_requeued' and aggregate_id = $1", Ct, s.Employment) >= 1);
        var details = await _h.GetAsync(s.User, $"{Requests}/{id}/details", Ct);
        Assert.True(details.Status == HttpStatusCode.OK, details.Body);
        Assert.False(details.Json.GetProperty("onBehalf").GetBoolean());
        Assert.Equal("usage", details.Json.GetProperty("entries")[0].GetProperty("kind").GetString());

        // The attendance engine reads the approved days (contract of S1.29a): the halves on the first and last day.
        var leaveDays = await InCustomerScopeAsync<IReadOnlyList<Erp.Hrm.Contracts.Attendance.ApprovedLeaveDay>>(sp =>
            sp.GetRequiredService<Erp.Hrm.Contracts.Attendance.IApprovedLeaveDays>().ForAsync(s.Employment, Base, Base.AddDays(7), Ct));
        Assert.Equal(7, leaveDays.Count);
        Assert.Equal("second_half", leaveDays[0].Part);
        Assert.Equal("first_half", leaveDays[^1].Part);
    }

    [Fact]
    public async Task GHR_09_FR_XM_32_HR_applies_for_an_employee_is_recorded_and_cannot_approve_it_and_notice_binds_only_the_employee()
    {
        var o = await OrgAsync();
        var employeeUser = await _h.UserAsync(Ct, null, "employee");
        var e = await HireAsync(o, Base.AddYears(-2), employeeUser); // no line manager: the route falls back to the HR administrators
        var type = await TypeAsync(o, "SICK");
        await PolicyAsync(o, Rule("SICK", new { minNoticeDays = 60 }));
        await GrantAsync(o, e, type, 5m);

        var own = await _h.PostAsync(employeeUser, Requests, Ask(type, Base), Ct);
        Assert.True(own.Status == HttpStatusCode.Conflict, own.Body);
        Assert.Contains("errors.hrm.leave.request.notice", own.Body, StringComparison.Ordinal);

        var applied = await _h.PostAsync(o.Admin, Requests, Ask(type, Base, employment: e), Ct);
        Assert.True(applied.Status == HttpStatusCode.Accepted, applied.Body);
        var id = applied.Json.GetProperty("id").GetGuid();
        Assert.Equal(o.Admin, await _h.ScalarAsync<Guid>("select created_by from hrm.leave_requests where id = $1", Ct, id));
        var details = await _h.GetAsync(employeeUser, $"{Requests}/{id}/details", Ct);
        Assert.True(details.Json.GetProperty("onBehalf").GetBoolean());
        Assert.Equal(o.Admin, details.Json.GetProperty("appliedBy").GetGuid());

        var task = await PendingTaskAsync(id);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Admin, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(employeeUser, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        await ApproveAsync(o.SecondHr, id);
        Assert.Equal("approved", await StatusAsync(id));
        Assert.Equal(4m, await SumAsync(e, type));
    }

    [Fact]
    public async Task FR_HRM_LVE_04_the_checks_refuse_balance_longest_overlap_gender_combining_no_policy_and_another_persons_employment()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o, gender: "male");
        var annual = await TypeAsync(o, "ANNUAL");
        var casual = await TypeAsync(o, "CASUAL");
        var maternity = await TypeAsync(o, "MATERN");
        var unlisted = await TypeAsync(o, "UNLISTED");
        await PolicyAsync(o, Rule("ANNUAL", new { maxConsecutiveDays = 3, notCombinedWith = new[] { "CASUAL" } }), Rule("CASUAL"), Rule("MATERN", new { eligibility = new { genders = new[] { "female" } } }));
        await GrantAsync(o, s.Employment, annual, 3m);
        await GrantAsync(o, s.Employment, casual, 2m);

        async Task RefusedAsync(object body, string key, HttpStatusCode status = HttpStatusCode.Conflict)
        {
            var answer = await _h.PostAsync(s.User, Requests, body, Ct);
            Assert.True(answer.Status == status, answer.Body);
            Assert.Contains(key, answer.Body, StringComparison.Ordinal);
        }

        await RefusedAsync(Ask(casual, Base, Base.AddDays(2)), "errors.hrm.leave.below_allowed"); // 3 days of a balance of 2, no negative limit
        await RefusedAsync(Ask(annual, Base, Base.AddDays(3)), "errors.hrm.leave.request.longest");
        await RefusedAsync(Ask(maternity, Base), "errors.hrm.leave.request.gender");
        await RefusedAsync(Ask(unlisted, Base), "errors.hrm.leave.no_policy");
        await RefusedAsync(Ask(annual, Base.AddDays(3), Base.AddDays(1)), "errors.hrm.leave.request.date_order", HttpStatusCode.BadRequest);

        // Casual leave on Monday and Tuesday is approved; annual leave on Tuesday overlaps and on Wednesday touches it (not combined).
        var first = await _h.PostAsync(s.User, Requests, Ask(casual, Base, Base.AddDays(1)), Ct);
        Assert.True(first.Status == HttpStatusCode.Accepted, first.Body);
        await ApproveAsync(s.ManagerUser, first.Json.GetProperty("id").GetGuid());
        await RefusedAsync(Ask(annual, Base.AddDays(1)), "errors.hrm.leave.request.overlap");
        await RefusedAsync(Ask(annual, Base.AddDays(2)), "errors.hrm.leave.request.combined");
        Assert.Equal(HttpStatusCode.Accepted, (await _h.PostAsync(s.User, Requests, Ask(annual, Base.AddDays(3)), Ct)).Status); // a working day between them

        // Waiting requests count against the balance: 1 annual day of 3 is waiting, 3 more would go below zero.
        await RefusedAsync(Ask(annual, Base.AddDays(7), Base.AddDays(9)), "errors.hrm.leave.below_allowed");

        // The employee applies only for their own employment: another one does not exist for them; without the permission it is refused.
        var colleague = await HireAsync(o, Base.AddYears(-1));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(s.User, Requests, Ask(annual, Base.AddDays(14), employment: colleague), Ct)).Status);
        var outsider = await _h.UserAsync(Ct, null, "auditor");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(outsider, Requests, Ask(annual, Base.AddDays(14), employment: colleague), Ct)).Status);
    }

    // ------------------------------------------------------------------ cancellation and change (FR-HRM-LVE-06)

    [Fact]
    public async Task FR_HRM_LVE_06_cancellation_restores_the_balance_by_reversal_and_a_change_replaces_the_old_request_once_approved()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var type = await TypeAsync(o, "ANNUAL");
        var casual = await TypeAsync(o, "CASUAL");
        await PolicyAsync(o, Rule("ANNUAL"), Rule("CASUAL", new { cancellationApproval = true }));
        await GrantAsync(o, s.Employment, type, 20m);
        await GrantAsync(o, s.Employment, casual, 5m);

        var a = (await _h.PostAsync(s.User, Requests, Ask(type, Base, Base.AddDays(1)), Ct)).Json.GetProperty("id").GetGuid();
        await ApproveAsync(s.ManagerUser, a);
        Assert.Equal(18m, await SumAsync(s.Employment, type));

        // A change is a new request linked to the old one; the old one stays approved until the change is approved.
        var changed = await _h.PostAsync(s.User, $"{Requests}/{a}/change", Ask(type, Base.AddDays(7), Base.AddDays(9)), Ct);
        Assert.True(changed.Status == HttpStatusCode.Accepted, changed.Body);
        var b = changed.Json.GetProperty("id").GetGuid();
        Assert.Equal(a, await _h.ScalarAsync<Guid>("select modifies_request_id from hrm.leave_requests where id = $1", Ct, b));
        Assert.Equal("approved", await StatusAsync(a));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(s.User, $"{Requests}/{a}/change", Ask(type, Base.AddDays(14)), Ct)).Status); // one change at a time
        await ApproveAsync(s.ManagerUser, b);
        Assert.Equal("cancelled", await StatusAsync(a));
        Assert.Equal("approved", await StatusAsync(b));
        Assert.Equal("grant:20.00,usage:-3.00,usage:-2.00,usage_reversal:2.00", await KindsAsync(s.Employment, type));

        // Cancelling approved leave reverses its usage at once when the policy asks no approval.
        var withdrawn = await _h.PostAsync(s.User, $"{Requests}/{b}/cancel", new { reason = "Plans changed" }, Ct);
        Assert.True(withdrawn.Status == HttpStatusCode.OK, withdrawn.Body);
        Assert.Equal("cancelled", await StatusAsync(b));
        Assert.Equal(20m, await SumAsync(s.Employment, type));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(s.User, $"{Requests}/{b}/cancel", new { reason = "Again" }, Ct)).Status);

        // Where the policy says so, the cancellation waits for its own approval (the leave stays taken until then).
        var c = (await _h.PostAsync(s.User, Requests, Ask(casual, Base.AddDays(14)), Ct)).Json.GetProperty("id").GetGuid();
        await ApproveAsync(s.ManagerUser, c);
        var asked = await _h.PostAsync(s.User, $"{Requests}/{c}/cancel", new { reason = "Not needed" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.Equal("cancel_requested", await StatusAsync(c));
        Assert.Equal(4m, await SumAsync(s.Employment, casual));
        await ApproveAsync(s.ManagerUser, c, "hrm.leave_cancellation");
        Assert.Equal("cancelled", await StatusAsync(c));
        Assert.Equal(5m, await SumAsync(s.Employment, casual));

        // A request still with its approvers is withdrawn through the approvals, which tells the leave engine.
        var d = await _h.PostAsync(s.User, Requests, Ask(type, Base.AddDays(21)), Ct);
        var dId = d.Json.GetProperty("id").GetGuid();
        var cancelWaiting = await _h.PostAsync(s.User, $"{Requests}/{dId}/cancel", new { reason = "x" }, Ct);
        Assert.Contains("errors.hrm.leave.request.withdraw", cancelWaiting.Body, StringComparison.Ordinal);
        var instance = d.Json.GetProperty("workflowInstanceId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(s.User, $"/api/wf/instances/{instance}/withdraw", new { reason = "Not needed" }, Ct)).Status);
        Assert.Equal("cancelled", await StatusAsync(dId));
    }

    // ------------------------------------------------------------------ documents (FR-HRM-LVE-16)

    [Fact]
    public async Task FR_HRM_LVE_16_a_type_needing_a_document_is_submitted_once_a_file_is_attached_to_the_draft()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var type = await TypeAsync(o, "MEDICAL", new { requiresDocument = true });
        await PolicyAsync(o, Rule("MEDICAL"));
        await GrantAsync(o, s.Employment, type, 5m);

        var direct = await _h.PostAsync(s.User, Requests, Ask(type, Base), Ct);
        Assert.True(direct.Status == HttpStatusCode.BadRequest, direct.Body);
        Assert.Contains("errors.hrm.leave.request.document_required", direct.Body, StringComparison.Ordinal);
        var draft = await _h.PostAsync(s.User, Requests, Ask(type, Base, submit: false), Ct);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        var id = draft.Json.GetProperty("id").GetGuid();
        Assert.Equal("draft", await StatusAsync(id));
        Assert.Contains("errors.hrm.leave.request.document_required", (await _h.PostAsync(s.User, $"{Requests}/{id}/submit", new { }, Ct)).Body, StringComparison.Ordinal);

        // A draft can still change; the file (as the file store keeps it) is attached to the request, then the request is submitted.
        var moved = await _h.PutAsync(s.User, $"{Requests}/{id}/draft", new { startDate = Iso(Base.AddDays(1)) }, Ct);
        Assert.True(moved.Status == HttpStatusCode.OK, moved.Body);
        var file = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, is_sensitive, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'hrm.leave_requests', $3, 'certificate.pdf', 'application/pdf', 10, 'x', 'k', 1, true, 'clean', now(), $4, now(), $4)
            """, Ct, file, o.Company, id, s.User);
        var sent = await _h.PostAsync(s.User, $"{Requests}/{id}/submit", new { }, Ct);
        Assert.True(sent.Status == HttpStatusCode.Accepted, sent.Body);
        Assert.Equal(file, await _h.ScalarAsync<Guid>("select file_ids[1] from hrm.leave_requests where id = $1", Ct, id));
        Assert.Equal(Base.AddDays(1), await _h.ScalarAsync<DateOnly>("select start_date from hrm.leave_requests where id = $1", Ct, id));
        var current = await InCustomerScopeAsync(sp => sp.GetRequiredService<Erp.Kernel.Contracts.Documents.IRecordFiles>().CurrentAsync("hrm.leave_requests", id, null, Ct));
        Assert.Equal([file], current); // the kernel's answer of the request's current files
    }

    // ------------------------------------------------------------------ unpaid leave, hourly leave, locked periods (FR-HRM-LVE-07, -04, FR-XM-26)

    [Fact]
    public async Task FR_HRM_LVE_07_FR_XM_26_unpaid_days_reach_the_payroll_feed_and_leave_in_a_locked_period_is_refused()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var unpaid = await TypeAsync(o, "LWP", new { isPaid = false, isUnpaidDeduction = true });
        await PolicyAsync(o, Rule("LWP"));

        // No balance is needed for unpaid leave; its days reach payroll once approved.
        var asked = await _h.PostAsync(s.User, Requests, Ask(unpaid, Base, Base.AddDays(1), endPart: "first_half"), Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        var none = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILeaveDeductions>().ForAsync([s.Employment], Base, Base.AddDays(6), Ct));
        Assert.Empty(none);
        await ApproveAsync(s.ManagerUser, id);
        var days = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILeaveDeductions>().ForAsync([s.Employment], Base, Base.AddDays(6), Ct));
        Assert.Equal("LWP:1:False,LWP:0.5:False", string.Join(',', days.Select(d => $"{d.LeaveTypeCode}:{d.Days.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}:{d.Locked}")));

        // Payroll locks the attendance period of the week after: leave in it is refused, cancelling leave in it too.
        var group = (Guid)(await _h.RowAsync("hrm.pay_groups", o.Company, null, Ct))["id"]!;
        var start = Base.AddDays(7);
        var end = Base.AddDays(13);
        var period = (Guid)(await _h.RowAsync("hrm.pay_periods", o.Company, new Dictionary<string, object?> { ["pay_group_id"] = group, ["start_date"] = start, ["end_date"] = end, ["cut_off_date"] = end }, Ct))["id"]!;
        await _h.RowAsync("hrm.attendance_periods", o.Company, new Dictionary<string, object?> { ["pay_group_id"] = group, ["pay_period_id"] = period, ["period_start"] = start, ["period_end"] = end, ["status"] = "open" }, Ct);
        await _h.ExecAsync("update hrm.employment_assignments set pay_group_id = $2 where employment_id = $1", Ct, s.Employment, group);
        var later = (await _h.PostAsync(s.User, Requests, Ask(unpaid, Base.AddDays(8)), Ct)).Json.GetProperty("id").GetGuid();
        await ApproveAsync(s.ManagerUser, later);
        await _h.ExecAsync("update hrm.attendance_periods set status = 'locked' where pay_period_id = $1", Ct, period);
        var refused = await _h.PostAsync(s.User, Requests, Ask(unpaid, Base.AddDays(9)), Ct);
        Assert.True(refused.Status == HttpStatusCode.Conflict, refused.Body);
        Assert.Contains("errors.hrm.leave.period_locked", refused.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.period_locked", (await _h.PostAsync(s.User, $"{Requests}/{later}/cancel", new { reason = "x" }, Ct)).Body, StringComparison.Ordinal);
        var locked = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILeaveDeductions>().ForAsync([s.Employment], start, end, Ct));
        Assert.True(Assert.Single(locked).Locked);
    }

    [Fact]
    public async Task FR_HRM_LVE_04_hourly_leave_takes_hours_of_the_working_day_from_the_setting_when_no_shift_plans_it()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var shortLeave = await TypeAsync(o, "SHORT", new { counting = "hours", unit = "hour" });
        await PolicyAsync(o, Rule("SHORT"));
        await GrantAsync(o, s.Employment, shortLeave, 16m);

        var unknown = await _h.PostAsync(s.User, Requests, Ask(shortLeave, Base, hours: 3m), Ct);
        Assert.Contains("errors.hrm.leave.request.hours_unknown", unknown.Body, StringComparison.Ordinal); // no number is filled in for you
        var tenant = await _h.UserAsync(Ct, null, "tenant_admin");
        var set = await _h.PutAsync(tenant, "/api/cfg/settings/hrm.leave.hours_per_day", new { layer = "company", scopeId = o.Company, value = 8, reason = "test" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);

        Assert.Contains("errors.hrm.leave.request.hours_too_many", (await _h.PostAsync(s.User, Requests, Ask(shortLeave, Base, hours: 9m), Ct)).Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.leave.request.hours_one_day", (await _h.PostAsync(s.User, Requests, Ask(shortLeave, Base, Base.AddDays(1), hours: 2m), Ct)).Body, StringComparison.Ordinal);
        var asked = await _h.PostAsync(s.User, Requests, Ask(shortLeave, Base, hours: 3m), Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        Assert.Equal(0.375m, await _h.ScalarAsync<decimal>("select days from hrm.leave_requests where id = $1", Ct, id));
        await ApproveAsync(s.ManagerUser, id);
        Assert.Equal(13m, await SumAsync(s.Employment, shortLeave));

        // Five more hours fit the day; one more would be more than the day's eight.
        Assert.Equal(HttpStatusCode.Accepted, (await _h.PostAsync(s.User, Requests, Ask(shortLeave, Base, hours: 5m), Ct)).Status);
        Assert.Contains("errors.hrm.leave.request.overlap", (await _h.PostAsync(s.User, Requests, Ask(shortLeave, Base, hours: 1m), Ct)).Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ comp-off and expiry (FR-HRM-LVE-08)

    [Fact]
    public async Task FR_HRM_LVE_08_comp_off_is_earned_once_from_the_event_with_the_policy_expiry_and_the_expiry_job_takes_what_is_left()
    {
        var o = await OrgAsync();
        var e = await HireAsync(o, new DateOnly(Prev - 1, 1, 1));
        var comp = await TypeAsync(o, "COMP", new { isCompOff = true, unit = "day" });
        await PolicyAsync(o, Rule("COMP", new { compOffExpiryDays = 30 }));
        var workDate = new DateOnly(Prev, 3, 1);
        var source = Guid.CreateVersion7();
        async Task PublishAsync() => await _h.ExecAsync("""
            insert into hrm.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers)
            values ($1, now(), 'hrm.attendance.comp_off_earned', 'hrm.attendance_day', $2, jsonb_build_object('employmentId', $2::text, 'companyId', $3::text, 'workDate', $4::text, 'days', 1, 'minutes', 480, 'sourceId', $5::text), '{}'::jsonb)
            """, Ct, Guid.CreateVersion7(), e, o.Company, Iso(workDate), source);

        await PublishAsync();
        await PublishAsync(); // the same overtime announced twice earns once
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        await DispatchAsync(rig);
        Assert.Equal("comp_off_earned:1.00", await KindsAsync(e, comp));
        Assert.Equal(workDate.AddDays(30), await _h.ScalarAsync<DateOnly>("select expires_on from hrm.leave_ledger where employment_id = $1 and kind = 'comp_off_earned'", Ct, e));

        await RunJobAsync(rig, "hrm.leave_expiry", new { }, o.Company);
        await RunJobAsync(rig, "hrm.leave_expiry", new { }, o.Company); // a second run posts nothing new
        Assert.Equal("comp_off_earned:1.00,comp_off_expired:-1.00", await KindsAsync(e, comp));
        Assert.Equal(workDate.AddDays(31), await _h.ScalarAsync<DateOnly>("select entry_date from hrm.leave_ledger where employment_id = $1 and kind = 'comp_off_expired'", Ct, e));
    }

    // ------------------------------------------------------------------ year-end (FR-HRM-LVE-11)

    [Fact]
    public async Task FR_HRM_LVE_11_the_preview_changes_nothing_rollback_leaves_no_entry_and_a_confirmation_by_a_second_person_posts_once()
    {
        var o = await OrgAsync();
        var e = await HireAsync(o, new DateOnly(Prev - 1, 1, 1));
        var type = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, Rule("ANNUAL", new { carryForward = new { cap = 5, expiryMonths = 3 }, encashment = new { cap = 2, rateBasis = "basic" } }));
        await GrantAsync(o, e, type, 10m, new DateOnly(Prev, 1, 1));
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);

        await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = Prev }, o.Company);
        var first = await _h.ScalarAsync<Guid>("select id from hrm.leave_year_end_runs where company_id = $1 and status = 'preview'", Ct, o.Company);
        Assert.Equal("10:5:2:3", await _h.ScalarAsync<string>("""
            select concat_ws(':', (l->>'closing')::numeric::int, (l->>'carryForward')::numeric::int, (l->>'encash')::numeric::int, (l->>'lapse')::numeric::int)
            from hrm.leave_year_end_runs r, jsonb_array_elements(r.summary->'lines') l where r.id = $1
            """, Ct, first));
        Assert.Equal("grant:10.00", await KindsAsync(e, type)); // the preview posts nothing
        var rolled = await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{first}/rollback", new { reason = "Policy to be checked" }, Ct);
        Assert.True(rolled.Status == HttpStatusCode.OK, rolled.Body);
        Assert.Equal("grant:10.00", await KindsAsync(e, type));

        // A new preview; the confirmation waits for a second person (the setting is on by default), who cannot be the maker.
        await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = Prev }, o.Company);
        var run = await _h.ScalarAsync<Guid>("select id from hrm.leave_year_end_runs where company_id = $1 and status = 'preview'", Ct, o.Company);
        var held = await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{run}/confirm", new { reason = "Year closed" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal("grant:10.00", await KindsAsync(e, type));
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Admin, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct)).Status);
        var approved = await _h.PostAsync(o.SecondHr, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "Checked" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);

        Assert.Equal("applied", await _h.ScalarAsync<string>("select status from hrm.leave_year_end_runs where id = $1", Ct, run));
        Assert.Equal(0m, await SumAsync(e, type, Prev));
        Assert.Equal(5m, await SumAsync(e, type, Prev + 1));
        Assert.Equal(new DateOnly(Prev + 1, 3, 31), await _h.ScalarAsync<DateOnly>("select expires_on from hrm.leave_ledger where employment_id = $1 and leave_year = $2 and kind = 'carry_forward'", Ct, e, Prev + 1));
        Assert.Equal("carry_forward:-5.00,carry_forward:5.00,encashment:-2.00,grant:10.00,lapse:-3.00", await KindsAsync(e, type));
        var paid = await InCustomerScopeAsync(sp => sp.GetRequiredService<ILeaveEncashments>().ForAsync([e], new DateOnly(Prev, 1, 1), new DateOnly(Prev, 12, 31), Ct));
        var encashment = Assert.Single(paid);
        Assert.Equal((2m, "basic", "year_end"), (encashment.Units, encashment.RateBasis, encashment.Source));

        // Confirming again posts nothing; another preview of the year is refused; the closed year takes no more leave.
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{run}/confirm", new { }, Ct)).Status);
        var again = await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = Prev }, o.Company, expectOk: false);
        Assert.NotEqual("succeeded", await rig.StatusAsync(again, Ct));
        Assert.Equal(5L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where employment_id = $1", Ct, e));
        var late = await _h.PostAsync(o.Admin, $"{Employments}/{e}/leave-ledger", new { leaveTypeId = type, kind = "adjustment", quantity = 1, entryDate = Iso(new DateOnly(Prev, 6, 1)), reason = "Late" }, Ct);
        Assert.Contains("errors.hrm.leave.year_closed", late.Body, StringComparison.Ordinal); // a correction goes into the open year
        var request2 = await _h.PostAsync(o.Admin, Requests, Ask(type, new DateOnly(Prev, 6, 3), employment: e), Ct);
        Assert.Contains("errors.hrm.leave.year_closed", request2.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ transfer carry-over (FR-XM-30, CR-095)

    private async Task NoTransferApprovalAsync(Guid company)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private async Task<Guid> TransferAsync(Org from, Org to, Guid employment, DateOnly date, string leave)
    {
        var moved = await _h.PostAsync(from.Admin, $"{Employments}/{employment}/transfer", new
        {
            toCompanyId = to.Company,
            effectiveDate = Iso(date),
            assignment = new { orgUnitId = to.Place.OrgUnit, branchId = to.Place.Branch, title = "Analyst" },
            carryOver = new { leave },
            rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment),
        }, Ct);
        Assert.True(moved.Status == HttpStatusCode.Created, moved.Body);
        return moved.Json.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task FR_XM_30_a_transfer_with_carry_moves_each_balance_by_one_pair_with_the_same_source_lapse_posts_one_lapse_and_a_repeat_posts_nothing()
    {
        var first = await OrgAsync();
        var second = first with { Company = await _h.CompanyAsync(Ct) };
        second = second with { Place = await _h.PlaceAsync(second.Company, Ct) };
        await NoTransferApprovalAsync(first.Company);
        await NoTransferApprovalAsync(second.Company);
        var march = new DateOnly(Today.Year, 3, 1) >= Today ? new DateOnly(Today.Year - 1, 3, 1) : new DateOnly(Today.Year, 3, 1);
        var annual = await TypeAsync(first, "ANNUAL");
        var casual = await TypeAsync(first, "CASUAL");
        var annualThere = await TypeAsync(second, "ANNUAL");
        var carried = await HireAsync(first, march.AddDays(-300));
        var lapsed = await HireAsync(first, march.AddDays(-300));
        foreach (var e in new[] { carried, lapsed })
        {
            await GrantAsync(first, e, annual, 7m, new DateOnly(march.Year, 1, 1));
            await GrantAsync(first, e, casual, 2m, new DateOnly(march.Year, 1, 1));
        }

        var next = await TransferAsync(first, second, carried, march, "carry");
        var nextLapsed = await TransferAsync(first, second, lapsed, march, "lapse");
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        await DispatchAsync(rig);

        // Carry: one pair per type with a match (same code), the same source on both sides; the type with no match waits for HR.
        Assert.Equal("grant:7.00,transfer_out:-7.00", await KindsAsync(carried, annual));
        Assert.Equal("transfer_in:7.00", await KindsAsync(next, annualThere));
        Assert.Equal(1L, await _h.ScalarAsync<long>("""
            select count(distinct o.source_id) from hrm.leave_ledger o join hrm.leave_ledger i on i.source_id = o.source_id and i.kind = 'transfer_in' and i.employment_id = $2
            where o.employment_id = $1 and o.kind = 'transfer_out'
            """, Ct, carried, next));
        Assert.Equal(march.AddDays(-1), await _h.ScalarAsync<DateOnly>("select entry_date from hrm.leave_ledger where employment_id = $1 and kind = 'transfer_out'", Ct, carried));
        var view = await _h.GetAsync(first.Admin, $"{Employments}/{next}/leave-transfer", Ct);
        Assert.True(view.Status == HttpStatusCode.OK, view.Body);
        Assert.Equal("ANNUAL:posted,CASUAL:needs_mapping", string.Join(',', view.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString() + ":" + i.GetProperty("status").GetString())));

        // Lapse: one lapse per type, nothing moves.
        Assert.Equal("grant:7.00,lapse:-7.00", await KindsAsync(lapsed, annual));
        Assert.Equal(string.Empty, await KindsAsync(nextLapsed, annualThere));

        // The same event delivered again posts nothing new; HR maps the waiting type and its pair is posted once.
        await _h.ExecAsync("""
            insert into hrm.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers)
            select $2, now(), event_type, aggregate_type, aggregate_id, payload, headers from hrm.outbox_messages where event_type = 'hrm.employee_separated' and aggregate_id = $1
            """, Ct, carried, Guid.CreateVersion7());
        await DispatchAsync(rig);
        Assert.Equal(4L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where employment_id in ($1, $2)", Ct, carried, next));
        var mapped = await _h.PostAsync(first.Admin, $"{Employments}/{carried}/leave-transfer", new { maps = new[] { new { fromLeaveTypeId = casual, toLeaveTypeId = annualThere } } }, Ct);
        Assert.True(mapped.Status == HttpStatusCode.OK, mapped.Body);
        Assert.Equal(1, mapped.Json.GetProperty("posted").GetInt32());
        Assert.Equal("transfer_in:2.00,transfer_in:7.00", await KindsAsync(next, annualThere));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(first.Admin, $"{Employments}/{carried}/leave-transfer", new { maps = new[] { new { fromLeaveTypeId = casual, toLeaveTypeId = annualThere } } }, Ct)).Status);
    }

    // ------------------------------------------------------------------ review S1.32b fixes (findings 2 to 6)

    [Fact]
    public async Task GHR_09_review_2_for_an_employee_without_a_sign_in_the_line_manager_step_goes_to_the_employees_manager_not_the_appliers()
    {
        var o = await OrgAsync();
        var hrManagerUser = await _h.UserAsync(Ct, null, "line_manager");
        var hrManager = await HireAsync(o, Base.AddYears(-3), hrManagerUser);
        await HireAsync(o, Base.AddYears(-3), o.Admin, hrManager); // the HR administrator is an employee with a line manager of their own
        var workerManagerUser = await _h.UserAsync(Ct, null, "line_manager");
        var workerManager = await HireAsync(o, Base.AddYears(-3), workerManagerUser);
        var worker = await HireAsync(o, Base.AddYears(-2), manager: workerManager); // no sign-in
        var type = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, Rule("ANNUAL"));
        await GrantAsync(o, worker, type, 5m);

        var applied = await _h.PostAsync(o.Admin, Requests, Ask(type, Base, employment: worker), Ct);
        Assert.True(applied.Status == HttpStatusCode.Accepted, applied.Body);
        var id = applied.Json.GetProperty("id").GetGuid();
        Assert.Equal(workerManagerUser, await _h.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, await PendingTaskAsync(id)));
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(hrManagerUser, $"/api/wf/tasks/{await PendingTaskAsync(id)}/approve", new { comment = "not mine" }, Ct)).Status);
        await ApproveAsync(workerManagerUser, id);
        Assert.Equal("approved", await StatusAsync(id));
    }

    [Fact]
    public async Task FR_HRM_LVE_06_review_3_a_returned_request_can_be_withdrawn_and_a_returned_cancellation_is_sent_again_or_withdrawn()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var type = await TypeAsync(o, "CASUAL");
        await PolicyAsync(o, Rule("CASUAL", new { cancellationApproval = true }));
        await GrantAsync(o, s.Employment, type, 5m);

        // A request returned for correction is a draft; the employee drops it by withdrawing it.
        var a = await _h.PostAsync(s.User, Requests, Ask(type, Base), Ct);
        var aId = a.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(s.ManagerUser, $"/api/wf/tasks/{await PendingTaskAsync(aId)}/return", new { comment = "Wrong dates" }, Ct)).Status);
        Assert.Equal("draft", await StatusAsync(aId));
        var instance = await _h.ScalarAsync<Guid>("select workflow_instance_id from hrm.leave_requests where id = $1", Ct, aId);
        var withdrawn = await _h.PostAsync(s.User, $"/api/wf/instances/{instance}/withdraw", new { reason = "Not needed" }, Ct);
        Assert.True(withdrawn.Status == HttpStatusCode.OK, withdrawn.Body);
        Assert.Equal("cancelled", await StatusAsync(aId));

        // A returned cancellation keeps the leave taken; cancelling again sends it back to its route; then it is approved.
        var b = (await _h.PostAsync(s.User, Requests, Ask(type, Base.AddDays(7)), Ct)).Json.GetProperty("id").GetGuid();
        await ApproveAsync(s.ManagerUser, b);
        Assert.Equal(HttpStatusCode.Accepted, (await _h.PostAsync(s.User, $"{Requests}/{b}/cancel", new { reason = "Plans changed" }, Ct)).Status);
        var cancelTask = await _h.ScalarAsync<Guid>("""
            select t.id from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id
            where i.document_type = 'hrm.leave_cancellation' and i.document_id = $1 and t.status = 'pending'
            """, Ct, b);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(s.ManagerUser, $"/api/wf/tasks/{cancelTask}/return", new { comment = "Say why" }, Ct)).Status);
        Assert.Equal("cancel_requested", await StatusAsync(b));
        Assert.Equal(4m, await SumAsync(s.Employment, type));
        var again = await _h.PostAsync(s.User, $"{Requests}/{b}/cancel", new { reason = "Family plans changed" }, Ct);
        Assert.True(again.Status == HttpStatusCode.Accepted, again.Body);
        await ApproveAsync(s.ManagerUser, b, "hrm.leave_cancellation");
        Assert.Equal("cancelled", await StatusAsync(b));
        Assert.Equal(5m, await SumAsync(s.Employment, type));
    }

    [Fact]
    public async Task FR_HRM_LVE_11_review_4_and_6_a_balance_added_after_the_preview_makes_it_stale_waiting_leave_blocks_it_and_late_comp_off_keeps_its_expiry()
    {
        var o = await OrgAsync();
        var managerUser = await _h.UserAsync(Ct, null, "line_manager");
        var manager = await HireAsync(o, new DateOnly(Prev - 1, 1, 1), managerUser);
        var e1 = await HireAsync(o, new DateOnly(Prev - 1, 1, 1), await _h.UserAsync(Ct, null, "employee"), manager);
        var e2 = await HireAsync(o, new DateOnly(Prev - 1, 1, 1));
        var type = await TypeAsync(o, "ANNUAL");
        var comp = await TypeAsync(o, "COMP", new { isCompOff = true, unit = "day" });
        await PolicyAsync(o, Rule("ANNUAL", new { carryForward = new { cap = 5 } }), Rule("COMP", new { compOffExpiryDays = 30 }));
        await GrantAsync(o, e1, type, 10m, new DateOnly(Prev, 1, 1));

        // Comp-off earned on 20 December of the closing year, usable for 30 days (into January).
        await _h.ExecAsync("""
            insert into hrm.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers)
            values ($1, now(), 'hrm.attendance.comp_off_earned', 'hrm.attendance_day', $2, jsonb_build_object('employmentId', $2::text, 'companyId', $3::text, 'workDate', $4::text, 'days', 1, 'minutes', 480, 'sourceId', $5::text), '{}'::jsonb)
            """, Ct, Guid.CreateVersion7(), e1, o.Company, Iso(new DateOnly(Prev, 12, 20)), Guid.CreateVersion7());
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        await DispatchAsync(rig);
        Assert.Equal("comp_off_earned:1.00", await KindsAsync(e1, comp));

        // A grant to someone after the preview: the held confirmation is refused as stale when the second person approves it; nothing is posted.
        await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = Prev }, o.Company);
        var first = await _h.ScalarAsync<Guid>("select id from hrm.leave_year_end_runs where company_id = $1 and status = 'preview'", Ct, o.Company);
        await GrantAsync(o, e2, type, 4m, new DateOnly(Prev, 6, 1));
        var held = await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{first}/confirm", new { reason = "Year closed" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var stale = await _h.PostAsync(o.SecondHr, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "Checked" }, Ct);
        Assert.Contains("errors.hrm.leave.year_end.stale", stale.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_ledger where company_id = $1 and source_type = 'hrm.leave_year_end'", Ct, o.Company));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{first}/rollback", new { reason = "Stale" }, Ct)).Status);

        // Leave of the year still waiting for its decision blocks the confirmation (HR applies for the employee; the line manager decides).
        var waiting = await _h.PostAsync(o.Admin, Requests, Ask(type, new DateOnly(Prev, 11, 3), employment: e1), Ct);
        Assert.True(waiting.Status == HttpStatusCode.Accepted, waiting.Body);
        await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = Prev }, o.Company);
        var run = await _h.ScalarAsync<Guid>("select id from hrm.leave_year_end_runs where company_id = $1 and status = 'preview'", Ct, o.Company);
        Assert.Contains("errors.hrm.leave.year_end.requests_waiting", (await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{run}/confirm", new { }, Ct)).Body, StringComparison.Ordinal);
        await ApproveAsync(managerUser, waiting.Json.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{run}/rollback", new { reason = "Leave approved since" }, Ct)).Status);

        await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = Prev }, o.Company);
        run = await _h.ScalarAsync<Guid>("select id from hrm.leave_year_end_runs where company_id = $1 and status = 'preview'", Ct, o.Company);
        held = await _h.PostAsync(o.Admin, $"/api/hrm/leave-year-end-runs/{run}/confirm", new { reason = "Year closed" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var approved = await _h.PostAsync(o.SecondHr, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "Checked" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal(4m, await SumAsync(e2, type, Prev + 1)); // the late grant is carried (cap 5)
        Assert.Equal(5m, await SumAsync(e1, type, Prev + 1)); // 10 less 1 taken, capped at 5

        // The December comp-off is carried with its own expiry, outside the cap (the type sets no carry-forward).
        Assert.Equal(1m, await SumAsync(e1, comp, Prev + 1));
        Assert.Equal(new DateOnly(Prev, 12, 20).AddDays(30), await _h.ScalarAsync<DateOnly>("select expires_on from hrm.leave_ledger where employment_id = $1 and leave_type_id = $2 and leave_year = $3 and quantity > 0", Ct, e1, comp, Prev + 1));
    }

    [Fact]
    public async Task FR_XM_30_review_5_a_January_transfer_waits_for_last_years_year_end_and_then_moves_the_carried_balance()
    {
        var first = await OrgAsync();
        var second = first with { Company = await _h.CompanyAsync(Ct) };
        second = second with { Place = await _h.PlaceAsync(second.Company, Ct) };
        await NoTransferApprovalAsync(first.Company);
        await NoTransferApprovalAsync(second.Company);
        var year = new DateOnly(Today.Year, 1, 16) <= Today ? Today.Year : Today.Year - 1;
        var annual = await TypeAsync(first, "ANNUAL");
        var annualThere = await TypeAsync(second, "ANNUAL");
        await PolicyAsync(first, Rule("ANNUAL", new { carryForward = new { cap = 10 } }));
        var e = await HireAsync(first, new DateOnly(year - 2, 1, 1));
        await GrantAsync(first, e, annual, 8m, new DateOnly(year - 1, 1, 1));

        var next = await TransferAsync(first, second, e, new DateOnly(year, 1, 15), "carry");
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        await DispatchAsync(rig);
        var view = await _h.GetAsync(first.Admin, $"{Employments}/{e}/leave-transfer", Ct);
        Assert.Contains("waiting_year_end", view.Body, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await KindsAsync(next, annualThere));

        // Last year's year-end carries the 8 days into this year; the transfer then moves them to the new company.
        await RunJobAsync(rig, "hrm.leave_year_end", new { leave_year = year - 1 }, first.Company);
        var run = await _h.ScalarAsync<Guid>("select id from hrm.leave_year_end_runs where company_id = $1 and status = 'preview'", Ct, first.Company);
        var held = await _h.PostAsync(first.Admin, $"/api/hrm/leave-year-end-runs/{run}/confirm", new { }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(first.SecondHr, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "Checked" }, Ct)).Status);
        Assert.Equal("transfer_in:8.00", await KindsAsync(next, annualThere));
        Assert.Equal(0m, await SumAsync(e, annual, year));
    }

    // ------------------------------------------------------------------ database rules (FR-HRM-LVE-04, FR-XM-30) and closed generated writes

    [Fact]
    public async Task FR_HRM_LVE_04_the_database_refuses_a_second_whole_day_and_a_transfer_in_without_its_transfer_out_and_generated_writes_are_closed()
    {
        var o = await OrgAsync();
        var s = await StaffAsync(o);
        var type = await TypeAsync(o, "ANNUAL");
        await PolicyAsync(o, Rule("ANNUAL"));
        await GrantAsync(o, s.Employment, type, 5m);
        var id = (await _h.PostAsync(s.User, Requests, Ask(type, Base), Ct)).Json.GetProperty("id").GetGuid();
        var other = (await _h.PostAsync(o.Admin, Requests, Ask(type, Base.AddDays(1), employment: s.Employment, submit: false), Ct)).Json.GetProperty("id").GetGuid();

        var day = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("""
            insert into hrm.leave_request_days (id, company_id, leave_request_id, employment_id, leave_date, quantity, is_paid, request_status, day_kind, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, 1, true, 'submitted', 'leave', now(), $6, now(), $6)
            """, Ct, Guid.CreateVersion7(), o.Company, other, s.Employment, Base, o.Admin));
        Assert.Equal("ck_leave_request_days_daily_total", day.ConstraintName);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.leave_request_days where leave_request_id = $1", Ct, id));

        var transfer = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, source_type, source_id, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, 'transfer_in', 3, 'hrm.leave_transfer', $7, now(), $8)
            """, Ct, Guid.CreateVersion7(), o.Company, s.Employment, type, Base.Year, Base, Guid.CreateVersion7(), o.Admin));
        Assert.Equal("ck_leave_ledger_transfer", transfer.ConstraintName);

        foreach (var path in new[] { "/api/hrm/leave-request-days", "/api/hrm/leave-year-end-runs" })
        {
            Assert.False((await _h.PostAsync(o.Admin, path, new { companyId = o.Company }, Ct)).Status is HttpStatusCode.Created or HttpStatusCode.OK, path);
        }

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(s.User, $"/api/hrm/leave-requests/{id}", Ct)).Status); // reads stay generated (own scope)
    }
}
