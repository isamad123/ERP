using System.Diagnostics;
using System.Globalization;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.PayRuns;

/// <summary>
/// Performance of the payroll calculation (slice S1.35 Build item 10; GHR-01, FR-HRM-PAY-34: 10,000 employees in under 15 minutes; CR-093 S1.35b detail).
/// A customer of its own with a synthetic company: one employee hired and paid through the API on a structure of ten components (a fixed basic, four
/// percentages, two formulas, two fixed deductions and a one-time component), copied by SQL to <see cref="Employees"/> employees (1,000 in the normal
/// suite; <c>ERP_PAY_PERF_EMPLOYEES=10000</c>, and <c>ERP_PAY_PERF_LANES</c> for another number of lanes, for the measured runs of the evidence, <c>docs/performance/S1.35b-payroll-calculation.md</c>). The real
/// job calculates them in its default lanes and chunks through the real worker. The budget is the 10,000-employee target scaled linearly (90 seconds for
/// 1,000), checked on the run's own projection, and the scaling is compared with a baseline in the same run (a recalculation of a tenth of the employees):
/// per employee the full run may cost at most twice the baseline, so a calculation that grows faster than the number of employees fails whatever the
/// machine's speed. Memory: the managed heap after the run stays bounded (the calculation keeps one chunk at a time).
/// </summary>
[Collection(TenancyGroup.Name)]
[Trait("Suite", "Performance")]
public sealed class PayRunPerformanceTests(TenancyFixture fx) : IAsyncLifetime
{
    private static readonly int Employees = int.TryParse(Environment.GetEnvironmentVariable("ERP_PAY_PERF_EMPLOYEES"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 100 ? n : 1_000;
    private static readonly int? Lanes = int.TryParse(Environment.GetEnvironmentVariable("ERP_PAY_PERF_LANES"), NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l is >= 1 and <= 8 ? l : null;
    private static readonly TimeSpan Target = TimeSpan.FromMinutes(15);
    private const int TargetEmployees = 10_000;
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForLoadAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task GHR_01_FR_HRM_PAY_34_a_synthetic_company_with_ten_components_calculates_within_the_budget_of_10000_employees_in_15_minutes()
    {
        var rig = await PayRunRig.CreateAsync(_h, fx, lanes: Lanes);
        var from = rig.Period.Start.AddDays(-400);
        var basic = await rig.ComponentAsync("earning", 1, PayRunRig.Version(from, "fixed", "calendar_days"));
        var items = new List<(Guid, int, decimal?, decimal?)> { (basic.Id, 1, null, null) };
        foreach (var (order, percent) in new[] { (2, 40m), (3, 10m), (4, 5m), (5, 2.5m) })
        {
            items.Add(((await rig.ComponentAsync("earning", order, PayRunRig.Version(from, "percent_of", "calendar_days", [basic.Id]))).Id, order, null, percent));
        }

        items.Add(((await rig.ComponentAsync("earning", 6, PayRunRig.Version(from, "formula", formula: $"round({basic.Code} * 0.08 / $period_days * $paid_days, 2)"))).Id, 6, null, null));
        items.Add(((await rig.ComponentAsync("earning", 7, PayRunRig.Version(from, "formula", formula: $"if($paid_days >= 15, min({basic.Code} * 0.05, 5000), 0)"))).Id, 7, null, null));
        items.Add(((await rig.ComponentAsync("deduction", 8, PayRunRig.Version(from, "fixed"))).Id, 8, 750m, null));
        items.Add(((await rig.ComponentAsync("deduction", 9, PayRunRig.Version(from, "fixed"))).Id, 9, 300m, null));
        items.Add(((await rig.ComponentAsync("earning", 10, PayRunRig.Version(from, "manual"), oneTime: true)).Id, 10, null, null));
        var structure = await rig.StructureAsync(rig.Period.Start.AddDays(-60), [.. items]);
        var template = await rig.HireAsync(rig.Today.AddDays(-400));
        await rig.PayAsync(template, structure, rig.Period.Start.AddDays(-40), (basic.Id, 85_000m));

        var load = Stopwatch.StartNew();
        await CloneAsync(template, Employees - 1);
        load.Stop();
        var run = await rig.RunAsync();

        // The full run: from the request (the lock first) to the last lane's end.
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        var asked = await rig.AskAsync(run);
        await rig.RunJobsAsync();
        watch.Stop();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var summary = await rig.SummaryAsync(run);
        Assert.Equal("calculated", summary.GetProperty("status").GetString());
        Assert.Equal(Employees, summary.GetProperty("employeeCount").GetInt32());

        // Every employee has the template's result (same data) and a trace.
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(distinct net) from hrm.pay_results where pay_run_id = $1", Ct, run));
        Assert.Equal((long)Employees, await _h.ScalarAsync<long>("select count(*) from hrm.pay_traces t join hrm.pay_results r on r.id = t.result_id where r.pay_run_id = $1", Ct, run));
        Assert.Equal(Employees * 9L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_result_lines l join hrm.pay_results r on r.id = l.result_id where r.pay_run_id = $1", Ct, run));

        // The baseline in the same run: a tenth of the employees recalculated from fresh snapshots.
        var tenth = await _h.ScalarAsync<Guid[]>("select array_agg(employment_id) from (select employment_id from hrm.pay_run_employees where pay_run_id = $1 order by employment_id limit $2) x", Ct, run, Employees / 10);
        var baseline = Stopwatch.StartNew();
        await rig.AskAsync(run, "recalculate", new { employmentIds = tenth, freshSnapshot = true, acceptOpenItems = true });
        await rig.RunJobsAsync();
        baseline.Stop();
        Assert.Equal("calculated", (await rig.SummaryAsync(run)).GetProperty("status").GetString());

        var perEmployee = watch.Elapsed / Employees;
        var basePerEmployee = baseline.Elapsed / tenth.Length;
        var projected = perEmployee * TargetEmployees;
        var traceBytes = await _h.ScalarAsync<long>("select sum(pg_column_size(t.trace)) from hrm.pay_traces t join hrm.pay_results r on r.id = t.result_id where r.pay_run_id = $1", Ct, run);
        var snapshotBytes = await _h.ScalarAsync<long>("select sum(pg_column_size(snapshot)) from hrm.pay_run_employees where pay_run_id = $1", Ct, run);
        var report = string.Join("\n",
            $"S1.35b payroll calculation, {Employees} employees, ten components, lanes {asked.GetProperty("lanes").GetInt32()}, chunk 200",
            $"machine: {Environment.ProcessorCount} logical processors, {Environment.OSVersion}, .NET {Environment.Version}",
            $"load (SQL copy) {load.Elapsed.TotalSeconds:0.0} s",
            $"calculation {watch.Elapsed.TotalSeconds:0.0} s = {perEmployee.TotalMilliseconds:0.00} ms per employee; projected for {TargetEmployees}: {projected.TotalMinutes:0.0} min (target {Target.TotalMinutes} min)",
            $"baseline recalculation of {tenth.Length}: {baseline.Elapsed.TotalSeconds:0.0} s = {basePerEmployee.TotalMilliseconds:0.00} ms per employee",
            $"managed heap of the test process before {heapBefore / 1048576.0:0.0} MiB, after {heapAfter / 1048576.0:0.0} MiB; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:0} MiB",
            $"stored: snapshots {snapshotBytes / 1024.0 / Employees:0.0} KiB per employee, traces {traceBytes / 1024.0 / Employees:0.0} KiB per employee");
        TestContext.Current.SendDiagnosticMessage(report);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"s135b-perf-{Employees}.txt"), report, Ct);

        Assert.True(projected < Target, report);
        Assert.True(perEmployee <= basePerEmployee * 2, "the calculation grows faster than the number of employees:\n" + report);
        Assert.True(heapAfter - heapBefore < 512L * 1048576, "memory is not bounded:\n" + report);
    }

    /// <summary>Copies of the template employee (person, employment, assignment, compensation row and lines) with new keys, as the services wrote them.</summary>
    private async Task CloneAsync(Guid template, int copies)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var statements = new[]
        {
            $"""
            insert into hrm.persons
            select r.* from hrm.persons t, generate_series(1, {copies}) g,
                 lateral jsonb_populate_record(null::hrm.persons, to_jsonb(t) || jsonb_build_object('id', md5('{tag}p' || g)::uuid, 'person_no', 'PP{tag}' || g)) r
            where t.id = (select person_id from hrm.employments where id = '{template}')
            """,
            $"""
            insert into hrm.employments
            select r.* from hrm.employments t, generate_series(1, {copies}) g,
                 lateral jsonb_populate_record(null::hrm.employments, to_jsonb(t) || jsonb_build_object('id', md5('{tag}e' || g)::uuid, 'person_id', md5('{tag}p' || g)::uuid, 'employee_no', 'PE{tag}' || g, 'user_ref', null, 'previous_employment_id', null)) r
            where t.id = '{template}'
            """,
            $"""
            insert into hrm.employment_assignments
            select r.* from hrm.employment_assignments t, generate_series(1, {copies}) g,
                 lateral jsonb_populate_record(null::hrm.employment_assignments, to_jsonb(t) || jsonb_build_object('id', md5('{tag}a' || g)::uuid, 'employment_id', md5('{tag}e' || g)::uuid)) r
            where t.employment_id = '{template}'
            """,
            $"""
            insert into hrm.compensation_assignments
            select r.* from hrm.compensation_assignments t, generate_series(1, {copies}) g,
                 lateral jsonb_populate_record(null::hrm.compensation_assignments, to_jsonb(t) || jsonb_build_object('id', md5('{tag}c' || g)::uuid, 'employment_id', md5('{tag}e' || g)::uuid)) r
            where t.employment_id = '{template}'
            """,
            $"""
            insert into hrm.compensation_lines
            select r.* from hrm.compensation_lines t join hrm.compensation_assignments c on c.id = t.assignment_id, generate_series(1, {copies}) g,
                 lateral jsonb_populate_record(null::hrm.compensation_lines, to_jsonb(t) || jsonb_build_object('id', md5('{tag}l' || g || t.id::text)::uuid, 'assignment_id', md5('{tag}c' || g)::uuid)) r
            where c.employment_id = '{template}'
            """,
            "analyze hrm.persons, hrm.employments, hrm.employment_assignments, hrm.compensation_assignments, hrm.compensation_lines",
        };
        await using var connection = await _h.OwnerAsync(null, Ct);
        foreach (var sql in statements)
        {
            await using var command = new Npgsql.NpgsqlCommand(sql, connection) { CommandTimeout = 900 };
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal((long)copies + 1, await _h.ScalarAsync<long>("select count(*) from hrm.compensation_assignments where structure_id = (select structure_id from hrm.compensation_assignments where employment_id = $1)", Ct, template));
    }
}
