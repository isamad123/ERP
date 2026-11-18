using System.Collections.Concurrent;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Host.Worker;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.Generated.Org;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Generated.Org;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// The jobs framework of slice S1.11a as the worker host runs it: each "worker" is a separate service provider built by
/// <see cref="WorkerHost.Configure"/> (its own worker id, slots and lease), as separate processes would be, against the
/// shared test control plane and real PostgreSQL. Every pass is limited to this rig's own customers, so the jobs of other
/// tests' customers are never touched. The test module's jobs (<see cref="Manifest"/>) are registered next to the kernel's.
/// </summary>
public sealed class JobRig : IAsyncDisposable
{
    private readonly TenancyFixture _fx;
    private readonly List<ServiceProvider> _providers = [];

    private JobRig(TenancyFixture fx, string code, TestClock? clock = null)
    {
        _fx = fx;
        Code = code;
        Codes.Add(code);
        Clock = clock ?? new TestClock();
    }

    /// <summary>The rig's first customer.</summary>
    public string Code { get; }

    /// <summary>Every customer of the rig (passes are limited to these).</summary>
    public List<string> Codes { get; } = [];

    public TestClock Clock { get; }

    public TenancyFixture Fixture => _fx;

    public const string Manifest = """
        {"module": "test", "jobs": [
          {"code": "test.rows", "name": {"en": "Rows", "ur": "قطاریں"}, "description": {"en": "Writes rows in chunks", "ur": "حصوں میں قطاریں لکھتا ہے"},
           "parameters": [{"name": "rows", "type": "int", "required": true}, {"name": "chunk", "type": "int"}, {"name": "pause_at", "type": "int"}],
           "permissions": [], "retry": {"max_attempts": 3, "backoff_seconds": [5]}, "timeout_seconds": 600, "concurrency": 4,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.count", "name": {"en": "Count", "ur": "گنتی"}, "description": {"en": "Counts its runs", "ur": "اپنے چلنے گنتا ہے"},
           "permissions": [], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 16,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.scheduled", "name": {"en": "Every minute", "ur": "ہر منٹ"}, "description": {"en": "Counts its runs", "ur": "اپنے چلنے گنتا ہے"},
           "schedule": {"every_minutes": 1}, "enabled": true,
           "permissions": [], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 1,
           "runs_in": ["full"], "entitlement": "platform.base"},
          {"code": "test.sleep", "name": {"en": "Sleep", "ur": "نیند"}, "description": {"en": "Runs past its time limit", "ur": "مقررہ مدت سے زیادہ چلتا ہے"},
           "permissions": [], "retry": {"max_attempts": 2, "backoff_seconds": [5]}, "timeout_seconds": 1, "concurrency": 4,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.block", "name": {"en": "Block", "ur": "رکاوٹ"}, "description": {"en": "Waits until released", "ur": "اجازت تک انتظار کرتا ہے"},
           "permissions": [], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 600, "concurrency": 16,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.write", "name": {"en": "Write a branch", "ur": "شاخ لکھنا"}, "description": {"en": "Creates a branch", "ur": "شاخ بناتا ہے"},
           "parameters": [{"name": "company_id", "type": "uuid", "required": true}, {"name": "code", "type": "text", "required": true}],
           "permissions": ["org.branches.create"], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 4,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.write_denied", "name": {"en": "Write without the right", "ur": "اختیار کے بغیر لکھنا"}, "description": {"en": "Tries to create a branch", "ur": "شاخ بنانے کی کوشش کرتا ہے"},
           "parameters": [{"name": "company_id", "type": "uuid", "required": true}, {"name": "code", "type": "text", "required": true}],
           "permissions": ["org.branches.read"], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 4,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.requester", "name": {"en": "As the requester", "ur": "درخواست گزار کے طور پر"}, "description": {"en": "Acts with the requester's rights", "ur": "درخواست گزار کے اختیارات سے کام کرتا ہے"},
           "permissions": ["org.companies.update"], "runs_as": "requester", "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 4,
           "runs_in": ["full"], "entitlement": "platform.base", "manual": true, "request_permission": "job.runs.create"},
          {"code": "test.health", "name": {"en": "Health", "ur": "صحت"}, "description": {"en": "Runs in read-only and locked mode", "ur": "صرف پڑھنے اور مقفل حالت میں چلتا ہے"},
           "schedule": {"every_minutes": 1}, "enabled": true,
           "permissions": [], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 1,
           "runs_in": ["full", "read_only", "locked"], "entitlement": "platform.base"},
          {"code": "test.suspended", "name": {"en": "Also suspended", "ur": "معطل میں بھی"}, "description": {"en": "Runs in every mode", "ur": "ہر حالت میں چلتا ہے"},
           "schedule": {"every_minutes": 1}, "enabled": true,
           "permissions": [], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 1,
           "runs_in": ["full", "read_only", "locked", "suspended"], "entitlement": "platform.base"},
          {"code": "test.unlicensed", "name": {"en": "Unlicensed", "ur": "غیر لائسنس یافتہ"}, "description": {"en": "Feature not licensed", "ur": "فیچر لائسنس میں نہیں"},
           "schedule": {"every_minutes": 1}, "enabled": true,
           "permissions": [], "retry": {"max_attempts": 1, "backoff_seconds": [5]}, "timeout_seconds": 60, "concurrency": 1,
           "runs_in": ["full"], "entitlement": "hrm.recruitment"}
        ]}
        """;

