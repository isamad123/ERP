using System.Net;
using Erp.Hrm.Application.Payroll;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.PayRuns;

/// <summary>
/// Slice S1.35b through the real API pipeline, the real access engine, the real job runner and a real PostgreSQL: FR-HRM-PAY-32 (a trace per result with
/// every step, read only with <c>hrm.pay.trace.read</c> and the salary class, never in the audit search), FR-HRM-PAY-34 (the job <c>hrm.payroll_calculate</c>
/// in lanes and chunks with checkpoints: a worker killed in the middle, a second worker finishes, counts equal an uninterrupted run; cancel at the next
/// chunk; progress), FR-HRM-PAY-31 (the same snapshot twice and after the restart gives byte-identical result and trace hashes; one changed input changes
/// only that employee), Build item 11 (recalculation replaces unlocked results as new row versions, the change log keeps the earlier ones; refused once
/// locked, FR-HRM-PAY-33, FR-XM-05), FR-XM-26 (the lock first) and GHR-06. The company's date is the harness clock's in Asia/Karachi.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PayRunJobTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Runs = PayRunRig.Runs;
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Six employees on a structure of a fixed basic, a percentage, a formula, a fixed deduction and a one-time bonus component.</summary>
    private async Task<(PayRunRig Rig, Guid Run, List<Guid> Employees, (Guid Id, string Code) Basic, (Guid Id, string Code) Bonus)> SixAsync(int? chunk = 2, int? lanes = 2)
    {
        var rig = await PayRunRig.CreateAsync(_h, fx, chunk, lanes);
        var from = rig.Period.Start.AddDays(-400);
        var basic = await rig.ComponentAsync("earning", 1, PayRunRig.Version(from, "fixed", "calendar_days"));
        var house = await rig.ComponentAsync("earning", 2, PayRunRig.Version(from, "percent_of", "calendar_days", [basic.Id]));
        var conv = await rig.ComponentAsync("earning", 3, PayRunRig.Version(from, "formula", formula: $"round({basic.Code} * 0.1 / $period_days * $paid_days, 2)"));
        var canteen = await rig.ComponentAsync("deduction", 4, PayRunRig.Version(from, "fixed"));
        var bonus = await rig.ComponentAsync("earning", 5, PayRunRig.Version(from, "manual"), oneTime: true);
        var structure = await rig.StructureAsync(rig.Period.Start.AddDays(-60), (basic.Id, 1, null, null), (house.Id, 2, null, 40m), (conv.Id, 3, null, null), (canteen.Id, 4, 750m, null), (bonus.Id, 5, null, null));
        var employees = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            // Two joiners inside the period, the others employed all month; salaries differ so every employee's numbers differ.
            var joining = i < 2 ? rig.Period.Start.AddDays(5 + (i * 7)) : rig.Today.AddDays(-400);
            var employment = await rig.HireAsync(joining);
            await rig.PayAsync(employment, structure, i < 2 ? joining : rig.Period.Start.AddDays(-40), (basic.Id, 40_000m + (i * 1_234.56m)));
            employees.Add(employment);
        }

        return (rig, await rig.RunAsync(), employees, basic, bonus);
    }

    // ------------------------------------------------------------------ the trace (FR-HRM-PAY-32, GHR-06)

    [Fact]
    public async Task FR_HRM_PAY_32_GHR_06_every_result_has_a_trace_with_its_steps_read_only_with_the_trace_permission_and_the_salary_class_and_never_audited()
    {
        var (rig, run, employees, basic, _) = await SixAsync(chunk: null, lanes: 1);
        var asked = await rig.AskAsync(run);
        Assert.Equal(1, asked.GetProperty("lanes").GetInt32());
        await rig.RunJobsAsync();
        Assert.Equal("calculated", (await rig.SummaryAsync(run)).GetProperty("status").GetString());

        // One trace per result, with the engine version (PAY-32).
        Assert.Equal(6L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_traces t join hrm.pay_results r on r.id = t.result_id where r.pay_run_id = $1 and t.engine_version = $2", Ct, run, PayEngine.Version));
        var result = await _h.ScalarAsync<Guid>("select id from hrm.pay_results where pay_run_id = $1 and employment_id = $2", Ct, run, employees[0]);
        var answer = await _h.GetAsync(rig.Manager, $"/api/hrm/pay-results/{result}/trace", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        var trace = answer.Json.GetProperty("trace");
        Assert.Equal(PayEngine.Version, trace.GetProperty("engineVersion").GetString());
        Assert.Equal(Convert.ToHexStringLower(await _h.ScalarAsync<byte[]>("select snapshot_hash from hrm.pay_run_employees where pay_run_id = $1 and employment_id = $2", Ct, run, employees[0])),
            trace.GetProperty("snapshotHash").GetString());
        Assert.Equal(PayRounding.HalfAwayFromZero, trace.GetProperty("rounding").GetProperty("mode").GetString());

        // Every step: the inputs, each component's method (the formula with its evaluated steps), pro-rata, and the table and rule versions used.
        var steps = trace.GetProperty("steps").EnumerateArray().ToList();
        Assert.Contains(steps, s => s.GetProperty("step").GetString() == "inputs" && s.GetProperty("text").GetString()!.Contains("$paid_days", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.GetProperty("component").GetString() == basic.Code && s.GetProperty("step").GetString() == "method");
        Assert.Contains(steps, s => s.GetProperty("step").GetString() == "pro_rata");
        Assert.Contains(steps, s => s.GetProperty("step").GetString() == "method" && s.GetProperty("text").GetString()!.Contains("round(", StringComparison.Ordinal));
        var versionId = await _h.ScalarAsync<Guid>("select id from hrm.pay_component_versions where component_id = $1", Ct, basic.Id);
        Assert.Contains(trace.GetProperty("versions").GetProperty("components").EnumerateArray(),
            c => c.GetProperty("code").GetString() == basic.Code && c.GetProperty("versions")[0].GetProperty("versionId").GetGuid() == versionId);
        var net = await _h.ScalarAsync<decimal>("select net from hrm.pay_results where id = $1", Ct, result);
        Assert.Equal(net, trace.GetProperty("result").GetProperty("net").GetDecimal());
        Assert.Equal(Convert.ToHexStringLower(PayTraceDocument.Hash(trace)), answer.Json.GetProperty("traceHash").GetString());

        // The read is in the access log; without the trace permission 403; with it but without the salary class 403; another company's officer 404.
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'hrm.pay_traces' and entity_id = $1 and actor_user_id = $2", Ct, result, rig.Manager) >= 1);
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.pay.results.read", "hrm.pay_runs.read", "hrm.pay_results.read"], [("*", "all")], [("*", "class:salary", "read_only")]), null, rig.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(reader, $"/api/hrm/pay-results/{result}/trace", Ct)).Status);
        var noSalary = await _h.UserAsync(Ct);
        await _h.AssignAsync(noSalary, await _h.RoleAsync(Ct, ["hrm.pay.trace.read", "hrm.pay_results.read"], [("*", "all")]), null, rig.Today.AddDays(-1), null, Ct);
        var hidden = await _h.GetAsync(noSalary, $"/api/hrm/pay-results/{result}/trace", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, hidden.Status);
        Assert.Equal("errors.hrm.pay.trace.salary_class", hidden.Json.GetProperty("title").GetString());
        var outsider = await _h.UserAsync(Ct);
        await _h.AssignAsync(outsider, await _h.RoleAsync(Ct, ["hrm.pay.trace.read", "hrm.pay_results.read"], [("*", "all")], [("*", "class:salary", "read_only")]), await _h.CompanyAsync(Ct), rig.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"/api/hrm/pay-results/{result}/trace", Ct)).Status);

        // Never audited (CR-034): no change-log entry of the trace table, and the audit search of the result shows the result's entries only.
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_traces' and entity_id in (select id from hrm.pay_results where pay_run_id = $1)", Ct, run));
        var search = await _h.GetAsync(rig.Admin, $"/api/aud/changes?entityId={result}", Ct);
        Assert.True(search.Status == HttpStatusCode.OK, search.Body);
        Assert.All(search.Json.GetProperty("items").EnumerateArray(), i => Assert.Equal("hrm.pay_results", i.GetProperty("entityType").GetString()));
        Assert.DoesNotContain("pay-trace-v1", search.Body, StringComparison.Ordinal);
        var byType = await _h.GetAsync(rig.Admin, $"/api/aud/changes?entityType=hrm.pay_traces&entityId={result}", Ct);
        Assert.True(byType.Status == HttpStatusCode.OK, byType.Body);
        Assert.Empty(byType.Json.GetProperty("items").EnumerateArray());

        // GHR-06 in the audit search: a reader of the change log without the salary class sees the result's amounts hidden.
        var auditor = await _h.UserAsync(Ct);
        await _h.AssignAsync(auditor, await _h.RoleAsync(Ct, ["aud.changes.view", "hrm.pay_results.read"], [("*", "all")]), null, rig.Today.AddDays(-1), null, Ct);
        var masked = await _h.GetAsync(auditor, $"/api/aud/changes?entityId={result}", Ct);
        Assert.True(masked.Status == HttpStatusCode.OK, masked.Body);
        Assert.NotEmpty(masked.Json.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(net.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), masked.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ determinism and restart (FR-HRM-PAY-31, FR-HRM-PAY-34)

    [Fact]
    public async Task FR_HRM_PAY_31_FR_HRM_PAY_34_a_killed_worker_resumes_from_its_checkpoints_and_every_hash_is_byte_identical_and_one_changed_input_changes_only_that_employee()
    {
        var (rig, run, employees, _, bonus) = await SixAsync();

        // 1. An uninterrupted calculation in two lanes of chunks of two.
        var asked = await rig.AskAsync(run);
        Assert.Equal(2, asked.GetProperty("lanes").GetInt32());
        await rig.RunJobsAsync();
        var summary = await rig.SummaryAsync(run);
        Assert.Equal("calculated", summary.GetProperty("status").GetString());
        Assert.Equal(6, summary.GetProperty("calculation").GetProperty("calculated").GetInt32());
        Assert.Equal(100m, summary.GetProperty("calculation").GetProperty("percent").GetDecimal());
        Assert.All(summary.GetProperty("calculation").GetProperty("lanes").EnumerateArray(), l => Assert.Equal("succeeded", l.GetProperty("status").GetString()));
        var first = await rig.HashesAsync(run);
        Assert.Equal(6, first.Count);
        Assert.Equal(6, first.Values.Select(v => v.Result).Distinct().Count()); // every employee's numbers differ
        var stages = summary.GetProperty("stageLog").EnumerateArray().Select(s => s.GetProperty("stage").GetString()).ToList();
        Assert.Contains("calculation_requested", stages);
        Assert.Contains("calculation_started", stages);
        Assert.Contains("calculated", stages);
        Assert.InRange(stages.Count(s => s == "lane_done"), 1, 2); // a lane finishing after the run was finished by the other records nothing
        Assert.All(summary.GetProperty("stageLog").EnumerateArray(), s => Assert.Equal(rig.Manager, s.GetProperty("by").GetGuid()));

        // 2. The same snapshots calculated again (recalculation from the stored snapshots): byte-identical results and traces, as new row versions.
        await rig.AskAsync(run, "recalculate", new { freshSnapshot = false });
        await rig.RunJobsAsync();
        Assert.Equal("calculated", (await rig.SummaryAsync(run)).GetProperty("status").GetString());
        var again = await rig.HashesAsync(run);
        foreach (var (employment, h) in first)
        {
            Assert.Equal(h.Snapshot, again[employment].Snapshot);
            Assert.Equal(h.Result, again[employment].Result);
            Assert.Equal(h.Trace, again[employment].Trace);
            Assert.Equal(h.ResultId, again[employment].ResultId); // the same row, a new version
            Assert.True(again[employment].RowVersion > h.RowVersion);
        }

        // Nothing changed, so the change log has no new entry for the results (it records changed fields only; the history of a recalculation that changes
        // amounts is checked in Build_11_...).
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_results' and entity_id = $1 and action = 'update'", Ct, first[employees[0]].ResultId));

        // 3. Fresh snapshots, and the worker killed in the middle of a chunk: a second worker takes the lanes over after the lease and finishes.
        var gate = new PauseGate(pauseOnCall: 2);
        await rig.AskAsync(run, "recalculate", new { freshSnapshot = true });
        await using (var jobs = await rig.JobsAsync())
        {
            var a = jobs.Worker(extra: gate.Register);
            Assert.True((await jobs.PollAsync(a, Ct)).Started >= 1);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await a.KillAsync();
            var midway = await rig.SummaryAsync(run);
            Assert.Equal("created", midway.GetProperty("status").GetString());
            Assert.True(midway.GetProperty("calculation").GetProperty("running").GetBoolean());
            Assert.True(midway.GetProperty("calculation").GetProperty("waiting").GetInt32() > 0, midway.ToString());

            _h.Clock.Advance(TimeSpan.FromSeconds(61));
            var b = jobs.Worker();
            await jobs.RunUntilIdleAsync(b, Ct);
            await PayRunRig.AssertNoFailedLaneAsync(jobs);
            Assert.True(await jobs.ScalarAsync<int>("select max(r.attempt) from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'hrm.payroll_calculate' and r.parameters->>'pay_run_id' = $1", Ct, null, run.ToString()) >= 2);
        }

        var resumed = await rig.SummaryAsync(run);
        Assert.Equal("calculated", resumed.GetProperty("status").GetString());
        Assert.Equal(6, resumed.GetProperty("employeeCount").GetInt32()); // counts equal the uninterrupted run
        var afterRestart = await rig.HashesAsync(run);
        Assert.Equal(first.Keys.Order(), afterRestart.Keys.Order());
        foreach (var (employment, h) in first)
        {
            Assert.Equal(h.Snapshot, afterRestart[employment].Snapshot);
            Assert.Equal(h.Result, afterRestart[employment].Result);
            Assert.Equal(h.Trace, afterRestart[employment].Trace);
        }

        Assert.Equal(6L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_results where pay_run_id = $1", Ct, run)); // never twice
        Assert.Equal(6L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_traces t join hrm.pay_results r on r.id = t.result_id where r.pay_run_id = $1", Ct, run));

        // 4. One changed input (a bonus for one employee): only that employee's snapshot, result and trace change.
        var added = await _h.PostAsync(rig.Manager, "/api/hrm/pay-inputs", new { items = new[] { new { employmentId = employees[3], payPeriodId = rig.Period.Id, componentId = bonus.Id, amount = 2_500m, sourceType = "bonus" } } }, Ct);
        Assert.True(added.Status == HttpStatusCode.Created, added.Body);
        await rig.AskAsync(run, "recalculate", new { freshSnapshot = true });
        await rig.RunJobsAsync();
        var changed = await rig.HashesAsync(run);
        foreach (var (employment, h) in first)
        {
            var same = employment != employees[3];
            Assert.Equal(same, h.Snapshot == changed[employment].Snapshot);
            Assert.Equal(same, h.Result == changed[employment].Result);
            Assert.Equal(same, h.Trace == changed[employment].Trace);
        }

        var lines = await _h.GetAsync(rig.Manager, $"{Runs}/{run}/results?employmentId={employees[3]}", Ct);
        Assert.Contains(lines.Json.GetProperty("items")[0].GetProperty("lines").EnumerateArray(), l => l.GetProperty("componentCode").GetString() == bonus.Code && l.GetProperty("amount").GetDecimal() == 2_500m);
    }

    // ------------------------------------------------------------------ recalculation of a subset, lock (Build item 11, FR-HRM-PAY-33)

    [Fact]
    public async Task Build_11_FR_HRM_PAY_33_a_recalculation_of_one_employee_replaces_only_that_result_and_a_locked_run_is_never_recalculated()
    {
        var (rig, run, employees, basic, _) = await SixAsync(chunk: null, lanes: null);
        await rig.AskAsync(run);
        await rig.RunJobsAsync();
        var before = await rig.HashesAsync(run);

        // A salary change of one employee from the period start, then a recalculation of that employee only, from a fresh snapshot.
        var raised = await _h.PostAsync(rig.Manager, $"/api/hrm/employments/{employees[4]}/compensation", new { structureId = await _h.ScalarAsync<Guid>("select structure_id from hrm.compensation_assignments where employment_id = $1", Ct, employees[4]), validFrom = PayRunRig.Iso(rig.Period.Start), reasonCode = "increment", lines = new[] { new { componentId = basic.Id, amount = 90_000m } } }, Ct);
        Assert.True(raised.Status == HttpStatusCode.Created, raised.Body);
        var unknown = await _h.PostAsync(rig.Manager, $"{Runs}/{run}/recalculate", new { employmentIds = new[] { Guid.NewGuid() }, freshSnapshot = true, acceptOpenItems = true }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        var asked = await rig.AskAsync(run, "recalculate", new { employmentIds = new[] { employees[4] }, freshSnapshot = true, acceptOpenItems = true });
        Assert.Equal(1, asked.GetProperty("toCalculate").GetInt32());
        Assert.Equal("created", (await rig.SummaryAsync(run)).GetProperty("status").GetString()); // not calculated until every employee has a result again
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status); // one calculation at a time
        await rig.RunJobsAsync();
        var after = await rig.HashesAsync(run);
        Assert.Equal("calculated", (await rig.SummaryAsync(run)).GetProperty("status").GetString());
        foreach (var (employment, h) in before)
        {
            Assert.Equal(employment != employees[4], h.Result == after[employment].Result);
            Assert.Equal(employment != employees[4], h.RowVersion == after[employment].RowVersion); // the others are not touched
        }

        Assert.Equal(90_000m, await _h.ScalarAsync<decimal>("select l.amount from hrm.pay_result_lines l join hrm.pay_results r on r.id = l.result_id where r.pay_run_id = $1 and r.employment_id = $2 and l.component_id = $3", Ct, run, employees[4], basic.Id));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_result_lines l join hrm.pay_results r on r.id = l.result_id where r.pay_run_id = $1 and r.employment_id = $2 and l.component_id = $3", Ct, run, employees[4], basic.Id));
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.pay_run.calculated' and aggregate_id = $1", Ct, run));
        // The replaced lines and the earlier result stay in the change log (history kept), the lines as deletions, the result as an update.
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_result_lines' and action = 'delete' and company_id = $1", Ct, rig.Company) >= 1);
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_results' and action = 'update' and entity_id = $1", Ct, after[employees[4]].ResultId) >= 1);

        // Once the run is locked (S1.38's stage; set here directly) it is never recalculated, and the database refuses its results' change (PAY-33).
        await _h.ExecAsync("update hrm.pay_runs set status = 'locked' where id = $1", Ct, run);
        var locked = await _h.PostAsync(rig.Manager, $"{Runs}/{run}/recalculate", new { acceptOpenItems = true }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, locked.Status);
        Assert.Equal("errors.hrm.pay.run.not_calculable", locked.Json.GetProperty("title").GetString());
        var e = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.pay_traces set engine_version = 'x' where result_id = $1", Ct, after[employees[0]].ResultId));
        Assert.Equal("ck_pay_run_locked", e.ConstraintName);
    }

    // ------------------------------------------------------------------ cancel and progress (FR-HRM-PAY-34)

    [Fact]
    public async Task FR_HRM_PAY_34_cancel_stops_the_lanes_at_their_next_chunk_keeps_what_was_saved_and_a_new_calculation_goes_on_from_there()
    {
        var (rig, run, _, _, _) = await SixAsync(chunk: 1, lanes: 1);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Manager, $"{Runs}/{run}/cancel-calculation", new { }, Ct)).Status); // nothing to cancel
        var gate = new PauseGate(pauseOnCall: 3);
        await rig.AskAsync(run);
        await using (var jobs = await rig.JobsAsync())
        {
            var worker = jobs.Worker(extra: gate.Register);
            Assert.Equal(1, (await jobs.PollAsync(worker, Ct)).Started);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            // Progress while the third chunk waits: two employees saved.
            var progress = (await rig.SummaryAsync(run)).GetProperty("calculation");
            Assert.True(progress.GetProperty("running").GetBoolean());
            Assert.Equal(6, progress.GetProperty("employees").GetInt32());
            Assert.Equal(2, progress.GetProperty("calculated").GetInt32());
            Assert.Equal(4, progress.GetProperty("waiting").GetInt32());
            Assert.Equal(Math.Round(2 * 100m / 6, 1), progress.GetProperty("percent").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Manager, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct)).Status);

            // A cancel waits for the chunk that is running to commit (the job's row is held by the chunk), then the lane stops at its next chunk.
            var asking = _h.PostAsync(rig.Manager, $"{Runs}/{run}/cancel-calculation", new { }, Ct);
            await Task.Delay(500, Ct);
            gate.Release.TrySetResult();
            var cancel = await asking;
            Assert.True(cancel.Status == HttpStatusCode.OK, cancel.Body);
            Assert.Equal(1, cancel.Json.GetProperty("cancelled").GetInt32());
            await worker.WhenIdleAsync();
            Assert.Equal("cancelled", await jobs.ScalarAsync<string>("select r.status from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'hrm.payroll_calculate' and r.parameters->>'pay_run_id' = $1", Ct, null, run.ToString()));
        }

        // The chunk that was running when the cancel came committed; the lane stopped at the next one, and the stop is in the stage log.
        var stopped = await rig.SummaryAsync(run);
        Assert.Equal("created", stopped.GetProperty("status").GetString());
        Assert.False(stopped.GetProperty("calculation").GetProperty("running").GetBoolean());
        var saved = stopped.GetProperty("calculation").GetProperty("calculated").GetInt32();
        Assert.Equal(3, saved); // the two before and the chunk that was running
        var stages = stopped.GetProperty("stageLog").EnumerateArray().Select(s => s.GetProperty("stage").GetString()).ToList();
        Assert.Contains("calculation_cancel_requested", stages);
        Assert.Contains("calculation_cancelled", stages);
        var kept = await rig.HashesAsync(run);

        // A new calculation goes on with the employees left; the saved ones are not calculated again.
        Assert.Equal(6 - saved, (await rig.AskAsync(run)).GetProperty("toCalculate").GetInt32());
        await rig.RunJobsAsync();
        Assert.Equal("calculated", (await rig.SummaryAsync(run)).GetProperty("status").GetString());
        var all = await rig.HashesAsync(run);
        Assert.Equal(6, all.Count);
        foreach (var (employment, h) in kept)
        {
            Assert.Equal(h.RowVersion, all[employment].RowVersion);
        }
    }
}
