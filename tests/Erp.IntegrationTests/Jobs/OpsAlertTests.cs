using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Jobs;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// Operational alerts of slice S1.11a (Build item 6; FR-KRN-JOB-06; rulings Q8, Q9, P4; R-012): stuck runs and stopped
/// workers, overdue schedules, waiting events and failing consumers, critical audit events and unusual access to
/// sensitive data, each raised once per key per hour by the <c>kernel.ops_alerts</c> job.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class OpsAlertTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await JobRig.StartAsync(fx, "ja", Ct);

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task CheckAsync()
    {
        var run = await _rig.QueueAsync(KernelJobs.OpsAlertsCheck, ct: Ct);
        await _rig.RunUntilIdleAsync(_rig.Worker(), Ct);
        Assert.Equal("succeeded", await _rig.StatusAsync(run, Ct));
    }

    [Fact]
    public async Task S111a_FR_KRN_JOB_06_stuck_runs_stopped_workers_overdue_schedules_waiting_events_and_failing_consumers_raise_one_alert_each()
    {
        var now = _rig.Clock.UtcNow;
        var (stuck, orphan, late) = (await _rig.QueueAsync("test.count", ct: Ct), await _rig.QueueAsync("test.count", ct: Ct), await _rig.QueueAsync("test.count", ct: Ct));
        // Running for 2 minutes past a 60-second limit while its lease is renewed; running with a lease that expired 5 minutes ago.
        await _rig.ExecAsync("update job.runs set status = 'running', attempt = 1, started_at = $2, lease_owner = 'w1', lease_expires_at = $3 where id = $1", Ct, null, stuck, now.AddMinutes(-3), now.AddMinutes(1));
        await _rig.ExecAsync("update job.runs set status = 'running', attempt = 1, started_at = $2, lease_owner = 'w2', lease_expires_at = $3 where id = $1", Ct, null, orphan, now.AddMinutes(-6), now.AddMinutes(-5));
        // A scheduled slot 20 minutes old, still queued (its definition is off, so the worker does not start it here).
        await _rig.ExecAsync("update job.runs set scheduled_for = $2, next_attempt_at = $3 where id = $1", Ct, null, late, now.AddMinutes(-20), now.AddHours(1));
        // Events: one waiting 6 minutes, one failing for 2 hours.
        await _rig.ExecAsync("""
            insert into wf.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, next_attempt_at)
            values ($1, $3, 'test.wait', 'test', $1, '{}', $5), ($2, $4, 'test.fail', 'test', $2, '{}', $5)
            """, Ct, null, Guid.CreateVersion7(), Guid.CreateVersion7(), now.AddMinutes(-6), now.AddHours(-2), now.AddHours(1));
        await _rig.ExecAsync("update wf.outbox_messages set attempts = 5 where event_type = 'test.fail'", Ct);

        await CheckAsync();
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.JobStuck, Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.WorkerStopped, Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.JobOverdue, Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.EventBacklog, Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.EventConsumerFailing, Ct));
        Assert.Equal("""["tenant_admin"]""", await _rig.ScalarAsync<string>("select payload->>'audience' from job.outbox_messages where payload->>'kind' = 'job_stuck'", Ct));
        Assert.Equal("true", await _rig.ScalarAsync<string>("select payload->>'mandatory' from job.outbox_messages where payload->>'kind' = 'job_stuck'", Ct));

        // Five minutes later the problems remain, but within the hour nothing is raised again.
        _rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await CheckAsync();
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.JobStuck, Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.EventBacklog, Ct));
    }

    [Fact]
    public async Task S111a_R_012_HRM_PRV_05_critical_audit_events_and_unusual_access_to_sensitive_data_raise_alerts_to_the_right_people()
    {
        var now = _rig.Clock.UtcNow;
        // A tamper attempt recorded by the audit functions, and a user who opened 51 people's salaries in the last hour (setting 50).
        await _rig.ExecAsync("select aud.append_security_event($1, $2, 'aud.tamper_attempt', 'probe', 'aud.change_log', 'critical', '{}', $3)", Ct, null, Guid.CreateVersion7(), now.AddMinutes(-1), Guid.CreateVersion7());
        var viewer = Guid.CreateVersion7();
        for (var i = 0; i < 51; i++)
        {
            await _rig.ExecAsync("select aud.append_access($1, $2, $3, 'hrm.employments', $4, array['salary'], 'view', 1, null, $3)", Ct, null, Guid.CreateVersion7(), now.AddMinutes(-30), viewer, Guid.CreateVersion7());
        }

        var calm = Guid.CreateVersion7();
        await _rig.ExecAsync("select aud.append_access($1, $2, $3, 'hrm.employments', null, array['salary'], 'export', 40, null, $3)", Ct, null, Guid.CreateVersion7(), now.AddMinutes(-30), calm);

        await CheckAsync();
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.AuditCritical, Ct));
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.UnusualAccess, Ct));
        Assert.Equal(viewer.ToString(), await _rig.ScalarAsync<string>("select payload->'details'->>'user_id' from job.outbox_messages where payload->>'kind' = 'unusual_access'", Ct));
        Assert.Equal("""["tenant_admin", "auditor"]""", await _rig.ScalarAsync<string>("select payload->>'audience' from job.outbox_messages where payload->>'kind' = 'unusual_access'", Ct));

        // The next check starts after the events it has seen: the tamper attempt is not raised again.
        _rig.Clock.Advance(TimeSpan.FromMinutes(70));
        await CheckAsync();
        Assert.Equal(1, await _rig.AlertsAsync(AlertKinds.AuditCritical, Ct));

        // The threshold is a setting (ruling Q9): at 30, the export of 40 rows counts too.
        await _rig.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'aud.alerts.sensitive_people_per_hour', 'tenant', null, '30', now(), $2, now(), $2)
            """, Ct, null, Guid.CreateVersion7(), Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        await _rig.ExecAsync("select aud.append_access($1, $2, $3, 'hrm.employments', null, array['salary'], 'export', 40, null, $3)", Ct, null, Guid.CreateVersion7(), _rig.Clock.UtcNow.AddMinutes(-5), calm);
        await CheckAsync();
        Assert.Equal(calm.ToString(), await _rig.ScalarAsync<string>("select payload->'details'->>'user_id' from job.outbox_messages where payload->>'kind' = 'unusual_access' order by occurred_at desc limit 1", Ct));
    }
}