    public static IReadOnlyList<JobSpec> Specs { get; } = JobManifest.Parse(Manifest);

    private static int _next;

    /// <summary>Each test gets its own customer: the codes of one fixture differ by a counter.</summary>
    private static string Next() => Interlocked.Increment(ref _next).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A rig with a fresh customer (prefix + the fixture's suffix) and the test jobs seeded.</summary>
    public static async Task<JobRig> StartAsync(TenancyFixture fx, string prefix, CancellationToken ct)
    {
        var rig = new JobRig(fx, prefix + Next() + fx.Suffix);
        await fx.ProvisionAsync(rig.Code, ct);
        await rig.PrepareAsync(rig.Code, ct);
        return rig;
    }

    /// <summary>
    /// A rig on a customer another harness owns (the workflow tests' customer), sharing that harness's clock, so its
    /// kernel jobs can be run through the worker exactly as the loops ran them. Its schedules are switched off (no worker runs
    /// there otherwise); a test queues the run it needs.
    /// </summary>
    public static async Task<JobRig> ForExistingAsync(TenancyFixture fx, string code, TestClock clock, CancellationToken ct)
    {
        var rig = new JobRig(fx, code, clock);
        await using var c = await fx.OpenTenantAsync(code, TenantLogins.Owner, ct);
        await JobSetup.EnsureAsync(c, null, Specs, clock.UtcNow, ct);
        await TenancyFixture.ExecAsync(c, "update job.definitions set is_enabled = false", ct);
        return rig;
    }

    /// <summary>Another customer for this rig (fair-share tests).</summary>
    public async Task<string> AddCustomerAsync(string prefix, CancellationToken ct)
    {
        var code = prefix + Next() + _fx.Suffix;
        await _fx.ProvisionAsync(code, ct);
        await PrepareAsync(code, ct);
        Codes.Add(code);
        return code;
    }

    private async Task PrepareAsync(string code, CancellationToken ct)
    {
        await using var c = await _fx.OpenTenantAsync(code, TenantLogins.Owner, ct);
        await JobSetup.EnsureAsync(c, null, Specs, Clock.UtcNow, ct);
        // The rows job writes into a scratch table; the primary key refuses a row written twice.
        await TenancyFixture.ExecAsync(c, """
            create schema if not exists job_test;
            create table if not exists job_test.rows (run_id uuid not null, n integer not null, primary key (run_id, n));
            grant usage on schema job_test to app_rw;
            grant select, insert on job_test.rows to app_rw;
            """, ct);
        // Every schedule is off in these customers until a test switches one on, so only the runs a test asks for run.
        await TenancyFixture.ExecAsync(c, "update job.definitions set is_enabled = false", ct);
    }

    /// <summary>A worker process: its own provider, worker id, slots and lease.</summary>
    public JobWorker Worker(int slots = 4, int perTenant = 2, int leaseSeconds = 60, Action<IServiceCollection>? extra = null) =>
        Provider(slots, perTenant, leaseSeconds, extra).GetRequiredService<JobWorker>();

    public ServiceProvider Provider(int slots = 4, int perTenant = 2, int leaseSeconds = 60, Action<IServiceCollection>? extra = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ERP_CONTROL_PLANE"] = _fx.Options.ConnectionString,
            ["ERP_PLATFORM_ACTOR"] = _fx.Admin.Email,
            ["Settings:RefreshMilliseconds"] = "0",
            ["Jobs:Slots"] = slots.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Jobs:PerTenantSlots"] = perTenant.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Jobs:LeaseSeconds"] = leaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var services = new ServiceCollection();
        WorkerHost.Configure(services, config, _fx.Secrets, s =>
        {
            s.RemoveAll<IClock>();
            s.AddSingleton<IClock>(Clock);
            s.AddSingleton(new JobManifestSource(Manifest));
            TestJobs.Register(s);
            Workflow.WorkflowRig.RegisterTestModule(s);
            extra?.Invoke(s);
        });
        var provider = services.BuildServiceProvider();
        lock (_providers)
        {
            _providers.Add(provider);
        }

        return provider;
    }

