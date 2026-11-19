using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.Kernel.Api.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>
/// S1.03 acceptance: two customers, each in its own database; data written in one is never visible in the other
/// (through the API); a forged host or token is refused; suspended, locked and read-only customers are limited.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class TenantIsolationAndResolutionTests(TenancyFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Company(string code) => new
    {
        code,
        nameI18n = new { en = "Company " + code },
        legalName = "Company " + code + " (Private) Limited",
        countryCode = "PK",
        baseCurrencyCode = "PKR",
        defaultLanguage = "en",
        costCenterModel = "none",
        status = "active",
    };

    [Fact]
    public async Task FR_KRN_TEN_03_data_written_through_the_api_for_one_customer_is_never_visible_to_the_other()
    {
        var ct = TestContext.Current.CancellationToken;
        using var a = fx.Client(TenancyFixture.HostOf(fx.CodeA), fx.Token(fx.CodeA));
        using var b = fx.Client(TenancyFixture.HostOf(fx.CodeB), fx.Token(fx.CodeB));
        var code = "ISO" + Guid.NewGuid().ToString("N")[..6];

        using var created = await a.PostAsJsonAsync("/api/org/companies", Company(code), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("id").GetGuid();

        // Customer A sees it.
        using var onA = await a.GetAsync($"/api/org/companies/{id}", ct);
        Assert.Equal(HttpStatusCode.OK, onA.StatusCode);

        // Customer B, asking for the same id or listing everything, never sees it.
        using var onB = await b.GetAsync($"/api/org/companies/{id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, onB.StatusCode);
        var listB = await b.GetStringAsync("/api/org/companies?limit=500", ct);
        Assert.DoesNotContain(id.ToString(), listB, StringComparison.Ordinal);
        Assert.DoesNotContain(code, listB, StringComparison.Ordinal);

        // And the rows really are in different databases: A has the company and its SC-2 default branch, B has neither.
        await using var dbA = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadOnly, ct);
        await using var dbB = await fx.OpenTenantAsync(fx.CodeB, TenantLogins.ReadOnly, ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(dbA, "select count(*) from org.companies where id = $1", ct, id));
        Assert.Equal(1, await TenancyFixture.CountAsync(dbA, "select count(*) from org.branches where company_id = $1", ct, id));
        Assert.Equal(0, await TenancyFixture.CountAsync(dbB, "select count(*) from org.companies where id = $1 or code = $2", ct, id, code));
    }

    [Fact]
    public async Task FR_KRN_TEN_04_an_unknown_or_forged_host_is_refused_with_404()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var host in new[] { "unknown.erp.test", "evil-" + fx.CodeA + ".erp.test", fx.CodeA + ".erp.test.attacker.example" })
        {
            using var client = fx.Client(host, fx.Token(fx.CodeA));
            using var response = await client.GetAsync("/api/org/companies", ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains(TenantResolution.NotFound, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FR_KRN_TEN_04_the_customer_is_never_taken_from_query_string_body_or_headers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var b = fx.Client(TenancyFixture.HostOf(fx.CodeB), fx.Token(fx.CodeB));
        var code = "HDR" + Guid.NewGuid().ToString("N")[..6];
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/org/companies?tenant={fx.CodeA}&tenant_code={fx.CodeA}")
        {
            Content = JsonContent.Create(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["code"] = code, ["nameI18n"] = new { en = code }, ["legalName"] = code, ["countryCode"] = "PK", ["baseCurrencyCode"] = "PKR",
                ["defaultLanguage"] = "en", ["costCenterModel"] = "none", ["status"] = "active", ["tenant"] = fx.CodeA, ["tenantCode"] = fx.CodeA,
            }),
        };
        request.Headers.Add("X-Tenant", fx.CodeA);
        request.Headers.Add("X-Forwarded-Host", TenancyFixture.HostOf(fx.CodeA));
        using var response = await b.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var dbA = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadOnly, ct);
        await using var dbB = await fx.OpenTenantAsync(fx.CodeB, TenantLogins.ReadOnly, ct);
        Assert.Equal(0, await TenancyFixture.CountAsync(dbA, "select count(*) from org.companies where code = $1", ct, code));
        Assert.Equal(1, await TenancyFixture.CountAsync(dbB, "select count(*) from org.companies where code = $1", ct, code));
    }

    [Fact]
    public async Task FR_KRN_TEN_04_a_forged_or_foreign_token_is_refused_with_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostA = TenancyFixture.HostOf(fx.CodeA);
        var cases = new (string Why, string? Token)[]
        {
            ("no token", null),
            ("signed with a key the platform does not trust", fx.Token(fx.CodeA, key: forger)),
            ("valid token issued for customer B, used on A's host", fx.Token(fx.CodeB)),
            ("wrong issuer", fx.Token(fx.CodeA, issuer: "https://attacker.example")),
            ("not a token", "abc.def.ghi"),
        };
        foreach (var (why, token) in cases)
        {
            using var client = fx.Client(hostA, token);
            using var response = await client.GetAsync("/api/org/companies", ct);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{why}: got {(int)response.StatusCode}");
        }

        using var good = fx.Client(hostA, fx.Token(fx.CodeA));
        using var ok = await good.GetAsync("/api/org/companies", ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task FR_KRN_TEN_03_the_health_check_needs_no_customer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fx.Client("localhost");
        using var response = await client.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FR_KRN_TEN_09_suspended_customer_is_locked_and_resumes_without_data_loss()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "s" + fx.Suffix;
        await fx.ProvisionAsync(code, ct);
        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));
        var before = await client.GetStringAsync("/api/org/companies", ct);

        var lifecycle = new Erp.Kernel.Infrastructure.ControlPlane.Platform.TenantLifecycle(fx.Options, fx.Clock);
        await lifecycle.SuspendAsync(fx.Admin, code, "unpaid invoice (test)", ct);
        using (var locked = await client.GetAsync("/api/org/companies", ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
            Assert.Contains(TenantResolution.Locked, await locked.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        await lifecycle.ResumeAsync(fx.Admin, code, "paid (test)", ct);
        Assert.Equal(before, await client.GetStringAsync("/api/org/companies", ct));

        await using var cp = await fx.OpenControlPlaneAsync(ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = 'tenant.suspend'", ct, code));
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = 'tenant.resume'", ct, code));
    }

    [Fact]
    public async Task FR_KRN_TEN_09_FR_KRN_ENT_04_read_only_serves_reads_and_refuses_changes_and_locked_serves_only_the_licence_page_and_exports()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "r" + fx.Suffix;
        await fx.ProvisionAsync(code, ct);
        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));

        // Slice S1.09b: the state follows from the end date on the customer's day (ruling Q6); here the end date is moved.
        await fx.SetDaysAfterEndAsync(code, 20, ct); // read-only (grace 14 days, then 30 days read-only)
        using (var read = await client.GetAsync("/api/org/companies", ct))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using (var write = await client.PostAsJsonAsync("/api/org/companies", Company("RO" + fx.Suffix), ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.Contains(TenantResolution.ReadOnly, await write.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        await fx.SetDaysAfterEndAsync(code, 60, ct); // locked
        using (var read = await client.GetAsync("/api/org/companies", ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.Contains(TenantResolution.LicenceLocked, await read.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        using (var licence = await client.GetAsync("/api/cfg/licence", ct))
        {
            Assert.Equal(HttpStatusCode.OK, licence.StatusCode);
            Assert.Contains("\"state\":\"locked\"", await licence.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        using (var export = await client.GetAsync("/api/aud/export", ct))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        }

        await fx.SetDaysAfterEndAsync(code, 5, ct); // grace: full use
        using (var write = await client.PostAsJsonAsync("/api/org/companies", Company("GR" + fx.Suffix), ct))
        {
            Assert.Equal(HttpStatusCode.Created, write.StatusCode);
        }
    }

    [Fact]
    public async Task FR_KRN_ENT_03_a_customer_without_the_hrm_module_has_no_hrm_tables_or_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "k" + fx.Suffix;
        fx.Track(code);
        await fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code, modules: []), null, ct);
        await using (var db = await fx.OpenTenantAsync(code, TenantLogins.ReadOnly, ct))
        {
            Assert.Equal(0, await TenancyFixture.CountAsync(db, "select count(*) from pg_namespace where nspname = 'hrm'", ct));
        }

        using var client = fx.Client(TenancyFixture.HostOf(code), fx.Token(code));
        using var hrm = await client.GetAsync("/api/hrm/leave-types", ct);
        Assert.Equal(HttpStatusCode.NotFound, hrm.StatusCode);
        using var kernel = await client.GetAsync("/api/org/companies", ct);
        Assert.Equal(HttpStatusCode.OK, kernel.StatusCode);
    }

    [Fact]
    public async Task CR004_a_customer_login_cannot_connect_to_another_customers_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await fx.TenantAsync(fx.CodeA, ct);
        var b = await fx.TenantAsync(fx.CodeB, ct);
        foreach (var (kind, _) in TenantLogins.All)
        {
            var credential = Erp.Kernel.Infrastructure.ControlPlane.Secrets.DatabaseCredential.Parse((await fx.Secrets.GetAsync(TenantLogins.SecretRef(a.AppSecretRef, kind), ct))!);
            var builder = new NpgsqlConnectionStringBuilder { Host = b.ClusterHost, Port = b.ClusterPort, Database = b.DatabaseName, Username = credential.Username, Password = credential.Password, Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            var error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync(ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    [Trait("Invariant", "3")]
    public async Task Spec03_3_6_app_rw_cannot_update_or_delete_append_only_rows_or_change_the_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rw = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadWrite, ct);
        foreach (var sql in new[]
        {
            "update meta.schema_history set checksum = 'x'",
            "delete from meta.schema_history",
            "update aud.change_log set id = id",
            "insert into aud.change_log (id) values (gen_random_uuid())",
            "create table org.not_allowed (id int)",
            "drop table org.branches",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => TenancyFixture.ExecAsync(rw, sql, ct));
            Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{sql}: {error.SqlState} {error.MessageText}");
        }

        // Ordinary business tables are writable.
        Assert.Equal(1, await TenancyFixture.CountAsync(rw, "select count(*) from (select 1 from org.companies limit 1) x", ct));
    }

    [Fact]
    public async Task SC2_every_company_gets_a_default_branch_and_keeps_at_least_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rw = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadWrite, ct);
        var company = await TenancyFixture.CountAsync(rw, "select count(*) from org.companies c where not exists (select 1 from org.branches b where b.company_id = c.id and not b.is_deleted)", ct);
        Assert.Equal(0, company);

        var id = Guid.CreateVersion7();
        await TenancyFixture.ExecAsync(rw, """
            insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"SC2"}', 'SC2 Ltd', 'PK', 'PKR', 'en', 'none', 'active', now(), $1, now(), $1)
            """, ct, id, "SC2" + fx.Suffix);
        Assert.Equal(1, await TenancyFixture.CountAsync(rw, "select count(*) from org.branches where company_id = $1 and code = $2", ct, id, "SC2" + fx.Suffix));

        var delete = await Assert.ThrowsAsync<PostgresException>(() => TenancyFixture.ExecAsync(rw, "delete from org.branches where company_id = $1", ct, id));
        Assert.Equal(PostgresErrorCodes.CheckViolation, delete.SqlState);
        var softDelete = await Assert.ThrowsAsync<PostgresException>(() => TenancyFixture.ExecAsync(rw, "update org.branches set is_deleted = true where company_id = $1", ct, id));
        Assert.Equal("ck_branches_one_per_company", softDelete.ConstraintName);
    }
}
