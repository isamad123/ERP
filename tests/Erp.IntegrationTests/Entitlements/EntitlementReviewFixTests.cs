using System.Net;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Entitlements;

namespace Erp.IntegrationTests.Entitlements;

/// <summary>
/// The fixes of the S1.09b fresh-session review (docs/reviews/S1.09b-review.md, CR-057), each against a real control plane and
/// real customer databases. Each test failed (or, for the races, could fail) on the merged S1.09b code.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EntitlementReviewFixTests(TenancyFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> CustomerAsync(string prefix)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..6];
        fx.Track(code);
        await fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code, null), null, Ct);
        return code;
    }

    private static EntitlementPayload Payload(string code, int version, DateOnly from, DateOnly? to, JsonObject? claims = null) =>
        new(code, version, "business", from, to, claims ?? TenancyFixture.Claims(["hrm"]));

    private async Task<long> CustomerCountAsync(string code, string sql, params object[] values)
    {
        await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
        return await TenancyFixture.CountAsync(owner, sql, Ct, values);
    }

    // ------------------------------------------------------------------ finding 1

    [Fact]
    public async Task FR_ADM_USR_08_ruling_B2_a_session_past_the_idle_time_does_not_hold_a_concurrent_seat()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = await CustomerAsync("ei");
        var (walkedAway, _, _) = await h.ActiveUserAsync(code, Ct); // signed in, then closed the browser without signing out
        var (_, _, newcomer) = await h.InviteAsync(code, Ct);
        await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
        {
            // Two hours without a refresh: four times the default idle time (30 minutes), well inside the absolute time (720).
            await TenancyFixture.ExecAsync(owner, "update iam.sessions set last_seen_at = last_seen_at - interval '2 hours' where user_id = $1", Ct, walkedAway);
            var live = await TenancyFixture.CountAsync(owner,
                "select count(distinct user_id) from iam.sessions where revoked_at is null and expires_at > now() and last_seen_at > now() - interval '30 minutes'", Ct);

            // One seat is free for the newcomer once the idle session is not counted.
            await fx.SetClaimAsync(code, "limits,users.concurrent", live + 1, Ct);
        }

        var activated = await h.PostAsync(code, "/api/iam/auth/activate", new { token = newcomer, newPassword = IdentityHarness.Password }, ct: Ct);
        Assert.True(activated.Status == HttpStatusCode.OK, $"the idle session still held a seat: {(int)activated.Status} {activated.Body}");

        // The newcomer's live session holds the last seat: the next person is refused.
        var (_, _, third) = await h.InviteAsync(code, Ct);
        var refused = await h.PostAsync(code, "/api/iam/auth/activate", new { token = third, newPassword = IdentityHarness.Password }, ct: Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("users.concurrent", refused.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ finding 2

    [Fact]
    public async Task FR_KRN_ENT_04_review_finding_2_migrate_stops_before_any_change_when_a_stored_entitlement_no_longer_passes_the_catalogue()
    {
        var code = await CustomerAsync("ec");
        var migrator = new FleetMigrator(fx.Options, fx.Secrets, fx.Clock, fx.Migrations);
        var before = await fx.SetClaimAsync(code, "limits,retired.limit", 5, Ct); // as if a later release had dropped a limit key
        try
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(fx.Admin, code, Ct));
            Assert.Contains(code, refused.Message, StringComparison.Ordinal);
            Assert.Contains("retired.limit", refused.Message, StringComparison.Ordinal);
            await using var cp = await fx.OpenControlPlaneAsync(Ct);
            Assert.Equal("ready", await TenancyFixture.ScalarTextAsync(cp,
                "select d.status from cp.tenant_databases d join cp.tenants t on t.id = d.tenant_id where t.code = $1 and d.env_type = 'production'", Ct, code));
            Assert.Single(await StoredEntitlementCheck.UnreadableAsync(cp, code, Ct));
        }
        finally
        {
            await fx.RestoreClaimsAsync(code, before, CancellationToken.None);
        }

        Assert.True(Assert.Single(await migrator.MigrateAsync(fx.Admin, code, Ct)).Succeeded);
    }

    // ------------------------------------------------------------------ finding 3

    [Fact]
    public async Task FR_KRN_ENT_04_KRN_AUD_02_review_finding_3_runs_at_the_same_moment_announce_each_change_once()
    {
        var code = await CustomerAsync("ex");
        var clock = new DayClock();
        var end = clock.Today.AddDays(40);
        await new EntitlementApplier(fx.Options, fx.Secrets, clock, fx.Migrations).ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 2, clock.Today, end)), Ct);

        var changes = 0;
        foreach (var day in new[] { end.AddDays(-29), end.AddDays(1), end.AddDays(15) })
        {
            clock.Set(day);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new LicenceMaintenance(fx.Options, fx.Secrets, clock).RunAsync(code, Ct)));
            changes++;
            Assert.Equal(changes, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = $1", LicenceEvents.StateChanged));
            Assert.Equal(changes, await CustomerCountAsync(code, "select count(*) from meta.outbox_messages where event_type = $1", LicenceEvents.StateChanged));
        }

        // An apply and licence runs at the same moment: the new version is announced once.
        var applying = new EntitlementApplier(fx.Options, fx.Secrets, clock, fx.Migrations).ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 3, clock.Today, null)), Ct);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => (Task)new LicenceMaintenance(fx.Options, fx.Secrets, clock).RunAsync(code, Ct)).Append(applying));
        await new LicenceMaintenance(fx.Options, fx.Secrets, clock).RunAsync(code, Ct);
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = $1 and details ->> 'version' = '3'", LicenceEvents.Applied));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from meta.local_entitlement where (payload -> 'payload' ->> 'version')::int = 3 and state = 'active'"));
    }

    // ------------------------------------------------------------------ finding 4

    [Fact]
    public async Task FR_ADM_OPS_07_ruling_Q16_review_finding_4_capabilities_show_limit_warnings_only_to_licence_readers()
    {
        var harness = await AccessHarness.ForAsync(fx);
        var code = await harness.ProvisionAsync("ew", Ct);
        var admin = await harness.UserAsync(Ct, code, "tenant_admin");
        var auditor = await harness.UserAsync(Ct, code, "auditor");
        var employee = await harness.UserAsync(Ct, code, "business_user");
        await fx.SetClaimAsync(code, "limits,companies", await CustomerCountAsync(code, LimitSql.Companies), Ct); // 100 percent

        foreach (var (user, role, sees) in new[] { (admin, "tenant_admin", true), (auditor, "auditor", true), (employee, "business_user", false) })
        {
            var capabilities = await harness.GetAsync(user, "/api/kernel/capabilities", Ct, code);
            Assert.Equal(HttpStatusCode.OK, capabilities.Status);
            var warnings = capabilities.Json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("key").GetString()).ToList();
            Assert.True(sees == warnings.Contains(LimitKeys.Companies), $"{role}: {capabilities.Body}");
        }
    }

    // ------------------------------------------------------------------ finding 5 and test gap T2

    [Fact]
    public async Task FR_KRN_ENT_05_FR_ADM_USR_08_review_finding_5_a_refused_seat_names_the_person_and_a_warning_is_announced_once_per_crossing()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = await CustomerAsync("eh");
        await h.ActiveUserAsync(code, Ct);
        var (first, _, firstToken) = await h.InviteAsync(code, Ct);
        var (second, _, secondToken) = await h.InviteAsync(code, Ct);
        var (refusedId, _, refusedToken) = await h.InviteAsync(code, Ct);
        var used = await CustomerCountAsync(code, LimitSql.NamedUsers);

        // Warning at 50 percent and room for exactly two more: the first seat crosses the threshold, the second does not.
        await h.SetSettingAsync(code, "cfg.limits.warning_percent", "50", null, Ct);
        await fx.SetClaimAsync(code, "limits,users.named", (2 * used) + 1, Ct);
        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync(code, "/api/iam/auth/activate", new { token = firstToken, newPassword = IdentityHarness.Password }, ct: Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync(code, "/api/iam/auth/activate", new { token = secondToken, newPassword = IdentityHarness.Password }, ct: Ct)).Status);
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = $1 and details ->> 'limit' = 'users.named'", LicenceEvents.LimitWarning));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = $1 and created_by = $2", LicenceEvents.LimitWarning, first));

        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync(code, "/api/iam/auth/activate", new { token = refusedToken, newPassword = IdentityHarness.Password }, ct: Ct)).Status);
        Assert.Equal(1, await CustomerCountAsync(code,
            "select count(*) from aud.security_events where event_type = $1 and details ->> 'holder' = $2 and created_by = $3 and actor = $2",
            LicenceEvents.LimitReached, refusedId.ToString(), refusedId));
        Assert.Equal(0, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = $1 and created_by = $2", LicenceEvents.LimitReached, second));
    }

    // ------------------------------------------------------------------ finding 7

    [Fact]
    public void FR_KRN_ENT_06_review_finding_7_a_customer_day_ends_at_its_own_midnight()
    {
        Assert.Equal(new DateTimeOffset(2026, 5, 31, 19, 0, 0, TimeSpan.Zero), UsageMeters.DayEndUtc(new DateOnly(2026, 5, 31), "Asia/Karachi"));
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), UsageMeters.DayEndUtc(new DateOnly(2026, 5, 31), "Etc/UTC"));
        Assert.Equal(new DateTimeOffset(2026, 5, 31, 19, 0, 0, TimeSpan.Zero), UsageMeters.DayEndUtc(new DateOnly(2026, 5, 31), "Not/AZone")); // falls back to Asia/Karachi
    }

    [Fact]
    public async Task FR_KRN_ENT_06_FR_ADM_OPS_07_review_finding_7_an_entitlement_applied_after_the_customer_midnight_belongs_to_the_next_month()
    {
        var code = await CustomerAsync("et");
        var today = new DayClock().Today;
        var month = new DateOnly(today.Year, today.Month, 1).AddMonths(2);
        var monthEnd = month.AddMonths(1).AddDays(-1);

        // 22:00 UTC on the month's last day is 03:00 on the 1st in Karachi: the customer's next month.
        var clock = new DayClock { UtcNow = new DateTimeOffset(monthEnd.ToDateTime(new TimeOnly(22, 0)), TimeSpan.Zero) };
        await new EntitlementApplier(fx.Options, fx.Secrets, clock, fx.Migrations).ApplyAsync(fx.Admin, code,
            fx.Sign(Payload(code, 2, monthEnd, null, TenancyFixture.Claims(["hrm"], new JsonObject { ["companies"] = 777 }))), Ct);

        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        var tenantId = Guid.Parse(await TenancyFixture.ScalarTextAsync(cp, "select id::text from cp.tenants where code = $1", Ct, code));
        var period = UsageMeters.Period(month);
        var next = UsageMeters.Period(month.AddMonths(1));
        await UsageMeters.RecordAsync(cp, null, tenantId, period, [new UsageReading(UsageMeters.Companies, 1)], clock.UtcNow, BaselineSeed.SystemUserId, Ct);
        await UsageMeters.RecordAsync(cp, null, tenantId, next, [new UsageReading(UsageMeters.Companies, 1)], clock.UtcNow, BaselineSeed.SystemUserId, Ct);

        var statements = await UsageMeters.StatementsAsync(cp, tenantId, period, next, "Asia/Karachi", Ct);
        Assert.NotEqual(777, statements.Single(s => s.Period == period).Lines.Single(l => l.Meter == UsageMeters.Companies).Limit);
        Assert.Equal(777, statements.Single(s => s.Period == next).Lines.Single(l => l.Meter == UsageMeters.Companies).Limit);
    }
}