    /// <summary>One pass of a worker over this rig's customers.</summary>
    public Task<JobPollOutcome> PollAsync(JobWorker worker, CancellationToken ct) => worker.PollOnceAsync(Codes, ct);

    /// <summary>Passes until nothing more starts, waiting for the started runs each time.</summary>
    public async Task RunUntilIdleAsync(JobWorker worker, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            var outcome = await PollAsync(worker, ct);
            Assert.True(outcome.Errors.Count == 0, string.Join("; ", outcome.Errors));
            await worker.WhenIdleAsync();
            if (outcome.Started == 0)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ database

    public Task<NpgsqlConnection> OwnerAsync(string? code = null, CancellationToken ct = default) => _fx.OpenTenantAsync(code ?? Code, TenantLogins.Owner, ct);

    /// <summary>Queues a run as the job centre would (a person's "run now"), returning its id.</summary>
    public async Task<Guid> QueueAsync(string job, object? parameters = null, string? code = null, Guid? requestedBy = null, Guid? actor = null, Guid? company = null, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        await using var c = await OwnerAsync(code, ct);
        await TenancyFixture.ExecAsync(c, """
            insert into job.runs (id, definition_id, company_id, status, priority, attempt, parameters, requested_by, actor_user_id, created_at, created_by, updated_at, updated_by, row_version)
            select $1, d.id, $6, 'queued', d.priority, 0, $3::jsonb, $4, $5, $7, $8, $7, $8, 1 from job.definitions d where d.code = $2
            """, ct, id, job, JsonSerializer.Serialize(parameters ?? new { }), (object?)requestedBy ?? DBNull.Value, (object?)actor ?? DBNull.Value, (object?)company ?? DBNull.Value, Clock.UtcNow, BaselineSeed.SystemUserId);
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, string? code = null, params object?[] values)
    {
        await using var c = await OwnerAsync(code, ct);
        return await Access.AccessHarness.ScalarAsync<T>(c, sql, ct, values);
    }

    public async Task ExecAsync(string sql, CancellationToken ct, string? code = null, params object?[] values)
    {
        await using var c = await OwnerAsync(code, ct);
        await Access.AccessHarness.ExecAsync(c, sql, ct, values);
    }

    public Task<string> StatusAsync(Guid run, CancellationToken ct, string? code = null) => ScalarAsync<string>("select status from job.runs where id = $1", ct, code, run);

    public async Task<List<string>> LogsAsync(Guid run, CancellationToken ct)
    {
        await using var c = await OwnerAsync(null, ct);
        await using var command = new NpgsqlCommand("select level || ': ' || message from job.run_logs where run_id = $1 order by created_at, id", c);
        command.Parameters.Add(new NpgsqlParameter { Value = run });
        var list = new List<string>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    public Task<long> AlertsAsync(string kind, CancellationToken ct, string? code = null) =>
        ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = 'kernel.alert.raised' and payload->>'kind' = $1", ct, code, kind);

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            foreach (var worker in new[] { provider.GetService<JobWorker>() }.OfType<JobWorker>())
            {
                await worker.KillAsync();
            }

            await provider.DisposeAsync();
        }

        TestJobs.Release();
    }
}

/// <summary>The test module's job handlers and what they saw.</summary>
public static class TestJobs
{
    /// <summary>How many times each run's handler started (a run executed once has 1).</summary>
    public static ConcurrentDictionary<Guid, int> Starts { get; } = new();

    /// <summary>The actor each run saw: (ICurrentActor user, IAuditContext source, actor kind).</summary>
    public static ConcurrentDictionary<Guid, (Guid User, string Source, string Kind)> Seen { get; } = new();

