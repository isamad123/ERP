using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Documents;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Itg;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.IntegrationTests.ImportCentre;

/// <summary>One customer with the API host (folder store, fake scanner, real access engine) and a worker over the same customer and clock (slice S1.16a tests).</summary>
public sealed class ImportRig : IAsyncDisposable
{
    private readonly TenancyFixture _fx;
    private readonly JobRig _jobs;

    private ImportRig(TenancyFixture fx, DocumentRig docs, JobRig jobs)
    {
        _fx = fx;
        Docs = docs;
        _jobs = jobs;
    }

    public DocumentRig Docs { get; }

    public string Code => Docs.Code;

    public Guid Admin { get; private set; }

    public static async Task<ImportRig> StartAsync(TenancyFixture fx, CancellationToken ct)
    {
        var docs = await DocumentRig.StartAsync(fx, ct);
        var jobs = await JobRig.ForExistingAsync(fx, docs.Code, docs.Clock, ct);
        var rig = new ImportRig(fx, docs, jobs);
        rig.Admin = await docs.UserAsync(ct, "tenant_admin");
        return rig;
    }

    private JobWorker? _worker;

    /// <summary>A worker over this customer; every call to <see cref="RunJobsAsync"/> uses the same one unless <see cref="NewWorker"/> replaced it.</summary>
    public JobWorker Worker => _worker ??= NewWorker();

    public JobWorker NewWorker(int leaseSeconds = 60, Action<IServiceCollection>? extra = null) => _worker = _jobs.Worker(leaseSeconds: leaseSeconds, extra: s =>
    {
        DocumentTestServices.Register(s, Docs.Root);
        extra?.Invoke(s);
    });

    public async Task RunJobsAsync(CancellationToken ct) => await _jobs.RunUntilIdleAsync(Worker, ct);

    public JobRig Jobs => _jobs;

    private EventDispatcher? _dispatcher;

    /// <summary>Delivers the outbox events of the customer (the invitation e-mails are queued by the notification consumer) until nothing is left.</summary>
    public async Task DispatchEventsAsync(CancellationToken ct)
    {
        _dispatcher ??= Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<EventDispatcher>(_jobs.Provider(extra: s => DocumentTestServices.Register(s, Docs.Root)));
        for (var i = 0; i < 100; i++)
        {
            var outcomes = await _dispatcher.DispatchOnceAsync(Code, ct);
            Assert.All(outcomes, o => Assert.True(o.Error is null, o.Error));
            if (outcomes.All(o => o.Delivered == 0))
            {
                return;
            }
        }
    }

    public Task<FileAnswer> GetAsync(string path, CancellationToken ct, Guid? user = null) => Docs.SendAsync(HttpMethod.Get, user ?? Admin, path, null, ct);

    public Task<FileAnswer> PostJsonAsync(string path, object? body, CancellationToken ct, Guid? user = null) =>
        Docs.SendAsync(HttpMethod.Post, user ?? Admin, path, new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json"), ct);

    public Task<FileAnswer> PutJsonAsync(string path, object body, CancellationToken ct, Guid? user = null) =>
        Docs.SendAsync(HttpMethod.Put, user ?? Admin, path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

    public Task<FileAnswer> UploadAsync(Guid templateId, string fileName, byte[] content, CancellationToken ct, Guid? user = null) =>
        Docs.SendAsync(HttpMethod.Post, user ?? Admin, "/api/itg/imports/batches", Form(content, fileName, templateId), ct);

    public Task<FileAnswer> TestAsync(Guid templateId, string fileName, byte[] content, CancellationToken ct, Guid? user = null) =>
        Docs.SendAsync(HttpMethod.Post, user ?? Admin, $"/api/itg/imports/templates/{templateId}/test", Form(content, fileName, null), ct);

    private static MultipartFormDataContent Form(byte[] content, string fileName, Guid? templateId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "File", fileName);
        if (templateId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "TemplateId");
        }

        return form;
    }

    /// <summary>The id of the shipped template of an import type.</summary>
    public async Task<Guid> TemplateAsync(string type, CancellationToken ct) =>
        await Docs.ScalarAsync<Guid>("select id from itg.import_templates where code = $1", ct, "default." + type);

