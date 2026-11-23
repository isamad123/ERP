using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Jobs;
using static Erp.IntegrationTests.Workflow.WorkflowRig;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// The loops and operator commands of earlier slices as jobs (slice S1.11 Build item 5): licence refresh (S1.09b),
/// approval escalation (S1.10b), audit anchor copy and the nightly chain check with the clock check (S1.07; R-012, R-014),
/// monthly partitions ahead (spec 03 section 8, FR-HRM-DAT-04), expiry of maker-checker requests (S1.06) and the daily
/// clean-up (P3). Each run goes through the real worker, scheduler and runner.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class MaintenanceJobTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await JobRig.StartAsync(fx, "jm", Ct);

    public async ValueTask DisposeAsync()
    {
        await fx.SetDaysAfterEndAsync(_rig.Code, null, CancellationToken.None);
        await _rig.DisposeAsync();
    }

    private static async Task<Guid> RunAsync(JobRig rig, string job)
    {
        var run = await rig.QueueAsync(job, ct: Ct);
        await rig.RunUntilIdleAsync(rig.Worker(), Ct);
        return run;
    }

    [Fact]
    public async Task S111a_FR_KRN_ENT_04_the_licence_refresh_job_records_the_state_change_as_the_hourly_loop_did()
    {
        await fx.SetDaysAfterEndAsync(_rig.Code, 20, Ct); // read-only by the dates
        var run = await RunAsync(_rig, KernelJobs.LicenceRefresh); // runs in read-only mode too (ruling Q6)

        Assert.Equal("succeeded", await _rig.StatusAsync(run, Ct));
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        Assert.Equal("read_only", await TenancyFixture.ScalarTextAsync(cp, "select e.status from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1 and e.status <> 'superseded'", Ct, _rig.Code));
        Assert.True(await _rig.ScalarAsync<long>("select count(*) from meta.outbox_messages where event_type = 'kernel.licence.state_changed' and payload->>'to' = 'read_only'", Ct) == 1, await _rig.DumpAsync(Ct));
        Assert.Contains(await _rig.LogsAsync(run, Ct), l => l.Contains("state read_only", StringComparison.Ordinal));

        // Run again: nothing is recorded twice.
        await RunAsync(_rig, KernelJobs.LicenceRefresh);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from meta.outbox_messages where event_type = 'kernel.licence.state_changed' and payload->>'to' = 'read_only'", Ct));
    }

    [Fact]
    public async Task S111a_FR_XM_32_the_escalation_job_escalates_as_the_loop_did_and_never_approves()
    {
        var w = await ForAsync(fx);
        w.H.Clock.Reset();
        try
        {
            var rig = await JobRig.ForExistingAsync(fx, w.H.Code, w.H.Clock, Ct);
            await using var _ = rig;
            var admin = await w.AdminAsync(Ct);
            var (requester, lonely) = (await w.UserAsync(Ct, "business_user"), await w.UserAsync(Ct, "approver"));
            await w.ActivateRouteAsync(admin, Expense, Route(Step("one", UserApprover(lonely))), Ct);
            var instance = InstanceOf(await w.StartAsync(requester, Expense, await w.H.DefaultCompanyAsync(Ct), new { amount = "1" }, ct: Ct));
            var task = Assert.Single(await w.OpenTasksAsync(instance, Ct));

            w.H.Clock.Advance(TimeSpan.FromDays(10));
            var run = await RunAsync(rig, KernelJobs.WorkflowEscalate);

            Assert.Equal("succeeded", await rig.StatusAsync(run, Ct));
            Assert.Equal("pending", await w.TaskValueAsync<string>(task, "status", Ct));
            Assert.NotNull(await w.TaskTimeAsync(task, "escalated_at", Ct));
            Assert.Single(await w.HistoryAsync(instance, Ct), h => h.Event == "escalation_not_possible");
            Assert.Equal("running", await w.StatusAsync(instance, Ct));
            Assert.Contains(await rig.LogsAsync(run, Ct), l => l.Contains("with nobody above", StringComparison.Ordinal));
        }
        finally
        {
            w.H.Clock.Reset();
        }
    }

    [Fact]
    public async Task S111a_FR_HRM_DAT_04_the_partition_job_creates_monthly_partitions_three_months_ahead_and_reports_a_month_already_in_the_default_partition()
    {
        // Partitions exist to 2027-12 (codegen-settings.yaml). In November 2027 the job adds January and February 2028.
        _rig.Clock.Advance(new DateTimeOffset(2027, 11, 15, 6, 0, 0, TimeSpan.Zero) - _rig.Clock.UtcNow);
        var run = await RunAsync(_rig, KernelJobs.PartitionsEnsure);
        Assert.Equal("succeeded", await _rig.StatusAsync(run, Ct));
        foreach (var partition in new[] { "job.run_logs_p2028_01", "job.run_logs_p2028_02", "aud.change_log_p2028_02", "aud.access_log_p2028_02" })
        {
            Assert.True(await _rig.ScalarAsync<bool>("select to_regclass($1) is not null", Ct, null, partition), partition);
        }

        Assert.False(await _rig.ScalarAsync<bool>("select to_regclass('job.run_logs_p2028_03') is not null", Ct));

        // A row of March 2028 already in the default partition: that month is not created and the run says why.
        await _rig.ExecAsync("insert into job.run_logs (id, run_id, level, message, created_at, created_by) values ($1, $1, 'info', 'early', '2028-03-10 00:00:00+00', $1)", Ct, null, Guid.CreateVersion7());
        _rig.Clock.Advance(TimeSpan.FromDays(60)); // January 2028: this month and the next three
        var second = await RunAsync(_rig, KernelJobs.PartitionsEnsure);
        Assert.Equal("errors.job.partition_default_has_rows", await _rig.ScalarAsync<string>("select error from job.runs where id = $1", Ct, null, second));
        Assert.True(await _rig.ScalarAsync<bool>("select to_regclass('job.run_logs_p2028_04') is not null", Ct));
        Assert.False(await _rig.ScalarAsync<bool>("select to_regclass('job.run_logs_p2028_03') is not null", Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.PartitionProblem, Ct));
        Assert.Contains(await _rig.LogsAsync(second, Ct), l => l.Contains("job.run_logs: run_logs_p2028_03 default_has_rows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S111a_R_012_the_nightly_audit_check_verifies_the_previous_day_and_a_broken_chain_fails_with_a_critical_alert()
    {
        // A change made today, checked by the job tomorrow (the previous day).
        var company = await _rig.ScalarAsync<Guid>("select id from org.companies order by created_at limit 1", Ct);
        await _rig.QueueAsync("test.write", new { company_id = company, code = "AUD1" }, ct: Ct);
        await _rig.RunUntilIdleAsync(_rig.Worker(), Ct);
        _rig.Clock.Advance(TimeSpan.FromDays(1));

        var intact = await RunAsync(_rig, KernelJobs.AuditVerify);
        Assert.Equal("succeeded", await _rig.StatusAsync(intact, Ct));
        Assert.Contains(await _rig.LogsAsync(intact, Ct), l => l.Contains("intact", StringComparison.Ordinal));

        // The owner, with the triggers off, changes yesterday's row: the job fails and raises the critical alert.
        await _rig.ExecAsync("""
            alter table aud.change_log disable trigger user;
            update aud.change_log set changes = '[]' where entity_type = 'org.branches';
            alter table aud.change_log enable trigger user;
            """, Ct);
        var broken = await RunAsync(_rig, KernelJobs.AuditVerify);
        Assert.True(await _rig.StatusAsync(broken, Ct) != "succeeded", await _rig.DumpAsync(Ct));
        Assert.Equal("errors.job.audit_chain_broken", await _rig.ScalarAsync<string>("select error from job.runs where id = $1", Ct, null, broken));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.AuditVerifyFailed, Ct));
    }

    [Fact]
    public async Task S111a_P6_the_anchor_copy_job_acts_as_the_configured_platform_user()
    {
        var run = await RunAsync(_rig, KernelJobs.AuditAnchorsPublish);
        Assert.Equal("succeeded", await _rig.StatusAsync(run, Ct));
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        Assert.True(await TenancyFixture.CountAsync(cp, """
            select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id
            where t.code = $1 and a.action = 'audit.anchors_publish'
            """, Ct, _rig.Code) >= 1);
    }

    [Fact]
    public async Task S111a_FR_ADM_ROL_04_the_maker_checker_job_expires_requests_older_than_the_setting_and_keeps_newer_ones()
    {
        var h = await Access.AccessHarness.ForAsync(fx);
        var requester = await h.UserAsync(Ct, _rig.Code, "tenant_admin");
        var (old, recent) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await _rig.ExecAsync("""
            insert into iam.maker_checker_requests (id, action_type, subject_type, subject_id, payload, requested_by, status, created_at, created_by, updated_at, updated_by)
            values ($1, 'role.permissions', 'iam.roles', $3, '{}', $4, 'pending', $5, $4, $5, $4),
                   ($2, 'role.permissions', 'iam.roles', $3, '{}', $4, 'pending', $6, $4, $6, $4)
            """, Ct, null, old, recent, Guid.CreateVersion7(), requester, _rig.Clock.UtcNow.AddDays(-8), _rig.Clock.UtcNow.AddDays(-1));

        var run = await RunAsync(_rig, KernelJobs.MakerCheckerExpire);
        Assert.Equal("succeeded", await _rig.StatusAsync(run, Ct));
        Assert.Equal("expired", await _rig.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, null, old));
        Assert.Equal("pending", await _rig.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, null, recent));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.maker_checker_expired' and aggregate_id = $1", Ct, null, old));
    }

    [Fact]
    public async Task S111a_P3_the_clean_up_job_removes_old_finished_runs_and_delivered_events_and_keeps_the_rest()
    {
        var oldRun = await _rig.QueueAsync("test.count", ct: Ct);
        var keptRun = await _rig.QueueAsync("test.count", ct: Ct);
        await _rig.RunUntilIdleAsync(_rig.Worker(), Ct);
        await _rig.ExecAsync("update job.runs set finished_at = $2 where id = $1", Ct, null, oldRun, _rig.Clock.UtcNow.AddDays(-31));
        var (oldEvent, newEvent, waiting) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await _rig.ExecAsync("""
            insert into wf.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, published_at)
            values ($1, $4, 'test.old', 'test', $1, '{}', $4), ($2, $5, 'test.new', 'test', $2, '{}', $5), ($3, $4, 'test.waiting', 'test', $3, '{}', null)
            """, Ct, null, oldEvent, newEvent, waiting, _rig.Clock.UtcNow.AddDays(-31), _rig.Clock.UtcNow.AddDays(-1));

        var run = await RunAsync(_rig, KernelJobs.Purge);
        Assert.True(await _rig.StatusAsync(run, Ct) == "succeeded", await _rig.DumpAsync(Ct));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from job.runs where id = $1", Ct, null, oldRun));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from job.runs where id = $1", Ct, null, keptRun));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from wf.outbox_messages where id = $1", Ct, null, oldEvent));
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from wf.outbox_messages where id = any($1)", Ct, null, new[] { newEvent, waiting })); // an event never delivered is never removed
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-UXA-05")]
    public async Task S121a_FR_ADM_UXA_05_the_clean_up_job_removes_form_drafts_past_their_keeping_time_and_keeps_the_rest()
    {
        var h = await Access.AccessHarness.ForAsync(_rig.Fixture);
        var user = await h.UserAsync(Ct, _rig.Code, "tenant_admin");
        var (expired, live) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await _rig.ExecAsync("""
            insert into cfg.form_drafts (id, user_id, form_key, record_id, data, expires_at, created_at, created_by, updated_at, updated_by)
            values ($1, $3, 'hrm.hire', null, '\x00', $4, $4, $3, $4, $3), ($2, $3, 'hrm.hire', $2, '\x00', $5, $4, $3, $4, $3)
            """, Ct, null, expired, live, user, _rig.Clock.UtcNow.AddDays(-1), _rig.Clock.UtcNow.AddDays(5));

        var run = await RunAsync(_rig, KernelJobs.Purge);
        Assert.True(await _rig.StatusAsync(run, Ct) == "succeeded", await _rig.DumpAsync(Ct));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from cfg.form_drafts where id = $1", Ct, null, expired));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from cfg.form_drafts where id = $1", Ct, null, live));
    }
}
