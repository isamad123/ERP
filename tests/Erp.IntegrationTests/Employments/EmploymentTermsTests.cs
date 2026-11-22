#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Employments;

/// <summary>
/// Transfers between companies, rehires, contracts and probation (slice S1.20c, Build items 11 to 13) through the real API pipeline,
/// the real access and workflow engines, the real job worker and event dispatcher and a real PostgreSQL: FR-XM-30 (end plus linked
/// start, explicit carry-over), FR-XM-28 (one person), BR-01 (rehire), FR-HRM-EMP-04 (numbers never reused), FR-HRM-EMP-09
/// (contracts, renewal reminders), FR-HRM-EMP-10 (probation by setting until policies, extension, confirmation, reminders), FR-XM-32
/// (a held transfer; nobody approves their own) and the data scopes of both companies.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EmploymentTermsTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync()
    {
        _h.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private async Task<Guid> PersonAsync() => (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;

    /// <summary>A company of its own with a place to hire into; no employment event kind needs approval unless <paramref name="defaults"/>.</summary>
    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync(bool defaults = false)
    {
        var company = await _h.CompanyAsync(Ct);
        if (!defaults)
        {
            await ApprovalKindsAsync(company);
        }

        return (company, await _h.PlaceAsync(company, Ct));
    }

    private async Task ApprovalKindsAsync(Guid company, params string[] kinds)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = kinds, reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private async Task<(Guid Id, long Version)> HireAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, DateOnly joining, string type = "permanent", Guid? manager = null, Guid? person = null)
    {
        var answer = await _h.PostAsync(user, Employments, new
        {
            personId = person ?? await PersonAsync(),
            companyId = company,
            employeeType = type,
            joiningDate = Iso(joining),
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer", managerEmploymentId = manager },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Json.GetProperty("rowVersion").GetInt64());
    }

    private Task<long> VersionAsync(Guid employment) => _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment);

    private Task<string> StatusAsync(Guid employment) => _h.ScalarAsync<string>("select status from hrm.employments where id = $1", Ct, employment);

    private async Task<Answer> TransferAsync(Guid user, Guid employment, Guid toCompany, (Guid OrgUnit, Guid Branch) to, DateOnly date, object? carryOver = null, string path = "transfer") =>
        await _h.PostAsync(user, $"{Employments}/{employment}/{path}", new
        {
            toCompanyId = toCompany,
            effectiveDate = Iso(date),
            assignment = new { orgUnitId = to.OrgUnit, branchId = to.Branch, title = "Analyst" },
            carryOver,
            rowVersion = await VersionAsync(employment),
        }, Ct);

    private async Task<string?> TitleOnAsync(Guid user, Guid employment, DateOnly date)
    {
        var answer = await _h.GetAsync(user, $"{Employments}/{employment}/as-of?date={Iso(date)}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.GetProperty("assignment").ValueKind == JsonValueKind.Null ? null : answer.Json.GetProperty("assignment").GetProperty("title").GetString();
    }

    private Task<long> OutboxAsync(string type, Guid aggregate) =>
        _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = $1 and aggregate_id = $2", Ct, type, aggregate);

    /// <summary>The last 1 March before today (the acceptance example of the slice).</summary>
    private DateOnly FirstOfMarch()
    {
        var d = new DateOnly(Today.Year, 3, 1);
        return d >= Today ? d.AddYears(-1) : d;
    }

    // ------------------------------------------------------------------ transfers (FR-XM-30)

    [Fact]
    public async Task FR_XM_30_FR_XM_28_Acceptance_a_transfer_from_1_March_ends_the_first_employment_on_the_last_day_of_February_and_starts_a_linked_one()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (first, place) = await CompanyAsync();
        var (second, there) = await CompanyAsync();
        var march = FirstOfMarch();
        var (id, version) = await HireAsync(hr, first, place, march.AddDays(-300));
        var user = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{id}/user", new { userId = user, rowVersion = version }, Ct)).Status);
        var person = await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, id);

        // The check shows what would be recorded, without saving anything.
        var check = await TransferAsync(hr, id, second, there, march, new { leave = "encash" }, "transfer/check");
        Assert.True(check.Status == HttpStatusCode.OK, check.Body);
        Assert.Equal("effective", check.Text("status"));
        Assert.Equal(Iso(march.AddDays(-1)), check.Text("separationDate"));
        Assert.Equal("keep", check.Json.GetProperty("carryOver").GetProperty("service").GetString());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.employments where person_id = $1", Ct, person));

        var moved = await TransferAsync(hr, id, second, there, march, new { leave = "encash" });
        Assert.True(moved.Status == HttpStatusCode.Created, moved.Body);
        var next = moved.Json.GetProperty("id").GetGuid();

        // The first employment ends on the day before, its job details end with it; the linked one starts on 1 March.
        Assert.Equal("separated", await StatusAsync(id));
        Assert.Equal(march.AddDays(-1), await _h.ScalarAsync<DateOnly>("select separation_date from hrm.employments where id = $1", Ct, id));
        Assert.Equal(march, await _h.ScalarAsync<DateOnly>("select valid_to from hrm.employment_assignments where employment_id = $1 order by valid_from desc limit 1", Ct, id));
        Assert.Equal("Officer", await TitleOnAsync(hr, id, march.AddDays(-1)));
        Assert.Null(await TitleOnAsync(hr, id, march));
        Assert.Equal("Analyst", await TitleOnAsync(hr, next, march));

        // One person (FR-XM-28), a new employee number of the second company, the link, the service start kept, the user following.
        var row = await _h.ScalarAsync<string>("""
            select concat_ws('|', company_id, status, previous_employment_id, service_start_date, is_primary, user_ref, person_id, joining_date)
            from hrm.employments where id = $1
            """, Ct, next);
        Assert.Equal(string.Join('|', second, "active", id, Iso(march.AddDays(-300)), "t", user, person, Iso(march)), row);
        Assert.Equal(moved.Text("employeeNo"), await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, next));
        Assert.Matches(@"^EMP-\d{5}$", moved.Text("employeeNo")!);

        // The three carry-over choices recorded on both events (ruling Q9): as asked, the rest from the receiving company's settings.
        var recorded = await _h.ScalarAsync<string>("""
            select string_agg(kind || ':' || reason_code || ':' || status || ':' || (carry_over->>'service') || (carry_over->>'leave') || (carry_over->>'loans'), ',' order by kind)
            from hrm.employment_events where employment_id in ($1, $2) and kind in ('separation', 'hire') and created_at >= (select min(created_at) from hrm.employment_events where kind = 'separation' and employment_id = $1)
            """, Ct, id, next);
        Assert.Equal("hire:transfer_in:effective:keepencashcarry,separation:transfer:effective:keepencashcarry", recorded);

        // Events with ids only: the first employment separated (with the link), the second hired (with the link back).
        Assert.Equal(next.ToString(), await _h.ScalarAsync<string>("select payload->>'nextEmploymentId' from hrm.outbox_messages where event_type = 'hrm.employee_separated' and aggregate_id = $1", Ct, id));
        Assert.Equal(id.ToString(), await _h.ScalarAsync<string>("select payload->>'previousEmploymentId' from hrm.outbox_messages where event_type = 'hrm.employee_hired' and aggregate_id = $1", Ct, next));

        // Each company's data scope sees only its own employment.
        foreach (var (company, own, other) in new[] { (first, id, next), (second, next, id) })
        {
            var reader = await _h.UserAsync(Ct);
            var role = await _h.RoleAsync(Ct, ["hrm.employments.read"], [("hrm.employments", "all")]);
            await _h.AssignAsync(reader, role, company, _h.Today.AddDays(-1), null, Ct);
            var list = await _h.GetAsync(reader, $"/api/hrm/employments?personId={person}", Ct);
            Assert.Equal([own], list.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(reader, $"{Employments}/{other}/as-of", Ct)).Status);
        }

        // Nothing changes on the ended employment any more.
        var after = await _h.PostAsync(hr, $"{Employments}/{id}/events", new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(march.AddDays(-10)), assignment = new { title = "x" }, rowVersion = await VersionAsync(id) }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, after.Status);
    }

    [Fact]
    public async Task FR_XM_30_Q9_a_restarted_service_counts_from_the_transfer_date_and_the_receiving_companys_setting_gives_the_default_choice()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var (first, place) = await CompanyAsync();
        var (second, there) = await CompanyAsync();
        var set = await _h.PutAsync(admin, "/api/cfg/settings/hrm.transfer.carry_over.loans", new { layer = "company", scopeId = second, value = "settle", reason = "group policy" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        var (id, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        var moved = await TransferAsync(hr, id, second, there, Today, new { service = "restart" });
        Assert.True(moved.Status == HttpStatusCode.Created, moved.Body);
        var next = moved.Json.GetProperty("id").GetGuid();
        Assert.Equal(Today, await _h.ScalarAsync<DateOnly>("select service_start_date from hrm.employments where id = $1", Ct, next));
        Assert.Equal("restart|carry|settle", await _h.ScalarAsync<string>("select concat_ws('|', carry_over->>'service', carry_over->>'leave', carry_over->>'loans') from hrm.employment_events where employment_id = $1", Ct, next));

        // A wrong choice is refused by field.
        var (other, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        var wrong = await TransferAsync(hr, other, second, there, Today, new { leave = "keep" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Equal("errors.hrm.field.unknown_code", wrong.Json.GetProperty("errors").GetProperty("carryOver.leave")[0].GetString());
    }

    [Fact]
    public async Task FR_XM_30_Q12_a_future_transfer_keeps_the_first_employment_current_until_the_daily_job_ends_it_and_makes_the_linked_one_the_main_one()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (first, place) = await CompanyAsync();
        var (second, there) = await CompanyAsync();
        var (id, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        var date = Today.AddDays(5);
        var moved = await TransferAsync(hr, id, second, there, date);
        Assert.True(moved.Status == HttpStatusCode.Created, moved.Body);
        var next = moved.Json.GetProperty("id").GetGuid();
        Assert.Equal("active|true", await _h.ScalarAsync<string>("select status || '|' || is_primary::text from hrm.employments where id = $1", Ct, id));
        Assert.Equal("pre_joining|false", await _h.ScalarAsync<string>("select status || '|' || is_primary::text from hrm.employments where id = $1", Ct, next));
        Assert.Equal("approved", moved.Text("separationStatus"));
        Assert.Equal("Officer", await TitleOnAsync(hr, id, date.AddDays(-1)));
        Assert.Null(await TitleOnAsync(hr, id, date));
        Assert.Equal(0L, await OutboxAsync("hrm.employee_separated", id));

        // A change dated from the transfer on is refused; one before it is still possible.
        var late = await _h.PostAsync(hr, $"{Employments}/{id}/events", new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(date.AddDays(1)), assignment = new { title = "x" }, rowVersion = await VersionAsync(id) }, Ct);
        Assert.Equal("errors.hrm.employment.after_end", late.Json.GetProperty("errors").GetProperty("effectiveDate")[0].GetString());

        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        _h.Clock.Advance(TimeSpan.FromDays(6));
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.employment_events_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("separated|true", await _h.ScalarAsync<string>("select status || '|' || is_primary::text from hrm.employments where id = $1", Ct, id));
        Assert.Equal("active|true", await _h.ScalarAsync<string>("select status || '|' || is_primary::text from hrm.employments where id = $1", Ct, next));
        Assert.Equal(date.AddDays(-1), await _h.ScalarAsync<DateOnly>("select separation_date from hrm.employments where id = $1", Ct, id));
        await rig.QueueAsync("hrm.employment_events_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal(1L, await OutboxAsync("hrm.employee_separated", id)); // once
    }

    [Fact]
    public async Task FR_XM_30_FR_XM_32_a_transfer_waits_for_approval_when_listed_the_requester_cannot_approve_and_a_rejection_cancels_both_sides()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (first, place) = await CompanyAsync(defaults: true); // transfer is in the default list
        var (second, there) = await CompanyAsync();
        var (id, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        var asked = await TransferAsync(hr, id, second, there, Today);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var next = asked.Json.GetProperty("id").GetGuid();
        Assert.Equal("active", await StatusAsync(id));
        Assert.Equal("pre_joining|false", await _h.ScalarAsync<string>("select status || '|' || is_primary::text from hrm.employments where id = $1", Ct, next));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employment_assignments where employment_id = $1", Ct, next));

        var task = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, asked.Json.GetProperty("instanceId").GetGuid());
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(hr, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        var approved = await _h.PostAsync(checker, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("separated", await StatusAsync(id));
        Assert.Equal("active|true", await _h.ScalarAsync<string>("select status || '|' || is_primary::text from hrm.employments where id = $1", Ct, next));
        Assert.Equal("Analyst", await TitleOnAsync(hr, next, Today));
        Assert.Equal(1L, await OutboxAsync("hrm.employee_hired", next));

        // A rejected transfer cancels the separation, the linked start and the linked employment; its number stays used.
        var (other, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        var again = await TransferAsync(hr, other, second, there, Today);
        var rejectedNext = again.Json.GetProperty("id").GetGuid();
        var task2 = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, again.Json.GetProperty("instanceId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/wf/tasks/{task2}/reject", new { comment = "no" }, Ct)).Status);
        Assert.Equal("active", await StatusAsync(other));
        Assert.Equal("cancelled", await StatusAsync(rejectedNext));
        Assert.Equal("cancelled,cancelled", await _h.ScalarAsync<string>("select string_agg(status, ',') from hrm.employment_events where employment_id in ($1, $2) and kind in ('separation', 'hire') and reason_code in ('transfer', 'transfer_in')", Ct, other, rejectedNext));
        Assert.Equal("Officer", await TitleOnAsync(hr, other, Today));
    }

    [Fact]
    public async Task FR_XM_30_BR_02_a_transfer_is_refused_for_the_same_company_before_joining_with_pending_or_later_changes_and_without_permission_or_scope()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (first, place) = await CompanyAsync();
        var (second, there) = await CompanyAsync();
        var (id, _) = await HireAsync(hr, first, place, Today.AddDays(-200));

        var same = await TransferAsync(hr, id, first, place, Today);
        Assert.Equal("errors.hrm.transfer.same_company", same.Json.GetProperty("errors").GetProperty("toCompanyId")[0].GetString());
        var early = await TransferAsync(hr, id, second, there, Today.AddDays(-200));
        Assert.Equal("errors.hrm.transfer.before_joining", early.Json.GetProperty("errors").GetProperty("effectiveDate")[0].GetString());

        // An approved change dated on or after the transfer date would apply to an ended employment.
        var acting = await _h.PostAsync(hr, $"{Employments}/{id}/events", new { kind = "acting", reasonCode = "acting_start", effectiveDate = Iso(Today.AddDays(20)), assignment = new { title = "Acting" }, rowVersion = await VersionAsync(id) }, Ct);
        Assert.Equal(HttpStatusCode.Created, acting.Status);
        var later = await TransferAsync(hr, id, second, there, Today.AddDays(10));
        Assert.Equal(HttpStatusCode.Conflict, later.Status);
        Assert.Equal("errors.hrm.transfer.later_changes", later.Text("title"));

        // Without the permission; with the permission but no scope in the receiving company.
        var clerk = await _h.UserAsync(Ct);
        var readOnly = await _h.RoleAsync(Ct, ["hrm.employments.read"], [("hrm.employments", "all")]);
        await _h.AssignAsync(clerk, readOnly, null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await TransferAsync(clerk, id, second, there, Today)).Status);
        var limited = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employments.transfer", "hrm.employment_assignments.read"], [("hrm.employments", "all"), ("hrm.employment_assignments", "all")]);
        await _h.AssignAsync(limited, role, first, _h.Today.AddDays(-1), null, Ct);
        var (third, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        Assert.Equal(HttpStatusCode.Forbidden, (await TransferAsync(limited, third, second, there, Today)).Status);
        Assert.Equal("active", await StatusAsync(third));
    }

    [Fact]
    public async Task FR_XM_30_a_module_naming_open_items_the_carry_over_does_not_carry_refuses_the_transfer()
    {
        var module = new OpenLoans();
        await using var app = await fx.StartAppAsync(null, Ct, services => services.AddSingleton<Erp.Hrm.Contracts.Employments.IEmploymentChangeImpact>(module), realAccess: true);
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (first, place) = await CompanyAsync();
        var (second, there) = await CompanyAsync();
        var (id, _) = await HireAsync(hr, first, place, Today.AddDays(-200));
        using var client = TenancyFixture.Client(app, TenancyFixture.HostOf(_h.Code), _h.Token(hr));
        async Task<HttpResponseMessage> Send(string loans) => await client.PostAsJsonAsync($"{Employments}/{id}/transfer", new
        {
            toCompanyId = second, effectiveDate = Iso(Today.AddDays(3)), assignment = new { orgUnitId = there.OrgUnit, branchId = there.Branch, title = "Analyst" },
            carryOver = new { loans }, rowVersion = await VersionAsync(id),
        }, JsonSerializerOptions.Web, Ct);

        using var refused = await Send("settle");
        var text = await refused.Content.ReadAsStringAsync(Ct);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict, text);
        Assert.Contains("errors.hrm.transfer.open_items", text, StringComparison.Ordinal);
        Assert.Equal("settle", module.Seen!.CarryOver!.Loans);
        Assert.True(module.Seen.IsTermination);
        Assert.Equal("active", await StatusAsync(id));

        using var carried = await Send("carry");
        Assert.Equal(HttpStatusCode.Created, carried.StatusCode);
    }

    private sealed class OpenLoans : Erp.Hrm.Contracts.Employments.IEmploymentChangeImpact
    {
        public Erp.Hrm.Contracts.Employments.EmploymentChange? Seen { get; private set; }

        public string ModuleCode => "test.loans";

        public Task<Erp.Hrm.Contracts.Employments.EmploymentImpactAnswer> AnalyseAsync(Erp.Hrm.Contracts.Employments.EmploymentChange change, CancellationToken cancellationToken)
        {
            Seen = change;
            IReadOnlyList<string> open = change.CarryOver?.Loans == "settle" ? ["test.loans.open_loan_not_settled"] : [];
            return Task.FromResult(new Erp.Hrm.Contracts.Employments.EmploymentImpactAnswer(ModuleCode, [], null, [], open));
        }
    }

    // ------------------------------------------------------------------ rehire (BR-01)

    [Fact]
    public async Task BR_01_FR_HRM_EMP_04_a_rehire_links_to_the_earlier_person_and_employment_with_a_new_number_and_leaves_the_former_one_untouched()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (former, _) = await HireAsync(hr, company, place, Today.AddDays(-700));
        var formerNo = await _h.ScalarAsync<string>("select employee_no from hrm.employments where id = $1", Ct, former);

        // A rehire starts only from an ended employment.
        var current = await _h.PostAsync(hr, $"{Employments}/{former}/rehire", Rehire(place, Today), Ct);
        Assert.Equal(HttpStatusCode.Conflict, current.Status);
        Assert.Equal("errors.hrm.rehire.not_separated", current.Text("title"));

        // The separation itself is S1.25's: written directly here, as it will be recorded there.
        await _h.ExecAsync("update hrm.employments set status = 'separated', separation_date = $2 where id = $1", Ct, former, Today.AddDays(-300));
        var formerVersion = await VersionAsync(former);
        var early = await _h.PostAsync(hr, $"{Employments}/{former}/rehire", Rehire(place, Today.AddDays(-300)), Ct);
        Assert.Equal("errors.hrm.rehire.before_separation", early.Json.GetProperty("errors").GetProperty("joiningDate")[0].GetString());

        var rehired = await _h.PostAsync(hr, $"{Employments}/{former}/rehire", Rehire(place, Today.AddDays(-10)), Ct);
        Assert.True(rehired.Status == HttpStatusCode.Created, rehired.Body);
        var id = rehired.Json.GetProperty("id").GetGuid();
        Assert.NotEqual(formerNo, rehired.Text("employeeNo"));
        Assert.Equal($"{former}|active|rehire:rejoining", await _h.ScalarAsync<string>("""
            select e.previous_employment_id || '|' || e.status || '|' || v.kind || ':' || v.reason_code
            from hrm.employments e join hrm.employment_events v on v.employment_id = e.id where e.id = $1
            """, Ct, id));
        Assert.Equal(await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, former), rehired.Json.GetProperty("personId").GetGuid());
        Assert.Equal(formerVersion, await VersionAsync(former)); // untouched
        Assert.Equal("separated", await StatusAsync(former));
        Assert.Equal(former.ToString(), await _h.ScalarAsync<string>("select payload->>'previousEmploymentId' from hrm.outbox_messages where event_type = 'hrm.employee_hired' and aggregate_id = $1", Ct, id));

        // The former number is never reused: typed again, it is refused.
        var again = await _h.PostAsync(hr, Employments, new { personId = await PersonAsync(), companyId = company, employeeType = "permanent", joiningDate = Iso(Today), employeeNo = formerNo, assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "x" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);

        // A second rehire while the first is current is refused (one current main employment).
        await _h.ExecAsync("update hrm.employments set status = 'separated', separation_date = $2 where id = $1", Ct, former, Today.AddDays(-300));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(hr, $"{Employments}/{former}/rehire", Rehire(place, Today), Ct)).Status);
    }

    private static object Rehire((Guid OrgUnit, Guid Branch) place, DateOnly joining) => new
    {
        joiningDate = Iso(joining), employeeType = "contract", assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Rehired officer" },
    };

    // ------------------------------------------------------------------ contracts (FR-HRM-EMP-09)

    [Fact]
    public async Task FR_HRM_EMP_09_a_contract_is_numbered_renewed_as_a_new_linked_contract_expires_and_reminds_the_manager_and_HR_once()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var managerUser = await _h.UserAsync(Ct);
        var (manager, managerVersion) = await HireAsync(hr, company, place, Today.AddDays(-900));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{manager}/user", new { userId = managerUser, rowVersion = managerVersion }, Ct)).Status);
        var hrInCompany = await _h.UserAsync(Ct);
        await _h.AssignAsync(hrInCompany, await _h.ScalarAsync<Guid>("select id from iam.roles where code = 'hr_admin'", Ct), company, _h.Today.AddDays(-1), null, Ct);
        var (id, _) = await HireAsync(hr, company, place, Today.AddDays(-400), "contract", manager);

        var created = await _h.PostAsync(hr, $"{Employments}/{id}/contracts", new { startDate = Iso(Today.AddDays(-400)), endDate = Iso(Today.AddDays(10)), renewalNoticeDays = 20, terms = new { hours = "40 a week" } }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        Assert.Matches(@"^CON-\d{4}-\d{5}$", created.Text("contractNo")!);
        var contract = created.Json.GetProperty("id").GetGuid();

        // Overlaps, a start before joining and a wrong order are refused; the generated writes are closed.
        Assert.Equal("errors.hrm.contract.overlaps", (await _h.PostAsync(hr, $"{Employments}/{id}/contracts", new { startDate = Iso(Today), terms = new { } }, Ct)).Json.GetProperty("errors").GetProperty("startDate")[0].GetString());
        Assert.Equal("errors.hrm.contract.before_joining", (await _h.PostAsync(hr, $"{Employments}/{id}/contracts", new { startDate = Iso(Today.AddDays(-500)) }, Ct)).Json.GetProperty("errors").GetProperty("startDate")[0].GetString());
        Assert.Contains((await _h.PostAsync(hr, "/api/hrm/contracts", new { employmentId = id }, Ct)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains((await _h.PostAsync(hr, "/api/hrm/probation-records", new { employmentId = id }, Ct)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        // Within the notice days the daily job reminds the line manager and the company's HR administrators, once per end date.
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.employment_terms_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        await rig.QueueAsync("hrm.employment_terms_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal(1L, await OutboxAsync("hrm.contract_ending", contract));
        Assert.Equal(managerUser.ToString(), await _h.ScalarAsync<string>("select payload->>'manager_user_id' from hrm.outbox_messages where event_type = 'hrm.contract_ending' and aggregate_id = $1", Ct, contract));
        var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
        foreach (var outcome in await dispatcher.DispatchOnceAsync(_h.Code, Ct))
        {
            Assert.True(outcome.Error is null, outcome.Error);
        }

        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1 and event_code = 'hrm.contract_ending'", Ct, managerUser));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1 and event_code = 'hrm.contract_ending'", Ct, hrInCompany));

        // A renewal is a new linked contract from the day after the end; the renewed one is never edited, only marked renewed.
        var renewed = await _h.PostAsync(hr, $"/api/hrm/contracts/{contract}/renew", new { endDate = Iso(Today.AddDays(375)), rowVersion = created.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(renewed.Status == HttpStatusCode.Created, renewed.Body);
        Assert.Equal($"renewed|{Iso(Today.AddDays(11))}|{contract}|40 a week", await _h.ScalarAsync<string>("""
            select o.status || '|' || n.start_date || '|' || n.renews_contract_id || '|' || (n.terms->>'hours')
            from hrm.contracts n join hrm.contracts o on o.id = n.renews_contract_id where n.id = $1
            """, Ct, renewed.Json.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(hr, $"/api/hrm/contracts/{contract}/renew", new { rowVersion = 2 }, Ct)).Status);

        // After its end an active contract expires.
        var (other, _) = await HireAsync(hr, company, place, Today.AddDays(-400), "contract");
        var ended = await _h.PostAsync(hr, $"{Employments}/{other}/contracts", new { startDate = Iso(Today.AddDays(-400)), endDate = Iso(Today.AddDays(-1)) }, Ct);
        await rig.QueueAsync("hrm.employment_terms_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("expired", await _h.ScalarAsync<string>("select status from hrm.contracts where id = $1", Ct, ended.Json.GetProperty("id").GetGuid()));
    }

    // ------------------------------------------------------------------ probation (FR-HRM-EMP-10, ruling Q10)

    [Fact]
    public async Task FR_HRM_EMP_10_Q10_a_probation_opens_at_hire_for_the_listed_types_is_extended_confirmed_by_an_event_and_reminded_before_its_end()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var joining = Today.AddDays(-100);
        var (id, _) = await HireAsync(hr, company, place, joining);
        Assert.Equal($"in_progress|{Iso(joining)}|{Iso(joining.AddMonths(3).AddDays(-1))}", await _h.ScalarAsync<string>("select status || '|' || start_date || '|' || end_date from hrm.probation_records where employment_id = $1", Ct, id));
        var (contractor, _) = await HireAsync(hr, company, place, joining, "consultant");
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.probation_records where employment_id = $1", Ct, contractor));

        Task<long> ProbationVersion() => _h.ScalarAsync<long>("select row_version from hrm.probation_records where employment_id = $1 order by created_at desc limit 1", Ct, id);

        // Extension moves the last day later only.
        var notLater = await _h.PostAsync(hr, $"{Employments}/{id}/probation/extend", new { extendedTo = Iso(joining.AddMonths(2)), rowVersion = await ProbationVersion() }, Ct);
        Assert.Equal("errors.hrm.probation.extend_not_later", notLater.Json.GetProperty("errors").GetProperty("extendedTo")[0].GetString());
        var extended = await _h.PostAsync(hr, $"{Employments}/{id}/probation/extend", new { extendedTo = Iso(Today.AddDays(5)), rowVersion = await ProbationVersion() }, Ct);
        Assert.True(extended.Status == HttpStatusCode.OK, extended.Body);
        Assert.Equal("extended", extended.Text("status"));

        // A reminder within the reminder days of the extended end.
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.employment_terms_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        var record = await _h.ScalarAsync<Guid>("select id from hrm.probation_records where employment_id = $1", Ct, id);
        Assert.Equal(Iso(Today.AddDays(5)), await _h.ScalarAsync<string>("select payload->>'end_date' from hrm.outbox_messages where event_type = 'hrm.probation_ending' and aggregate_id = $1", Ct, record));

        // Confirmation is a confirmation event; the probation closes with it.
        var confirmed = await _h.PostAsync(hr, $"{Employments}/{id}/probation/confirm", new { rowVersion = await VersionAsync(id) }, Ct);
        Assert.True(confirmed.Status == HttpStatusCode.Created, confirmed.Body);
        Assert.Equal($"confirmed|{Iso(Today)}", await _h.ScalarAsync<string>("select status || '|' || decision_date from hrm.probation_records where id = $1", Ct, record));
        Assert.Equal("confirmation:early_confirmation", await _h.ScalarAsync<string>("select kind || ':' || reason_code from hrm.employment_events where employment_id = $1 and kind = 'confirmation'", Ct, id));
        Assert.Equal(Today, await _h.ScalarAsync<DateOnly>("select confirmation_date from hrm.employments where id = $1", Ct, id));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(hr, $"{Employments}/{id}/probation/extend", new { extendedTo = Iso(Today.AddDays(50)), rowVersion = 1 }, Ct)).Status);

        // Not confirmed: recorded on the probation (the separation follows through S1.25); without the permission it is refused.
        var (other, _) = await HireAsync(hr, company, place, joining);
        var clerk = await _h.UserAsync(Ct);
        var readOnly = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.probation_records.read"], [("hrm.employments", "all"), ("hrm.probation_records", "all")]);
        await _h.AssignAsync(clerk, readOnly, null, _h.Today.AddDays(-1), null, Ct);
        var otherVersion = await _h.ScalarAsync<long>("select row_version from hrm.probation_records where employment_id = $1", Ct, other);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(clerk, $"{Employments}/{other}/probation/not-confirm", new { rowVersion = otherVersion }, Ct)).Status);
        var notConfirmed = await _h.PostAsync(hr, $"{Employments}/{other}/probation/not-confirm", new { rowVersion = otherVersion }, Ct);
        Assert.True(notConfirmed.Status == HttpStatusCode.OK, notConfirmed.Body);
        Assert.Equal("not_confirmed", await _h.ScalarAsync<string>("select status from hrm.probation_records where employment_id = $1", Ct, other));
        Assert.Equal("active", await StatusAsync(other));
    }
}
