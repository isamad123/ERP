using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Domain.Identity;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Erp.IntegrationTests.Web;

/// <summary>
/// Browser end-to-end tests of slices S1.12a (ruling Q19; FR-KRN-IAM-01, FR-KRN-IAM-02, FR-KRN-IAM-04, FR-ADM-USR-02,
/// FR-ADM-USR-24, FR-KRN-LOC-01) and S1.12b (shell: FR-KRN-ENT-03, FR-KRN-ENT-04, FR-KRN-TEN-09, FR-ADM-ORG-06, FR-HRM-NTF-07,
/// FR-HRM-NTF-09, NFR-11, NFR-12): the real API host on Kestrel over HTTPS (the refresh cookie is Secure) serving the built
/// application, a freshly provisioned customer on real PostgreSQL, and Playwright with the preinstalled Chromium. Time is a
/// test clock: the browser tests move it together with the browser's clock through the control server, so 30 minutes of
/// activity take seconds. The control server is test machinery only (clock, e-mailed links and codes, settings); the
/// application talks only to the API host. Needs the test build of the web application:
/// <c>pnpm -C web install --frozen-lockfile &amp;&amp; pnpm -C web build:e2e</c>.
/// </summary>
[Collection(TenancyGroup.Name)]
[Trait("Suite", "WebE2E")]
public sealed class WebEndToEndTests(TenancyFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S112a_S112b_FR_KRN_IAM_01_02_04_FR_KRN_ENT_03_04_FR_HRM_NTF_09_sign_in_and_shell_work_end_to_end_in_a_real_browser()
    {
        var repo = RepositoryRoot();
        var webRoot = Path.Combine(repo, "web", "apps", "web", "dist-e2e");
        Assert.True(File.Exists(Path.Combine(webRoot, "index.html")),
            $"The web test build is missing ({webRoot}). Run: pnpm -C web install --frozen-lockfile && pnpm -C web build:e2e");

        var code = "e" + fx.Suffix;
        await fx.ProvisionAsync(code, Ct);

        // The browser reaches the customer as <code>.localhost: a loopback name every browser treats as a secure context,
        // and one that sandboxed build machines let through (they may filter TLS by server name).
        var host = code + ".localhost";
        await using (var cp = await fx.OpenControlPlaneAsync(Ct))
        {
            await TenancyFixture.ExecAsync(cp, """
                insert into cp.tenant_domains (id, tenant_id, host, is_primary, created_at, created_by, updated_at, updated_by)
                select $1, t.id, $2, false, now(), $3, now(), $3 from cp.tenants t where t.code = $4
                """, Ct, Guid.CreateVersion7(), host, BaselineSeed.SystemUserId, code);
        }

        var clock = new TestClock();
        using var certificate = SelfSigned(host);
        var api = await fx.StartAppAsync(null, Ct,
            services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
                // Access tokens are checked against the same test clock (the real time is also accepted, for the
                // administrator token the control server makes).
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.TokenValidationParameters.LifetimeValidator =
                    (notBefore, expires, _, _) => Valid(notBefore, expires, clock.UtcNow) || Valid(notBefore, expires, DateTimeOffset.UtcNow));
            },
            settings: new Dictionary<string, string?> { ["Web:Root"] = webRoot },
            realServer: web => web.UseKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate))));
        var control = await StartControlAsync(code, host, api, clock);
        try
        {
            var apiPort = new Uri(api.Urls.Single()).Port;
            var exit = await RunPlaywrightAsync(Path.Combine(repo, "web", "apps", "web"), new Dictionary<string, string>
            {
                ["E2E_BASE_URL"] = $"https://{host}:{apiPort}",
                ["E2E_HOST"] = host,
                ["E2E_CONTROL_URL"] = control.Urls.Single(),
            });
            Assert.True(exit == 0, "Playwright end-to-end tests failed; see the output above and web/apps/web/test-results.");
        }
        finally
        {
            await control.DisposeAsync();
            await api.DisposeAsync();
        }
    }

    private static bool Valid(DateTime? notBefore, DateTime? expires, DateTimeOffset now) =>
        (notBefore is null || notBefore.Value <= now.UtcDateTime) && expires is { } e && e > now.UtcDateTime;

    /// <summary>The control server of the browser tests (loopback only, test process only).</summary>
    private async Task<WebApplication> StartControlAsync(string code, string host, WebApplication api, TestClock clock)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var apiUrl = new Uri(api.Urls.Single());

        app.MapGet("/clock", () => Results.Ok(new { now = clock.UtcNow.ToUnixTimeMilliseconds() }));
        app.MapPost("/clock/advance", (Advance body) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(body.Milliseconds));
            return Results.Ok(new { now = clock.UtcNow.ToUnixTimeMilliseconds() });
        });

        // A new invited person, with the invitation e-mailed by the real endpoint (as a tenant administrator would). S1.12b:
        // the person holds the baseline business user role for all companies (the shell reads their own permissions); the
        // language and display name can be chosen.
        app.MapPost("/users", async (HttpRequest request) =>
        {
            var body = request.ContentLength > 0 ? await request.ReadFromJsonAsync<UserBody>(Ct) : null;
            var username = "e2e" + Guid.NewGuid().ToString("N")[..8];
            var id = Guid.CreateVersion7(clock.UtcNow);
            await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
            {
                await IdentityHarness.ExecAsync(owner, """
                    insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $2 || '@example.test', $5, $6, 'Asia/Karachi', false, 'invited', $3, $4, $3, $4)
                    """, Ct, id, username, clock.UtcNow, BaselineSeed.SystemUserId, body?.DisplayName ?? username, body?.Language ?? "en");
                await IdentityHarness.ExecAsync(owner, """
                    insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                    select $1, $2, r.id, null, 'browser test', $3, now(), $4, now(), $4 from iam.roles r where r.code = 'business_user' and not r.is_deleted
                    """, Ct, Guid.CreateVersion7(), id, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-1), BaselineSeed.SystemUserId);
            }

            using var client = ApiClient(apiUrl, host, fx.Token(code));
            using var invited = await client.PostAsJsonAsync($"/api/iam/users/{id}/invitation", new { }, Ct);
            return invited.IsSuccessStatusCode ? Results.Ok(new { userId = id, username, email = username + "@example.test" }) : Results.Problem($"invitation {(int)invited.StatusCode}");
        });

        // The link or code of the person's latest identity e-mail of the event, decrypted as the sender job does.
        app.MapGet("/mail/{eventCode}", async (string eventCode, string username) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await using var command = new NpgsqlCommand("""
                select m.secret from ntf.outbound_messages m join iam.users u on u.id = m.user_id
                where lower(u.username) = lower($1) and m.event_code = $2 order by m.created_at desc, m.xmin::text::bigint desc limit 1
                """, owner);
            command.Parameters.Add(new NpgsqlParameter { Value = username });
            command.Parameters.Add(new NpgsqlParameter { Value = eventCode });
            if (await command.ExecuteScalarAsync(Ct) is not byte[] secret)
            {
                return Results.NotFound();
            }

            var key = await fx.Secrets.GetAsync(TenantFieldKeys.Reference(code), Ct) ?? throw new InvalidOperationException("no field key");
            var plain = new AesGcmFieldEncryptor(Convert.FromBase64String(key), TenantFieldKeys.KeyVersion).Decrypt(secret, Kernel.Infrastructure.Notifications.Email.EmailOutbox.SecretPurpose);
            return Results.Text(plain, "application/json");
        });

        // The authenticator code of a secret at the server's (test) time.
        app.MapGet("/totp", (string secret, int? offsetSeconds) =>
            Results.Ok(new { code = Totp.Code(Base32.Decode(secret), Totp.StepAt(clock.UtcNow + TimeSpan.FromSeconds(offsetSeconds ?? 0))) }));

        // A customer-level setting (as the settings centre will set it, S1.15).
        app.MapPost("/settings", async (SettingBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await IdentityHarness.ExecAsync(owner, """
                insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 'tenant', null, $3::jsonb, $4, $5, $4, $5)
                on conflict (setting_key, layer, scope_id) do update set value = excluded.value, updated_at = excluded.updated_at, row_version = cfg.setting_values.row_version + 1
                """, Ct, Guid.CreateVersion7(), body.Key, body.Json, clock.UtcNow, BaselineSeed.SystemUserId);
            return Results.NoContent();
        });

        // S1.12b: a customer role with exactly these permissions (scope "all"), given to the person for all companies or for
        // the companies named by code (as the admin console will, S1.13).
        app.MapPost("/roles", async (RoleBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var role = Guid.CreateVersion7();
            await IdentityHarness.ExecAsync(owner, """
                insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by)
                values ($1, $2, '{"en":"Browser test role"}', 'tenant', now(), $3, now(), $3)
                """, Ct, role, "e2e" + Guid.NewGuid().ToString("N")[..10], BaselineSeed.SystemUserId);
            await IdentityHarness.ExecAsync(owner, "insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2)", Ct, role, body.Permissions);
            await IdentityHarness.ExecAsync(owner, "insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) select $1, '*', id from iam.data_scopes where code = 'all'", Ct, role);
            await IdentityHarness.ExecAsync(owner, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select gen_random_uuid(), u.id, $2, c.id, 'browser test', $3, now(), $4, now(), $4
                from iam.users u left join org.companies c on $5::text[] is not null and c.code = any($5)
                where lower(u.username) = lower($1) and ($5::text[] is null or c.id is not null)
                """, Ct, body.Username, role, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-1), BaselineSeed.SystemUserId, (object?)body.Companies ?? DBNull.Value);
            if (body.RemoveBaseline)
            {
                await IdentityHarness.ExecAsync(owner, """
                    delete from iam.user_roles ur using iam.users u, iam.roles r
                    where ur.user_id = u.id and lower(u.username) = lower($1) and ur.role_id = r.id and r.code = 'business_user'
                    """, Ct, body.Username);
            }

            return Results.NoContent();
        });

        // S1.12b: another company of the customer (as the organization screens will add one, S1.14); the database adds its branch.
        app.MapPost("/companies", async (CompanyBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var id = Guid.CreateVersion7();
            await IdentityHarness.ExecAsync(owner, """
                insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, jsonb_build_object('en', $3::text, 'ur', $4::text), $3, 'PK', 'PKR', 'en', 'none', 'active', now(), $5, now(), $5)
                """, Ct, id, body.Code, body.En, body.Ur, BaselineSeed.SystemUserId);
            return Results.Ok(new { id });
        });

        // S1.12b: the licence in force: days after its end on the test clock's day (null: no end), licensed features, the
        // company limit (as the licence loop and `entitlement apply` change them, S1.09b).
        app.MapPost("/licence", async (LicenceBody body) =>
        {
            await using var cp = await fx.OpenControlPlaneAsync(Ct);
            var before = await TenancyFixture.ScalarTextAsync(cp, "select e.claims::text from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1 and e.status not in ('superseded', 'scheduled')", Ct, code);
            if (body.SetEnd)
            {
                var today = Erp.Kernel.Domain.Entitlements.LicenceStates.Today(clock.UtcNow, Erp.Kernel.Domain.Entitlements.LicenceStates.Zone("Asia/Karachi"));
                await TenancyFixture.ExecAsync(cp, """
                    update cp.entitlements e set expires_at = $2, effective_from = least(e.effective_from, coalesce($2, e.effective_from))
                    from cp.tenants t where t.id = e.tenant_id and t.code = $1 and e.status not in ('superseded', 'scheduled')
                    """, Ct, code, body.DaysAfterEnd is { } d ? today.AddDays(-d) : DBNull.Value);
            }

            if (body.Features is { } features)
            {
                await fx.SetClaimAsync(code, "features", new System.Text.Json.Nodes.JsonArray([.. features.Select(f => (System.Text.Json.Nodes.JsonNode?)f)]), Ct);
            }

            if (body.CompaniesLimit is { } limit)
            {
                await fx.SetClaimAsync(code, "limits,companies", limit, Ct);
            }

            return Results.Ok(new { claims = before });
        });

        // S1.12b: puts back the claims a test changed (the answer of /licence).
        app.MapPost("/licence/claims", async (ClaimsBody body) =>
        {
            await fx.RestoreClaimsAsync(code, body.Claims, Ct);
            return Results.NoContent();
        });

        // S1.12b: in-app notifications of a person, as the notification consumer writes them (S1.11b1).
        app.MapPost("/notifications", async (NotificationsBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var at = clock.UtcNow;
            foreach (var item in body.Items)
            {
                at = at.AddSeconds(1);
                await IdentityHarness.ExecAsync(owner, """
                    insert into ntf.in_app_items (id, user_id, event_code, title, body, link, event_group, dedup_key, created_at, created_by, updated_at, updated_by)
                    select $1, u.id, $3, $4, $5, $6, $7, $1::text, $8, $9, $8, $9 from iam.users u where lower(u.username) = lower($2)
                    """, Ct, Guid.CreateVersion7(at), body.Username, item.EventCode, item.Title, item.Body, (object?)item.Link ?? DBNull.Value, item.Group, at, BaselineSeed.SystemUserId);
            }

            return Results.NoContent();
        });

        // S1.12c: branches of a company (default: the customer's first company), as the branch screen would add them one by
        // one, for the list's paging and its first-page timing (Acceptance: 100 rows within 1 second).
        app.MapPost("/branches", async (BranchesBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await IdentityHarness.ExecAsync(owner, """
                insert into org.branches (id, company_id, code, name_i18n, is_active, created_at, created_by, updated_at, updated_by)
                select gen_random_uuid(), c.id, $2 || '-' || lpad(n::text, 3, '0'), jsonb_build_object('en', 'Branch ' || $2 || ' ' || n, 'ur', 'برانچ ' || $2 || ' ' || n),
                       n % 7 <> 0, now(), $4, now(), $4
                from (select id from org.companies where ($1::text is null and lower(code) = lower($5)) or lower(code) = lower($1) limit 1) c, generate_series(1, $3) n
                """, Ct, (object?)body.Company ?? DBNull.Value, body.Prefix, body.Count, BaselineSeed.SystemUserId, code);
            return Results.NoContent();
        });

        // S1.12c: a label override of the customer (cfg.labels, layer tenant), as the settings centre will write it (S1.15);
        // a null text removes it.
        app.MapPost("/labels", async (LabelBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await IdentityHarness.ExecAsync(owner, "delete from cfg.labels where key = $1 and language = $2 and layer = 'tenant'", Ct, body.Key, body.Language);
            if (body.Text is { } text)
            {
                await IdentityHarness.ExecAsync(owner, """
                    insert into cfg.labels (id, key, language, layer, scope_id, text, created_at, created_by, updated_at, updated_by)
                    values (gen_random_uuid(), $1, $2, 'tenant', null, $3, now(), $4, now(), $4)
                    """, Ct, body.Key, body.Language, text, BaselineSeed.SystemUserId);
            }

            return Results.NoContent();
        });

        // Facts the browser cannot see: live sessions and trusts of a person.
        app.MapGet("/facts", async (string username) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await using var command = new NpgsqlCommand("""
                select (select count(*) from iam.sessions s where s.user_id = u.id and s.revoked_at is null),
                       (select count(*) from iam.sessions s where s.user_id = u.id and s.revoked_reason = 'refresh_reuse'),
                       (select count(*) from iam.trusted_devices d where d.user_id = u.id and d.revoked_at is null)
                from iam.users u where lower(u.username) = lower($1)
                """, owner);
            command.Parameters.Add(new NpgsqlParameter { Value = username });
            await using var reader = await command.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            return Results.Ok(new { liveSessions = reader.GetInt64(0), reusedSessions = reader.GetInt64(1), trustedDevices = reader.GetInt64(2) });
        });

        await app.StartAsync(Ct);
        return app;
    }

    private static HttpClient ApiClient(Uri apiUrl, string host, string token)
    {
#pragma warning disable CA2000, CA5400 // test client of the test server's self-signed certificate
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
#pragma warning restore CA2000, CA5400
        var client = new HttpClient(handler, disposeHandler: true) { BaseAddress = apiUrl };
        client.DefaultRequestHeaders.Host = $"{host}:{apiUrl.Port}";
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<int> RunPlaywrightAsync(string appFolder, Dictionary<string, string> environment)
    {
        // E2E_GREP runs only the matching scenarios (local debugging); CI runs them all.
        var grep = Environment.GetEnvironmentVariable("E2E_GREP");
        var start = new ProcessStartInfo("pnpm", grep is { Length: > 0 } ? $"exec playwright test --grep \"{grep.Replace("\"", string.Empty, StringComparison.Ordinal)}\"" : "exec playwright test")
        {
            WorkingDirectory = appFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start pnpm");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        TestContext.Current.SendDiagnosticMessage(await output);
        TestContext.Current.SendDiagnosticMessage(await errors);
        Console.WriteLine(await output);
        Console.Error.WriteLine(await errors);
        return process.ExitCode;
    }

    private static X509Certificate2 SelfSigned(string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private sealed record Advance(long Milliseconds);

    private sealed record SettingBody(string Key, string Json);

    private sealed record UserBody(string? DisplayName, string? Language);

    private sealed record RoleBody(string Username, string[] Permissions, string[]? Companies, bool RemoveBaseline);

    private sealed record CompanyBody(string Code, string En, string Ur);

    private sealed record ClaimsBody(string Claims);

    private sealed record LicenceBody(bool SetEnd, int? DaysAfterEnd, string[]? Features, int? CompaniesLimit);

    private sealed record NotificationsBody(string Username, NotificationBody[] Items);

    private sealed record NotificationBody(string EventCode, string Title, string Body, string? Link, string Group);

    private sealed record BranchesBody(string? Company, string Prefix, int Count);

    private sealed record LabelBody(string Key, string Language, string? Text);
}
