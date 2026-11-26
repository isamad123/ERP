using System.Net;
using System.Text.Json;
using Erp.Hrm.Contracts.Payroll;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.PayRuns;

/// <summary>
/// A company set up for payroll calculation tests of slice S1.35b, through the real API: approvals of pay and employment changes off, a payroll manager
/// (set-up, engine, trace, salary fields editable), an HR administrator, a monthly pay group with the periods of last month and the next one. The company's
/// date is the harness clock's in Asia/Karachi. Calculations run through the real job <c>hrm.payroll_calculate</c> on a worker over the harness customer.
/// </summary>
internal sealed class PayRunRig
{
    public const string Runs = "/api/hrm/pay-runs";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private static readonly string[] Rights =
    [
        "hrm.pay_components.read", "hrm.pay_components.create", "hrm.pay_components.update", "hrm.pay_component_versions.read",
        "hrm.salary_structures.read", "hrm.salary_structures.create", "hrm.salary_structures.update", "hrm.salary_structure_components.read", "hrm.salary_structure_components.update",
        "hrm.compensation_assignments.read", "hrm.compensation_assignments.create", "hrm.compensation_lines.read", "hrm.compensation_lines.create", "hrm.pay.compensation.read",
        "hrm.pay_groups.read", "hrm.pay_groups.create", "hrm.pay_periods.read", "hrm.pay_periods.generate", "hrm.employments.read", "hrm.employment_assignments.read",
        "hrm.pay.run", "hrm.pay.results.read", "hrm.pay.trace.read", "hrm.pay_inputs.create", "hrm.pay_inputs.cancel", "hrm.pay_runs.read", "hrm.pay_run_employees.read",
        "hrm.pay_results.read", "hrm.pay_result_lines.read", "hrm.pay_inputs.read",
    ];

    private readonly AccessHarness _h;
    private readonly TenancyFixture _fx;

    private PayRunRig(AccessHarness h, TenancyFixture fx)
    {
        _h = h;
        _fx = fx;
    }

    public Guid Company { get; private set; }

    public (Guid OrgUnit, Guid Branch) Place { get; private set; }

    public Guid Manager { get; private set; }

    public Guid Hr { get; private set; }

    public Guid Admin { get; private set; }

    public Guid Second { get; private set; }

    public Guid Group { get; private set; }

