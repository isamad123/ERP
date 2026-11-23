using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Itg;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the import centre endpoints (slice S1.16a). Each customer gets a person and three import batches on
/// one stored file (uploaded, checked, committed with a record that can be rolled back). Then B's host and token aimed at A's ids
/// answer 404 (or B's own list); crossed hosts and tokens 401; unknown host 404; A's database stays unchanged; and A is served on its own host.
/// </summary>
public sealed class ImportProbe(IsolationFixture fx, WebApplication app)
{
    private sealed record Material(Guid Admin, Guid Template, Guid Uploaded, Guid Validated, Guid Committed, string FileName);

    private sealed record Plan(string Path, Func<HttpContent?> Content, HttpStatusCode ForB, HttpStatusCode ForOwn);

    private const string Csv = "username,email,display_name\r\nprobe.import,probe.import@probe.test,Probe Import\r\n";

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var markers = new[] { a.Admin, a.Template, a.Uploaded, a.Validated, a.Committed }.Select(g => g.ToString()).Append(a.FileName).ToArray();
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
        var users = ImportManifest.Parse(ImportManifest.KernelJson).Single(t => t.Code == "users");
        var mapping = TemplateMapping.DefaultFor(users);
        const string Base = "/api/itg/imports";
        return key switch
        {
            "GET /api/itg/imports/types" => new Plan(Base + "/types", () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "GET /api/itg/imports/templates" => new Plan(Base + "/templates", () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "POST /api/itg/imports/templates" => new Plan(Base + "/templates", () => Json(new { code = "probe_" + Guid.NewGuid().ToString("N")[..10], name = new { en = "Probe" }, entityType = "users", format = "csv", mode = "create", mapping }), HttpStatusCode.Created, HttpStatusCode.Created),
            "PUT /api/itg/imports/templates/{id:guid}" => new Plan($"{Base}/templates/{m.Template}", () => Json(new { name = new { en = "Probe renamed" }, mode = "create", mapping, rowVersion = 1 }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/itg/imports/templates/{id:guid}/file" => new Plan($"{Base}/templates/{m.Template}/file", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/itg/imports/templates/{id:guid}/test" => new Plan($"{Base}/templates/{m.Template}/test", () => Form(null), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/itg/imports/batches" => new Plan(Base + "/batches", () => Form(m.Template), HttpStatusCode.NotFound, HttpStatusCode.Created),
            "GET /api/itg/imports/batches" => new Plan(Base + "/batches", () => null, HttpStatusCode.OK, HttpStatusCode.OK),
            "GET /api/itg/imports/batches/{id:guid}" => new Plan($"{Base}/batches/{m.Uploaded}", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "POST /api/itg/imports/batches/{id:guid}/validate" => new Plan($"{Base}/batches/{m.Uploaded}/validate", () => Json(new { }), HttpStatusCode.NotFound, HttpStatusCode.Accepted),
            "POST /api/itg/imports/batches/{id:guid}/commit" => new Plan($"{Base}/batches/{m.Validated}/commit", () => Json(new { skipErrors = false }), HttpStatusCode.NotFound, HttpStatusCode.Accepted),
            "POST /api/itg/imports/batches/{id:guid}/rollback" => new Plan($"{Base}/batches/{m.Committed}/rollback", () => Json(new { reason = "probe" }), HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/itg/imports/batches/{id:guid}/errors" => new Plan($"{Base}/batches/{m.Uploaded}/errors", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            "GET /api/itg/imports/batches/{id:guid}/errors.csv" => new Plan($"{Base}/batches/{m.Uploaded}/errors.csv", () => null, HttpStatusCode.NotFound, HttpStatusCode.OK),
            _ => throw new InvalidOperationException($"No probe target for import endpoint {key}; extend ImportProbe."),
        };
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static MultipartFormDataContent Form(Guid? template, string name = "probe-import.csv")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "File", name);
        if (template is { } t)
        {
            form.Add(new StringContent(t.ToString()), "TemplateId");
        }

        return form;
    }

    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        var system = BaselineSeed.SystemUserId;
        var admin = Guid.CreateVersion7();
        var name = "imp" + Guid.NewGuid().ToString("N")[..12];
        Guid template;
        await using (var c = await fx.OpenAdminAsync(code, ct))
        {
            await ExecAsync(c, """
                insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
                """, ct, admin, name, system);
            template = await ScalarAsync<Guid>(c, "select id from itg.import_templates where code = 'default.users'", ct);
        }

        var fileName = $"probe-{admin:N}.csv"; // unique to this customer's material, so it can be told from B's own
        var token = fx.Tenancy.Token(code, user: admin);
        using var client = TenancyFixture.Client(app, TenancyFixture.HostOf(code), token);
        using var response = await client.PostAsync("/api/itg/imports/batches", Form(template, fileName), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"Probe material upload failed ({(int)response.StatusCode}): {body}");
        }

        var uploaded = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        var (validated, committed, calendar) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var d = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(d, "insert into ref.calendars (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Probe import calendar\"}', 'tenant', now(), $3, now(), $3)", ct, calendar, "PIMP" + Guid.NewGuid().ToString("N")[..8], system);
        await ExecAsync(d, """
            insert into itg.import_batches (id, company_id, template_id, file_id, total_rows, ok_rows, error_rows, status, file_name, mode, created_rows, updated_rows, validated_at, committed_at, created_at, created_by, updated_at, updated_by)
            select $1, company_id, template_id, file_id, 1, 1, 0, 'validated', file_name, mode, null, null, now(), null, now(), created_by, now(), created_by from itg.import_batches where id = $3
            union all
            select $2, company_id, template_id, file_id, 1, 1, 0, 'committed', file_name, mode, 1, 0, now(), now(), now(), created_by, now(), created_by from itg.import_batches where id = $3
            """, ct, validated, committed, uploaded);
        await ExecAsync(d, "insert into itg.import_batch_rows (id, batch_id, row_no, table_name, record_id, action, record_version, created_at, created_by) values ($1, $2, 2, 'ref.calendars', $3, 'created', 1, now(), $4)", ct, Guid.CreateVersion7(), committed, calendar, system);
        return new Material(admin, template, uploaded, validated, committed, fileName);
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
