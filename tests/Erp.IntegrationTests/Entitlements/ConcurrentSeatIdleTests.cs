using System.Net;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane;

namespace Erp.IntegrationTests.Entitlements;

/// <summary>
/// S1.09b review finding 1 (CR-057): a session that has passed the idle time (<c>iam.session.idle_minutes</c>) is ended
/// at its next refresh and no longer listed as live, so it must not hold a concurrent-user seat (FR-ADM-USR-08, ruling B2
/// "the distinct users with an open session").
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ConcurrentSeatIdleTests(TenancyFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR_ADM_USR_08_ruling_B2_a_session_past_the_idle_time_does_not_hold_a_concurrent_seat()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = "ei" + Guid.NewGuid().ToString("N")[..6];
        fx.Track(code);
        await fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code, null), null, Ct);

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
    }
}
