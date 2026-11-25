using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.Hrm.Application.Payroll;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.PayRuns;

/// <summary>
/// The payroll engine of slice S1.35a through the real API pipeline, the real access engine and a real PostgreSQL: FR-XM-26 (the calculation locks the
/// period's attendance and leave first and refuses with open items unless accepted), FR-HRM-PAY-03 and -05 (methods and pro-rata of a full month and a
/// joiner, expected values computed here), FR-HRM-PAY-07 (one-time inputs consumed by the run), FR-HRM-PAY-31 (snapshot stored compressed with its hash;
/// the stored snapshot calculates to the stored result), FR-HRM-PAY-33 and FR-XM-05/-06 (locked results refused by the database; a correction is a new
/// result in a later run), ruling Q2 (a statutory component without its calculator: the employee is incomplete and the run does not finish) and GHR-06
/// (amounts only with the salary field class). The company's date is the harness clock's in Asia/Karachi.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PayEngineTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Runs = "/api/hrm/pay-runs";
    private const string Components = "/api/hrm/pay-components";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly string[] SetUp =
    [
        "hrm.pay_components.read", "hrm.pay_components.create", "hrm.pay_components.update", "hrm.pay_component_versions.read",
        "hrm.salary_structures.read", "hrm.salary_structures.create", "hrm.salary_structures.update", "hrm.salary_structure_components.read", "hrm.salary_structure_components.update",
        "hrm.compensation_assignments.read", "hrm.compensation_assignments.create", "hrm.compensation_lines.read", "hrm.compensation_lines.create", "hrm.pay.compensation.read",
        "hrm.pay_groups.read", "hrm.pay_groups.create", "hrm.pay_periods.read", "hrm.pay_periods.generate", "hrm.employments.read", "hrm.employment_assignments.read",
    ];

    private static readonly string[] Engine =
    [
        "hrm.pay.run", "hrm.pay.results.read", "hrm.pay_inputs.create", "hrm.pay_inputs.cancel", "hrm.pay_runs.read", "hrm.pay_run_employees.read",
        "hrm.pay_results.read", "hrm.pay_result_lines.read", "hrm.pay_inputs.read",
    ];

    /// <summary>
    /// A company with approvals of pay and employment changes off; a payroll manager (set-up, engine, salary fields editable), an HR administrator, a
    /// monthly pay group with the periods around last month, and the period of last month with the next one.
    /// </summary>
    private sealed record Rig(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Manager, Guid Hr, Guid Group, Period Period, Period Next);

    private sealed record Period(Guid Id, DateOnly Start, DateOnly End);

    private async Task<Rig> RigAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var a = await _h.UserAsync(Ct, null, "tenant_admin");
        var b = await _h.UserAsync(Ct, null, "tenant_admin");
        await SettingAsync(a, b, company, "hrm.employment_events.approval_kinds", Array.Empty<string>());
        await SettingAsync(a, b, company, "hrm.pay.change_approval", false);
        var manager = await _h.UserAsync(Ct);
        await _h.AssignAsync(manager, await _h.RoleAsync(Ct, [.. SetUp, .. Engine], [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var group = await _h.PostAsync(manager, "/api/hrm/pay-groups", new { companyId = company, code = Code("PG"), nameI18n = new { en = "Engine group", ur = "انجن گروپ" }, frequency = "monthly", taxYearStartMonth = 7 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var groupId = group.Json.GetProperty("id").GetGuid();
        var target = Today.AddMonths(-1);
        var taxYear = target.Month >= 7 ? target.Year : target.Year - 1;
        foreach (var year in new[] { taxYear, taxYear + 1 })
        {
            var generated = await _h.PostAsync(manager, $"/api/hrm/pay-groups/{groupId}/periods/generate", new { taxYear = year }, Ct);
            Assert.True(generated.Status == HttpStatusCode.Created, generated.Body);
        }

        var period = await PeriodAsync(groupId, target);
        var next = await PeriodAsync(groupId, period.End.AddDays(1));
        return new Rig(company, await _h.PlaceAsync(company, Ct), manager, hr, groupId, period, next);
    }

    private async Task<Period> PeriodAsync(Guid group, DateOnly day)
    {
        var id = await _h.ScalarAsync<Guid>("select id from hrm.pay_periods where pay_group_id = $1 and start_date <= $2 and end_date >= $2", Ct, group, day);
        return new Period(id, await _h.ScalarAsync<DateOnly>("select start_date from hrm.pay_periods where id = $1", Ct, id), await _h.ScalarAsync<DateOnly>("select end_date from hrm.pay_periods where id = $1", Ct, id));
    }

    private async Task SettingAsync(Guid admin, Guid second, Guid company, string key, object value)
    {
        var held = await _h.PutAsync(admin, "/api/cfg/settings/" + key, new { layer = "company", scopeId = company, value, reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
    }

    private async Task<(Guid Id, string Code)> ComponentAsync(Rig rig, string kind, int order, object version, bool oneTime = false)
    {
        var code = Code("C");
        var answer = await _h.PostAsync(rig.Manager, Components, new { companyId = rig.Company, code, nameI18n = new { en = "Component " + code, ur = "جزو " + code }, kind, payslipOrder = order, isRecurring = !oneTime, isOneTimeAllowed = oneTime, version }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), code);
    }

    private static object Version(DateOnly from, string method, string rule = "none", Guid[]? bases = null, string? formula = null, object? flags = null) =>
        new { validFrom = Iso(from), method, proRataRule = rule, baseComponentIds = bases, formula, statutoryFlags = flags ?? new { taxable = true } };

    private async Task<Guid> StructureAsync(Rig rig, DateOnly from, params (Guid Component, int Order, decimal? Amount, decimal? Percent)[] items)
    {
        var created = await _h.PostAsync(rig.Manager, "/api/hrm/salary-structures", new { companyId = rig.Company, code = Code("S"), nameI18n = new { en = "Engine structure", ur = "انجن ڈھانچہ" } }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        var set = await _h.PutAsync(rig.Manager, $"/api/hrm/salary-structures/{id}", new
        {
            rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.salary_structures where id = $1", Ct, id),
            components = new { validFrom = Iso(from), items = items.Select(i => new { componentId = i.Component, evaluationOrder = i.Order, defaultAmount = i.Amount, defaultPercent = i.Percent, isMandatory = false }).ToArray() },
        }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return id;
    }

    private async Task<Guid> HireAsync(Rig rig, DateOnly joining)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(rig.Hr, "/api/hrm/employments", new
        {
            personId = person,
            companyId = rig.Company,
            employeeType = "permanent",
            joiningDate = Iso(joining),
            assignment = new { orgUnitId = rig.Place.OrgUnit, branchId = rig.Place.Branch, title = "Officer", payGroupId = rig.Group },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private async Task PayAsync(Rig rig, Guid employment, Guid structure, DateOnly from, Guid basic, decimal amount)
    {
        var done = await _h.PostAsync(rig.Manager, $"/api/hrm/employments/{employment}/compensation", new { structureId = structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = basic, amount } } }, Ct);
        Assert.True(done.Status == HttpStatusCode.Created, done.Body);
    }

    private async Task<Guid> RunAsync(Rig rig, Period period)
    {
        var created = await _h.PostAsync(rig.Manager, Runs, new { payGroupId = rig.Group, payPeriodId = period.Id }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        Assert.Equal("created", created.Json.GetProperty("status").GetString());
        return created.Json.GetProperty("id").GetGuid();
    }

    private static decimal Q3(decimal value, int places = 2) => Math.Round(Math.Round(value, 4, MidpointRounding.AwayFromZero), places, MidpointRounding.AwayFromZero);

    private static decimal LineOf(JsonElement result, string code) =>
        result.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("componentCode").GetString() == code).GetProperty("amount").GetDecimal();

    // ------------------------------------------------------------------ the whole path

    [Fact]
    public async Task FR_XM_26_FR_HRM_PAY_03_05_07_31_a_run_locks_first_then_calculates_a_full_month_and_a_joiner_from_their_snapshots()
    {
        var rig = await RigAsync();
        var from = rig.Period.Start.AddDays(-400);
        var basic = await ComponentAsync(rig, "earning", 1, Version(from, "fixed", "calendar_days"));
        var house = await ComponentAsync(rig, "earning", 2, Version(from, "percent_of", "calendar_days", [basic.Id]));
        var conv = await ComponentAsync(rig, "earning", 3, Version(from, "formula", formula: $"round({basic.Code} * 0.1, 0)"));
        var canteen = await ComponentAsync(rig, "deduction", 4, Version(from, "fixed"));
        var bonus = await ComponentAsync(rig, "earning", 5, Version(from, "manual"), oneTime: true);
        var structure = await StructureAsync(rig, rig.Period.Start.AddDays(-60), (basic.Id, 1, null, null), (house.Id, 2, null, 45m), (conv.Id, 3, null, null), (canteen.Id, 4, 500m, null), (bonus.Id, 5, null, null));

        var full = await HireAsync(rig, Today.AddDays(-400));
        await PayAsync(rig, full, structure, rig.Period.Start.AddDays(-40), basic.Id, 60_000m);
        var joining = rig.Period.Start.AddDays(10);
        var joiner = await HireAsync(rig, joining);
        await PayAsync(rig, joiner, structure, joining, basic.Id, 31_000m);
        var run = await RunAsync(rig, rig.Period);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Manager, Runs, new { payGroupId = rig.Group, payPeriodId = rig.Period.Id }, Ct)).Status); // one regular run per period

        // A one-time bonus of the full-month employee (FR-HRM-PAY-07).
        var input = await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", new { items = new[] { new { employmentId = full, payPeriodId = rig.Period.Id, componentId = bonus.Id, amount = 5_000m, sourceType = "bonus" } } }, Ct);
        Assert.True(input.Status == HttpStatusCode.Created, input.Body);
        var inputId = input.Json.GetProperty("items")[0].GetProperty("id").GetGuid();

        // The lock waits for the period's cut-off date, as HR's lock does (review S1.35a finding 2).
        var cutOff = await _h.ScalarAsync<DateOnly>("select cut_off_date from hrm.pay_periods where id = $1", Ct, rig.Period.Id);
        await _h.ExecAsync("update hrm.pay_periods set cut_off_date = $1 where id = $2", Ct, Today, rig.Period.Id);
        var early = await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, early.Status);
        Assert.Equal("errors.hrm.pay.run.before_cut_off", early.Json.GetProperty("title").GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.attendance_periods where pay_period_id = $1 and status = 'locked'", Ct, rig.Period.Id));
        await _h.ExecAsync("update hrm.pay_periods set cut_off_date = $1 where id = $2", Ct, cutOff, rig.Period.Id);

        // FR-XM-26: no attendance is processed, so the lock refuses without the flag and nothing is calculated.
        var refused = await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.hrm.attendance.lock.open_items", refused.Json.GetProperty("title").GetString());
        Assert.True(refused.Json.GetProperty("lock").GetProperty("unprocessedDays").GetInt32() > 0, refused.Body);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_run_employees where pay_run_id = $1", Ct, run));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.attendance_periods where pay_period_id = $1 and status = 'locked'", Ct, rig.Period.Id));

        var calculated = await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct);
        Assert.True(calculated.Status == HttpStatusCode.OK, calculated.Body);
        Assert.Equal("calculated", calculated.Json.GetProperty("status").GetString());
        Assert.Equal(2, calculated.Json.GetProperty("calculated").GetInt32());
        Assert.Empty(calculated.Json.GetProperty("incomplete").EnumerateArray());

        // The lock came first and names the run.
        Assert.Equal("payroll", await _h.ScalarAsync<string>("select lock_kind from hrm.attendance_periods where pay_period_id = $1 and status = 'locked'", Ct, rig.Period.Id));
        Assert.Equal(run, await _h.ScalarAsync<Guid>("select locked_by_run_id from hrm.attendance_periods where pay_period_id = $1", Ct, rig.Period.Id));

        // Expected amounts, worked out here (Q3: four places inside, the currency's two on the line).
        var days = rig.Period.End.DayNumber - rig.Period.Start.DayNumber + 1;
        var employed = rig.Period.End.DayNumber - joining.DayNumber + 1;
        var results = await _h.GetAsync(rig.Manager, $"{Runs}/{run}/results", Ct);
        Assert.True(results.Status == HttpStatusCode.OK, results.Body);
        var fullResult = results.Json.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("employmentId").GetGuid() == full);
        Assert.Equal(60_000m, LineOf(fullResult, basic.Code));
        Assert.Equal(60_000m * 45m / 100m, LineOf(fullResult, house.Code));
        Assert.Equal(6_000m, LineOf(fullResult, conv.Code));
        Assert.Equal(500m, LineOf(fullResult, canteen.Code));
        Assert.Equal(5_000m, LineOf(fullResult, bonus.Code));
        Assert.Equal(60_000m + 27_000m + 6_000m + 5_000m, fullResult.GetProperty("gross").GetDecimal());
        Assert.Equal(98_000m - 500m, fullResult.GetProperty("net").GetDecimal());
        Assert.Equal(days, fullResult.GetProperty("paidDays").GetDecimal());

        var joinerResult = results.Json.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("employmentId").GetGuid() == joiner);
        var joinerBasic = Q3(31_000m * employed / days);
        var joinerHouse = Q3(31_000m * 45m / 100m * employed / days);
        Assert.Equal(joinerBasic, LineOf(joinerResult, basic.Code));
        Assert.Equal(joinerHouse, LineOf(joinerResult, house.Code));
        Assert.Equal(3_100m, LineOf(joinerResult, conv.Code)); // pro-rata "none": the full value
        Assert.Equal(joinerBasic + joinerHouse + 3_100m, joinerResult.GetProperty("gross").GetDecimal());
        Assert.Equal(employed, joinerResult.GetProperty("paidDays").GetDecimal());

        // The run's totals, count and stage log; the event carries no amount (FR-HRM-PAY-34, ruling Q8).
        var summary = await _h.GetAsync(rig.Manager, $"{Runs}/{run}/summary", Ct);
        Assert.True(summary.Status == HttpStatusCode.OK, summary.Body);
        Assert.Equal(2, summary.Json.GetProperty("employeeCount").GetInt32());
        Assert.Equal(98_000m + joinerBasic + joinerHouse + 3_100m, summary.Json.GetProperty("totalGross").GetDecimal());
        Assert.Contains(summary.Json.GetProperty("stageLog").EnumerateArray(), s => s.GetProperty("stage").GetString() == "calculated" && s.GetProperty("engineVersion").GetString() == PayEngine.Version);
        var payload = await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.pay_run.calculated' and aggregate_id = $1", Ct, run);
        Assert.Contains(run.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain("98000", payload, StringComparison.Ordinal);

        // The input is consumed by the run (Build item 1) and can no longer be cancelled.
        Assert.Equal("consumed", await _h.ScalarAsync<string>("select status from hrm.pay_inputs where id = $1", Ct, inputId));
        Assert.Equal(run, await _h.ScalarAsync<Guid>("select consumed_by_run_id from hrm.pay_inputs where id = $1", Ct, inputId));
        Assert.Equal("errors.hrm.pay.input.consumed", (await _h.PostAsync(rig.Manager, $"/api/hrm/pay-inputs/{inputId}/cancel", new { }, Ct)).Json.GetProperty("title").GetString());

        // FR-HRM-PAY-31: the snapshot is stored compressed with the SHA-256 of its canonical JSON, and calculates again to the stored lines.
        var stored = await _h.ScalarAsync<string>("select snapshot::text from hrm.pay_run_employees where pay_run_id = $1 and employment_id = $2", Ct, run, joiner);
        var hash = await _h.ScalarAsync<byte[]>("select snapshot_hash from hrm.pay_run_employees where pay_run_id = $1 and employment_id = $2", Ct, run, joiner);
        using var doc = JsonDocument.Parse(stored);
        Assert.Equal(PaySnapshotCodec.Format, doc.RootElement.GetProperty("format").GetString());
        var canonical = PaySnapshotCodec.Unpack(doc);
        Assert.Equal(SHA256.HashData(canonical), hash);
        var again = PayEngine.Calculate(PaySnapshotCodec.Read(canonical), []);
        Assert.True(again.Complete);
        Assert.Equal(joinerBasic, again.Lines.Single(l => l.ComponentId == basic.Id).Amount);
        Assert.Equal(joinerResult.GetProperty("net").GetDecimal(), again.Net);
        Assert.DoesNotContain("Officer", System.Text.Encoding.UTF8.GetString(canonical), StringComparison.Ordinal); // ids and amounts only

        // A calculated run is not calculated again here (recalculation is S1.35b's).
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status);
    }

    // ------------------------------------------------------------------ statutory plug-in point (ruling Q2)

    [Fact]
    public async Task Q2_a_statutory_component_without_its_calculator_leaves_the_employee_incomplete_and_the_run_unfinished()
    {
        var rig = await RigAsync();
        var from = rig.Period.Start.AddDays(-400);
        var basic = await ComponentAsync(rig, "earning", 1, Version(from, "fixed", "calendar_days"));
        var levy = await ComponentAsync(rig, "deduction", 2, Version(from, "manual", flags: new { statutory = true, test_levy = true }));
        var withLevy = await StructureAsync(rig, rig.Period.Start.AddDays(-60), (basic.Id, 1, null, null), (levy.Id, 2, null, null));
        var without = await StructureAsync(rig, rig.Period.Start.AddDays(-60), (basic.Id, 1, null, null));
        var taxed = await HireAsync(rig, Today.AddDays(-400));
        await PayAsync(rig, taxed, withLevy, rig.Period.Start.AddDays(-40), basic.Id, 50_000m);
        var plain = await HireAsync(rig, Today.AddDays(-400));
        await PayAsync(rig, plain, without, rig.Period.Start.AddDays(-40), basic.Id, 40_000m);
        var run = await RunAsync(rig, rig.Period);

        foreach (var attempt in new[] { 1, 2 })
        {
            var answer = await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct);
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
            Assert.Equal("created", answer.Json.GetProperty("status").GetString());
            var incomplete = Assert.Single(answer.Json.GetProperty("incomplete").EnumerateArray());
            Assert.Equal(taxed, incomplete.GetProperty("employmentId").GetGuid());
            Assert.Contains(incomplete.GetProperty("problems").EnumerateArray(), p => p.GetProperty("key").GetString() == PayEngine.StatutoryCalculatorMissing && p.GetProperty("subject").GetString() == levy.Code);
            Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_results where pay_run_id = $1", Ct, run)); // the complete employee once, never re-written
            _ = attempt;
        }

        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_results where pay_run_id = $1 and employment_id = $2", Ct, run, taxed)); // never a zero result
        Assert.Equal("error", await _h.ScalarAsync<string>("select status from hrm.pay_run_employees where pay_run_id = $1 and employment_id = $2", Ct, run, taxed));
        Assert.Equal("incomplete", await _h.ScalarAsync<string>("select validation_flags[1] from hrm.pay_run_employees where pay_run_id = $1 and employment_id = $2", Ct, run, taxed));
        Assert.Equal("created", await _h.ScalarAsync<string>("select status from hrm.pay_runs where id = $1", Ct, run));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.pay_run.calculated' and aggregate_id = $1", Ct, run));
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from jsonb_array_elements((select stage_log from hrm.pay_runs where id = $1)) e where e->>'stage' = 'calculation_incomplete'", Ct, run));
    }

    // ------------------------------------------------------------------ immutability (FR-HRM-PAY-33, FR-XM-05, -06)

    [Fact]
    public async Task FR_HRM_PAY_33_FR_XM_05_the_database_refuses_changes_to_a_locked_runs_results_and_a_correction_is_a_new_result_in_a_later_run()
    {
        var rig = await RigAsync();
        var from = rig.Period.Start.AddDays(-400);
        var basic = await ComponentAsync(rig, "earning", 1, Version(from, "fixed", "calendar_days"));
        var structure = await StructureAsync(rig, rig.Period.Start.AddDays(-60), (basic.Id, 1, null, null));
        var employment = await HireAsync(rig, Today.AddDays(-400));
        await PayAsync(rig, employment, structure, rig.Period.Start.AddDays(-40), basic.Id, 70_000m);
        var run = await RunAsync(rig, rig.Period);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status);
        var result = await _h.ScalarAsync<Guid>("select id from hrm.pay_results where pay_run_id = $1", Ct, run);

        // Before the run is locked, the engine's rows may still change (S1.35b recalculates).
        await _h.ExecAsync("update hrm.pay_run_employees set validation_flags = '{}' where pay_run_id = $1", Ct, run);

        // The run locks (S1.38 builds the stage; set here directly).
        await _h.ExecAsync("update hrm.pay_runs set status = 'locked' where id = $1", Ct, run);
        async Task Refused(string sql, params object?[] values)
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(sql, Ct, values));
            Assert.Equal("ck_pay_run_locked", e.ConstraintName);
        }

        await Refused("update hrm.pay_results set net = net + 1 where id = $1", result);
        await Refused("delete from hrm.pay_results where id = $1", result);
        await Refused("update hrm.pay_result_lines set amount = amount + 1 where result_id = $1", result);
        await Refused("delete from hrm.pay_result_lines where result_id = $1", result);
        await Refused("update hrm.pay_run_employees set validation_flags = '{negative_net}' where pay_run_id = $1", run);
        await Refused("delete from hrm.pay_run_employees where pay_run_id = $1", run);
        await Refused("""
            insert into hrm.pay_traces (result_id, trace, engine_version) values ($1, '{}'::jsonb, 'x')
            """, result);
        await Refused("""
            insert into hrm.pay_results (id, company_id, pay_run_id, employment_id, tax_year, pay_period_id, gross, deductions, net, employer_cost, taxable_income_ytd, tax_ytd,
                                         worked_days, paid_days, created_at, created_by, updated_at, updated_by)
            select $1, company_id, pay_run_id, employment_id, tax_year, pay_period_id, 1, 0, 1, 0, 0, 0, 0, 0, now(), created_by, now(), created_by from hrm.pay_results where id = $2
            """, Guid.CreateVersion7(), result);
        await Refused("update hrm.pay_runs set status = 'calculated' where id = $1", run);
        await Refused("update hrm.pay_runs set total_net = total_net + 1 where id = $1", run);
        await Refused("delete from hrm.pay_runs where id = $1", run);

        // Review S1.35a finding 4: no table of a run can be emptied, the employees' copy of the status only follows the run, and the stage log only grows.
        foreach (var table in new[] { "hrm.pay_result_lines", "hrm.pay_traces", "hrm.pay_results", "hrm.pay_run_employees", "hrm.pay_runs" })
        {
            await Refused($"truncate {table} cascade");
        }

        await Refused("update hrm.pay_run_employees set run_status = 'cancelled' where pay_run_id = $1", run);
        await Refused("update hrm.pay_runs set stage_log = '[]'::jsonb where id = $1", run);
        await Refused("update hrm.pay_runs set stage_log = jsonb_set(stage_log, '{0,by}', '\"someone\"') where id = $1", run);
        await _h.ExecAsync("update hrm.pay_runs set stage_log = stage_log || '[{\"stage\":\"note\"}]'::jsonb where id = $1", Ct, run); // appending is fine

        // What may follow a lock: the run moves forward and its employees' copy of the status follows it (only to the run's own status).
        await _h.ExecAsync("update hrm.pay_runs set status = 'paid' where id = $1", Ct, run);
        await _h.ExecAsync("update hrm.pay_run_employees set run_status = 'paid' where pay_run_id = $1", Ct, run);
        await Refused("update hrm.pay_run_employees set run_status = 'closed' where pay_run_id = $1", run);
        await Refused("update hrm.pay_runs set status = 'locked' where id = $1", run);

        // A correction is a new result in a later run that names the original (FR-XM-06; S1.39 builds corrections).
        var later = await RunAsync(rig, rig.Next);
        var correction = await _h.RowAsync("hrm.pay_results", rig.Company, new Dictionary<string, object?> { ["pay_run_id"] = later, ["employment_id"] = employment, ["pay_period_id"] = rig.Next.Id, ["corrects_result_id"] = result }, Ct);
        Assert.Equal(result, correction["corrects_result_id"]);
        Assert.Equal(70_000m, await _h.ScalarAsync<decimal>("select gross from hrm.pay_results where id = $1", Ct, result)); // the original never changed
    }

    // ------------------------------------------------------------------ one-time inputs (FR-HRM-PAY-07, FR-XM-32, GHR-06)

    [Fact]
    public async Task FR_HRM_PAY_07_one_time_inputs_are_checked_and_a_pending_one_can_be_cancelled()
    {
        var rig = await RigAsync();
        var from = rig.Period.Start.AddDays(-400);
        var basic = await ComponentAsync(rig, "earning", 1, Version(from, "fixed", "calendar_days"));
        var levy = await ComponentAsync(rig, "deduction", 2, Version(from, "manual", flags: new { statutory = true }), oneTime: true);
        var bonus = await ComponentAsync(rig, "earning", 3, Version(from, "manual"), oneTime: true);
        var employment = await HireAsync(rig, Today.AddDays(-400));
        object Item(Guid component, decimal? amount = 1_000m, decimal? units = null, Guid? who = null, Guid? period = null) =>
            new { items = new[] { new { employmentId = who ?? employment, payPeriodId = period ?? rig.Period.Id, componentId = component, amount, units } } };
        string Err(Answer a, string field) => a.Json.GetProperty("errors").GetProperty("items[0]." + field)[0].GetString()!;

        Assert.Equal("errors.hrm.pay.input.component_not_allowed", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(basic.Id), Ct), "componentId"));
        Assert.Equal("errors.hrm.pay.input.statutory", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(levy.Id), Ct), "componentId"));
        Assert.Equal("errors.hrm.pay.input.negative", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(bonus.Id, -5m), Ct), "amount"));
        Assert.Equal("errors.hrm.pay.input.amount_or_units", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(bonus.Id, null), Ct), "amount"));

        // An employee in another pay group's period, and one who is the person entering the input (FR-XM-32).
        var otherGroup = await _h.PostAsync(rig.Manager, "/api/hrm/pay-groups", new { companyId = rig.Company, code = Code("PG"), nameI18n = new { en = "Other group" }, frequency = "monthly", taxYearStartMonth = 7 }, Ct);
        var otherId = otherGroup.Json.GetProperty("id").GetGuid();
        await _h.PostAsync(rig.Manager, $"/api/hrm/pay-groups/{otherId}/periods/generate", new { taxYear = rig.Period.Start.Month >= 7 ? rig.Period.Start.Year : rig.Period.Start.Year - 1 }, Ct);
        var otherPeriod = await PeriodAsync(otherId, rig.Period.Start);
        Assert.Equal("errors.hrm.pay.input.not_in_group", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(bonus.Id, period: otherPeriod.Id), Ct), "payPeriodId"));
        var own = await HireAsync(rig, Today.AddDays(-400));
        await _h.ExecAsync("update hrm.employments set user_ref = $1 where id = $2", Ct, rig.Manager, own);
        Assert.Equal("errors.hrm.pay.input.own_pay", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(bonus.Id, who: own), Ct), "employmentId"));
        var colleague = await _h.UserAsync(Ct);
        await _h.AssignAsync(colleague, await _h.RoleAsync(Ct, [.. SetUp, .. Engine], [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        var onMine = await _h.PostAsync(colleague, "/api/hrm/pay-inputs", Item(bonus.Id, who: own), Ct);
        Assert.True(onMine.Status == HttpStatusCode.Created, onMine.Body);
        var ownCancel = await _h.PostAsync(rig.Manager, $"/api/hrm/pay-inputs/{onMine.Json.GetProperty("items")[0].GetProperty("id").GetGuid()}/cancel", new { }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, ownCancel.Status); // nobody cancels an input to their own pay either (review S1.35a finding 5)

        // An officer who may run payroll but only read salary fields cannot enter an amount; units are fine (GHR-06).
        var officer = await _h.UserAsync(Ct);
        await _h.AssignAsync(officer, await _h.RoleAsync(Ct, Engine, [("*", "all")], [("*", "class:salary", "read_only")]), null, Today.AddDays(-1), null, Ct);
        Assert.Equal("errors.access.field_not_editable", Err(await _h.PostAsync(officer, "/api/hrm/pay-inputs", Item(bonus.Id), Ct), "amount"));
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(officer, "/api/hrm/pay-inputs", Item(bonus.Id, null, 2m), Ct)).Status);

        // Added, then cancelled; a second cancel is refused; nothing at all is added when one row of several is wrong.
        var added = await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(bonus.Id, 2_500m), Ct);
        Assert.True(added.Status == HttpStatusCode.Created, added.Body);
        var id = added.Json.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(await _h.ScalarAsync<string>("select currency_code from hrm.pay_groups where id = $1", Ct, rig.Group), await _h.ScalarAsync<string>("select currency_code from hrm.pay_inputs where id = $1", Ct, id));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Manager, $"/api/hrm/pay-inputs/{id}/cancel", new { }, Ct)).Status);
        Assert.Equal("errors.hrm.pay.input.cancelled", (await _h.PostAsync(rig.Manager, $"/api/hrm/pay-inputs/{id}/cancel", new { }, Ct)).Json.GetProperty("title").GetString());
        var before = await _h.ScalarAsync<long>("select count(*) from hrm.pay_inputs where employment_id = $1", Ct, employment);
        var mixed = await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", new { items = new object[] { new { employmentId = employment, payPeriodId = rig.Period.Id, componentId = bonus.Id, amount = 10m }, new { employmentId = employment, payPeriodId = rig.Period.Id, componentId = basic.Id, amount = 10m } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.Status);
        Assert.Equal(before, await _h.ScalarAsync<long>("select count(*) from hrm.pay_inputs where employment_id = $1", Ct, employment));

        // A period whose run is locked takes no new input (FR-XM-05).
        var run = await RunAsync(rig, rig.Period);
        await _h.ExecAsync("update hrm.pay_runs set status = 'locked' where id = $1", Ct, run);
        Assert.Equal("errors.hrm.pay.input.run_locked", Err(await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", Item(bonus.Id), Ct), "payPeriodId"));

        // The catalogue of formula inputs.
        var names = await _h.GetAsync(rig.Manager, "/api/hrm/pay-input-names", Ct);
        Assert.True(names.Status == HttpStatusCode.OK, names.Body);
        Assert.Contains(names.Json.GetProperty("items").EnumerateArray(), n => n.GetProperty("formula").GetString() == "$worked_days");
    }

    // ------------------------------------------------------------------ permissions, scope, closed writes, GHR-06

    [Fact]
    public async Task GHR_06_amounts_show_only_with_the_salary_class_and_runs_need_the_run_permission_and_the_company_scope()
    {
        var rig = await RigAsync();
        var from = rig.Period.Start.AddDays(-400);
        var basic = await ComponentAsync(rig, "earning", 1, Version(from, "fixed", "calendar_days"));
        var structure = await StructureAsync(rig, rig.Period.Start.AddDays(-60), (basic.Id, 1, null, null));
        var employment = await HireAsync(rig, Today.AddDays(-400));
        await PayAsync(rig, employment, structure, rig.Period.Start.AddDays(-40), basic.Id, 54_321m);

        var nobody = await _h.UserAsync(Ct);
        await _h.AssignAsync(nobody, await _h.RoleAsync(Ct, ["hrm.pay_runs.read", "hrm.pay_results.read"], [("*", "all")]), null, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Runs, new { payGroupId = rig.Group, payPeriodId = rig.Period.Id }, Ct)).Status);
        var run = await RunAsync(rig, rig.Period);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Runs}/{run}/results", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status);

        // Results without the salary class: rows and days, never an amount; with it, the amounts.
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, Engine, [("*", "all")]), null, Today.AddDays(-1), null, Ct);
        var hidden = await _h.GetAsync(reader, $"{Runs}/{run}/results", Ct);
        Assert.True(hidden.Status == HttpStatusCode.OK, hidden.Body);
        var row = Assert.Single(hidden.Json.GetProperty("items").EnumerateArray());
        Assert.False(row.TryGetProperty("gross", out _));
        Assert.False(row.TryGetProperty("net", out _));
        Assert.False(row.GetProperty("lines")[0].TryGetProperty("amount", out _));
        Assert.DoesNotContain("54321", hidden.Body, StringComparison.Ordinal);
        Assert.Equal(54_321m, LineOf((await _h.GetAsync(rig.Manager, $"{Runs}/{run}/results", Ct)).Json.GetProperty("items")[0], basic.Code));

        // The generated lists hide the same fields, including the snapshot (it holds the compensation amounts).
        var list = await _h.GetAsync(reader, $"/api/hrm/pay-results?payRunId={run}", Ct);
        Assert.True(list.Status == HttpStatusCode.OK, list.Body);
        Assert.DoesNotContain("54321", list.Body, StringComparison.Ordinal);
        var lines = await _h.GetAsync(reader, $"/api/hrm/pay-result-lines?employmentId={employment}", Ct);
        Assert.DoesNotContain("54321", lines.Body, StringComparison.Ordinal);
        var employees = await _h.GetAsync(reader, $"/api/hrm/pay-run-employees?payRunId={run}", Ct);
        Assert.True(employees.Status == HttpStatusCode.OK, employees.Body);
        Assert.DoesNotContain("gzip-base64", employees.Body, StringComparison.Ordinal);

        // The change log keeps the amounts encrypted with the customer key; the trace table is never in it (CR-034).
        var logged = await _h.ScalarAsync<string>("select changes::text from aud.change_log where entity_type = 'hrm.pay_results' and entity_id = (select id from hrm.pay_results where pay_run_id = $1) and action = 'create'", Ct, run);
        Assert.DoesNotContain("54321", logged, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_traces' and entity_id in (select id from hrm.pay_results where pay_run_id = $1)", Ct, run));

        // Another company's payroll officer does not see the run; the generated writes of the engine's tables are closed.
        var otherCompany = await _h.CompanyAsync(Ct);
        var outsider = await _h.UserAsync(Ct);
        await _h.AssignAsync(outsider, await _h.RoleAsync(Ct, Engine, [("*", "all")], [("*", "class:salary", "read_only")]), otherCompany, Today.AddDays(-1), null, Ct);
        Assert.True((await _h.GetAsync(outsider, $"{Runs}/{run}/results", Ct)).Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.True((await _h.PostAsync(outsider, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        var result = await _h.ScalarAsync<Guid>("select id from hrm.pay_results where pay_run_id = $1", Ct, run);
        Assert.True((await _h.PutAsync(rig.Manager, $"/api/hrm/pay-results/{result}", new { gross = 1m }, Ct)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.True((await _h.PostAsync(rig.Manager, "/api/hrm/pay-result-lines", new { amount = 1m }, Ct)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.True((await _h.PutAsync(rig.Manager, $"/api/hrm/pay-runs/{run}", new { status = "locked" }, Ct)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }
}
