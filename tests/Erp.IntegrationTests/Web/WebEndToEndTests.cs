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
/// Browser end-to-end tests of slice S1.12a (ruling Q19; FR-KRN-IAM-01, FR-KRN-IAM-02, FR-KRN-IAM-04, FR-ADM-USR-02,
/// FR-ADM-USR-24, FR-KRN-LOC-01): the real API host on Kestrel over HTTPS (the refresh cookie is Secure) serving the built
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
    public async Task S112a_FR_KRN_IAM_01_02_04_sign_in_screens_work_end_to_end_in_a_real_browser()
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

        // A new invited person, with the invitation e-mailed by the real endpoint (as a tenant administrator would).
        app.MapPost("/users", async () =>
        {
            var username = "e2e" + Guid.NewGuid().ToString("N")[..8];
            var id = Guid.CreateVersion7(clock.UtcNow);
            await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
            {
                await IdentityHarness.ExecAsync(owner, """
                    insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $2 || '@example.test', $2, 'en', 'Asia/Karachi', false, 'invited', $3, $4, $3, $4)
                    """, Ct, id, username, clock.UtcNow, BaselineSeed.SystemUserId);
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
}
