using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Packs;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Packs;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the pack manager (slice S1.23b). A probe pack (one label, versions 1.0.0 and 1.0.1) is published once in
/// the control plane both customers share; before each endpoint both customers are reset and, except for the install, get version 1.0.0
/// installed. Then, as for the other kernel endpoints (<see cref="AccessProbe"/>):
/// <list type="number">
/// <item>B's host and token, with and without headers naming A: B's catalogue and items are B's own and show nothing of A's (no A pack
/// item id); install and upgrade act on B only; taking a pack value of A's item is 404; never 5xx.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint works for A.</item>
/// </list>
/// </summary>
public sealed class PackProbe(IsolationFixture fx, WebApplication app)
{
    private static readonly ConcurrentDictionary<TenancyFixture, Lazy<Task<string>>> Published = new();

    private sealed record Material(string PackId, Guid ItemId);

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var packId = await Published.GetOrAdd(fx.Tenancy, f => new Lazy<Task<string>>(() => PublishAsync(f))).Value;
        var install = endpoint.Kind != EndpointKind.PackAction || endpoint.Pattern.EndsWith("/upgrade", StringComparison.Ordinal);
        var a = await MaterialAsync(fx.CodeA, packId, install, ct);
        await MaterialAsync(fx.CodeB, packId, install, ct);
        var tokenA = fx.Tenancy.Token(fx.CodeA);
        var tokenB = fx.Tenancy.Token(fx.CodeB);
        var markers = new[] { a.ItemId.ToString() };
        var method = new HttpMethod(endpoint.Method);
        var (path, body) = Target(endpoint, a);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + B token + headers/query naming A" : "B host + B token";
            var reply = await SendAsync(fx.HostB, tokenB, method, path, body, hints, ct);
            if ((int)reply.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind == EndpointKind.PackRecordAction && reply.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: expected 404 for A's pack item, got {(int)reply.Status}. {Short(reply.Body)}");
            }
            else if (endpoint.Kind is EndpointKind.PackCatalog or EndpointKind.PackQuery && reply.Status != HttpStatusCode.OK)
            {
                failures.Add($"{endpoint} [{label}]: B's own answer was refused ({(int)reply.Status}); the path was not exercised. {Short(reply.Body)}");
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
            ("A host + token signed by an untrusted key", fx.HostA, fx.Tenancy.Token(fx.CodeA, key: forger), HttpStatusCode.Unauthorized),
            ("unknown host + A token", "unknown-" + fx.Tenancy.Suffix + ".erp.test", tokenA, HttpStatusCode.NotFound),
        })
        {
            var reply = await SendAsync(host, token, method, path, body, hints: false, ct);
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

        var own = await SendAsync(fx.HostA, tokenA, method, path, body, hints: false, ct);
        if ((int)own.Status is < 200 or >= 300)
        {
            failures.Add($"{endpoint}: positive control failed: A was not served on its own host ({(int)own.Status}). {Short(own.Body)}");
        }

        return failures;
    }

    private static (string Path, string? Body) Target(ApiEndpoint endpoint, Material m) => (endpoint.Method + " " + endpoint.Pattern) switch
    {
        "GET /api/cfg/packs/" => ("/api/cfg/packs/", null),
        "GET /api/cfg/packs/{packId}/items" => ($"/api/cfg/packs/{m.PackId}/items", null),
        "POST /api/cfg/packs/install" => ("/api/cfg/packs/install", Json(new { packId = m.PackId })),
        "POST /api/cfg/packs/{packId}/upgrade" => ($"/api/cfg/packs/{m.PackId}/upgrade", Json(new { version = "1.0.1" })),
        "POST /api/cfg/packs/items/{id:guid}/take-pack-value" => ($"/api/cfg/packs/items/{m.ItemId}/take-pack-value", Json(new { reason = "probe" })),
        var key => throw new InvalidOperationException($"No probe target for pack endpoint {key}; extend PackProbe."),
    };

    private static async Task<string> PublishAsync(TenancyFixture tenancy)
    {
        var id = "probe-" + tenancy.Suffix;
        foreach (var (version, text) in new[] { ("1.0.0", "Probe term"), ("1.0.1", "Probe term two") })
        {
            await PackTestSupport.PublishAsync(tenancy, id, version, [new TestObject("label", "labels.probe." + tenancy.Suffix, new { texts = new { en = text } })]);
        }

        return id;
    }

    /// <summary>Resets the customer's probe pack (rows and the label it wrote) and, when asked, installs version 1.0.0 as provisioning would.</summary>
    private async Task<Material> MaterialAsync(string code, string packId, bool install, CancellationToken ct)
    {
        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, "delete from cfg.pack_items where pack_id = $1", ct, packId);
        await ExecAsync(c, "delete from cfg.installed_packs where pack_id = $1", ct, packId);
        await ExecAsync(c, "delete from cfg.labels where key = $1", ct, "labels.probe." + fx.Tenancy.Suffix);
        if (!install)
        {
            return new Material(packId, Guid.Empty);
        }

        PackArchive pack;
        await using (var cp = await fx.Tenancy.OpenControlPlaneAsync(ct))
        {
            pack = await PackCatalog.LoadAsync(cp, packId, "1.0.0", DateTimeOffset.UtcNow, ct);
        }

        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await new PackInstaller(PackTestSupport.Handlers).ApplyAsync(c, tx, pack, upgrade: false, BaselineSeed.SystemUserId, DateTimeOffset.UtcNow, ct);
            await tx.CommitAsync(ct);
        }

        await using var read = new NpgsqlCommand("select id from cfg.pack_items where pack_id = $1", c);
        read.Parameters.Add(new NpgsqlParameter { Value = packId });
        return new Material(packId, (Guid)(await read.ExecuteScalarAsync(ct))!);
    }

    private async Task<Reply> SendAsync(string host, string? token, HttpMethod method, string pathAndQuery, string? body, bool hints, CancellationToken ct)
    {
        using var client = TenancyFixture.Client(app, host, token);
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var target = hints ? pathAndQuery + separator + "tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA : pathAndQuery;
        using var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (hints)
        {
            request.Headers.Add("X-Tenant", fx.CodeA);
            request.Headers.Add("X-Tenant-Code", fx.CodeA);
            request.Headers.Add("X-Forwarded-Host", fx.HostA);
            request.Headers.Add("Forwarded", "host=" + fx.HostA);
        }

        using var response = await client.SendAsync(request, ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