    public Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params object?[] values) => Docs.ScalarAsync<T>(sql, ct, values);

    public Task ExecAsync(string sql, CancellationToken ct, params object?[] values) => Docs.ExecAsync(sql, ct, values);

    /// <summary>Uploads, checks and waits for the validation run; returns the batch id and its answer.</summary>
    public async Task<(Guid Id, JsonElement Batch)> UploadAndValidateAsync(string type, string fileName, byte[] content, CancellationToken ct, Guid? user = null)
    {
        var up = await UploadAsync(await TemplateAsync(type, ct), fileName, content, ct, user);
        Assert.True(up.Status == HttpStatusCode.Created, up.Text);
        var id = up.Json.GetProperty("id").GetGuid();
        var check = await PostJsonAsync($"/api/itg/imports/batches/{id}/validate", null, ct, user);
        Assert.True(check.Status == HttpStatusCode.Accepted, check.Text);
        await RunJobsAsync(ct);
        var batch = await GetAsync($"/api/itg/imports/batches/{id}", ct, user);
        return (id, batch.Json.Clone());
    }

    public async Task<JsonElement> CommitAsync(Guid id, CancellationToken ct, bool skipErrors = false, Guid? user = null)
    {
        var commit = await PostJsonAsync($"/api/itg/imports/batches/{id}/commit", new { skipErrors }, ct, user);
        Assert.True(commit.Status == HttpStatusCode.Accepted, commit.Text);
        await RunJobsAsync(ct);
        return (await GetAsync($"/api/itg/imports/batches/{id}", ct, user)).Json.Clone();
    }

    /// <summary>A users file: <paramref name="count"/> rows, with the row numbers in <paramref name="bad"/> (1-based among data rows) spoiled in different ways.</summary>
    public static byte[] UsersCsv(string prefix, int count, params int[] bad)
    {
        var text = new StringBuilder("username,email,display_name,company,language,time_zone,access_valid_to\r\n");
        for (var i = 1; i <= count; i++)
        {
            var name = $"{prefix}{i:0000}";
            var line = (i % 3) switch
            {
                _ when !bad.Contains(i) => $"{name},{name}@import.test,Person {i},,en,Asia/Karachi,",
                0 => $"{name},{name}@import.test,,,en,Asia/Karachi,",
                1 => $"{name},{name}@import.test,Person {i},,xx,Asia/Karachi,",
                _ => $"{prefix}0001,{name}@import.test,Person {i},,en,Asia/Karachi,",
            };
            text.Append(line).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>A second company with a unit type and a hierarchy to import units into; returns (company B, its code, the first company, its code).</summary>
    public async Task<(Guid CompanyB, string CodeB, Guid CompanyA, string CodeA)> UnitSetupAsync(CancellationToken ct)
    {
        var companyB = Guid.CreateVersion7();
        var codeB = "CB" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await ExecAsync("""
            insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Company B"}', 'Company B', 'PK', 'PKR', 'en', 'none', 'active', now(), $3, now(), $3)
            """, ct, companyB, codeB, BaselineSeed.SystemUserId);
        await ExecAsync("""
            insert into org.org_unit_types (id, code, name_i18n, is_active, created_at, created_by, updated_at, updated_by)
            values ($1, 'DEPT', '{"en":"Department"}', true, now(), $2, now(), $2) on conflict do nothing
            """, ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
        await ExecAsync("""
            insert into org.org_hierarchies (id, code, name_i18n, kind, is_active, created_at, created_by, updated_at, updated_by)
            values ($1, 'REPORTING', '{"en":"Reporting"}', 'reporting', true, now(), $2, now(), $2) on conflict do nothing
            """, ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
        var companyA = Docs.Company;
        return (companyB, codeB, companyA, await ScalarAsync<string>("select code from org.companies where id = $1", ct, companyA));
    }

    /// <summary>A person who may import units for one company only (the role is assigned for that company).</summary>
    public async Task<Guid> UnitImporterAsync(Guid company, CancellationToken ct)
    {
        var user = await Docs.UserAsync(ct);
        var role = Guid.CreateVersion7();
        string[] permissions = ["itg.imports.manage", "org.org_units.create", "org.org_units.read", "org.org_units.update", "org.org_unit_links.create", "org.org_unit_links.read", "org.companies.read", "org.org_unit_types.read", "org.org_hierarchies.read"];
        await ExecAsync("insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Unit importer\"}', 'tenant', now(), $3, now(), $3)", ct, role, "ui" + Guid.NewGuid().ToString("N")[..10], BaselineSeed.SystemUserId);
        await ExecAsync("insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2)", ct, role, permissions);
        await ExecAsync("insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) select $1, '*', id from iam.data_scopes where code = 'all'", ct, role);
        await ExecAsync("insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4, 'test', current_date - 1, now(), $5, now(), $5)", ct, Guid.CreateVersion7(), user, role, company, BaselineSeed.SystemUserId);
        return user;
    }

    public async Task<NpgsqlConnection> OwnerAsync(CancellationToken ct) => await _fx.OpenTenantAsync(Code, TenantLogins.Owner, ct);

    public async ValueTask DisposeAsync()
    {
        await _jobs.DisposeAsync();
        await Docs.DisposeAsync();
    }
}

/// <summary>The commit job with a pause after a number of saved chunks, so a test can stop the worker at a known place (the pattern of the job tests' block job).</summary>
public sealed class PausingCommitJob(int afterChunks) : IJobHandler
{
    private readonly ImportCommitJob _inner = new();

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Code => _inner.Code;

    public static PausingCommitJob Install(IServiceCollection services, int afterChunks)
    {
        var job = new PausingCommitJob(afterChunks);
        foreach (var d in services.Where(d => d.ServiceType == typeof(IJobHandler) && d.ImplementationType == typeof(ImportCommitJob)).ToList())
        {
            services.Remove(d);
        }

        services.AddSingleton<IJobHandler>(job);
        return job;
    }

    public Task RunAsync(IJobExecution job, CancellationToken cancellationToken) => _inner.RunAsync(new Pausing(job, this, afterChunks), cancellationToken);

    private sealed class Pausing(IJobExecution inner, PausingCommitJob owner, int after) : IJobExecution
    {
        private int _chunks;

        public Guid RunId => inner.RunId;

        public string Code => inner.Code;

        public Guid? CompanyId => inner.CompanyId;

        public System.Text.Json.JsonElement Parameters => inner.Parameters;

        public System.Text.Json.JsonElement? Checkpoint => inner.Checkpoint;

        public int Attempt => inner.Attempt;

        public DateTimeOffset? ScheduledFor => inner.ScheduledFor;

        public IServiceProvider Services => inner.Services;

        public async Task CommitChunkAsync(Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? work, object? checkpoint, decimal? progressPct, CancellationToken cancellationToken)
        {
            await inner.CommitChunkAsync(work, checkpoint, progressPct, cancellationToken);
            if (++_chunks == after)
            {
                owner.Reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public Task LogAsync(string level, string message, CancellationToken cancellationToken) => inner.LogAsync(level, message, cancellationToken);
    }
}

/// <summary>The commit job that fails (an unexpected error, not a stopped worker) after some saved chunks, in its first attempts (review finding 8).</summary>
public sealed class FailingCommitJob(int afterChunks, int failingAttempts) : IJobHandler
{
    private readonly ImportCommitJob _inner = new();

    public string Code => _inner.Code;

    public static FailingCommitJob Install(IServiceCollection services, int afterChunks, int failingAttempts)
    {
        var job = new FailingCommitJob(afterChunks, failingAttempts);
        foreach (var d in services.Where(d => d.ServiceType == typeof(IJobHandler) && d.ImplementationType == typeof(ImportCommitJob)).ToList())
        {
            services.Remove(d);
        }

        services.AddSingleton<IJobHandler>(job);
        return job;
    }

    public Task RunAsync(IJobExecution job, CancellationToken cancellationToken) => _inner.RunAsync(new Failing(job, afterChunks, failingAttempts), cancellationToken);

    private sealed class Failing(IJobExecution inner, int after, int failingAttempts) : IJobExecution
    {
        private int _chunks;

        public Guid RunId => inner.RunId;

        public string Code => inner.Code;

        public Guid? CompanyId => inner.CompanyId;

        public System.Text.Json.JsonElement Parameters => inner.Parameters;

        public System.Text.Json.JsonElement? Checkpoint => inner.Checkpoint;

        public int Attempt => inner.Attempt;

        public DateTimeOffset? ScheduledFor => inner.ScheduledFor;

        public IServiceProvider Services => inner.Services;

        public async Task CommitChunkAsync(Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? work, object? checkpoint, decimal? progressPct, CancellationToken cancellationToken)
        {
            await inner.CommitChunkAsync(work, checkpoint, progressPct, cancellationToken);
            if (++_chunks == after && inner.Attempt <= failingAttempts)
            {
                throw new InvalidOperationException("The writer failed (test).");
            }
        }

        public Task LogAsync(string level, string message, CancellationToken cancellationToken) => inner.LogAsync(level, message, cancellationToken);
    }
}
