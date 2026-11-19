using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the document template endpoints (slice S1.11c2). Each customer gets an administrator, a template with
/// an active version and a template with a draft version. Then: B's host and token aimed at A's template and version ids (with and
/// without headers naming A) get 404 or B's own answer, never 5xx and nothing of A's; crossed host and token, missing or forged
/// token get 401, an unknown host 404; A's database is unchanged; a positive control shows the endpoint resolves A's own rows.
/// </summary>
public sealed class TemplateProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid Active, Guid ActiveVersion, long ActiveRow, long ActiveVersionRow, Guid Draft, Guid DraftVersion, long DraftVersionRow);

    private sealed record Plan(string Path, Func<HttpContent?> Content, HttpStatusCode ForB, HttpStatusCode ForOwn);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Admin, a.Active, a.ActiveVersion, a.Draft, a.DraftVersion }.Select(g => g.ToString()).ToArray();
        var method = new HttpMethod(endpoint.Method);
        var plan = Target(endpoint, a);
        var tokenA = fx.Tenancy.Token(fx.CodeA, user: a.Admin);
        var tokenB = fx.Tenancy.Token(fx.CodeB, user: b.Admin);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, method, plan.Path, plan.Content(), hints, ct);
            if ((int)reply.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (reply.Status != plan.ForB)
            {
                failures.Add($"{endpoint} [{label}]: expected {(int)plan.ForB}, got {(int)reply.Status}. {Short(reply.Body)}");
            }

            var leaked = markers.Where(m => reply.Body.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leaked.Count > 0)
            {
                failures.Add($"{endpoint} [{label}]: LEAK: the response contains customer A's data {string.Join(", ", leaked)}. {Short(reply.Body)}");
            }
        }

        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var (label, host, token, status) in new (string, string, string?, HttpStatusCode)[]
        {
            ("A host + B token", fx.HostA, tokenB, HttpStatusCode.Unauthorized),
            ("B host + A token", fx.HostB, tokenA, HttpStatusCode.Unauthorized),
            ("A host + no token", fx.HostA, null, HttpStatusCode.Unauthorized),
            ("A host + token signed by an untrusted key", fx.HostA, fx.Tenancy.Token(fx.CodeA, key: forger, user: a.Admin), HttpStatusCode.Unauthorized),
            ("unknown host + A token", "unknown-" + fx.Tenancy.Suffix + ".erp.test", tokenA, HttpStatusCode.NotFound),
        })
        {
            var reply = await SendAsync(host, token, method, plan.Path, plan.Content(), hints: false, ct);
            if (reply.Status != status)
            {
                failures.Add($"{endpoint} [{label}]: expected {(int)status}, got {(int)reply.Status}. {Short(reply.Body)}");
            }
        }

        var changed = IsolationFixture.Changed(before, await fx.FingerprintAsync(fx.CodeA, ct));
        if (changed.Count > 0)
        {
            failures.Add($"{endpoint}: customer A's database changed during the cross-customer calls: {string.Join(", ", changed)}");
        }

        var own = await SendAsync(fx.HostA, tokenA, method, plan.Path, plan.Content(), hints: false, ct);
        if (own.Status != plan.ForOwn)
        {
            failures.Add($"{endpoint}: positive control failed: A on its own host expected {(int)plan.ForOwn}, got {(int)own.Status}. {Short(own.Body)}");
        }

        return failures;
    }

    private static Plan Target(ApiEndpoint endpoint, Material m)
    {
        var key = endpoint.Method + " " + endpoint.Pattern;
        return key switch
        {
            "POST /api/doc/templates/" or "POST /api/doc/templates" => new Plan("/api/doc/templates", () => Json(new
            {
                code = "probe-" + Guid.NewGuid().ToString("N")[..12],
                name = new Dictionary<string, string> { ["en"] = "Probe" },
                kind = "part",
                entityType = "-",
                language = "en",
                outputFormat = "html",
                body = "<p>{{ content }}</p>",
            }), HttpStatusCode.Created, HttpStatusCode.Created),
            "POST /api/doc/templates/preview" => new Plan("/api/doc/templates/preview", () => Json(new { body = "<p>Probe</p>", outputFormat = "html", language = "en", entityType = "-" }), HttpStatusCode.OK, HttpStatusCode.OK),
            "POST /api/doc/templates/generate" => new Plan("/api/doc/templates/generate", () => Json(new { templateId = m.Active, ownerId = Guid.NewGuid() }), HttpStatusCode.NotFound, HttpStatusCode.BadRequest), // A resolves its own template, then finds no data provider for the probe's record type
            "GET /api/doc/templates/generated" => new Plan("/api/doc/templates/generated?ownerType=wf.workflow_tasks&ownerId=" + m.Admin, () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "GET /api/doc/templates/{id:guid}/versions" => new Plan($"/api/doc/templates/{m.Active}/versions", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/doc/templates/{id:guid}/versions" => new Plan($"/api/doc/templates/{m.Active}/versions", () => Json(new { body = "<p>Probe two</p>" }), HttpStatusCode.NotFound, HttpStatusCode.Created),
            "POST /api/doc/templates/{id:guid}/retire" => new Plan($"/api/doc/templates/{m.Active}/retire", () => Json(new { rowVersion = m.ActiveRow }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "PUT /api/doc/templates/versions/{versionId:guid}" => new Plan($"/api/doc/templates/versions/{m.DraftVersion}", () => Json(new { body = "<p>Changed</p>", rowVersion = m.DraftVersionRow }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/doc/templates/versions/{versionId:guid}/activate" => new Plan($"/api/doc/templates/versions/{m.DraftVersion}/activate", () => Json(new { rowVersion = m.DraftVersionRow }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            _ => throw new InvalidOperationException($"No probe target for template endpoint {key}; extend TemplateProbe."),
        };
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var admin = Guid.CreateVersion7();
        var name = "tpp" + Guid.NewGuid().ToString("N")[..12];
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, admin, name, system);
        await ExecAsync(c, """
            insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            select $1, $2, r.id, null, 'probe', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = 'tenant_admin'
            """, ct, Guid.CreateVersion7(), admin, system);
        var (active, activeVersion) = await TemplateAsync(c, "active", "active", system, ct);
        var (draft, draftVersion) = await TemplateAsync(c, "draft", "draft", system, ct);
        return new Material(admin, active, activeVersion, await RowAsync(c, "doc.templates", active, ct), await RowAsync(c, "doc.template_versions", activeVersion, ct), draft, draftVersion, await RowAsync(c, "doc.template_versions", draftVersion, ct));
    }

    private static async Task<(Guid Template, Guid Version)> TemplateAsync(NpgsqlConnection c, string label, string status, Guid system, CancellationToken ct)
    {
        Guid template = Guid.CreateVersion7(), version = Guid.CreateVersion7();
        await ExecAsync(c, """
            insert into doc.templates (id, code, name_i18n, kind, entity_type, company_id, language, source, active_version_id, is_active, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, '{"en":"Probe"}', 'letter', 'wf.workflow_tasks', null, 'en', 'tenant', null, true, now(), $3, now(), $3, 1)
            """, ct, template, "probe-" + label + "-" + template.ToString("N")[..10], system);
        await ExecAsync(c, """
            insert into doc.template_versions (id, template_id, version, body, output_format, status, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, 1, '<p>Probe</p>', 'html', $3, now(), $4, now(), $4, 1)
            """, ct, version, template, status, system);
        if (status == "active")
        {
            await ExecAsync(c, "update doc.templates set active_version_id = $2 where id = $1", ct, template, version);
        }

        return (template, version);
    }

    private static async Task<long> RowAsync(NpgsqlConnection c, string table, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"select row_version from {table} where id = $1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string host, string? token, HttpMethod method, string pathAndQuery, HttpContent? content, bool hints, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var target = hints ? pathAndQuery + separator + "tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA : pathAndQuery;
        using var request = new HttpRequestMessage(method, target) { Content = content };
        if (hints)
        {
            request.Headers.Add("X-Tenant", fx.CodeA);
            request.Headers.Add("X-Tenant-Code", fx.CodeA);
            request.Headers.Add("X-Forwarded-Host", fx.HostA);
            request.Headers.Add("Forwarded", "host=" + fx.HostA);
        }

        using var response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
