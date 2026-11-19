#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Infrastructure;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Api.ControlPlane.Tenancy;
using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.Entitlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.IntegrationTests.Entitlements;

/// <summary>A clock a test sets to a day (the licence state follows the customer's day, slice S1.09b).</summary>
public sealed class DayClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>Noon of <paramref name="day"/> in Asia/Karachi (the customers' default time zone).</summary>
    public void Set(DateOnly day) => UtcNow = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(5)).ToUniversalTime();

    public DateOnly Today => LicenceStates.Today(UtcNow, LicenceStates.Zone("Asia/Karachi"));
}

/// <summary>
/// Slice S1.09b (build items 6 to 12): applying entitlements (FR-ADM-PLT-03, FR-KRN-TEN-02, spec 02 section 25 entitlement
/// testing), the module and feature gate (FR-KRN-ENT-03), the licence states (FR-KRN-ENT-04, FR-KRN-TEN-09, FR-KRN-AUD-02),
/// limits (FR-KRN-ENT-05, FR-KRN-IAM-18, FR-ADM-USR-08), usage metering (FR-KRN-ENT-06, FR-ADM-OPS-07) and the licence
/// endpoints' permissions (ruling Q16), against a real control plane and real customer databases.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EntitlementTests(TenancyFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ helpers

    private async Task<string> CustomerAsync(string prefix, IReadOnlyList<string>? modules = null)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..6];
        fx.Track(code);
        await fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code, modules), null, Ct);
        return code;
    }

    private static EntitlementPayload Payload(string code, int version, DateOnly from, DateOnly? to, JsonObject? claims = null) =>
        new(code, version, "business", from, to, claims ?? TenancyFixture.Claims(["hrm"]));

    private EntitlementApplier Applier(IClock? clock = null) => new(fx.Options, fx.Secrets, clock ?? fx.Clock, fx.Migrations);

    private LicenceMaintenance Maintenance(IClock? clock = null) => new(fx.Options, fx.Secrets, clock ?? fx.Clock);

    private Task<WebApplication> AppAsync(IClock clock) => fx.StartAppAsync(null, Ct, services =>
    {
        services.RemoveAll<IClock>();
        services.AddSingleton(clock);
    });

    private async Task<long> CpCountAsync(string sql, params object[] values)
    {
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        return await TenancyFixture.CountAsync(cp, sql, Ct, values);
    }

    private async Task<long> CustomerCountAsync(string code, string sql, params object[] values)
    {
        await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
        return await TenancyFixture.CountAsync(owner, sql, Ct, values);
    }

    private async Task<string> CustomerTextAsync(string code, string sql)
    {
        await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
        return await TenancyFixture.ScalarTextAsync(owner, sql, Ct);
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Warning)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, Ct);
        var warning = response.Headers.TryGetValues("Erp-Limit-Warning", out var values) ? string.Join(",", values) : null;
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct), warning);
    }

    private static string StateOf(string capabilities) => JsonDocument.Parse(capabilities).RootElement.GetProperty("state").GetString()!;

    private static object Company(string code, string status = "active") => new
    {
        code,
        nameI18n = new { en = "Company " + code },
        legalName = "Company " + code + " (Private) Limited",
        countryCode = "PK",
        baseCurrencyCode = "PKR",
        defaultLanguage = "en",
        costCenterModel = "none",
        status,
    };

    // ------------------------------------------------------------------ applying (Q3; spec 02 section 25)

    [Fact]
    public async Task FR_ADM_PLT_03_Spec02_25_apply_refuses_tampered_wrong_customer_old_version_unknown_or_revoked_key_broken_claims_and_expired_with_the_reason()
    {
        var code = await CustomerAsync("ea");
        var today = new DayClock().Today;
        var applier = Applier();

        var tampered = fx.Sign(Payload(code, 2, today, null)).Replace("\"business\"", "\"enterprise\"", StringComparison.Ordinal);
        using var revokedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var revokedId = "rv-" + code;
        await new ControlPlaneAdmin(fx.Options, fx.Secrets, fx.Clock).AddSigningKeyAsync(fx.Admin, revokedId, "entitlement", revokedKey.ExportSubjectPublicKeyInfo(), Ct);
        await using (var cp = await fx.OpenControlPlaneAsync(Ct))
        {
            await TenancyFixture.ExecAsync(cp, "update cp.signing_keys set revoked_at = now() where key_id = $1", Ct, revokedId);
        }

        var cases = new (string Name, string File, string Reason)[]
        {
            ("tampered", tampered, "signature is not valid"),
            ("wrong customer", fx.Sign(Payload(fx.CodeB, 2, today, null)), $"issued to '{fx.CodeB}'"),
            ("equal version", fx.Sign(Payload(code, 1, today, null)), "not higher than the last one applied (1)"),
            ("unknown signing key", fx.Sign(Payload(code, 2, today, null), keyId: "nobody-" + code), "is not a registered entitlement key"),
            ("revoked signing key", fx.Sign(Payload(code, 2, today, null), revokedKey, revokedId), "was revoked"),
            ("missing parent key", fx.Sign(Payload(code, 2, today, null, TenancyFixture.Claims(["hrm"], features: ["platform.base", "hrm.payroll"]))), "hrm.core"),
            ("unknown limit key", fx.Sign(Payload(code, 2, today, null, TenancyFixture.Claims(["hrm"], new JsonObject { ["users"] = 5 }))), "not a limit key"),
            ("expired", fx.Sign(Payload(code, 2, today.AddDays(-30), today.AddDays(-1))), "already expired"),
        };
        foreach (var (name, file, reason) in cases)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => applier.ApplyAsync(fx.Admin, code, file, Ct));
            Assert.True(error.Message.Contains(reason, StringComparison.Ordinal), $"{name}: {error.Message}");
        }

        Assert.Equal(1, await CpCountAsync("select count(*) from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1", code));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from meta.local_entitlement where (payload -> 'payload' ->> 'version')::int = 1"));
    }

    [Fact]
    public async Task FR_ADM_PLT_03_KRN_AUD_02_a_valid_entitlement_supersedes_the_last_with_history_licence_event_platform_audit_and_a_customer_security_event()
    {
        var code = await CustomerAsync("eb");
        var today = new DayClock().Today;
        var template = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(MigratedDatabases.Root, "src", "Host", "Erp.Host.Migrator", "EntitlementTemplates", "business.json"), Ct))!.AsObject();
        var draft = await Applier().DraftAsync(code, template, today, today.AddYears(1), Ct);
        Assert.Equal(2, draft["version"]!.GetValue<int>()); // the next version, pre-filled from the template (ruling Q2)
        Assert.True(JsonNode.DeepEquals(template["claims"], draft["claims"]));

        var result = await Applier().ApplyAsync(fx.Admin, code, fx.Sign(EntitlementPayload.FromJson(draft)), Ct);
        Assert.Equal((2, "active", 1), (result.Version, result.Status, result.Superseded));
        Assert.Equal(1, await CpCountAsync("select count(*) from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1 and e.version = 1 and e.status = 'superseded'", code));
        Assert.Equal(1, await CpCountAsync("select count(*) from cp.license_events l join cp.tenants t on t.id = l.tenant_id where t.code = $1 and l.event_type = 'entitlement.applied' and l.to_state = 'active'", code));
        Assert.Equal(1, await CpCountAsync("select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = 'entitlement.apply'", code));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from meta.local_entitlement where (payload -> 'payload' ->> 'version')::int = 2"));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = 'kernel.licence.applied' and details ->> 'version' = '2'"));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from meta.outbox_messages where event_type = 'kernel.licence.applied'"));

        var history = await Applier().HistoryAsync(code, Ct);
        Assert.Equal([2, 1], history.Select(h => h.Version));
        Assert.Equal(["active", "superseded"], history.Select(h => h.Status));
        Assert.Equal(fx.Admin.Email, history[0].AppliedBy);

        // The new limits are in force for requests at once, without a restart (host cache 0 in tests; 30 s by default).
        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));
        var licence = JsonDocument.Parse((await SendAsync(client, HttpMethod.Get, "/api/cfg/licence")).Body).RootElement;
        Assert.Equal(2, licence.GetProperty("version").GetInt32());
        Assert.Equal(today.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), licence.GetProperty("expiresAt").GetString());
        Assert.Contains(licence.GetProperty("limits").EnumerateArray(), l => l.GetProperty("key").GetString() == "users.named" && l.GetProperty("max").GetInt64() == 100);
    }

    // ------------------------------------------------------------------ module and feature gate (Q9)

    [Fact]
    public async Task FR_KRN_ENT_03_FR_KRN_TEN_02_adding_a_module_installs_its_schema_removing_it_keeps_the_data_and_an_unlicensed_endpoint_answers_like_a_missing_one()
    {
        var code = await CustomerAsync("ec", modules: []);
        var today = new DayClock().Today;
        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));
        var missing = await SendAsync(client, HttpMethod.Get, "/api/hrm/no-such-endpoint");
        Assert.Equal((HttpStatusCode.NotFound, string.Empty), (missing.Status, missing.Body));
        Assert.Equal(missing, await SendAsync(client, HttpMethod.Get, "/api/hrm/persons")); // not installed, not licensed

        var added = await Applier().ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 2, today, null)), Ct);
        Assert.Equal(["hrm"], added.ModulesInstalled);
        Assert.Equal(1, await CpCountAsync("select count(*) from cp.tenant_module_versions v join cp.tenant_databases d on d.id = v.tenant_database_id join cp.tenants t on t.id = d.tenant_id where t.code = $1 and v.module_id = 'hrm'", code));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/hrm/persons")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/hrm/pay-groups")).Status);

        await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
        await using (var tx = await owner.BeginTransactionAsync(Ct))
        {
            await new RowFactory(owner, tx, MigratedDatabases.Catalog).InsertAsync(MigratedDatabases.Catalog.Tables.Single(t => t.FullName == "hrm.persons"), null, Ct);
            await tx.CommitAsync(Ct);
        }

        // A feature that is no longer licensed: its endpoints disappear, the rest of the module stays (ruling Q9).
        await Applier().ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 3, today, null, TenancyFixture.Claims(["hrm"], features: ["platform.base", "hrm.core"]))), Ct);
        Assert.Equal(missing, await SendAsync(client, HttpMethod.Get, "/api/hrm/pay-groups"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/hrm/persons")).Status);
        var capabilities = JsonDocument.Parse((await SendAsync(client, HttpMethod.Get, "/api/kernel/capabilities")).Body).RootElement;
        Assert.Equal(["kernel", "hrm"], capabilities.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(["platform.base", "hrm.core"], capabilities.GetProperty("features").EnumerateArray().Select(m => m.GetString()));

        // The module removed: its endpoints answer like missing ones; its schema and data stay; licensed again, they are back.
        await Applier().ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 4, today, null, TenancyFixture.Claims([]))), Ct);
        Assert.Equal(missing, await SendAsync(client, HttpMethod.Get, "/api/hrm/persons"));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from hrm.persons"));
        await Applier().ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 5, today, null)), Ct);
        var persons = await SendAsync(client, HttpMethod.Get, "/api/hrm/persons");
        Assert.Equal(HttpStatusCode.OK, persons.Status);
        Assert.Single(JsonDocument.Parse(persons.Body).RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void FR_KRN_ENT_03_every_business_module_endpoint_declares_a_catalogue_feature_key_of_its_module()
    {
        var endpoints = ((IEndpointRouteBuilder)fx.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        var hrm = endpoints.Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith("api/hrm/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(hrm);
        Assert.All(hrm, e => Assert.Equal("hrm", e.Metadata.GetMetadata<EntitlementMetadata>()?.Module));
        Assert.All(endpoints.Select(e => e.Metadata.GetMetadata<EntitlementMetadata>()).OfType<EntitlementMetadata>(),
            m => Assert.True(EntitlementCatalogs.Keys.Features.TryGetValue(m.FeatureKey, out var key) && key.Module == m.Module, m.ToString()));
    }

    // ------------------------------------------------------------------ licence states (Q4 to Q6)

    [Fact]
    public async Task FR_KRN_ENT_04_KRN_TEN_09_each_state_is_reached_on_its_day_recorded_once_announced_and_a_scheduled_entitlement_restores_full_use()
    {
        var code = await CustomerAsync("ed");
        var clock = new DayClock();
        var start = clock.Today;
        var end = start.AddDays(40);
        await Applier(clock).ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 2, start, end)), Ct);
        await using var app = await AppAsync(clock);
        using var client = TenancyFixture.Client(app, TenancyFixture.HostOf(code), fx.Token(code));

        var expected = 0;
        foreach (var (day, state) in new (DateOnly, string)[] { (end.AddDays(-30), "active"), (end.AddDays(-29), "warning"), (end, "warning"), (end.AddDays(1), "grace"), (end.AddDays(15), "read_only"), (end.AddDays(45), "locked") })
        {
            clock.Set(day);
            Assert.Equal(state, StateOf((await SendAsync(client, HttpMethod.Get, "/api/kernel/capabilities")).Body)); // from the dates, before the loop runs
            var first = (await Maintenance(clock).RunAsync(code, Ct)).Single();
            var again = (await Maintenance(clock).RunAsync(code, Ct)).Single();
            Assert.Equal((state, state), (first.State, again.State));
            Assert.False(again.Changed);
            expected += first.Changed ? 1 : 0;
            Assert.Equal(state, await CustomerTextAsync(code, "select state from meta.local_entitlement"));
            Assert.Equal(1, await CpCountAsync("select count(*) from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1 and e.version = 2 and e.status = $2", code, state));
        }

        Assert.Equal(4, expected); // warning, grace, read_only, locked: each recorded once
        Assert.Equal(4, await CpCountAsync("select count(*) from cp.license_events l join cp.tenants t on t.id = l.tenant_id where t.code = $1 and l.event_type = 'state.changed'", code));
        Assert.Equal(4, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = 'kernel.licence.state_changed'"));
        Assert.Equal(4, await CustomerCountAsync(code, "select count(*) from meta.outbox_messages where event_type = 'kernel.licence.state_changed'"));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = 'kernel.licence.state_changed' and details ->> 'to' = 'locked' and severity = 'warning'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, "/api/org/companies", Company("LK" + code))).Status);

        // A new entitlement with a future start date waits as scheduled; on its day it is in force at once (no loop, no
        // restart) and the loop then records the activation once (ruling Q3).
        var renewal = await Applier(clock).ApplyAsync(fx.Admin, code, fx.Sign(Payload(code, 3, clock.Today.AddDays(3), null)), Ct);
        Assert.Equal("scheduled", renewal.Status);
        Assert.Equal("locked", StateOf((await SendAsync(client, HttpMethod.Get, "/api/kernel/capabilities")).Body));
        clock.Set(clock.Today.AddDays(3));
        Assert.Equal("active", StateOf((await SendAsync(client, HttpMethod.Get, "/api/kernel/capabilities")).Body));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, "/api/org/companies", Company("RN" + code))).Status);
        var activation = (await Maintenance(clock).RunAsync(code, Ct)).Single();
        Assert.Equal((3, "active", "locked"), (activation.Activated, activation.State, activation.PreviousState));
        Assert.Null((await Maintenance(clock).RunAsync(code, Ct)).Single().Activated);
        Assert.Equal(1, await CpCountAsync("select count(*) from cp.license_events l join cp.tenants t on t.id = l.tenant_id where t.code = $1 and l.event_type = 'entitlement.activated'", code));
        Assert.Equal(1, await CpCountAsync("select count(*) from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1 and e.version = 2 and e.status = 'superseded'", code));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from meta.local_entitlement where (payload -> 'payload' ->> 'version')::int = 3 and state = 'active'"));
    }

    [Fact]
    public async Task FR_KRN_ENT_04_a_perpetual_entitlement_stays_active()
    {
        var code = await CustomerAsync("ep");
        var clock = new DayClock();
        clock.Set(clock.Today.AddYears(30));
        Assert.Equal(("active", false), (await Maintenance(clock).RunAsync(code, Ct)).Select(o => (o.State, o.Changed)).Single());
    }

    [Fact]
    public async Task FR_KRN_ENT_04_KRN_TEN_09_locked_and_suspended_keep_sign_in_the_licence_page_and_the_export_and_refuse_everything_else()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = await CustomerAsync("el");
        var (_, username, _) = await h.ActiveUserAsync(code, Ct);
        var companies = await CustomerCountAsync(code, "select count(*) from org.companies");
        var lifecycle = new TenantLifecycle(fx.Options, fx.Clock);
        foreach (var (mode, refusal) in new[] { ("locked licence", TenantResolution.LicenceLocked), ("suspended", TenantResolution.Locked) })
        {
            if (mode == "suspended")
            {
                await fx.SetDaysAfterEndAsync(code, null, Ct);
                await lifecycle.SuspendAsync(fx.Admin, code, "S1.09b test", Ct);
            }
            else
            {
                await fx.SetDaysAfterEndAsync(code, 60, Ct);
            }

            var signIn = await h.SignInAsync(code, username, IdentityHarness.Password, Ct);
            Assert.True(signIn.Status == HttpStatusCode.OK, $"{mode}: sign-in {(int)signIn.Status} {signIn.Body}");
            var token = signIn.Text("accessToken");
            Assert.Equal(HttpStatusCode.OK, (await h.GetAsync(code, "/api/cfg/licence", token, Ct)).Status);
            Assert.Equal(HttpStatusCode.OK, (await h.GetAsync(code, "/api/kernel/capabilities", token, Ct)).Status);
            Assert.Equal(HttpStatusCode.OK, (await h.GetAsync(code, "/api/aud/export", token, Ct)).Status);
            var refused = await h.GetAsync(code, "/api/org/companies", token, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Contains(refusal, refused.Body, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await h.GetAsync(code, "/api/cfg/licence/usage", token, Ct)).Status); // part of the licence page (review finding 6, CR-057)
            Assert.Equal(HttpStatusCode.Forbidden, (await h.PostAsync(code, "/api/iam/me/password", new { currentPassword = IdentityHarness.Password, newPassword = "Locked-Pass-12" }, token, Ct)).Status);
        }

        await lifecycle.ResumeAsync(fx.Admin, code, "S1.09b test", Ct);
        Assert.Equal(companies, await CustomerCountAsync(code, "select count(*) from org.companies")); // no state deletes or hides data
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(code, username, IdentityHarness.Password, Ct)).Status);
    }

    // ------------------------------------------------------------------ limits (Q7)

    [Fact]
    public async Task FR_KRN_ENT_05_FR_ADM_USR_08_named_users_warn_once_at_the_threshold_then_stop_with_a_message_naming_the_limit_and_the_loop_copies_each_event_once()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = await CustomerAsync("en");
        await h.ActiveUserAsync(code, Ct);
        var used = await CustomerCountAsync(code, LimitSql.NamedUsers);
        var (_, _, second) = await h.InviteAsync(code, Ct);
        var (thirdId, _, third) = await h.InviteAsync(code, Ct);
        await fx.SetClaimAsync(code, "limits,users.named", used + 1, Ct);

        var taken = await SendAsync(h.Client(code), HttpMethod.Post, "/api/iam/auth/activate", new { token = second, newPassword = IdentityHarness.Password });
        Assert.Equal(HttpStatusCode.OK, taken.Status);
        Assert.Equal("users.named;100", taken.Warning); // returned with the response
        var refused = await SendAsync(h.Client(code), HttpMethod.Post, "/api/iam/auth/activate", new { token = third, newPassword = IdentityHarness.Password });
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        var problem = JsonDocument.Parse(refused.Body).RootElement;
        Assert.Equal(("errors.licence.limit_reached", "errors.licence.limit_reached.users.named", "users.named", used + 1), (problem.GetProperty("title").GetString(), problem.GetProperty("detail").GetString(), problem.GetProperty("limit").GetString(), problem.GetProperty("max").GetInt64()));
        Assert.Equal("invited", await h.StatusAsync(code, thirdId, Ct));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = 'kernel.licence.limit_warning' and details ->> 'limit' = 'users.named'"));
        Assert.Equal(1, await CustomerCountAsync(code, "select count(*) from aud.security_events where event_type = 'kernel.licence.limit_reached' and details ->> 'limit' = 'users.named'"));

        Assert.Equal(2, (await Maintenance().RunAsync(code, Ct)).Single().LimitEventsCopied);
        Assert.Equal(0, (await Maintenance().RunAsync(code, Ct)).Single().LimitEventsCopied);
        Assert.Equal(2, await CpCountAsync("select count(*) from cp.license_events l join cp.tenants t on t.id = l.tenant_id where t.code = $1 and l.source_event_id is not null and l.event_type in ('limit.warning', 'limit.reached')", code));
    }

    [Fact]
    public async Task FR_KRN_IAM_18_two_administrators_taking_the_last_seat_at_the_same_time_cannot_both_succeed()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = await CustomerAsync("er");
        var (one, _, _) = await h.ActiveUserAsync(code, Ct);
        var (two, _, _) = await h.ActiveUserAsync(code, Ct);
        foreach (var user in new[] { one, two })
        {
            Identity.IdentityTests.AssertDisabled(await h.PostAsync(code, $"/api/iam/users/{user}/status", new { status = "disabled", reason = "seat race" }, h.AdminToken(code), Ct));
        }

        await fx.SetClaimAsync(code, "limits,users.named", await CustomerCountAsync(code, LimitSql.NamedUsers) + 1, Ct);
        var answers = await Task.WhenAll(new[] { one, two }.Select(u => h.PostAsync(code, $"/api/iam/users/{u}/status", new { status = "active", reason = "seat race" }, h.AdminToken(code), Ct)));
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.Status).OrderBy(s => (int)s));
    }

    [Fact]
    public async Task FR_ADM_USR_08_concurrent_users_are_counted_at_sign_in_a_signed_in_user_may_sign_in_again_and_a_lowered_limit_blocks_only_new_seats()
    {
        var h = await IdentityHarness.StartAsync(fx, Ct);
        await using var app = h.App;
        var code = await CustomerAsync("eu");
        var (_, x, _) = await h.ActiveUserAsync(code, Ct);
        await h.ActiveUserAsync(code, Ct);
        var (_, _, z) = await h.InviteAsync(code, Ct);
        var open = await CustomerCountAsync(code, "select count(distinct user_id) from iam.sessions where revoked_at is null and expires_at > now()");
        var claims = await fx.SetClaimAsync(code, "limits,users.concurrent", open, Ct);

        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(code, x, IdentityHarness.Password, Ct)).Status); // already holds a seat (ruling B2)
        var refused = await h.PostAsync(code, "/api/iam/auth/activate", new { token = z, newPassword = IdentityHarness.Password }, ct: Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("users.concurrent", refused.Body, StringComparison.Ordinal);

        // A limit lowered below current use: nothing is switched off, only new seats are refused.
        await fx.RestoreClaimsAsync(code, claims, Ct);
        await fx.SetClaimAsync(code, "limits,users.named", 1, Ct);
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(code, x, IdentityHarness.Password, Ct)).Status);
        var blocked = await h.PostAsync(code, "/api/iam/auth/activate", new { token = z, newPassword = IdentityHarness.Password }, ct: Ct);
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("users.named", blocked.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_IAM_18_creating_or_reactivating_a_company_beyond_the_limit_is_refused_and_raising_it_allows_it_with_a_warning()
    {
        var code = await CustomerAsync("eo");
        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));
        var active = await CustomerCountAsync(code, LimitSql.Companies);
        await fx.SetClaimAsync(code, "limits,companies", active, Ct);

        var refused = await SendAsync(client, HttpMethod.Post, "/api/org/companies", Company("C1" + code));
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("\"limit\":\"companies\"", refused.Body, StringComparison.Ordinal);
        var created = await SendAsync(client, HttpMethod.Post, "/api/org/companies", Company("C2" + code, "inactive")); // inactive takes no seat
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var row = JsonDocument.Parse(created.Body).RootElement;
        var update = new JsonObject { ["code"] = "C2" + code, ["nameI18n"] = new JsonObject { ["en"] = "Company" }, ["legalName"] = "Company (Private) Limited", ["countryCode"] = "PK",
            ["baseCurrencyCode"] = "PKR", ["defaultLanguage"] = "en", ["costCenterModel"] = "none", ["status"] = "active", ["rowVersion"] = row.GetProperty("rowVersion").GetInt64() };
        var id = row.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/org/companies/{id}", update)).Status);
        Assert.Equal(active, await CustomerCountAsync(code, LimitSql.Companies));

        await fx.SetClaimAsync(code, "limits,companies", active + 1, Ct);
        var reactivated = await SendAsync(client, HttpMethod.Put, $"/api/org/companies/{id}", update);
        Assert.Equal(HttpStatusCode.OK, reactivated.Status);
        Assert.Equal("companies;100", reactivated.Warning);
    }

    // ------------------------------------------------------------------ usage metering (Q8)

    [Fact]
    public async Task FR_KRN_ENT_06_FR_ADM_OPS_07_monthly_values_keep_the_highest_daily_reading_and_the_statement_matches_the_counted_data()
    {
        var code = await CustomerAsync("em");
        await fx.SetClaimAsync(code, "limits,users.named", 10, Ct);
        await fx.SetClaimAsync(code, "limits,companies", 4, Ct);
        await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
        {
            await TenancyFixture.ExecAsync(owner, "update iam.users set status = 'active' where not is_system", Ct);
        }

        var users = await CustomerCountAsync(code, LimitSql.NamedUsers);
        var companies = await CustomerCountAsync(code, LimitSql.Companies);
        await Maintenance().RunAsync(code, Ct);
        await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
        {
            await TenancyFixture.ExecAsync(owner, "update iam.users set status = 'disabled' where not is_system", Ct); // a lower later reading
        }

        await Maintenance().RunAsync(code, Ct);
        var period = new DayClock().Today.ToString("yyyyMM", CultureInfo.InvariantCulture);
        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));
        var statement = JsonDocument.Parse((await SendAsync(client, HttpMethod.Get, $"/api/cfg/licence/usage?from={period}&to={period}")).Body).RootElement.EnumerateArray().Single();
        var lines = statement.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("meter").GetString()!, l => l);
        Assert.Equal((users, 10L, (int)(users * 10)), (lines["active_users"].GetProperty("value").GetInt64(), lines["active_users"].GetProperty("limit").GetInt64(), lines["active_users"].GetProperty("percent").GetInt32()));
        Assert.Equal((companies, (int)(companies * 25)), (lines["companies"].GetProperty("value").GetInt64(), lines["companies"].GetProperty("percent").GetInt32()));
        Assert.True(lines["storage_bytes"].GetProperty("value").GetInt64() > 0);
        Assert.Equal(Math.Max(users * 10, companies * 25), statement.GetProperty("highestPercent").GetInt32());

        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        var operatorView = await UsageMeters.StatementAsync(cp, code, period, "Asia/Karachi", Ct);
        Assert.Equal(users, operatorView.Lines.Single(l => l.Meter == "active_users").Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Get, "/api/cfg/licence/usage?from=202613&to=202601")).Status);
    }

    // ------------------------------------------------------------------ permissions (Q16)

    [Fact]
    public async Task FR_ADM_OPS_07_ruling_Q16_tenant_admin_and_auditor_read_the_licence_company_admin_and_business_user_do_not_and_capabilities_serve_every_user()
    {
        var harness = await AccessHarness.ForAsync(fx);
        foreach (var (role, licence) in new[] { ("tenant_admin", HttpStatusCode.OK), ("auditor", HttpStatusCode.OK), ("company_admin", HttpStatusCode.Forbidden), ("business_user", HttpStatusCode.Forbidden) })
        {
            var user = await harness.UserAsync(Ct, null, role);
            Assert.True((await harness.GetAsync(user, "/api/cfg/licence", Ct)).Status == licence, role + " licence");
            Assert.True((await harness.GetAsync(user, "/api/cfg/licence/usage", Ct)).Status == licence, role + " usage");
            Assert.True((await harness.GetAsync(user, "/api/kernel/capabilities", Ct)).Status == HttpStatusCode.OK, role + " capabilities");
        }
    }
}
