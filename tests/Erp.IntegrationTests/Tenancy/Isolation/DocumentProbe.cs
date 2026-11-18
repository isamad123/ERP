using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Documents;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the attachment endpoints (slice S1.11c1). Each customer gets an administrator with a pending
/// approval task and a file with two versions on it (stored through the real API, so the content is really encrypted). Then:
/// <list type="number">
/// <item>B's host and token aimed at A's task and file ids, with and without headers naming A: the answer is 404 (or B's own
/// empty list); never 5xx; nothing of A's in any answer, and no content of A's file.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class DocumentProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid Task, Guid V1, Guid V2);

    private sealed record Plan(string Path, Func<HttpContent?> Content, HttpStatusCode ForB, HttpStatusCode ForOwn);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Admin, a.Task, a.V1, a.V2 }.Select(g => g.ToString()).Append("Contract text").ToArray();
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
        const string Task = "wf.workflow_tasks";
        return key switch
        {
            "POST /api/doc/attachments/" or "POST /api/doc/attachments" => new Plan("/api/doc/attachments", () => Form(Task, m.Task, "probe.pdf", Pdf("upload")), HttpStatusCode.NotFound, HttpStatusCode.Created),
            "GET /api/doc/attachments/" or "GET /api/doc/attachments" => new Plan($"/api/doc/attachments?ownerType={Task}&ownerId={m.Task}", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/doc/attachments/retention-review" => new Plan("/api/doc/attachments/retention-review", () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "GET /api/doc/attachments/{id:guid}" => new Plan($"/api/doc/attachments/{m.V2}", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/doc/attachments/{id:guid}/versions" => new Plan($"/api/doc/attachments/{m.V2}/versions", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/doc/attachments/{id:guid}/content" => new Plan($"/api/doc/attachments/{m.V2}/content", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/doc/attachments/{id:guid}/restore" => new Plan($"/api/doc/attachments/{m.V1}/restore", () => new StringContent("{}", Encoding.UTF8, "application/json"), HttpStatusCode.NotFound, HttpStatusCode.Created),
            "DELETE /api/doc/attachments/{id:guid}" => new Plan($"/api/doc/attachments/{m.V2}", () => null, HttpStatusCode.NotFound, HttpStatusCode.NoContent),
            "PUT /api/doc/attachments/{id:guid}/legal-hold" => new Plan($"/api/doc/attachments/{m.V2}/legal-hold", () => new StringContent("""{"hold":true,"reason":"Probe"}""", Encoding.UTF8, "application/json"), HttpStatusCode.NotFound, HttpStatusCode.NoContent),
            _ => throw new InvalidOperationException($"No probe target for document endpoint {key}; extend DocumentProbe."),
        };
    }

    private static byte[] Pdf(string extra) => DocumentRig.PdfBytes(extra);

    private static MultipartFormDataContent Form(string ownerType, Guid ownerId, string name, byte[] content, Guid? replaces = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(ownerType), "OwnerType" }, { new StringContent(ownerId.ToString()), "OwnerId" } };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "File", name);
        if (replaces is { } r)
        {
            form.Add(new StringContent(r.ToString()), "ReplacesFileId");
        }

        return form;
    }

    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        Guid admin = Guid.CreateVersion7(), task = Guid.CreateVersion7(), definition = Guid.CreateVersion7(), version = Guid.CreateVersion7(), instance = Guid.CreateVersion7();
        var type = "docp-" + definition.ToString("N")[..12];
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            var name = "dcp" + Guid.NewGuid().ToString("N")[..12];
            await ExecAsync(c, """
                insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
                """, ct, admin, name, system);
            await ExecAsync(c, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, r.id, null, 'probe', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = 'tenant_admin'
                """, ct, Guid.CreateVersion7(), admin, system);
            await ExecAsync(c, """
                insert into wf.workflow_definitions (id, code, name_i18n, document_type, status, is_active, requires_approval, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, '{"en":"Probe","ur":"جانچ"}', $2, 'active', true, false, now(), $3, now(), $3, 1)
                """, ct, definition, type, system);
            await ExecAsync(c, """
                insert into wf.workflow_versions (id, definition_id, version, definition, status, activated_at, activated_by, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 1, '{"steps": []}'::jsonb, 'active', now(), $3, now(), $3, now(), $3)
                """, ct, version, definition, system);
            var company = await ScalarAsync<Guid>(c, "select id from org.companies where not is_deleted order by created_at limit 1", ct);
            await ExecAsync(c, """
                insert into wf.workflow_instances (id, company_id, version_id, document_type, document_id, requester_user_id, started_at, context, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, now(), '{"values": {}}', 'running', now(), $6, now(), $6)
                """, ct, instance, company, version, type, Guid.CreateVersion7(), admin);
            await ExecAsync(c, """
                insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 'probe', $3, 'pending', now(), $3, now(), $3)
                """, ct, task, instance, admin);
        }

        var token = fx.Tenancy.Token(code, user: admin);
        var host = TenancyFixture.HostOf(code);
        var first = await UploadAsync(host, token, Form("wf.workflow_tasks", task, "contract.pdf", Pdf("Contract text one")), ct);
        var second = await UploadAsync(host, token, Form("wf.workflow_tasks", task, "contract.pdf", Pdf("Contract text two"), first), ct);
        return new Material(admin, task, first, second);
    }

    private async Task<Guid> UploadAsync(string host, string token, HttpContent content, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        using var response = await client.PostAsync("/api/doc/attachments", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.StatusCode == HttpStatusCode.Created ? JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid() : throw new InvalidOperationException($"Probe material upload failed ({(int)response.StatusCode}): {body}");
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

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