    /// <summary>The result of a write job: "ok" or the exception type.</summary>
    public static ConcurrentDictionary<Guid, string> Writes { get; } = new();

    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Reached = new();
    private static TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Continue = new();

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, RowsJob>();
        services.AddSingleton<IJobHandler>(new CountJob("test.count"));
        services.AddSingleton<IJobHandler>(new CountJob("test.scheduled"));
        services.AddSingleton<IJobHandler>(new CountJob("test.health"));
        services.AddSingleton<IJobHandler>(new CountJob("test.suspended"));
        services.AddSingleton<IJobHandler>(new CountJob("test.unlicensed"));
        services.AddSingleton<IJobHandler, SleepJob>();
        services.AddSingleton<IJobHandler, BlockJob>();
        services.AddSingleton<IJobHandler>(new WriteJob("test.write"));
        services.AddSingleton<IJobHandler>(new WriteJob("test.write_denied"));
        services.AddSingleton<IJobHandler, RequesterJob>();
    }

    /// <summary>Completes when the rows job of <paramref name="run"/> reaches its pause point.</summary>
    public static Task ReachedAsync(Guid run) => Reached.GetOrAdd(run, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>Lets the rows job of <paramref name="run"/> go on after its pause point.</summary>
    public static void Resume(Guid run) => Continue.GetOrAdd(run, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Releases every blocked job.</summary>
    public static void Release()
    {
        var release = _release;
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
    }

    private static void Count(IJobExecution job)
    {
        Starts.AddOrUpdate(job.RunId, 1, (_, n) => n + 1);
        var audit = job.Services.GetRequiredService<IAuditContext>();
        Seen[job.RunId] = (job.Services.GetRequiredService<ICurrentActor>().UserId, audit.Source, audit.ActorKind);
    }

    /// <summary>
    /// Writes rows 0..rows-1 into job_test.rows in chunks, saving "next" as its checkpoint. With pause_at, the first attempt
    /// stops at that row until <see cref="Resume"/> (or until its worker is killed).
    /// </summary>
    private sealed class RowsJob : IJobHandler
    {
        public string Code => "test.rows";

        public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
        {
            Count(job);
            var total = job.Parameters.GetProperty("rows").GetInt32();
            var chunk = job.Parameters.TryGetProperty("chunk", out var c) ? c.GetInt32() : 1000;
            int? pause = job.Parameters.TryGetProperty("pause_at", out var p) ? p.GetInt32() : null;
            var next = job.Checkpoint is { } checkpoint ? checkpoint.GetProperty("next").GetInt32() : 0;
            while (next < total)
            {
                if (pause == next && job.Attempt == 1)
                {
                    Reached.GetOrAdd(job.RunId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                    await Continue.GetOrAdd(job.RunId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
                }

                var from = next;
                var to = Math.Min(next + chunk, total);
                await job.CommitChunkAsync(async (connection, tx, ct) =>
                {
                    await using var command = new NpgsqlCommand("insert into job_test.rows (run_id, n) select $1, g from generate_series($2, $3) g", (NpgsqlConnection)connection, (NpgsqlTransaction)tx);
                    command.Parameters.Add(new NpgsqlParameter { Value = job.RunId });
                    command.Parameters.Add(new NpgsqlParameter { Value = from });
                    command.Parameters.Add(new NpgsqlParameter { Value = to - 1 });
                    await command.ExecuteNonQueryAsync(ct);
                }, new { next = to }, to * 100m / total, cancellationToken);
                next = to;
            }
        }
    }

    private sealed class CountJob(string code) : IJobHandler
    {
        public string Code => code;

        public Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
        {
            Count(job);
            return Task.CompletedTask;
        }
    }

    private sealed class SleepJob : IJobHandler
    {
        public string Code => "test.sleep";

        public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
        {
            Count(job);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private sealed class BlockJob : IJobHandler
    {
        public string Code => "test.block";

        public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
        {
            Count(job);
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Creates a branch through the generated repository, which asks the access engine (R-009).</summary>
    private sealed class WriteJob(string code) : IJobHandler
    {
        public string Code => code;

        public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
        {
            Count(job);
            var branch = new Branch
            {
                Id = Guid.CreateVersion7(),
                CompanyId = Guid.Parse(job.Parameters.GetProperty("company_id").GetString()!),
                Code = job.Parameters.GetProperty("code").GetString()!,
                NameI18n = JsonDocument.Parse("""{"en": "Job branch", "ur": "جاب شاخ"}"""),
            };
            try
            {
                await job.Services.GetRequiredService<IBranchRepository>().AddAsync(branch, cancellationToken);
                Writes[job.RunId] = "ok";
            }
            catch (Exception ex)
            {
                Writes[job.RunId] = ex.GetType().Name;
                throw;
            }
        }
    }

    private sealed class RequesterJob : IJobHandler
    {
        public string Code => "test.requester";

        public Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
        {
            Count(job);
            return Task.CompletedTask;
        }
    }
}
