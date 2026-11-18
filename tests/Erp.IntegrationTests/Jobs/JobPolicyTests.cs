using Erp.BuildingBlocks.Application.Audit;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Jobs;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// Which jobs run and whom they act as (slice S1.11a; rulings Q4, Q6, P9; risk R-009): unlicensed modules' jobs never run;
/// in read-only, locked and suspended mode only the jobs marked for the mode run; a scheduled job holds exactly the
/// permissions it declares; a requested run acts with the requester's rights, checked again when it starts; the audit log
/// shows source job with the run as correlation.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class JobPolicyTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await JobRig.StartAsync(fx, "jp", Ct);

    public async ValueTask DisposeAsync()
    {
        await fx.SetDaysAfterEndAsync(_rig.Code, null, CancellationToken.None);
        await _rig.DisposeAsync();
    }

    private async Task<IReadOnlyDictionary<string, long>> RunsByJobAsync()
    {
        await using var c = await _rig.OwnerAsync(null, Ct);
        await using var command = new Npgsql.NpgsqlCommand("select d.code, count(r.id) from job.definitions d left join job.runs r on r.definition_id = d.id and r.status = 'succeeded' where d.module_id = 'test' group by d.code", c);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var r = await command.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct))
        {
            counts[r.GetString(0)] = r.GetInt64(1);
        }

        return counts;
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_01_Q6_an_unlicensed_modules_job_never_runs_and_read_only_locked_and_suspended_run_only_the_jobs_marked_for_them()
    {
        await _rig.ExecAsync("update job.definitions set is_enabled = true where code in ('test.scheduled', 'test.health', 'test.suspended', 'test.unlicensed')", Ct);
        var worker = _rig.Worker();

        // Full: every licensed job runs; the job of an unlicensed feature (hrm.recruitment) does not even get a run.
        await _rig.RunUntilIdleAsync(worker, Ct);
        var full = await RunsByJobAsync();
        Assert.Equal(1, full["test.scheduled"]);
        Assert.Equal(1, full["test.health"]);
        Assert.Equal(1, full["test.suspended"]);
        Assert.Equal(0, full["test.unlicensed"]);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from job.runs r join job.definitions d on d.id = r.definition_id where d.code = 'test.unlicensed'", Ct));

        // Read-only (20 days after the end date): business jobs wait, the health jobs run.
        await fx.SetDaysAfterEndAsync(_rig.Code, 20, Ct);
        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await _rig.RunUntilIdleAsync(worker, Ct);
        var readOnly = await RunsByJobAsync();
        Assert.Equal(1, readOnly["test.scheduled"]);
        Assert.Equal(2, readOnly["test.health"]);
        Assert.Equal(2, readOnly["test.suspended"]);

        // Locked (60 days after): the same.
        await fx.SetDaysAfterEndAsync(_rig.Code, 60, Ct);
        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await _rig.RunUntilIdleAsync(worker, Ct);
        var locked = await RunsByJobAsync();
        Assert.Equal(1, locked["test.scheduled"]);
        Assert.Equal(3, locked["test.health"]);
        Assert.Equal(3, locked["test.suspended"]);

        // Suspended: only the jobs marked for it (the licence refresh in the kernel).
        await fx.SetDaysAfterEndAsync(_rig.Code, null, Ct);
        var lifecycle = new TenantLifecycle(fx.Options, fx.Clock);
        await lifecycle.SuspendAsync(fx.Admin, _rig.Code, "S1.11a test", Ct);
        try
        {
            _rig.Clock.Advance(TimeSpan.FromMinutes(1));
            await _rig.RunUntilIdleAsync(worker, Ct);
            var suspended = await RunsByJobAsync();
            Assert.Equal(1, suspended["test.scheduled"]);
            Assert.Equal(3, suspended["test.health"]);
            Assert.Equal(4, suspended["test.suspended"]);
        }
        finally
        {
            await lifecycle.ResumeAsync(fx.Admin, _rig.Code, "S1.11a test", CancellationToken.None);
        }

        // The kernel's licence refresh is the one product job marked for suspended customers (ruling Q6).
        var specs = JobManifest.Parse(JobManifest.KernelJson);
        Assert.Equal(["kernel.licence_refresh"], specs.Where(s => s.RunsIn.Contains(JobModes.Suspended)).Select(s => s.Code));
        Assert.DoesNotContain(specs, s => s.Code == "kernel.workflow_escalate" && s.RunsIn.Contains(JobModes.ReadOnly));
    }

    [Fact]
    public async Task S111a_R_009_a_scheduled_job_writes_with_its_declared_permissions_only_and_the_audit_log_shows_source_job_and_the_run()
    {
        var worker = _rig.Worker();
        var company = await _rig.ScalarAsync<Guid>("select id from org.companies order by created_at limit 1", Ct);
        var allowed = await _rig.QueueAsync("test.write", new { company_id = company, code = "JOB1" }, ct: Ct);
        var refused = await _rig.QueueAsync("test.write_denied", new { company_id = company, code = "JOB2" }, ct: Ct);
        await _rig.RunUntilIdleAsync(worker, Ct);

        // Declared org.branches.create: the branch is written, as the system user, by source job.
        Assert.True(await _rig.StatusAsync(allowed, Ct) == "succeeded", await _rig.DumpAsync(Ct));
        Assert.Equal("ok", TestJobs.Writes[allowed]);
        var branch = await _rig.ScalarAsync<Guid>("select id from org.branches where code = 'JOB1'", Ct);
        Assert.Equal(BaselineSeed.SystemUserId, await _rig.ScalarAsync<Guid>("select created_by from org.branches where id = $1", Ct, null, branch));
        Assert.Equal(AuditSources.Job, await _rig.ScalarAsync<string>("select source from aud.change_log where entity_id = $1", Ct, null, branch));
        Assert.Equal(ActorKinds.Job, await _rig.ScalarAsync<string>("select actor_kind from aud.change_log where entity_id = $1", Ct, null, branch));
        Assert.Equal(allowed, await _rig.ScalarAsync<Guid>("select correlation_id from aud.change_log where entity_id = $1", Ct, null, branch));
        Assert.Equal((BaselineSeed.SystemUserId, AuditSources.Job, ActorKinds.Job), TestJobs.Seen[allowed]);

        // Declared only org.branches.read: the same write is refused by the access engine; nothing is written.
        Assert.Equal("failed", await _rig.StatusAsync(refused, Ct));
        Assert.Equal("AccessDeniedException", TestJobs.Writes[refused]);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from org.branches where code = 'JOB2'", Ct));
    }

    [Fact]
    public async Task S111a_R_009_Q4_a_requested_run_acts_with_the_requesters_rights_and_fails_with_the_reason_when_they_lost_the_right()
    {
        var h = await AccessHarness.ForAsync(fx);
        var admin = await h.UserAsync(Ct, _rig.Code, "tenant_admin");
        var auditor = await h.UserAsync(Ct, _rig.Code, "auditor"); // reads jobs, may not start them or change companies
        var worker = _rig.Worker();
        var mine = await _rig.QueueAsync("test.requester", requestedBy: admin, actor: admin, ct: Ct);
        var lost = await _rig.QueueAsync("test.requester", requestedBy: auditor, actor: auditor, ct: Ct);
        await _rig.RunUntilIdleAsync(worker, Ct);

        Assert.Equal("succeeded", await _rig.StatusAsync(mine, Ct));
        Assert.Equal((admin, AuditSources.Job, ActorKinds.User), TestJobs.Seen[mine]);

        Assert.Equal("failed", await _rig.StatusAsync(lost, Ct));
        Assert.False(TestJobs.Starts.ContainsKey(lost)); // the handler never ran
        Assert.StartsWith(JobErrors.RequesterLostPermission, await _rig.ScalarAsync<string>("select error from job.runs where id = $1", Ct, null, lost), StringComparison.Ordinal);
    }
}
