using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Documents;

/// <summary>Services that give a test host a folder file store and a scanner that finds nothing.</summary>
public static class DocumentTestServices
{
    public static void Register(IServiceCollection services, string root)
    {
        services.RemoveAll<IFileStore>();
        services.AddSingleton<IFileStore>(new LocalFolderFileStore(root));
        services.RemoveAll<IVirusScanner>();
        services.AddSingleton<IVirusScanner>(new FakeScanner());
    }
}

/// <summary>What a test says about one record that carries files: who may read, change and read sensitive files of it.</summary>
public sealed class TestRecordBook
{
    public const string OwnerType = "test.records";

    private readonly ConcurrentDictionary<Guid, (Guid Company, HashSet<Guid> Read, HashSet<Guid> Write, HashSet<Guid> Sensitive)> _records = new();

    public Guid Add(Guid company, IEnumerable<Guid> read, IEnumerable<Guid> write, IEnumerable<Guid>? sensitive = null)
    {
        var id = Guid.CreateVersion7();
        _records[id] = (company, [.. read], [.. write], [.. sensitive ?? []]);
        return id;
    }

    internal AttachmentOwnerAccess? Find(Guid id, Guid user) =>
        _records.TryGetValue(id, out var r) ? new AttachmentOwnerAccess(r.Company, r.Read.Contains(user), r.Write.Contains(user), r.Sensitive.Contains(user)) : null;
}

/// <summary>A kind of record for the tests; the rules of <see cref="TestRecordBook"/> stand in for a module's own data scopes.</summary>
public sealed class TestRecordOwner(TestRecordBook book, ICurrentActor actor) : IAttachmentOwner
{
    public string OwnerType => TestRecordBook.OwnerType;

    public Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken) => Task.FromResult(book.Find(ownerId, actor.UserId));
}

/// <summary>A scanner the test controls: clean, infected (content contains EICAR) or unreachable.</summary>
public sealed class FakeScanner : IVirusScanner
{
    public bool Down { get; set; }

    public int Scans { get; private set; }

    public Task<(ScanVerdict Verdict, string? Detail)> ScanAsync(byte[] content, CancellationToken cancellationToken)
    {
        Scans++;
        if (Down)
        {
            return Task.FromResult<(ScanVerdict, string?)>((ScanVerdict.Unavailable, "down"));
        }

        return Task.FromResult<(ScanVerdict, string?)>(Encoding.ASCII.GetString(content).Contains("EICAR", StringComparison.Ordinal) ? (ScanVerdict.Infected, "Test.EICAR") : (ScanVerdict.Clean, null));
    }
}

/// <summary>A tenant context for code that runs outside a request.</summary>
public sealed class FixedTenant(string code) : Erp.BuildingBlocks.Application.Tenancy.ITenantContext
{
    public bool IsResolved => true;

    public string TenantCode => code;

    public Erp.BuildingBlocks.Application.Tenancy.TenantAccessMode AccessMode => Erp.BuildingBlocks.Application.Tenancy.TenantAccessMode.Full;

    public IReadOnlySet<string> InstalledModules => new HashSet<string>();
}

/// <summary>A binary answer of the API.</summary>
public sealed record FileAnswer(HttpStatusCode Status, byte[] Bytes)
{
    public string Text => Encoding.UTF8.GetString(Bytes);

    public System.Text.Json.JsonElement Json => System.Text.Json.JsonDocument.Parse(Bytes).RootElement;
}

/// <summary>One customer, one API host with a folder store, the fake scanner and the test record kind (slice S1.11c1 tests).</summary>
public sealed class DocumentRig : IAsyncDisposable
{
    public const string Pdf = "%PDF-1.7\nContract text that must never appear unencrypted in the store.\n";

    private readonly TenancyFixture _fx;

    private DocumentRig(TenancyFixture fx, WebApplication app, TestClock clock, string code, string root, FakeScanner scanner, TestRecordBook book, Guid company)
    {
        _fx = fx;
        App = app;
        Clock = clock;
        Code = code;
        Root = root;
        Scanner = scanner;
        Records = book;
        Company = company;
    }

    public WebApplication App { get; }

    public TestClock Clock { get; }

    public string Code { get; }

    public string Root { get; }

    public FakeScanner Scanner { get; }

    public TestRecordBook Records { get; }

    public Guid Company { get; }

    public IFileStore Store => new LocalFolderFileStore(Root);

