using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>
/// R-031 (slice S1.11f2): two provisioning runs for the same customer never work at the same time (FR-KRN-TEN-01, 02).
/// The timing is controlled, not left to chance: the first run stops inside a step until the test lets it go, and the
/// test waits until the second run's session is waiting on the customer's advisory lock in the control-plane database.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ProvisioningRaceTests(TenancyFixture fx)
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Holds the run after the given step until <see cref="Resume"/>; optionally fails it there.</summary>
    private sealed class PauseAfter(string step, bool failAfterwards = false) : IProvisioningProgress
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Resume() => _resume.TrySetResult();

        public async ValueTask StepCompletedAsync(string stepName, CancellationToken cancellationToken)
        {
            if (stepName != step)
            {
                return;
            }

            _reached.TrySetResult();
            await _resume.Task.WaitAsync(cancellationToken);
            if (failAfterwards)
            {
                throw new InvalidOperationException($"forced failure after {stepName}");
            }
        }
    }

    /// <summary>Records that the run was told another run holds the customer.</summary>
    private sealed class Watcher : IProvisioningProgress
    {
        public List<string> Waits { get; } = [];

        public ValueTask StepCompletedAsync(string stepName, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WaitingForOtherRunAsync(string code, CancellationToken cancellationToken)
        {
            lock (Waits)
            {
                Waits.Add(code);
            }

            return ValueTask.CompletedTask;
        }
    }

    private async Task<long> AuditCountAsync(string code, string action)
    {
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        return await TenancyFixture.CountAsync(cp,
            "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = $2", Ct, code, action);
    }

    private async Task<long> LockSessionsAsync(bool waitingOnly)
    {
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        return await TenancyFixture.CountAsync(cp, """
            select count(*) from pg_stat_activity
            where datname = current_database() and application_name = 'erp-provision-lock'
              and (not $1 or (wait_event_type = 'Lock' and wait_event = 'advisory'))
            """, Ct, waitingOnly);
    }

    /// <summary>Waits until a provisioning run is blocked on a customer's lock (the pattern of GroupRoleRaceTests).</summary>
    private async Task WaitUntilARunIsBlockedAsync()
    {
        var deadline = DateTimeOffset.UtcNow.Add(Patience);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await LockSessionsAsync(waitingOnly: true) > 0)
            {
                return;
            }

            await Task.Delay(20, Ct);
        }

        Assert.Fail("The second provisioning run never waited on the first one's lock.");
    }

    /// <summary>Waits until exactly <paramref name="expected"/> lock sessions are left (a closed session leaves the server's list shortly after).</summary>
    private async Task AssertLockSessionsSettleAtAsync(long expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        long count;
        while ((count = await LockSessionsAsync(waitingOnly: false)) != expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(expected, count);
    }

    /// <summary>Every login's stored password opens the customer database: the secret store and the server agree.</summary>
    private async Task AssertEveryLoginWorksAsync(string code)
    {
        foreach (var (kind, _) in TenantLogins.All)
        {
            await using var connection = await fx.OpenTenantAsync(code, kind, Ct);
            Assert.Equal(TenantLogins.LoginName(code, kind), connection.UserName);
        }
    }

    [Fact]
    public async Task FR_KRN_TEN_02_R031_a_second_run_for_the_same_customer_waits_and_then_stops_because_the_first_run_finished()
    {
        var code = "rw" + fx.Suffix;
        var first = new PauseAfter("migrate");
        var watcher = new Watcher();
        var runA = fx.ProvisionAsync(code, Ct, first);
        await first.Reached.WaitAsync(Patience, Ct);

        var runB = fx.ProvisionAsync(code, Ct, watcher);
        await WaitUntilARunIsBlockedAsync();
        Assert.False(runB.IsCompleted);
        Assert.Equal([code], watcher.Waits);

        first.Resume();
        var a = await runA.WaitAsync(Patience, Ct);
        var b = await runB.WaitAsync(Patience, Ct);
        Assert.False(a.AlreadyProvisioned);
        Assert.True(b.AlreadyProvisioned);
        Assert.Equal(a.TenantId, b.TenantId);
        Assert.Equal("active", (await fx.TenantAsync(code, Ct)).Status);

        // Each step ran once, by the first run only.
        foreach (var step in TenantProvisioner.Steps)
        {
            Assert.Equal(1, await AuditCountAsync(code, "tenant.provision." + step));
        }

        Assert.Equal(0, await AuditCountAsync(code, "tenant.provision.failed"));
        await AssertEveryLoginWorksAsync(code);
        await AssertLockSessionsSettleAtAsync(0); // both runs let their lock go
    }

    [Fact]
    public async Task FR_KRN_TEN_02_R031_a_second_run_waits_for_a_first_run_that_fails_and_then_resumes_and_completes_it()
    {
        var code = "rf" + fx.Suffix;
        var first = new PauseAfter("database", failAfterwards: true);
        var runA = fx.ProvisionAsync(code, Ct, first);
        await first.Reached.WaitAsync(Patience, Ct);

        var runB = fx.ProvisionAsync(code, Ct, new Watcher());
        await WaitUntilARunIsBlockedAsync();
        Assert.False(runB.IsCompleted);

        first.Resume();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runA.WaitAsync(Patience, Ct));
        Assert.Contains("forced failure", failure.Message, StringComparison.Ordinal);
        var b = await runB.WaitAsync(Patience, Ct);
        Assert.False(b.AlreadyProvisioned);

        var tenant = await fx.TenantAsync(code, Ct);
        Assert.Equal(("active", "ready", "active"), (tenant.Status, tenant.DatabaseStatus, tenant.Entitlements.Single().Status));
        Assert.Equal(1, await AuditCountAsync(code, "tenant.provision.failed"));
        Assert.Equal(2, await AuditCountAsync(code, "tenant.provision.database")); // once by each run, one after the other
        Assert.Equal(1, await AuditCountAsync(code, "tenant.provision.activate"));
        await using (var cp = await fx.OpenControlPlaneAsync(Ct))
        {
            Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.tenants where code = $1", Ct, code));
            Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.tenant_databases d join cp.tenants t on t.id = d.tenant_id where t.code = $1", Ct, code));
        }

        await AssertEveryLoginWorksAsync(code);
    }

    [Fact]
    public async Task FR_KRN_TEN_01_R031_another_customer_is_provisioned_while_a_run_holds_its_own_customer()
    {
        var (held, other) = ("rh" + fx.Suffix, "ro" + fx.Suffix);
        var first = new PauseAfter("register");
        var runA = fx.ProvisionAsync(held, Ct, first);
        await first.Reached.WaitAsync(Patience, Ct);

        var watcher = new Watcher();
        var result = await fx.ProvisionAsync(other, Ct, watcher).WaitAsync(Patience, Ct);
        Assert.False(result.AlreadyProvisioned);
        Assert.Empty(watcher.Waits);
        Assert.False(runA.IsCompleted);

        first.Resume();
        Assert.False((await runA.WaitAsync(Patience, Ct)).AlreadyProvisioned);
        Assert.Equal("active", (await fx.TenantAsync(held, Ct)).Status);
        Assert.Equal("active", (await fx.TenantAsync(other, Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_TEN_02_R031_a_waiting_run_can_be_cancelled_and_writes_nothing()
    {
        var code = "rc" + fx.Suffix;
        var first = new PauseAfter("register");
        var runA = fx.ProvisionAsync(code, Ct, first);
        await first.Reached.WaitAsync(Patience, Ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runB = fx.ProvisionAsync(code, stop.Token, new Watcher());
        await WaitUntilARunIsBlockedAsync();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runB.WaitAsync(Patience, Ct));
        await AssertLockSessionsSettleAtAsync(1); // only the first run's lock is left

        first.Resume();
        Assert.False((await runA.WaitAsync(Patience, Ct)).AlreadyProvisioned);
        Assert.Equal(1, await AuditCountAsync(code, "tenant.provision.register"));
        Assert.Equal(0, await AuditCountAsync(code, "tenant.provision.failed"));
        await AssertLockSessionsSettleAtAsync(0);
    }
}
