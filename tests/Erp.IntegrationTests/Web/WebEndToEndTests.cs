#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Documents;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.IntegrationTests.Workflow;
using Erp.Kernel.Domain.Identity;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Workflow;
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

        // S1.12d: the test workflow module (document types, outcome handlers and its start endpoint), as a module would
        // register them, so the inbox has real requests to decide; its routes are seeded like provisioning seeds a module's.
        await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
        {
            await WorkflowSetup.EnsureAsync(owner, null, WorkflowRig.Types, DateTimeOffset.UtcNow, Ct);
        }

        var clock = new TestClock();
        using var certificate = SelfSigned(host);
        var api = await fx.StartAppAsync(WorkflowRig.MapModule, Ct,
            services =>
            {
                WorkflowRig.RegisterTestModule(services);
                // S1.15a: the login logo is a stored file; the browser tests use a folder store and a scanner that finds nothing.
                DocumentTestServices.Register(services, Path.Combine(Path.GetTempPath(), "erp-e2e-files-" + Guid.NewGuid().ToString("N")));
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
                // Access tokens are checked against the same test clock (the real time is also accepted, for the
                // administrator token the control server makes).
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.TokenValidationParameters.LifetimeValidator =
                    (notBefore, expires, _, _) => Valid(notBefore, expires, clock.UtcNow) || Valid(notBefore, expires, DateTimeOffset.UtcNow));
            },
            realAccess: true, // S1.13b (ruling Q10, R-072, R-082): the browser tests run on the real access engine
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

        // S1.13b (ruling Q10): the control server acts as a real tenant administrator (a person with the baseline role), not as the
        // system user, because the browser tests run on the real access engine.
        var controlAdmin = Guid.CreateVersion7(clock.UtcNow);
        await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
        {
            await IdentityHarness.ExecAsync(owner, """
                insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                values ($1, 'control.admin', 'control.admin@example.test', 'Control administrator', 'en', 'Asia/Karachi', false, 'active', $2, $3, $2, $3)
                """, Ct, controlAdmin, clock.UtcNow, BaselineSeed.SystemUserId);
            await IdentityHarness.ExecAsync(owner, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, r.id, null, 'browser test control server', $3, now(), $4, now(), $4 from iam.roles r where r.code = 'tenant_admin' and not r.is_deleted
                """, Ct, Guid.CreateVersion7(), controlAdmin, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-1), BaselineSeed.SystemUserId);
        }

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

            using var client = ApiClient(apiUrl, host, fx.Token(code, user: controlAdmin));
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

        // S1.15b: a field rule on every role of the person (a custom field is `custom.<key>`), written as the field matrix of S1.13b writes it.
        app.MapPost("/field-policy", async (FieldPolicyBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await IdentityHarness.ExecAsync(owner, """
                delete from iam.field_policies fp using iam.user_roles ur, iam.users u
                where ur.user_id = u.id and lower(u.username) = lower($1) and fp.role_id = ur.role_id and fp.resource_type = $2 and fp.field_key = $3
                """, Ct, body.Username, body.Resource, body.Field);
            await IdentityHarness.ExecAsync(owner, """
                insert into iam.field_policies (id, role_id, resource_type, field_key, access, created_at, created_by, updated_at, updated_by)
                select gen_random_uuid(), ur.role_id, $2, $3, $4, now(), $5, now(), $5
                from iam.user_roles ur join iam.users u on u.id = ur.user_id join iam.roles r on r.id = ur.role_id
                where lower(u.username) = lower($1) and r.source = 'tenant'
                """, Ct, body.Username, body.Resource, body.Field, body.Access, BaselineSeed.SystemUserId);
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

        // S1.14a: reference rows the country pack installs later (S1.23): the US dollar and a bank, for the organization screens.
        app.MapPost("/reference", async () =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await IdentityHarness.ExecAsync(owner, "insert into ref.currencies (code, name_i18n, symbol, decimals, is_active) values ('USD', '{\"en\":\"US dollar\"}', '$', 2, true) on conflict do nothing", Ct);
            await IdentityHarness.ExecAsync(owner, """
                insert into ref.banks (id, code, name_i18n, country_code, created_at, created_by, updated_at, updated_by)
                values ($1, 'TESTBANK', '{"en":"Test Bank"}', 'PK', now(), $2, now(), $2) on conflict do nothing
                """, Ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
            return Results.NoContent();
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

            if (body.UsersLimit is { } users)
            {
                await fx.SetClaimAsync(code, "limits,users.named", users, Ct); // S1.13a: the named-user limit
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

        // S1.14b: a reporting hierarchy with a five-level structure in the customer's first company, linked from 1 January 2026:
        // <prefix>-ROOT, under it <prefix>-DIVA and <prefix>-DIVB, <prefix>-DEPT under DIVA, <prefix>-TEAM under DEPT, and
        // <prefix>-LOOSE that is in no place. Answers the hierarchy id.
        app.MapPost("/org-structure", async (OrgStructureBody body) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var system = BaselineSeed.SystemUserId;
            var hierarchy = Guid.CreateVersion7();
            var type = Guid.CreateVersion7();
            await using var find = new NpgsqlCommand("select id from org.companies where lower(code) = lower($1)", owner) { Parameters = { new NpgsqlParameter { Value = code } } };
            var company = (Guid)(await find.ExecuteScalarAsync(Ct))!;
            await IdentityHarness.ExecAsync(owner, "insert into org.org_hierarchies (id, code, name_i18n, kind, created_at, created_by, updated_at, updated_by) values ($1, $2, $3::jsonb, 'reporting', now(), $4, now(), $4)", Ct,
                hierarchy, body.Prefix + "-reporting", "{\"en\":\"Reporting " + body.Prefix + "\",\"ur\":\"رپورٹنگ " + body.Prefix + "\"}", system);
            await IdentityHarness.ExecAsync(owner, "insert into org.org_unit_types (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, $3::jsonb, now(), $4, now(), $4)", Ct,
                type, body.Prefix + "-TYPE", "{\"en\":\"Department\",\"ur\":\"شعبہ\"}", system);
            var ids = new Dictionary<string, Guid>();
            foreach (var (name, parent) in new (string, string?)[] { ("ROOT", null), ("DIVA", "ROOT"), ("DIVB", "ROOT"), ("DEPT", "DIVA"), ("TEAM", "DEPT"), ("LOOSE", "-") })
            {
                var id = Guid.CreateVersion7();
                ids[name] = id;
                await IdentityHarness.ExecAsync(owner, "insert into org.org_units (id, company_id, code, name_i18n, unit_type_id, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4::jsonb, $5, now(), $6, now(), $6)", Ct,
                    id, company, body.Prefix + "-" + name, "{\"en\":\"Unit " + name + "\",\"ur\":\"یونٹ " + name + "\"}", type, system);
                if (parent != "-")
                {
                    await IdentityHarness.ExecAsync(owner, "insert into org.org_unit_links (id, hierarchy_id, unit_id, parent_unit_id, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4, '2026-01-01', now(), $5, now(), $5)", Ct,
                        Guid.CreateVersion7(), hierarchy, id, parent is null ? null : ids[parent], system);
                }
            }

            return Results.Ok(new { hierarchy });
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

        // S1.12d: a route for a test document type: one step decided by the named person, bulk approval allowed or not (as
        // the route editor will put one in force, S1.15), activated with the administrator token.
        app.MapPost("/workflow/route", async (RouteBody body) =>
        {
            var approver = await UserIdAsync(code, body.Approver);
            using var client = ApiClient(apiUrl, host, fx.Token(code, user: controlAdmin));
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var definition = await TenancyFixture.ScalarTextAsync(owner, "select id::text from wf.workflow_definitions where document_type = $1", Ct, body.DocumentType);
            var route = System.Text.Json.JsonSerializer.Serialize(new
            {
                route = new { steps = new[] { new { key = "manager", name = new { en = "Manager approval", ur = "مینیجر کی منظوری" }, approver = new { kind = "user", user_id = approver }, bulk_approval = body.Bulk } } },
            });
            using var draft = await client.PostAsync($"/api/wf/definitions/{definition}/versions", new StringContent(route, System.Text.Encoding.UTF8, "application/json"), Ct);
            if (!draft.IsSuccessStatusCode)
            {
                return Results.Problem($"draft {(int)draft.StatusCode} {await draft.Content.ReadAsStringAsync(Ct)}");
            }

            var version = (await draft.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("id").GetGuid();
            using var activated = await client.PostAsJsonAsync($"/api/wf/versions/{version}/activate", new { rowVersion = 1 }, Ct);
            return activated.IsSuccessStatusCode ? Results.NoContent() : Results.Problem($"activate {(int)activated.StatusCode} {await activated.Content.ReadAsStringAsync(Ct)}");
        });

        // S1.12d: a request of the test module started by the named person in the customer's first company (as a module's
        // save starts one); answers the request and its open tasks.
        app.MapPost("/workflow/start", async (StartBody body) =>
        {
            var requester = await UserIdAsync(code, body.Requester);
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var company = Guid.Parse((await TenancyFixture.ScalarTextAsync(owner, "select id::text from org.companies where lower(code) = lower($1)", Ct, code))!);
            using var client = ApiClient(apiUrl, host, fx.Token(code, user: requester));
            using var started = await client.PostAsJsonAsync("/test/wf/start", new
            {
                documentType = body.DocumentType,
                documentId = Guid.CreateVersion7(),
                companyId = company,
                subjects = Array.Empty<Guid>(),
                values = new Dictionary<string, object> { ["company"] = company, ["amount"] = body.Amount, ["category"] = "travel" },
            }, Ct);
            if (!started.IsSuccessStatusCode)
            {
                return Results.Problem($"start {(int)started.StatusCode} {await started.Content.ReadAsStringAsync(Ct)}");
            }

            var instance = (await started.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("instanceId").GetGuid();
            var tasks = await TenancyFixture.ScalarTextAsync(owner, "select string_agg(id::text, ',') from wf.workflow_tasks where instance_id = $1 and status = 'pending'", Ct, instance);
            return Results.Ok(new { instanceId = instance, taskIds = (tasks ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries) });
        });

        // S1.12d: the named person hands their approvals to another for a few days (self-service delegation, S1.26).
        app.MapPost("/delegations", async (DelegationBody body) =>
        {
            var from = await UserIdAsync(code, body.From);
            var to = await UserIdAsync(code, body.To);
            var today = DateOnly.FromDateTime(clock.UtcNow.ToOffset(TimeSpan.FromHours(5)).DateTime);
            using var client = ApiClient(apiUrl, host, fx.Token(code, user: from));
            using var created = await client.PostAsJsonAsync("/api/iam/me/delegations", new { toUserId = to, from = today, to = today.AddDays(5), reason = "leave" }, Ct);
            return created.IsSuccessStatusCode ? Results.NoContent() : Results.Problem($"delegation {(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        });

        // S1.12d: a customer setting that needs a second administrator, changed by the administrator token (held for
        // approval, 202); answers the key and the held request. GET answers the setting's customer value.
        app.MapPost("/changes/setting", async (HeldSettingBody body) =>
        {
            var key = "test.e2e_" + Guid.NewGuid().ToString("N")[..8];
            await using (var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct))
            {
                await IdentityHarness.ExecAsync(owner, """
                    insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n, name_i18n, is_visible, area, role_rule, self_service, tighten_only)
                    values ($1, 'test', 'int', '10', '{"min":1,"max":1000}', '{tenant}', true, '{"en":"Days to keep drafts","ur":"مسودے رکھنے کے دن"}', '{"en":"Days to keep drafts","ur":"مسودے رکھنے کے دن"}', true, 'test', 'highest', false, false)
                    """, Ct, key);
            }

            using var client = ApiClient(apiUrl, host, fx.Token(code, user: controlAdmin));
            using var held = await client.PutAsJsonAsync($"/api/cfg/settings/{key}", new { layer = "tenant", scopeId = (Guid?)null, value = body.Value, reason = body.Reason }, Ct);
            var text = await held.Content.ReadAsStringAsync(Ct);
            return held.StatusCode == HttpStatusCode.Accepted
                ? Results.Ok(new { key, requestId = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("requestId").GetGuid() })
                : Results.Problem($"setting {(int)held.StatusCode} {text}");
        });
        // Removes the customer-level value of a setting (back to the inherited or standard value); scenarios call it to restore what they changed.
        app.MapDelete("/settings", async (string key) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            await IdentityHarness.ExecAsync(owner, "delete from cfg.setting_values where setting_key = $1 and layer = 'tenant'", Ct, key);
            return Results.NoContent();
        });
        app.MapGet("/settings/value", async (string key) =>
        {
            await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            return Results.Ok(new { value = await TenancyFixture.ScalarTextAsync(owner, "select value::text from cfg.setting_values where setting_key = $1 and layer = 'tenant'", Ct, key) });
        });

        // S1.30b (R-162): the attendance of the person and three colleagues in the customer's first company, written as rows the HR services write (RowFactory):
        // the person is an employee (current assignment in a unit of its own) with an open exception two days ago, a waiting correction request and a waiting
        // duty request; today everyone works a shift from 00:00 to 23:59: the person and one colleague have punched, one colleague is on approved leave, one has no
        // punch yet. No attendance policy is assigned, so the dashboard shows those with a shift and no punch as "no policy" (no number has a default).
        // Answers the ids the browser tests look for.
        app.MapPost("/attendance/seed", async (AttendanceSeedBody body) =>
        {
            try
            {
            await using var c = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
            var catalog = Erp.IntegrationTests.Infrastructure.MigratedDatabases.Catalog;
            var user = (Guid)(await new NpgsqlCommand("select id from iam.users where lower(username) = lower($1)", c) { Parameters = { new NpgsqlParameter { Value = body.Username } } }.ExecuteScalarAsync(Ct))!;
            var company = (Guid)(await new NpgsqlCommand("select id from org.companies where lower(code) = lower($1)", c) { Parameters = { new NpgsqlParameter { Value = code } } }.ExecuteScalarAsync(Ct))!;
            var branch = (Guid)(await new NpgsqlCommand("select id from org.branches where company_id = $1 order by created_at, id limit 1", c) { Parameters = { new NpgsqlParameter { Value = company } } }.ExecuteScalarAsync(Ct))!;
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
            var now = clock.UtcNow;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            await using var tx = await c.BeginTransactionAsync(Ct);
            var factory = new Erp.IntegrationTests.Infrastructure.RowFactory(c, tx, catalog, 700_000 + Random.Shared.Next(0, 200_000), company);
            async Task<IReadOnlyDictionary<string, object?>> Row(string table, params (string Column, object? Value)[] values) =>
                await factory.InsertAsync(catalog.Tables.Single(t => t.Database == "tenant" && t.FullName == table), values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), Ct);
            var unit = (Guid)(await Row("org.org_units"))["id"]!;
            async Task<Guid> Hire(Guid? account)
            {
                var employment = (Guid)(await Row("hrm.employments", ("company_id", company), ("user_ref", account), ("status", "active"), ("employee_type", "permanent"), ("is_primary", true),
                    ("joining_date", today.AddDays(-90)), ("service_start_date", today.AddDays(-90)), ("separation_date", null), ("previous_employment_id", null)))["id"]!;
                var change = (Guid)(await Row("hrm.employment_events", ("company_id", company), ("employment_id", employment)))["id"]!;
                await Row("hrm.employment_assignments", ("company_id", company), ("employment_id", employment), ("event_id", change), ("org_unit_id", unit), ("branch_id", branch),
                    ("valid_from", today.AddDays(-90)), ("valid_to", null), ("location_id", null), ("manager_employment_id", null), ("pay_group_id", null));
                return employment;
            }

            var me = await Hire(user);
            var punched = await Hire(null);
            var onLeave = await Hire(null);
            var silent = await Hire(null);
            var shift = (Guid)(await Row("hrm.shifts", ("company_id", company), ("start_time", new TimeOnly(0, 0)), ("end_time", new TimeOnly(23, 59)), ("crosses_midnight", false)))["id"]!;
            var roster = (Guid)(await Row("hrm.rosters", ("company_id", company), ("org_unit_id", unit), ("status", "published"), ("period_start", today.AddDays(-1)), ("period_end", today.AddDays(1))))["id"]!;
            foreach (var e in new[] { me, punched, onLeave, silent })
            {
                await Row("hrm.roster_lines", ("company_id", company), ("roster_id", roster), ("employment_id", e), ("work_date", today), ("shift_id", shift), ("day_type", "working"), ("roster_status", "published"), ("reason", "Seeded for the browser tests"));
            }

            foreach (var e in new[] { me, punched })
            {
                await Row("hrm.attendance_events", ("company_id", company), ("employment_id", e), ("event_date", today), ("event_at", now.AddMinutes(-2)), ("direction", "in"), ("source", "hr_entry"),
                    ("is_duplicate", false), ("supersedes_event_id", null), ("device_id", null), ("received_at", now.AddMinutes(-2)));
            }

            await Row("hrm.leave_request_days", ("company_id", company), ("employment_id", onLeave), ("leave_date", today), ("request_status", "approved"));
            var day = (Guid)(await Row("hrm.attendance_days", ("company_id", company), ("employment_id", me), ("work_date", today.AddDays(-2)), ("superseded_at", null), ("result", "incomplete")))["id"]!;
            var exception = (Guid)(await Row("hrm.attendance_exceptions", ("company_id", company), ("employment_id", me), ("attendance_day_id", day), ("work_date", today.AddDays(-2)), ("kind", "missing_out"), ("status", "open")))["id"]!;
            var correction = (Guid)(await Row("hrm.regularization_requests", ("company_id", company), ("employment_id", me), ("exception_id", null), ("work_date", today.AddDays(-3)), ("reason", "Seeded correction"), ("status", "submitted"),
                ("requested_in", now.AddDays(-3)), ("requested_out", null), ("workflow_instance_id", null), ("created_by", user)))["id"]!;
            var duty = (Guid)(await Row("hrm.duty_requests", ("company_id", company), ("employment_id", me), ("kind", "official_visit"), ("status", "submitted"), ("purpose", "Seeded visit"),
                ("start_at", now.AddDays(2)), ("end_at", now.AddDays(2).AddHours(8)), ("workflow_instance_id", null), ("created_by", user)))["id"]!;
            await tx.CommitAsync(Ct);
            return Results.Ok(new { company, unit, me, punched, onLeave, silent, exception, correction, duty });
            }
            catch (Exception ex)
            {
                return Results.Text(ex.ToString(), statusCode: 500); // the seeding says why it failed (test machinery)
            }
        });

        // S1.33a: leave for the named person, who is the line manager of four reports (and an employee without a manager of their own). Everything goes through the
        // real HR API as an HR administrator (leave types with design-token colours, leave and shift policies, grants, requests applied on behalf of the reports, a decision
        // by the manager), except the people and the holiday calendar, which are rows. Next week: two reports on approved leave on Tuesday and Wednesday, one report waiting
        // for the named person's decision (the approval card), the person's own waiting request; Thursday is a public holiday. Answers the ids the browser tests look for.
        app.MapPost("/leave/seed", async (LeaveSeedBody body) =>
        {
            try
            {
                var user = await UserIdAsync(code, body.Username);
                var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
                var monday = today.AddDays(8 - (int)today.DayOfWeek + (today.DayOfWeek == DayOfWeek.Sunday ? -7 : 0)); // the Monday after this week
                while (monday.DayOfWeek != DayOfWeek.Monday)
                {
                    monday = monday.AddDays(1);
                }

                if (monday.AddDays(6).Year != monday.Year)
                {
                    monday = new DateOnly(monday.Year + 1, 1, 12);
                    while (monday.DayOfWeek != DayOfWeek.Monday)
                    {
                        monday = monday.AddDays(1);
                    }
                }

                await using var c = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
                var catalog = Erp.IntegrationTests.Infrastructure.MigratedDatabases.Catalog;
                var company = (Guid)(await new NpgsqlCommand("select id from org.companies where lower(code) = lower($1)", c) { Parameters = { new NpgsqlParameter { Value = code } } }.ExecuteScalarAsync(Ct))!;
                var branch = (Guid)(await new NpgsqlCommand("select id from org.branches where company_id = $1 order by created_at, id limit 1", c) { Parameters = { new NpgsqlParameter { Value = company } } }.ExecuteScalarAsync(Ct))!;
                var hr = Guid.CreateVersion7(clock.UtcNow);
                await IdentityHarness.ExecAsync(c, """
                    insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                    values ($1, $4, $5, 'Leave HR', 'en', 'Asia/Karachi', false, 'active', $2, $3, $2, $3)
                    """, Ct, hr, clock.UtcNow, BaselineSeed.SystemUserId, "leave.hr." + hr.ToString("N")[^8..], "leave.hr." + hr.ToString("N")[^8..] + "@example.test");
                await IdentityHarness.ExecAsync(c, """
                    insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                    select $1, $2, r.id, null, 'browser test leave seed', $3, now(), $4, now(), $4 from iam.roles r where r.code = 'hr_admin' and not r.is_deleted
                    """, Ct, Guid.CreateVersion7(), hr, today.AddDays(-1), BaselineSeed.SystemUserId);

                var joined = today.AddDays(-400);
                await using var tx = await c.BeginTransactionAsync(Ct);
                var factory = new Erp.IntegrationTests.Infrastructure.RowFactory(c, tx, catalog, 800_000 + Random.Shared.Next(0, 100_000), company);
                async Task<IReadOnlyDictionary<string, object?>> Row(string table, params (string Column, object? Value)[] values) =>
                    await factory.InsertAsync(catalog.Tables.Single(t => t.Database == "tenant" && t.FullName == table), values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), Ct);
                var unit = (Guid)(await Row("org.org_units"))["id"]!;
                async Task<Guid> Hire(Guid? account, Guid? manager)
                {
                    var employment = (Guid)(await Row("hrm.employments", ("company_id", company), ("user_ref", account), ("status", "active"), ("employee_type", "permanent"), ("is_primary", true),
                        ("joining_date", joined), ("service_start_date", joined), ("separation_date", null), ("previous_employment_id", null)))["id"]!;
                    var change = (Guid)(await Row("hrm.employment_events", ("company_id", company), ("employment_id", employment)))["id"]!;
                    await Row("hrm.employment_assignments", ("company_id", company), ("employment_id", employment), ("event_id", change), ("org_unit_id", unit), ("branch_id", branch),
                        ("valid_from", joined), ("valid_to", null), ("location_id", null), ("manager_employment_id", manager), ("pay_group_id", null));
                    return employment;
                }

                var me = await Hire(user, null);
                var reports = new List<Guid>();
                for (var i = 0; i < 4; i++)
                {
                    reports.Add(await Hire(null, me));
                }

                var calendar = (Guid)(await Row("ref.calendars"))["id"]!;
                await Row("ref.holidays", ("calendar_id", calendar), ("holiday_date", monday.AddDays(3)), ("kind", "public"), ("is_tentative", false));
                await new NpgsqlCommand("update org.companies set holiday_calendar_id = $2 where id = $1", c, tx) { Parameters = { new NpgsqlParameter { Value = company }, new NpgsqlParameter { Value = calendar } } }.ExecuteNonQueryAsync(Ct);
                await tx.CommitAsync(Ct);

                using var admin = ApiClient(apiUrl, host, fx.Token(code, user: hr));
                async Task<System.Text.Json.JsonElement> Send(HttpClient client, HttpMethod method, string url, object? payload = null)
                {
                    using var request = new HttpRequestMessage(method, url) { Content = payload is null ? null : JsonContent.Create(payload) };
                    using var response = await client.SendAsync(request, Ct);
                    var text = await response.Content.ReadAsStringAsync(Ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"{method} {url} {(int)response.StatusCode} {text}");
                    }

                    return text.Length == 0 ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
                }

                async Task<Guid> Type(string typeCode, string en, string ur, string colour, bool document, string unitKind = "day") =>
                    (await Send(admin, HttpMethod.Post, "/api/hrm/leave-types", new
                    {
                        companyId = company, code = typeCode, nameI18n = new { en, ur }, isPaid = true, counting = "working_days", unit = unitKind, requiresDocument = document,
                        isCompOff = false, isUnpaidDeduction = false, colorToken = colour,
                    })).GetProperty("id").GetGuid();
                var annual = await Type("ANNUAL", "Annual leave", "سالانہ رخصت", "info", false, "half_day");
                var sick = await Type("SICK", "Sick leave", "بیماری کی رخصت", "warning", true, "half_day");
                var from = today.AddYears(-3);
                string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var leaveParameters = new { types = new object[] { new { leaveType = "ANNUAL", entitlement = new { model = "manual" } }, new { leaveType = "SICK", entitlement = new { model = "manual" } } } };
                async Task Policy(string type, object parameters)
                {
                    var created = await Send(admin, HttpMethod.Post, "/api/hrm/policies", new { companyId = company, policyType = type, code = "E2E" + type.ToUpperInvariant(), name = new { en = "Policy " + type, ur = "پالیسی " + type }, parameters });
                    var draft = created.GetProperty("versions")[0];
                    await Send(admin, HttpMethod.Post, $"/api/hrm/policy-versions/{draft.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = Iso(from), rowVersion = draft.GetProperty("rowVersion").GetInt64() });
                    var latest = await Send(admin, HttpMethod.Get, $"/api/hrm/policy-assignment-sets?companyId={company}&policyType={type}");
                    await Send(admin, HttpMethod.Post, "/api/hrm/policy-assignment-sets/activate", new
                    {
                        companyId = company, policyType = type, effectiveFrom = Iso(from), basedOn = latest.GetProperty("latestFrom").ValueKind == System.Text.Json.JsonValueKind.Null ? null : latest.GetProperty("latestFrom").GetString(),
                        assignments = new[] { new { policyId = created.GetProperty("id").GetGuid(), precedence = 1, rule = new { conditions = new object[] { new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } } } } } },
                    });
                }

                await Policy("leave", leaveParameters);
                await Policy("shift", new { weeklyOffKind = "fixed", offDays = new[] { 6, 7 } });
                foreach (var employment in reports.Append(me))
                {
                    foreach (var type in new[] { annual, sick })
                    {
                        await Send(admin, HttpMethod.Post, $"/api/hrm/employments/{employment}/leave-ledger", new { leaveTypeId = type, kind = "grant", quantity = 12m, entryDate = Iso(new DateOnly(monday.Year, 1, 1)), reason = "Seeded for the browser tests" });
                    }
                }

                using var manager = ApiClient(apiUrl, host, fx.Token(code, user: user));
                async Task<Guid> Ask(Guid employment, DateOnly start, DateOnly end, string reason) =>
                    (await Send(admin, HttpMethod.Post, "/api/hrm/leave-requests", new { employmentId = employment, leaveTypeId = annual, startDate = Iso(start), endDate = Iso(end), reason })).GetProperty("id").GetGuid();
                async Task Approve(Guid request)
                {
                    await using var read = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
                    var task = await TenancyFixture.ScalarTextAsync(read, """
                        select t.id::text from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id
                        where i.document_id = $1 and t.status = 'pending' order by t.created_at desc limit 1
                        """, Ct, request);
                    await Send(manager, HttpMethod.Post, $"/api/wf/tasks/{task}/approve", new { comment = "Seeded approval" });
                }

                var first = await Ask(reports[0], monday.AddDays(1), monday.AddDays(2), "Seeded: approved");
                await Approve(first);
                var second = await Ask(reports[1], monday.AddDays(1), monday.AddDays(1), "Seeded: approved");
                await Approve(second);
                var waiting = await Ask(reports[2], monday.AddDays(1), monday.AddDays(2), "Seeded: waiting for the manager");
                return Results.Ok(new { company, unit, me, reports, annual, sick, waiting, monday = Iso(monday) });
            }
            catch (Exception ex)
            {
                return Results.Text(ex.ToString(), statusCode: 500); // the seeding says why it failed (test machinery)
            }
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

    private async Task<Guid> UserIdAsync(string code, string username)
    {
        await using var owner = await fx.OpenTenantAsync(code, TenantLogins.Owner, Ct);
        return Guid.Parse((await TenancyFixture.ScalarTextAsync(owner, "select id::text from iam.users where lower(username) = lower($1)", Ct, username))
            ?? throw new InvalidOperationException("no user " + username));
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

    private sealed record LicenceBody(bool SetEnd, int? DaysAfterEnd, string[]? Features, int? CompaniesLimit, int? UsersLimit = null);

    private sealed record NotificationsBody(string Username, NotificationBody[] Items);

    private sealed record NotificationBody(string EventCode, string Title, string Body, string? Link, string Group);

    private sealed record BranchesBody(string? Company, string Prefix, int Count);

    private sealed record OrgStructureBody(string Prefix);

    private sealed record LabelBody(string Key, string Language, string? Text);

    private sealed record FieldPolicyBody(string Username, string Resource, string Field, string Access);

    private sealed record AttendanceSeedBody(string Username);

    private sealed record LeaveSeedBody(string Username);

    private sealed record RouteBody(string DocumentType, string Approver, bool Bulk);

    private sealed record StartBody(string DocumentType, string Requester, string Amount);

    private sealed record DelegationBody(string From, string To);

    private sealed record HeldSettingBody(int Value, string Reason);
}
