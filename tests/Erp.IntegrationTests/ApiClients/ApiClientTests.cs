#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.IntegrationTests.ApiClients;

/// <summary>
/// API clients and keys (slice S1.27 part c, Build items 10 to 13) through the real API pipeline, the real access engine and a real PostgreSQL:
/// FR-ADM-SEC-02 (create with scopes, expiry, IP allow list and limit; the secret shown once; rotate with a grace period; revoke; every action audited;
/// key authentication that stays inside its scopes and never reaches /api/iam or a web session) and FR-KRN-INT-07 (the per-client limit and the customer ceiling
/// with 429 and Retry-After, usage with the 95th percentile, one notice per limit and per expiry).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ApiClientTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Clients = "/api/iam/api-clients";
    private const string PushScope = "hrm.attendance_devices.push";
    private const string Inside = "10.1.2.3";
    private static readonly string[] Allow = ["10.1.2.0/24"];
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await SetSettingAsync("iam.maker_checker.sensitive_roles", "false");
    }

    public async ValueTask DisposeAsync()
    {
        await SetSettingAsync("iam.maker_checker.sensitive_roles", "true");
        await _h.ExecAsync("delete from cfg.setting_values where setting_key in ('itg.api.tenant_rate_per_minute', 'iam.api_client.rotation_grace_minutes', 'iam.api_client.expiry_notice_days')", CancellationToken.None);
        _h.Clock.Reset();
    }

    // ------------------------------------------------------------------ material

    private Task SetSettingAsync(string key, string json) =>
        _h.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'tenant', null, $3::jsonb, now(), $4, now(), $4)
            on conflict (setting_key, layer, scope_id) do update set value = excluded.value
            """, Ct, Guid.CreateVersion7(), key, json, BaselineSeed.SystemUserId);

    private async Task<Guid> UserWithAsync(params string[] permissions)
    {
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, await _h.RoleAsync(Ct, permissions), null, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    private Task<Guid> ManagerAsync() => UserWithAsync("iam.api_clients.manage", "iam.api_clients.read", "iam.maker_checker.approve");

    private sealed record Made(Guid Id, string ClientId, string Secret, string ApiKey);

    private static object Body(string? name = null, string[]? scopes = null, string[]? ip = null, int rate = 60, DateTimeOffset? expires = null) => new
    {
        name = name ?? "Gateway " + Guid.NewGuid().ToString("N")[..8],
        scopes = scopes ?? [PushScope],
        ipAllow = ip ?? Allow,
        rateLimitPerMinute = rate,
        expiresAt = expires,
    };

    private async Task<Made> CreateAsync(Guid? manager = null, object? body = null)
    {
        var by = manager ?? await ManagerAsync();
        var answer = await _h.PostAsync(by, Clients, body ?? Body(), Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return new Made(answer.Json.GetProperty("id").GetGuid(), answer.Text("clientId")!, answer.Text("secret")!, answer.Text("apiKey")!);
    }

    private sealed record Called(HttpStatusCode Status, string Body, int? RetryAfter);

    /// <summary>A call with the key, from a caller address, to the push endpoint of a device that does not exist: 404 means the key was accepted and allowed in.</summary>
    private async Task<Called> CallAsync(string? key, string path = "", string? ip = Inside, string? host = null, HttpMethod? method = null, string? body = null)
    {
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(host ?? _h.Code));
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path.Length > 0 ? path : $"/api/hrm/attendance/devices/{Guid.NewGuid()}/push");
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "ApiKey " + key);
        }

        if (ip is not null)
        {
            request.Headers.Add("X-Test-Remote-Address", ip);
        }

        if ((method ?? HttpMethod.Post) != HttpMethod.Get)
        {
            request.Content = new StringContent(body ?? """{"records":[]}""", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        return new Called(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Headers.RetryAfter?.Delta is { } d ? (int)d.TotalSeconds : null);
    }

    private Task FlushAsync() => _h.App.Services.GetRequiredService<ApiUsageRecorder>().FlushAsync(Ct);

    private Task<long> EventsAsync(string type, Guid? aggregate = null) =>
        _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = $1 and ($2::uuid is null or aggregate_id = $2)", Ct, type, aggregate);

    private async Task AlignMinuteAsync(int second = 5)
    {
        var now = _h.Clock.UtcNow;
        _h.Clock.Advance(TimeSpan.FromSeconds((60 - now.Second + second) % 60));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ create, read, never a secret (SEC-02)

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_secret_is_shown_once_and_kept_only_as_a_hash_in_no_answer_row_log_or_event()
    {
        var manager = await ManagerAsync();
        var made = await CreateAsync(manager);
        Assert.StartsWith("ak_", made.ClientId, StringComparison.Ordinal);
        Assert.Equal(made.ClientId + "." + made.Secret, made.ApiKey);

        foreach (var answer in new[] { await _h.GetAsync(manager, Clients, Ct), await _h.GetAsync(manager, $"{Clients}/{made.Id}", Ct), await _h.GetAsync(manager, $"{Clients}/{made.Id}/usage", Ct), await _h.GetAsync(manager, $"{Clients}/scope-options", Ct) })
        {
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.DoesNotContain(made.Secret, answer.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("secretHash", answer.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("argon2", answer.Body, StringComparison.OrdinalIgnoreCase);
        }

        var read = (await _h.GetAsync(manager, $"{Clients}/{made.Id}", Ct)).Json;
        Assert.Equal("active", read.GetProperty("status").GetString());
        Assert.Equal(60, read.GetProperty("rateLimitPerMinute").GetInt32());

        var hash = await _h.ScalarAsync<string>("select secret_hash from iam.api_clients where id = $1", Ct, made.Id);
        Assert.StartsWith("$argon2id$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain(made.Secret, hash, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where changes::text like '%' || $1 || '%' or changes::text like '%argon2%'", Ct, made.Secret));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where payload::text like '%' || $1 || '%' or payload::text like '%argon2%'", Ct, made.Secret));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.api_clients' and entity_id = $1 and action = 'create' and actor_user_id = $2", Ct, made.Id, manager));
        Assert.Equal(1L, await EventsAsync(ApiClientEvents.Created, made.Id));
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_only_people_with_the_permission_manage_or_read_api_clients()
    {
        var made = await CreateAsync();
        var nobody = await UserWithAsync("hrm.persons.read");
        var reader = await UserWithAsync("iam.api_clients.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, Clients, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Clients, Body(), Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(reader, Clients, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(reader, $"{Clients}/{made.Id}/usage", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, Clients, Body(), Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, $"{Clients}/{made.Id}/rotate", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, $"{Clients}/{made.Id}/revoke", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(reader, $"{Clients}/{Guid.NewGuid()}", Ct)).Status);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_a_client_is_refused_unless_name_scopes_addresses_limit_and_expiry_are_valid_and_scopes_carry_the_api_client_flag()
    {
        var manager = await ManagerAsync();
        async Task<string?> ErrorAsync(object body) => (await _h.PostAsync(manager, Clients, body, Ct)) is { Status: HttpStatusCode.BadRequest } a ? a.Text("title") : "accepted";

        Assert.Equal("errors.iam.api_client.name_required", await ErrorAsync(Body(name: " ")));
        Assert.Equal("errors.iam.api_client.scopes_required", await ErrorAsync(Body(scopes: [])));
        Assert.Equal("errors.iam.api_client.scope_not_allowed", await ErrorAsync(Body(scopes: ["hrm.persons.read"]))); // a real permission without the flag
        Assert.Equal("errors.iam.api_client.scope_not_allowed", await ErrorAsync(Body(scopes: [PushScope, "iam.roles.manage"])));
        Assert.Equal("errors.iam.api_client.scope_not_allowed", await ErrorAsync(Body(scopes: ["no.such.permission"])));
        Assert.Equal("errors.iam.api_client.ip_invalid", await ErrorAsync(Body(ip: ["not-an-ip"])));
        Assert.Equal("errors.iam.api_client.ip_invalid", await ErrorAsync(Body(ip: ["10.0.0.0/99"])));
        Assert.Equal("errors.iam.api_client.rate_invalid", await ErrorAsync(Body(rate: 0)));
        Assert.Equal("errors.iam.api_client.expiry_invalid", await ErrorAsync(Body(expires: _h.Clock.UtcNow.AddMinutes(-1))));
        Assert.Equal("accepted", await ErrorAsync(Body(ip: ["10.0.0.1", "2001:db8::/32", "192.168.1.0/24"], expires: _h.Clock.UtcNow.AddDays(30))));

        var options = (await _h.GetAsync(manager, $"{Clients}/scope-options", Ct)).Json.EnumerateArray().Select(o => o.GetProperty("code").GetString()).ToList();
        Assert.Equal([PushScope], options); // the first flagged permission is the S1.27b push; no other permission can be offered
    }

    // ------------------------------------------------------------------ key authentication (SEC-02)

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_a_key_is_accepted_only_with_the_right_secret_from_an_allowed_address()
    {
        var made = await CreateAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status); // 404: the device does not exist, but the key was accepted and the scope allowed
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ClientId + ".wrong-" + made.Secret)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ClientId + "." + made.Secret[..^1])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("ak_unknown000000000000000000.whatever")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("no-dot-at-all")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ClientId + ".")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, ip: "10.1.3.1")).Status); // outside the allow list
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, ip: "192.168.0.9")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, ip: null)).Status); // no address: never allowed
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey, ip: "10.1.2.254")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(null)).Status);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_an_empty_allow_list_refuses_every_call_and_an_exact_address_and_a_mapped_address_work()
    {
        var closed = await CreateAsync(body: Body(ip: []));
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(closed.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(closed.ApiKey, ip: "127.0.0.1")).Status);

        var exact = await CreateAsync(body: Body(ip: ["203.0.113.7"]));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(exact.ApiKey, ip: "203.0.113.7")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(exact.ApiKey, ip: "::ffff:203.0.113.7")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(exact.ApiKey, ip: "203.0.113.8")).Status);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_a_key_of_one_customer_is_refused_on_another_customers_host()
    {
        var made = await CreateAsync();
        var other = await _h.ProvisionAsync("kx", Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ApiKey, host: other)).Status);
        await using var owner = await _h.OwnerAsync(other, Ct);
        Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from iam.api_clients", Ct)); // the other customer's database knows no such client
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_a_revoked_key_gets_401_at_once_and_an_expired_one_too()
    {
        var manager = await ManagerAsync();
        var revoked = await CreateAsync(manager);
        var expiring = await CreateAsync(manager, Body(expires: _h.Clock.UtcNow.AddHours(1)));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(revoked.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(expiring.ApiKey)).Status);

        var answer = await _h.PostAsync(manager, $"{Clients}/{revoked.Id}/revoke", null, Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal("revoked", answer.Text("status"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(revoked.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(manager, $"{Clients}/{revoked.Id}/revoke", null, Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(manager, $"{Clients}/{revoked.Id}/rotate", null, Ct)).Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.api_clients' and entity_id = $1 and action = 'status'", Ct, revoked.Id));

        _h.Clock.Advance(TimeSpan.FromMinutes(61)); // the expiry passes (the daily job marks the row later; the call checks the date itself)
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(expiring.ApiKey)).Status);
        Assert.Equal("expired", (await _h.GetAsync(manager, $"{Clients}/{expiring.Id}", Ct)).Text("status"));
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_a_rotated_key_works_and_the_old_secret_stops_after_the_grace_period()
    {
        var manager = await ManagerAsync();
        var made = await CreateAsync(manager);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);

        var rotated = await _h.PostAsync(manager, $"{Clients}/{made.Id}/rotate", null, Ct);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        var fresh = rotated.Text("apiKey")!;
        Assert.NotEqual(made.ApiKey, fresh);
        Assert.StartsWith(made.ClientId + ".", fresh, StringComparison.Ordinal);
        Assert.NotNull(rotated.Json.GetProperty("validUntil").GetString()); // when the old secret stops

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(fresh)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status); // the old secret still works during the grace period (60 minutes)
        _h.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);
        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(fresh)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where changes::text like '%' || $1 || '%'", Ct, rotated.Text("secret")));
        Assert.Equal(1L, await EventsAsync(ApiClientEvents.Rotated, made.Id));
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_grace_period_is_a_setting_and_zero_stops_the_old_secret_at_once()
    {
        var manager = await ManagerAsync();
        await SetSettingAsync("iam.api_client.rotation_grace_minutes", "0");
        var made = await CreateAsync(manager);
        var rotated = await _h.PostAsync(manager, $"{Clients}/{made.Id}/rotate", null, Ct);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.Equal(JsonValueKind.Null, rotated.Json.GetProperty("validUntil").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(rotated.Text("apiKey"))).Status);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_caller_address_is_taken_from_x_forwarded_for_only_when_the_request_comes_from_a_listed_proxy()
    {
        var made = await CreateAsync();
        await using var behind = await fx.StartAppAsync(null, Ct, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(_h.Clock);
            services.AddTransient<IStartupFilter, TestRemoteAddressFilter>();
        }, realAccess: true, settings: new Dictionary<string, string?> { ["Network:TrustedProxies:0"] = "172.16.0.0/16" });

        async Task<HttpStatusCode> CallBehindAsync(string remote, string? forwarded)
        {
            using var client = TenancyFixture.Client(behind, TenancyFixture.HostOf(_h.Code));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hrm/attendance/devices/{Guid.NewGuid()}/push") { Content = new StringContent("""{"records":[]}""", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", "ApiKey " + made.ApiKey);
            request.Headers.Add("X-Test-Remote-Address", remote);
            if (forwarded is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwarded);
            }

            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await CallBehindAsync("172.16.0.5", "10.1.2.9")); // from the listed proxy: the client's address counts
        Assert.Equal(HttpStatusCode.Forbidden, await CallBehindAsync("172.16.0.5", "10.9.9.9"));
        Assert.Equal(HttpStatusCode.Forbidden, await CallBehindAsync("172.16.0.5", null)); // no forwarded address: the proxy's own, which is not in the list
        Assert.Equal(HttpStatusCode.NotFound, await CallBehindAsync("10.1.2.77", "8.8.8.8")); // not a listed proxy: the header is ignored, the real address counts
        Assert.Equal(HttpStatusCode.Forbidden, await CallBehindAsync("10.9.9.9", "10.1.2.9")); // a forged header from anywhere else does not help
    }

    // ------------------------------------------------------------------ scopes (SEC-02)

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_a_key_cannot_use_a_permission_outside_its_scopes_any_api_iam_endpoint_or_a_web_session()
    {
        var made = await CreateAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);

        // /api/iam: reads and writes of the API clients themselves, users, roles, sessions
        foreach (var path in new[] { Clients, $"{Clients}/scope-options", $"{Clients}/{made.Id}", $"{Clients}/{made.Id}/usage", "/api/iam/me/sessions", "/api/iam/roles", "/api/iam/user-overview" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, path, method: HttpMethod.Get)).Status);
        }

        foreach (var path in new[] { Clients, $"{Clients}/{made.Id}/rotate", $"{Clients}/{made.Id}/revoke", "/api/iam/auth/refresh", "/api/iam/roles" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, path, method: HttpMethod.Post, body: JsonSerializer.Serialize(Body()))).Status);
        }

        // other endpoints, which name no api_client permission: the shell, settings, attendance reads, the generated lists
        foreach (var path in new[] { "/api/kernel/me", "/api/cfg/settings", "/api/hrm/attendance/punch-log", "/api/hrm/persons", "/api/org/companies" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, path, method: HttpMethod.Get)).Status);
        }

        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.api_clients where id = $1 and status = 'active'", Ct, made.Id)); // nothing was changed
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_scopes_that_are_no_longer_flagged_or_were_never_flagged_give_the_key_nothing()
    {
        var made = await CreateAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);

        // a row edited behind the service's back to name a permission without the flag: the key keeps no such scope
        await _h.ExecAsync("update iam.api_clients set scopes = array['hrm.persons.read'] where id = $1", Ct, made.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey)).Status);
        await _h.ExecAsync("update iam.api_clients set scopes = array[$2] where id = $1", Ct, made.Id, PushScope);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);

        // the manifest flag is what counts: without it the scope is gone at once
        await _h.ExecAsync("update iam.permissions set is_api_client = false where code = $1", Ct, PushScope);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey)).Status);
        }
        finally
        {
            await _h.ExecAsync("update iam.permissions set is_api_client = true where code = $1", Ct, PushScope);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_flag_is_in_the_manifest_only_for_the_push_permission_and_every_key_endpoint_names_a_flagged_permission()
    {
        var flagged = (await _h.ScalarAsync<string>("select string_agg(code, ',' order by code) from iam.permissions where is_api_client and retired_at is null", Ct)).Split(',');
        Assert.Equal([PushScope], flagged);

        var sources = _h.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var marked = sources.Where(e => e.Metadata.GetMetadata<ApiClientEndpointMetadata>() is not null).ToList();
        Assert.NotEmpty(marked);
        Assert.All(marked, e => Assert.Contains(e.Metadata.GetMetadata<ApiClientEndpointMetadata>()!.Permission, flagged));
        Assert.All(marked, e => Assert.DoesNotContain("/api/iam", e.RoutePattern.RawText, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ maker-checker (SEC-02)

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_with_approval_on_a_new_client_and_a_rotation_wait_for_a_second_person_and_the_secret_opens_nothing_before_that()
    {
        await SetSettingAsync("iam.maker_checker.sensitive_roles", "true");
        var maker = await ManagerAsync();
        var checker = await ManagerAsync();

        var created = await _h.PostAsync(maker, Clients, Body(), Ct);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var held = created.Json.GetProperty("heldRequestId").GetGuid();
        var key = created.Text("apiKey")!;
        Assert.Equal(JsonValueKind.Null, created.Json.GetProperty("id").ValueKind);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.api_clients where client_id = $1", Ct, created.Text("clientId")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(key)).Status); // the requester holds the secret, which opens nothing yet
        Assert.DoesNotContain(created.Text("secret")!, await _h.ScalarAsync<string>("select payload::text from iam.maker_checker_requests where id = $1", Ct, held), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(maker, $"/api/iam/maker-checker-requests/{held}/approve", new { reason = "mine" }, Ct)).Status);
        var approved = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{held}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(key)).Status);
        var id = await _h.ScalarAsync<Guid>("select id from iam.api_clients where client_id = $1", Ct, created.Text("clientId"));
        Assert.Equal(maker, await _h.ScalarAsync<Guid>("select created_by from iam.api_clients where id = $1", Ct, id)); // created for the requester, decided by the checker

        var rotation = await _h.PostAsync(maker, $"{Clients}/{id}/rotate", null, Ct);
        Assert.Equal(HttpStatusCode.OK, rotation.Status);
        var rotationRequest = rotation.Json.GetProperty("heldRequestId").GetGuid();
        var newKey = rotation.Text("apiKey")!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(newKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(key)).Status); // the current secret keeps working while the rotation waits
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{rotationRequest}/approve", new { reason = "agreed" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(newKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(key)).Status); // the grace period
        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(key)).Status);

        var rejected = await _h.PostAsync(maker, Clients, Body(), Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{rejected.Json.GetProperty("heldRequestId").GetGuid()}/reject", new { reason = "no" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(rejected.Text("apiKey"))).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.api_clients where client_id = $1", Ct, rejected.Text("clientId")));
    }

    // ------------------------------------------------------------------ rate limits (INT-07)

    [Fact]
    public async Task S127c_FR_KRN_INT_07_61_calls_in_a_minute_against_a_limit_of_60_give_one_429_with_retry_after_and_one_limit_notification()
    {
        var manager = await ManagerAsync();
        var made = await CreateAsync(manager, Body(rate: 60));
        await AlignMinuteAsync(5);
        var results = new List<Called>();
        for (var i = 0; i < 61; i++)
        {
            results.Add(await CallAsync(made.ApiKey));
        }

        Assert.Equal(60, results.Count(r => r.Status == HttpStatusCode.NotFound));
        var refused = Assert.Single(results, r => r.Status == HttpStatusCode.TooManyRequests);
        Assert.Equal(55, refused.RetryAfter); // seconds to the next minute (the clock stands at second 5)
        Assert.Contains("errors.iam.api_client.rate_limited", refused.Body, StringComparison.Ordinal);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(made.ApiKey)).Status);
        }

        Assert.Equal(1L, await EventsAsync(ApiClientEvents.LimitReached, made.Id)); // one notice, not one per refused call
        var payload = JsonDocument.Parse(await _h.ScalarAsync<string>("select payload::text from iam.outbox_messages where event_type = $1 and aggregate_id = $2", Ct, ApiClientEvents.LimitReached, made.Id)).RootElement;
        Assert.Contains(manager, payload.GetProperty("recipients").EnumerateArray().Select(r => r.GetGuid()));
        Assert.Equal(60, payload.GetProperty("limit").GetInt32());

        _h.Clock.Advance(TimeSpan.FromMinutes(1)); // the next minute starts a new window
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);
        for (var i = 0; i < 60; i++)
        {
            await CallAsync(made.ApiKey);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(made.ApiKey)).Status);
        Assert.Equal(1L, await EventsAsync(ApiClientEvents.LimitReached, made.Id)); // still one for the day
        await FlushAsync();
        var usage = (await _h.GetAsync(manager, $"{Clients}/{made.Id}/usage", Ct)).Json.GetProperty("days")[0];
        Assert.Equal(7, usage.GetProperty("rateLimited").GetInt32()); // 1 + 4 in the first minute, 2 in the second
    }

    [Fact]
    public async Task S127c_FR_KRN_INT_07_the_customer_ceiling_applies_across_two_clients_without_a_notice_for_either()
    {
        var manager = await ManagerAsync();
        await SetSettingAsync("itg.api.tenant_rate_per_minute", "5");
        var a = await CreateAsync(manager, Body(rate: 100));
        var b = await CreateAsync(manager, Body(rate: 100));
        await AlignMinuteAsync(5);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(a.ApiKey)).Status);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(b.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(b.ApiKey)).Status);
        var over = await CallAsync(b.ApiKey);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.Status);
        Assert.Equal(55, over.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(a.ApiKey)).Status);
        Assert.Equal(0L, await EventsAsync(ApiClientEvents.LimitReached)); // the clients stayed under their own limits
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(a.ApiKey)).Status);
    }

    [Fact]
    public async Task S127c_FR_KRN_INT_07_counts_of_other_hosts_reach_this_host_through_the_database_at_the_minute_reconcile()
    {
        var manager = await ManagerAsync();
        var made = await CreateAsync(manager, Body(rate: 10));
        await AlignMinuteAsync(5);
        for (var i = 0; i < 4; i++)
        {
            await CallAsync(made.ApiKey);
        }

        // another API host added 6 calls of this minute to the shared counter
        var minute = DateTimeOffset.FromUnixTimeSeconds(_h.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60);
        await _h.ExecAsync("""
            insert into iam.api_client_windows (id, client_id, window_start, calls, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 6, now(), $4, now(), $4)
            """, Ct, Guid.CreateVersion7(), made.Id, minute, BaselineSeed.SystemUserId);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status); // not seen before the reconcile (R-208)
        await FlushAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(made.ApiKey)).Status); // 5 of its own + 6 of the other = over 10
        Assert.Equal(11L, await _h.ScalarAsync<long>("select calls::bigint from iam.api_client_windows where client_id = $1 and window_start = $2", Ct, made.Id, minute));
    }

    // ------------------------------------------------------------------ usage (SEC-02, INT-07)

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_usage_shows_calls_errors_the_95th_percentile_and_the_last_use_and_last_use_is_written_once_a_minute()
    {
        var manager = await ManagerAsync();
        var made = await CreateAsync(manager);
        var start = _h.Clock.UtcNow;
        Assert.Equal(JsonValueKind.Null, (await _h.GetAsync(manager, $"{Clients}/{made.Id}", Ct)).Json.GetProperty("lastUsedAt").ValueKind);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(made.ApiKey)).Status);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(made.ClientId + ".wrong")).Status); // a refused call of a known client is counted too
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(made.ApiKey, "/api/kernel/me", method: HttpMethod.Get)).Status);

        var first = (await _h.GetAsync(manager, $"{Clients}/{made.Id}/usage", Ct)).Json;
        var day = first.GetProperty("days")[0];
        Assert.Equal(DateOnly.FromDateTime(start.UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), day.GetProperty("date").GetString());
        Assert.Equal(5, day.GetProperty("calls").GetInt32());
        Assert.Equal(5, day.GetProperty("errors4xx").GetInt32());
        Assert.Equal(0, day.GetProperty("errors5xx").GetInt32());
        Assert.InRange(day.GetProperty("p95Ms").GetInt32(), 1, 60_000);
        Assert.True(day.GetProperty("p95Ms").GetInt32() <= Math.Max(day.GetProperty("maxMs").GetInt32(), 1));

        var used = (await _h.GetAsync(manager, $"{Clients}/{made.Id}", Ct)).Json.GetProperty("lastUsedAt").GetDateTimeOffset();
        Assert.Equal(start, used); // written by the first call, not again by the next four within the minute
        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        await CallAsync(made.ApiKey);
        Assert.Equal(start.AddMinutes(2), (await _h.GetAsync(manager, $"{Clients}/{made.Id}", Ct)).Json.GetProperty("lastUsedAt").GetDateTimeOffset());

        // a day later: its own row, newest first
        _h.Clock.Advance(TimeSpan.FromDays(1));
        await CallAsync(made.ApiKey);
        var days = (await _h.GetAsync(manager, $"{Clients}/{made.Id}/usage?days=7", Ct)).Json.GetProperty("days");
        Assert.Equal(2, days.GetArrayLength());
        Assert.Equal(1, days[0].GetProperty("calls").GetInt32());
        Assert.Equal(6, days[1].GetProperty("calls").GetInt32());
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_every_refused_key_is_a_security_event_that_never_holds_the_secret()
    {
        var manager = await ManagerAsync();
        var made = await CreateAsync(manager);
        var before = await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.api_key_refused'", Ct);
        await CallAsync(made.ClientId + ".WRONG-SECRET-VALUE");
        await CallAsync(made.ApiKey, ip: "198.51.100.9");
        await CallAsync("ak_nobody000000000000000000.x");
        await _h.PostAsync(manager, $"{Clients}/{made.Id}/revoke", null, Ct);
        await CallAsync(made.ApiKey);
        var rows = (await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.api_key_refused'", Ct)) - before;
        Assert.Equal(4, rows);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.security_events where details::text like '%WRONG-SECRET-VALUE%' or details::text like '%' || $1 || '%'", Ct, made.Secret));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.api_key_refused' and details ->> 'reason' = 'IpNotAllowed' and details ->> 'ip' = '198.51.100.9'", Ct));
    }

    // ------------------------------------------------------------------ the daily job (SEC-02)

    private sealed class FakeRun(IServiceProvider services, List<string> log) : Erp.Kernel.Contracts.Jobs.IJobExecution
    {
        public Guid RunId { get; } = Guid.CreateVersion7();

        public string Code => ApiClientCodes.ExpiryJob;

        public Guid? CompanyId => null;

        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement;

        public JsonElement? Checkpoint => null;

        public int Attempt => 1;

        public DateTimeOffset? ScheduledFor => null;

        public IServiceProvider Services { get; } = services;

        public Task CommitChunkAsync(Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? work, object? checkpoint, decimal? progressPct, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LogAsync(string level, string message, CancellationToken cancellationToken)
        {
            log.Add(level + ": " + message);
            return Task.CompletedTask;
        }
    }

    private async Task<List<string>> RunExpiryJobAsync()
    {
        ResolvedTenant tenant;
        await using (var cp = new Npgsql.NpgsqlConnection(fx.Options.ConnectionString))
        {
            await cp.OpenAsync(Ct);
            tenant = (await Erp.Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory.FindByCodeAsync(cp, _h.Code, Ct))!;
        }

        var dataSource = await _h.App.Services.GetRequiredService<ITenantDataSourceFactory>().GetAsync(tenant, Ct);
        var licence = await _h.App.Services.GetRequiredService<ILicenceResolver>().ResolveAsync(tenant, dataSource, Ct);
        await using var scope = _h.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, TenantAccessMode.Full, dataSource, licence);
        var log = new List<string>();
        await new ApiClientExpiryJob().RunAsync(new FakeRun(scope.ServiceProvider, log), Ct);
        return log;
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_daily_job_marks_expired_clients_warns_once_before_expiry_and_removes_old_rate_counters()
    {
        var manager = await ManagerAsync();
        var soon = await CreateAsync(manager, Body(expires: _h.Clock.UtcNow.AddDays(10)));
        var later = await CreateAsync(manager, Body(expires: _h.Clock.UtcNow.AddDays(60)));
        var never = await CreateAsync(manager);

        var first = await RunExpiryJobAsync();
        Assert.Contains("1 expiry notice", first.Single(), StringComparison.Ordinal);
        await RunExpiryJobAsync();
        Assert.Equal(1L, await EventsAsync(ApiClientEvents.Expiring, soon.Id)); // one notice per expiry date, however often the job runs
        Assert.Equal(0L, await EventsAsync(ApiClientEvents.Expiring, later.Id));
        Assert.Equal(0L, await EventsAsync(ApiClientEvents.Expiring, never.Id));
        var notice = JsonDocument.Parse(await _h.ScalarAsync<string>("select payload::text from iam.outbox_messages where event_type = $1 and aggregate_id = $2", Ct, ApiClientEvents.Expiring, soon.Id)).RootElement;
        Assert.Contains(manager, notice.GetProperty("recipients").EnumerateArray().Select(r => r.GetGuid()));
        Assert.Equal(10, notice.GetProperty("days").GetInt32());

        await _h.ExecAsync("insert into iam.api_client_windows (id, client_id, window_start, calls, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, 4, now(), $4, now(), $4)",
            Ct, Guid.CreateVersion7(), never.Id, _h.Clock.UtcNow.AddDays(-3), BaselineSeed.SystemUserId);
        _h.Clock.Advance(TimeSpan.FromDays(11));
        var second = await RunExpiryJobAsync();
        Assert.Contains("1 client(s) marked expired", second.Single(), StringComparison.Ordinal);
        Assert.Equal("expired", await _h.ScalarAsync<string>("select status from iam.api_clients where id = $1", Ct, soon.Id));
        Assert.Equal("active", await _h.ScalarAsync<string>("select status from iam.api_clients where id = $1", Ct, later.Id));
        Assert.Equal(1L, await EventsAsync(ApiClientEvents.Expired, soon.Id));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.api_clients' and entity_id = $1 and action = 'status' and actor_kind <> 'user'", Ct, soon.Id));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.api_client_windows where client_id = $1", Ct, never.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(soon.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(later.ApiKey)).Status);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_expiry_notice_goes_to_people_who_manage_api_clients_through_a_role_or_a_group_and_nobody_else()
    {
        var direct = await ManagerAsync();
        var reader = await UserWithAsync("iam.api_clients.read");
        var grouped = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["iam.api_clients.manage"]);
        var group = Guid.CreateVersion7();
        await _h.ExecAsync("insert into iam.user_groups (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"G\"}', now(), $3, now(), $3)", Ct, group, "g" + Guid.NewGuid().ToString("N")[..8], BaselineSeed.SystemUserId);
        await _h.ExecAsync("insert into iam.user_group_members (group_id, user_id) values ($1, $2)", Ct, group, grouped);
        await _h.ExecAsync("insert into iam.user_group_roles (id, group_id, role_id, company_id, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, null, current_date - 1, now(), $4, now(), $4)", Ct, Guid.CreateVersion7(), group, role, BaselineSeed.SystemUserId);

        var soon = await CreateAsync(direct, Body(expires: _h.Clock.UtcNow.AddDays(3)));
        await RunExpiryJobAsync();
        var recipients = JsonDocument.Parse(await _h.ScalarAsync<string>("select payload::text from iam.outbox_messages where event_type = $1 and aggregate_id = $2", Ct, ApiClientEvents.Expiring, soon.Id)).RootElement
            .GetProperty("recipients").EnumerateArray().Select(r => r.GetGuid()).ToList();
        Assert.Contains(direct, recipients);
        Assert.Contains(grouped, recipients);
        Assert.DoesNotContain(reader, recipients);
    }

    [Fact]
    public async Task S127c_FR_ADM_SEC_02_the_usage_and_window_tables_have_no_generated_api_and_a_client_row_cannot_be_read_through_one()
    {
        var manager = await ManagerAsync();
        foreach (var path in new[] { "/api/iam/api-client-usage", "/api/iam/api-client-windows", "/api/iam/api_clients" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(manager, path, Ct)).Status);
        }
    }
}