    public static async Task<DocumentRig> StartAsync(TenancyFixture fx, CancellationToken ct)
    {
        var clock = new TestClock();
        var scanner = new FakeScanner();
        var book = new TestRecordBook();
        var root = Path.Combine(Path.GetTempPath(), "erp-doc-" + Guid.NewGuid().ToString("N"));
        var code = "dc" + fx.Suffix;
        await fx.ProvisionAsync(code, ct);
        var app = await fx.StartAppAsync(null, ct, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<IFileStore>();
            services.AddSingleton<IFileStore>(new LocalFolderFileStore(root));
            services.RemoveAll<IVirusScanner>();
            services.AddSingleton<IVirusScanner>(scanner);
            services.AddSingleton(book);
            services.AddScoped<IAttachmentOwner, TestRecordOwner>();
        }, realAccess: true);
        await using var c = await fx.OpenTenantAsync(code, TenantLogins.Owner, ct);
        var company = await ScalarAsync<Guid>(c, "select id from org.companies where not is_deleted order by created_at limit 1", ct);
        return new DocumentRig(fx, app, clock, code, root, scanner, book, company);
    }

    public Task<NpgsqlConnection> OwnerAsync(CancellationToken ct) => _fx.OpenTenantAsync(Code, TenantLogins.Owner, ct);

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params object?[] values)
    {
        await using var c = await OwnerAsync(ct);
        return await ScalarAsync<T>(c, sql, ct, values);
    }

    public async Task ExecAsync(string sql, CancellationToken ct, params object?[] values)
    {
        await using var c = await OwnerAsync(ct);
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var v in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlConnection c, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var v in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        }

        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>An active user holding the given baseline roles for all companies.</summary>
    public async Task<Guid> UserAsync(CancellationToken ct, params string[] roles)
    {
        var id = Guid.CreateVersion7();
        var name = "doc" + Guid.NewGuid().ToString("N")[..12];
        await using var c = await OwnerAsync(ct);
        await ExecInAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@doc.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, ct, id, name, BaselineSeed.SystemUserId);
        foreach (var role in roles)
        {
            await ExecInAsync(c, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
                select $1, $2, r.id, null, 'test', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = $4
                """, ct, Guid.CreateVersion7(), id, BaselineSeed.SystemUserId, role);
        }

        return id;
    }

    private static async Task ExecInAsync(NpgsqlConnection c, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var v in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ HTTP

    public async Task<FileAnswer> SendAsync(HttpMethod method, Guid user, string path, HttpContent? content, CancellationToken ct, string? code = null)
    {
        using var client = TenancyFixture.Client(App, TenancyFixture.HostOf(code ?? Code), _fx.Token(code ?? Code, user: user));
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        using var response = await client.SendAsync(request, ct);
        return new FileAnswer(response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct));
    }

    public Task<FileAnswer> GetAsync(Guid user, string path, CancellationToken ct) => SendAsync(HttpMethod.Get, user, path, null, ct);

    public Task<FileAnswer> DeleteAsync(Guid user, string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, user, path, null, ct);

    public Task<FileAnswer> PostAsync(Guid user, string path, CancellationToken ct) => SendAsync(HttpMethod.Post, user, path, new StringContent("{}", Encoding.UTF8, "application/json"), ct);

    public Task<FileAnswer> PutJsonAsync(Guid user, string path, string json, CancellationToken ct) => SendAsync(HttpMethod.Put, user, path, new StringContent(json, Encoding.UTF8, "application/json"), ct);

    public Task<FileAnswer> UploadAsync(Guid user, Guid ownerId, string fileName, byte[] content, CancellationToken ct, string ownerType = TestRecordBook.OwnerType, string? documentType = null,
        DateOnly? expiresOn = null, bool sensitive = false, Guid? replaces = null, string? code = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(ownerType), "OwnerType" },
            { new StringContent(ownerId.ToString()), "OwnerId" },
        };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "File", fileName);
        if (documentType is not null)
        {
            form.Add(new StringContent(documentType), "DocumentType");
        }

        if (expiresOn is { } date)
        {
            form.Add(new StringContent(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), "ExpiresOn");
        }

        if (sensitive)
        {
            form.Add(new StringContent("true"), "IsSensitive");
        }

        if (replaces is { } r)
        {
            form.Add(new StringContent(r.ToString()), "ReplacesFileId");
        }

        return SendAsync(HttpMethod.Post, user, "/api/doc/attachments", form, ct, code);
    }

    /// <summary>The scan logic the scan job runs, bound to this customer (the job itself needs a worker host).</summary>
    public FileScanning Scanning() => new(Store, new FileEncryption(new FixedTenant(Code), _fx.Secrets), Scanner, Clock);

    /// <summary>A pending approval task for <paramref name="assignee"/> on a request by <paramref name="requester"/> (the rows the engine would write).</summary>
    public async Task<Guid> WorkflowTaskAsync(Guid assignee, Guid requester, CancellationToken ct, string? code = null)
    {
        var system = BaselineSeed.SystemUserId;
        var (definition, version, instance, task) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var type = "doc-test-" + definition.ToString("N")[..12];
        await using var c = await _fx.OpenTenantAsync(code ?? Code, TenantLogins.Owner, ct);
        var company = await ScalarAsync<Guid>(c, "select id from org.companies where not is_deleted order by created_at limit 1", ct);
        await ExecInAsync(c, """
            insert into wf.workflow_definitions (id, code, name_i18n, document_type, status, is_active, requires_approval, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, '{"en":"Doc test","ur":"جانچ"}', $2, 'active', true, false, now(), $3, now(), $3, 1)
            """, ct, definition, type, system);
        await ExecInAsync(c, """
            insert into wf.workflow_versions (id, definition_id, version, definition, status, activated_at, activated_by, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 1, '{"steps": []}'::jsonb, 'active', now(), $3, now(), $3, now(), $3)
            """, ct, version, definition, system);
        await ExecInAsync(c, """
            insert into wf.workflow_instances (id, company_id, version_id, document_type, document_id, requester_user_id, started_at, context, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, now(), '{"values": {}}', 'running', now(), $6, now(), $6)
            """, ct, instance, company, version, type, Guid.CreateVersion7(), requester);
        await ExecInAsync(c, """
            insert into wf.workflow_tasks (id, instance_id, step_key, assignee_user_id, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'probe', $3, 'pending', now(), $4, now(), $4)
            """, ct, task, instance, assignee, requester);
        return task;
    }

    public static byte[] PdfBytes(string extra = "") => Encoding.UTF8.GetBytes(Pdf + extra);

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }

        return ValueTask.CompletedTask;
    }
}
