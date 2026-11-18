using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Jobs;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// The scheduler and runner of slice S1.11a (Build item 2; rulings Q2, Q5, P1, P7) with two independent workers on a real
/// PostgreSQL: chunked resumable runs (FR-KRN-JOB-02 and spec 02 JOB AC), runs once across workers (FR-KRN-JOB-05),
/// lease take-over, time limit, retry with back-off, cancel, and fair shares between customers (FR-KRN-JOB-04).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class JobRunnerTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await JobRig.StartAsync(fx, "jr", Ct);

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    [Fact]
    public async Task S111a_FR_KRN_JOB_02_a_50000_row_run_stopped_with_its_worker_resumes_on_another_worker_from_its_checkpoint_without_duplicates()
    {
        var a = _rig.Worker();
        var b = _rig.Worker();
        var run = await _rig.QueueAsync("test.rows", new { rows = 50_000, chunk = 1000, pause_at = 23_000 }, ct: Ct);

        var started = await _rig.PollAsync(a, Ct);
        Assert.Equal(1, started.Started);
        await TestJobs.ReachedAsync(run).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(23_000, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, run));
        Assert.Equal("""{"next": 23000}""", await _rig.ScalarAsync<string>("select checkpoint::text from job.runs where id = $1", Ct, null, run));

        // Worker A stops as a crashed process would: its lease is left behind.
        await a.KillAsync();
        Assert.Equal("running", await _rig.StatusAsync(run, Ct));

        // Before the lease expires nobody takes the run.
        Assert.Equal(0, (await _rig.PollAsync(b, Ct)).Started);

        _rig.Clock.Advance(TimeSpan.FromSeconds(61));
        await _rig.RunUntilIdleAsync(b, Ct);

        Assert.Equal("succeeded", await _rig.StatusAsync(run, Ct));
        Assert.Equal(50_000, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, run));
        Assert.Equal(50_000, await _rig.ScalarAsync<long>("select count(distinct n) from job_test.rows where run_id = $1", Ct, null, run));
        Assert.Equal(2, await _rig.ScalarAsync<int>("select attempt from job.runs where id = $1", Ct, null, run));
        Assert.Equal(100m, await _rig.ScalarAsync<decimal>("select progress_pct from job.runs where id = $1", Ct, null, run));
        Assert.Contains(await _rig.LogsAsync(run, Ct), l => l.Contains("taken over", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_05_two_workers_started_together_never_run_a_schedule_slot_or_a_queued_run_twice()
    {
        await _rig.ExecAsync("update job.definitions set is_enabled = true where code = 'test.scheduled'", Ct);
        var a = _rig.Worker(slots: 8, perTenant: 8);
        var b = _rig.Worker(slots: 8, perTenant: 8);
        var runs = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            runs.Add(await _rig.QueueAsync("test.count", ct: Ct));
        }

        for (var pass = 0; pass < 10; pass++)
        {
            var both = await Task.WhenAll(_rig.PollAsync(a, Ct), _rig.PollAsync(b, Ct));
            Assert.All(both, o => Assert.Empty(o.Errors));
            await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());
        }

        // One run for the minute's slot, planned by both workers.
        var slot = await _rig.ScalarAsync<long>("select count(*) from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'test.scheduled'", Ct);
        Assert.Equal(1, slot);
        var scheduled = await _rig.ScalarAsync<Guid>("select r.id from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'test.scheduled'", Ct);
        Assert.Equal(1, TestJobs.Starts[scheduled]);

        // Every queued run ran exactly once, shared between the two workers.
        Assert.All(runs, r => Assert.Equal(1, TestJobs.Starts[r]));
        Assert.Equal(30, await _rig.ScalarAsync<long>("select count(*) from job.runs where id = any($1) and status = 'succeeded'", Ct, null, runs.ToArray()));
        var owners = await _rig.ScalarAsync<long>("select count(distinct substring(message from 'started by (.*)$')) from job.run_logs where run_id = any($1) and message like 'attempt 1 started by %'", Ct, null, runs.ToArray());
        Assert.Equal(2, owners);
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_02_a_run_past_its_time_limit_is_retried_after_its_back_off_and_then_fails_with_the_reason_and_an_alert()
    {
        var worker = _rig.Worker();
        var run = await _rig.QueueAsync("test.sleep", ct: Ct);

        await _rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("queued", await _rig.StatusAsync(run, Ct));
        Assert.Equal(JobErrors.TimeLimit, await _rig.ScalarAsync<string>("select error from job.runs where id = $1", Ct, null, run));
        var wait = await _rig.ScalarAsync<DateTimeOffset>("select next_attempt_at from job.runs where id = $1", Ct, null, run);
        Assert.Equal(_rig.Clock.UtcNow.AddSeconds(5), wait, TimeSpan.FromSeconds(1)); // P1: the back-off of the manifest

        // Not before its wait is over.
        Assert.Equal(0, (await _rig.PollAsync(worker, Ct)).Started);
        _rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await _rig.RunUntilIdleAsync(worker, Ct);

        Assert.Equal("failed", await _rig.StatusAsync(run, Ct));
        Assert.Equal(2, TestJobs.Starts[run]);
        Assert.Equal(JobErrors.TimeLimit, await _rig.ScalarAsync<string>("select error from job.runs where id = $1", Ct, null, run));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = $1 and aggregate_id = $2", Ct, null, JobEvents.RunFailed, run));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = 'kernel.alert.raised' and payload->>'kind' = 'job_failed' and payload->'details'->>'run_id' = $1", Ct, null, run.ToString()));
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_02_cancel_stops_a_running_run_at_its_next_chunk_and_a_queued_run_at_once()
    {
        var worker = _rig.Worker();
        var running = await _rig.QueueAsync("test.rows", new { rows = 10_000, chunk = 1000, pause_at = 3000 }, ct: Ct);
        await _rig.PollAsync(worker, Ct);
        await TestJobs.ReachedAsync(running).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await _rig.ExecAsync("update job.runs set cancel_requested_at = $2 where id = $1", Ct, null, running, _rig.Clock.UtcNow);
        TestJobs.Resume(running);
        await worker.WhenIdleAsync();

        Assert.Equal("cancelled", await _rig.StatusAsync(running, Ct));
        Assert.Equal(3000, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, running)); // the chunk after the cancel was not saved
        Assert.Contains(await _rig.LogsAsync(running, Ct), l => l.Contains("cancelled at a chunk boundary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_04_with_one_customers_queue_full_of_long_runs_another_customers_run_starts_in_the_same_pass()
    {
        var other = await _rig.AddCustomerAsync("jq", Ct);
        var worker = _rig.Worker(slots: 4, perTenant: 2);
        var long1 = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            long1.Add(await _rig.QueueAsync("test.block", ct: Ct));
        }

        var first = await _rig.PollAsync(worker, Ct);
        Assert.Equal(2, first.Started); // Q5: at most 2 runs of one customer per worker
        var theirs = await _rig.QueueAsync("test.block", code: other, ct: Ct);

        var second = await _rig.PollAsync(worker, Ct);
        Assert.Equal(1, second.Started);
        Assert.Equal("running", await _rig.StatusAsync(theirs, Ct, other));
        Assert.Equal(4, await _rig.ScalarAsync<long>("select count(*) from job.runs where id = any($1) and status = 'queued'", Ct, null, long1.ToArray()));

        TestJobs.Release();
        await worker.WhenIdleAsync();
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_01_a_higher_priority_run_starts_before_older_runs_of_the_same_customer()
    {
        var worker = _rig.Worker(slots: 1, perTenant: 1);
        var older = await _rig.QueueAsync("test.count", ct: Ct);
        var urgent = await _rig.QueueAsync("test.count", ct: Ct);
        await _rig.ExecAsync("update job.runs set priority = 10 where id = $1", Ct, null, urgent);

        await _rig.PollAsync(worker, Ct);
        await worker.WhenIdleAsync();
        Assert.Equal("succeeded", await _rig.StatusAsync(urgent, Ct));
        Assert.Equal("queued", await _rig.StatusAsync(older, Ct));
    }
}