    public (Guid Id, DateOnly Start, DateOnly End) Period { get; private set; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static async Task<PayRunRig> CreateAsync(AccessHarness h, TenancyFixture fx, int? chunkSize = null, int? lanes = null)
    {
        var rig = new PayRunRig(h, fx);
        rig.Company = await h.CompanyAsync(Ct);
        rig.Admin = await h.UserAsync(Ct, null, "tenant_admin");
        rig.Second = await h.UserAsync(Ct, null, "tenant_admin");
        await rig.SettingAsync("hrm.employment_events.approval_kinds", Array.Empty<string>());
        await rig.SettingAsync("hrm.pay.change_approval", false);
        if (chunkSize is { } size)
        {
            await rig.SettingAsync("hrm.pay.chunk_size", size);
        }

        if (lanes is { } n)
        {
            await rig.SettingAsync("hrm.pay.max_parallel_chunks", n);
        }

        rig.Manager = await h.UserAsync(Ct);
        await h.AssignAsync(rig.Manager, await h.RoleAsync(Ct, Rights, [("*", "all")], [("*", "class:salary", "editable")]), null, rig.Today.AddDays(-1), null, Ct);
        rig.Hr = await h.UserAsync(Ct, null, "hr_admin");
        var group = await h.PostAsync(rig.Manager, "/api/hrm/pay-groups", new { companyId = rig.Company, code = Code("PG"), nameI18n = new { en = "Engine group", ur = "انجن گروپ" }, frequency = "monthly", taxYearStartMonth = 7 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        rig.Group = group.Json.GetProperty("id").GetGuid();
        var target = rig.Today.AddMonths(-1);
        var taxYear = target.Month >= 7 ? target.Year : target.Year - 1;
        foreach (var year in new[] { taxYear, taxYear + 1 })
        {
            var generated = await h.PostAsync(rig.Manager, $"/api/hrm/pay-groups/{rig.Group}/periods/generate", new { taxYear = year }, Ct);
            Assert.True(generated.Status == HttpStatusCode.Created, generated.Body);
        }

        var period = await h.ScalarAsync<Guid>("select id from hrm.pay_periods where pay_group_id = $1 and start_date <= $2 and end_date >= $2", Ct, rig.Group, target);
        rig.Period = (period, await h.ScalarAsync<DateOnly>("select start_date from hrm.pay_periods where id = $1", Ct, period), await h.ScalarAsync<DateOnly>("select end_date from hrm.pay_periods where id = $1", Ct, period));
        rig.Place = await h.PlaceAsync(rig.Company, Ct);
        return rig;
    }

    public async Task SettingAsync(string key, object value)
    {
        var held = await _h.PutAsync(Admin, "/api/cfg/settings/" + key, new { layer = "company", scopeId = Company, value, reason = "test" }, Ct);
        if (held.Status == HttpStatusCode.OK)
        {
            return; // a setting that needs no second person
        }

        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(Second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
    }

    public static object Version(DateOnly from, string method, string rule = "none", Guid[]? bases = null, string? formula = null) =>
        new { validFrom = Iso(from), method, proRataRule = rule, baseComponentIds = bases, formula, statutoryFlags = new { taxable = true } };

    public async Task<(Guid Id, string Code)> ComponentAsync(string kind, int order, object version, bool oneTime = false)
    {
        var code = Code("C");
        var answer = await _h.PostAsync(Manager, "/api/hrm/pay-components", new { companyId = Company, code, nameI18n = new { en = "Component " + code, ur = "جزو " + code }, kind, payslipOrder = order, isRecurring = !oneTime, isOneTimeAllowed = oneTime, version }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), code);
    }

    public async Task<Guid> StructureAsync(DateOnly from, params (Guid Component, int Order, decimal? Amount, decimal? Percent)[] items)
    {
        var created = await _h.PostAsync(Manager, "/api/hrm/salary-structures", new { companyId = Company, code = Code("S"), nameI18n = new { en = "Engine structure", ur = "انجن ڈھانچہ" } }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        var set = await _h.PutAsync(Manager, $"/api/hrm/salary-structures/{id}", new
        {
            rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.salary_structures where id = $1", Ct, id),
            components = new { validFrom = Iso(from), items = items.Select(i => new { componentId = i.Component, evaluationOrder = i.Order, defaultAmount = i.Amount, defaultPercent = i.Percent, isMandatory = false }).ToArray() },
        }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return id;
    }

    public async Task<Guid> HireAsync(DateOnly joining)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(Hr, "/api/hrm/employments", new
        {
            personId = person,
            companyId = Company,
            employeeType = "permanent",
            joiningDate = Iso(joining),
            assignment = new { orgUnitId = Place.OrgUnit, branchId = Place.Branch, title = "Officer", payGroupId = Group },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    public async Task PayAsync(Guid employment, Guid structure, DateOnly from, params (Guid Component, decimal Amount)[] lines)
    {
        var done = await _h.PostAsync(Manager, $"/api/hrm/employments/{employment}/compensation", new { structureId = structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = lines.Select(l => new { componentId = l.Component, amount = l.Amount }).ToArray() }, Ct);
        Assert.True(done.Status == HttpStatusCode.Created, done.Body);
    }

    public async Task<Guid> RunAsync()
    {
        var created = await _h.PostAsync(Manager, Runs, new { payGroupId = Group, payPeriodId = Period.Id }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return created.Json.GetProperty("id").GetGuid();
    }

    /// <summary>Asks for the calculation (202 with the lanes' job runs) without running it.</summary>
    public async Task<JsonElement> AskAsync(Guid run, string action = "calculate", object? body = null)
    {
        var answer = await _h.PostAsync(Manager, $"{Runs}/{run}/{action}", body ?? new { acceptOpenItems = true }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Accepted, answer.Body);
        return answer.Json;
    }

    /// <summary>A job rig on the harness customer (the harness clock).</summary>
    public Task<JobRig> JobsAsync() => JobRig.ForExistingAsync(_fx, _h.Code, _h.Clock, Ct);

    /// <summary>Runs every queued calculation lane to its end and checks none failed.</summary>
    public async Task RunJobsAsync(Action<IServiceCollection>? extra = null)
    {
        await using var jobs = await JobsAsync();
        await jobs.RunUntilIdleAsync(jobs.Worker(extra: extra), Ct);
        await AssertNoFailedLaneAsync(jobs);
    }

    public static async Task AssertNoFailedLaneAsync(JobRig jobs)
    {
        var failed = await jobs.ScalarAsync<long>("select count(*) from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'hrm.payroll_calculate' and r.status not in ('succeeded', 'cancelled')", Ct);
        Assert.True(failed == 0, await jobs.DumpAsync(Ct));
    }

    public async Task<JsonElement> SummaryAsync(Guid run)
    {
        var summary = await _h.GetAsync(Manager, $"{Runs}/{run}/summary", Ct);
        Assert.True(summary.Status == HttpStatusCode.OK, summary.Body);
        return summary.Json;
    }

    /// <summary>Per employment of the run: the snapshot hash, the result hash in the trace, the trace's stored text, the result id and its row version.</summary>
    public async Task<Dictionary<Guid, (string Snapshot, string Result, string Trace, Guid ResultId, long RowVersion)>> HashesAsync(Guid run)
    {
        await using var c = await _h.OwnerAsync(null, Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            select e.employment_id, encode(e.snapshot_hash, 'hex'), t.trace->>'resultHash', t.trace::text, r.id, r.row_version
            from hrm.pay_run_employees e join hrm.pay_results r on r.pay_run_id = e.pay_run_id and r.employment_id = e.employment_id join hrm.pay_traces t on t.result_id = r.id
            where e.pay_run_id = $1
            """, c);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = run });
        var map = new Dictionary<Guid, (string, string, string, Guid, long)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            map[reader.GetGuid(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4), reader.GetInt64(5));
        }

        return map;
    }
}

/// <summary>
/// A test-only input provider that gives nothing (so snapshots stay as they are) and stops in the n-th batch it is asked for until released or until its
/// worker is killed: a chunk that never commits, as a crashed worker leaves it.
/// </summary>
internal sealed class PauseGate(int pauseOnCall)
{
    private int _calls;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task CallAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _calls) == pauseOnCall)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddScoped<IPayInputProvider, PausingProvider>();
    }
}

internal sealed class PausingProvider(PauseGate gate) : IPayInputProvider
{
    public string Code => "test_pause";

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        await gate.CallAsync(cancellationToken);
        return [];
    }

    public Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken) => Task.CompletedTask;
}
