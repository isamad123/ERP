using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Itg;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>A scope of the run's services with its own database context, bound to the same customer and person as the run (slice S1.16a). Writes in it carry the audit source <c>import</c>.</summary>
internal sealed class ImportRunScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;

    private ImportRunScope(AsyncServiceScope scope)
    {
        _scope = scope;
    }

    public IServiceProvider Services => _scope.ServiceProvider;

    public KernelDbContext Db => _scope.ServiceProvider.GetRequiredService<KernelDbContext>();

    public static ImportRunScope Create(IServiceProvider parent)
    {
        var scope = parent.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var tenant = parent.GetRequiredService<TenantContext>();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant.Tenant, tenant.AccessMode, tenant.DataSource, tenant.Licence);
        var from = parent.GetRequiredService<JobRunContext>();
        var to = scope.ServiceProvider.GetRequiredService<JobRunContext>();
        if (from.ActorUserId is { } person)
        {
            to.BindRequester(from.RunId, from.Code, person);
        }
        else
        {
            to.BindJobActor(from.RunId, from.Code, from.Grant ?? new HashSet<string>());
        }

        to.AuditSource = AuditSources.Import;
        return new ImportRunScope(scope);
    }

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}

/// <summary>What a run of an import job needs: the batch, its template and type, and the file's rows.</summary>
internal sealed record ImportRunData(Guid BatchId, Guid CompanyId, string Status, string Mode, Guid FileId, string FileName, int Total, ImportTypeDefinition Type, TemplateMapping Mapping, string Format, int OkRows, int ErrorRows);

internal static class ImportRunSupport
{
    public static Guid BatchIdOf(IJobExecution job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Parameters.ValueKind == JsonValueKind.Object && job.Parameters.TryGetProperty("batchId", out var b) && b.TryGetGuid(out var id)
            ? id : throw new InvalidOperationException("The run needs the parameter batchId.");
    }

    public static async Task<ImportRunData?> LoadAsync(IServiceProvider services, Guid batchId, string expectedStatus, CancellationToken ct)
    {
        var tenant = services.GetRequiredService<ITenantConnectionProvider>();
        var catalog = services.GetRequiredService<ImportCatalog>();
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        var row = (await OpsAlerts.ReadAsync(c, """
            select b.company_id, b.status, b.mode, b.file_id, coalesce(b.file_name, ''), coalesce(b.total_rows, 0), t.entity_type, t.mapping::text, t.format, coalesce(b.ok_rows, 0), coalesce(b.error_rows, 0)
            from itg.import_batches b join itg.import_templates t on t.id = b.template_id where b.id = $1
            """, r => (Company: r.GetGuid(0), Status: r.GetString(1), Mode: r.GetString(2), File: r.GetGuid(3), Name: r.GetString(4), Total: r.GetInt32(5), Type: r.GetString(6), Mapping: r.GetString(7), Format: r.GetString(8), Ok: r.GetInt32(9), Err: r.GetInt32(10)), ct, batchId).ConfigureAwait(false)).SingleOrDefault();
        if (row.Type is null || row.Status != expectedStatus)
        {
            return null;
        }

        var type = catalog.Find(row.Type) ?? throw new JobFailedException("errors.import.type_unknown");
        using var doc = JsonDocument.Parse(row.Mapping);
        return new ImportRunData(batchId, row.Company, row.Status, row.Mode, row.File, row.Name, row.Total, type, TemplateMapping.Parse(doc.RootElement), row.Format, row.Ok, row.Err);
    }

    public static async Task SetStatusAsync(IServiceProvider services, Guid batchId, string status, string? from, CancellationToken ct)
    {
        var tenant = services.GetRequiredService<ITenantConnectionProvider>();
        var clock = services.GetRequiredService<IClock>();
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, null, "update itg.import_batches set status = $2, updated_at = $3, row_version = row_version + 1 where id = $1 and ($4::text is null or status = $4)", ct, batchId, status, clock.UtcNow, from).ConfigureAwait(false);
    }

    public static async Task WriteErrorsAsync(IServiceProvider services, Guid batchId, IReadOnlyList<(int RowNo, ImportRowError Error)> errors, string phase, DbConnection? connection, DbTransaction? transaction, CancellationToken ct)
    {
        if (errors.Count == 0)
        {
            return;
        }

        var clock = services.GetRequiredService<IClock>();
        var by = services.GetRequiredService<ICurrentActor>().UserId;
        var now = clock.UtcNow;
        if (connection is null)
        {
            var tenant = services.GetRequiredService<ITenantConnectionProvider>();
            await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await InsertAsync(c, tx, batchId, errors, phase, now, by, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        await InsertAsync((NpgsqlConnection)connection, (NpgsqlTransaction?)transaction, batchId, errors, phase, now, by, ct).ConfigureAwait(false);
    }

    private static async Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid batchId, IReadOnlyList<(int RowNo, ImportRowError Error)> errors, string phase, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        foreach (var (rowNo, error) in errors)
        {
            await IdentityDb.ExecuteAsync(c, tx, "insert into itg.import_batch_errors (id, batch_id, row_no, field, message, phase, created_at, created_by) values ($1, $2, $3, $4, $5, $6, $7, $8)", ct,
                Guid.CreateVersion7(now), batchId, rowNo, error.Field, error.Message, phase, now, by).ConfigureAwait(false);
        }
    }

    public static async Task AuditStatusAsync(IServiceProvider services, DbConnection c, DbTransaction tx, Guid batchId, Guid company, string from, string to, CancellationToken ct)
    {
        var clock = services.GetRequiredService<IClock>();
        var by = services.GetRequiredService<ICurrentActor>().UserId;
        var context = services.GetRequiredService<IAuditContext>();
        var now = clock.UtcNow;
        await AuditDb.AppendChangesAsync((NpgsqlConnection)c, (NpgsqlTransaction)tx, [new ChangeEntry(Guid.CreateVersion7(now), now, by, context.ActorKind, "itg.import_batches", batchId, company, "status",
            new JsonArray(new JsonObject { ["field"] = "status", ["before"] = from, ["after"] = to }), null, context.CorrelationId, context.IpAddress, AuditSources.Import, by)], ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The validation run of an import batch (slice S1.16a, FR-KRN-INT-03, -04): every row goes through its writer inside one database
/// transaction that is rolled back at the end, so the run proves the file would load without saving anything. Problems are
/// recorded as they are found. The run is never retried (the problem list of a second run would double the first).
/// </summary>
public sealed class ImportValidateJob : IJobHandler
{
    private const int FlushEvery = 200;
    private const int ProgressEvery = 500;
    private const int SegmentRows = 500;

    public string Code => ImportJobCodes.Validate;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var batchId = ImportRunSupport.BatchIdOf(job);
        var data = await ImportRunSupport.LoadAsync(job.Services, batchId, "validating", cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            await job.LogAsync("warning", "The batch is not waiting for a validation run; nothing done.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var content = await job.Services.GetRequiredService<ImportFiles>().ReadAsync(data.FileId, cancellationToken).ConfigureAwait(false);
            using var sheet = ImportFileReader.Open(content, ImportFileReader.Detect(content, data.FileName) ?? data.Format, data.Mapping);
            var errors = new List<(int, ImportRowError)>();
            foreach (var missing in ImportRowPipeline.MissingHeaders(data.Type, data.Mapping, sheet.Headers))
            {
                errors.Add((1, new ImportRowError(missing, "errors.import.header_missing")));
            }

            int ok = 0, bad = 0, done = 0;
            if (errors.Count == 0)
            {
                await using var run = ImportRunScope.Create(job.Services);
                var db = run.Db;
                await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var pipeline = new ImportRowPipeline(data.Type, data.Mapping, data.Mode, sheet.Headers, ImportWriters.Generated(run.Services, data.Type.Writer ?? data.Type.Table), db, run.Services.GetRequiredService<ICallerAccess>());
                    foreach (var (rowNo, cells) in sheet.Rows())
                    {
                        var result = await pipeline.ProcessAsync(rowNo, cells, dryRun: true, mapOnly: false, cancellationToken).ConfigureAwait(false);
                        done++;
                        if (result.Ok)
                        {
                            ok++;
                        }
                        else
                        {
                            bad++;
                            errors.AddRange(result.Errors.Select(e => (rowNo, e)));
                        }

                        if (errors.Count >= FlushEvery)
                        {
                            await ImportRunSupport.WriteErrorsAsync(job.Services, batchId, errors, "validate", null, null, cancellationToken).ConfigureAwait(false);
                            errors.Clear();
                        }

                        if (done % SegmentRows == 0)
                        {
                            // One transaction for the whole file would collect tens of thousands of savepoints and slow every row down: the check
                            // starts a new transaction every SegmentRows rows (the keys of good rows are remembered for later rows).
                            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            await tx.DisposeAsync().ConfigureAwait(false);
                            db.ChangeTracker.Clear();
                            tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                        }

                        if (done % ProgressEvery == 0)
                        {
                            await job.CommitChunkAsync(null, new { row = rowNo }, data.Total == 0 ? 0 : Math.Min(99m, done * 100m / data.Total), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                bad = data.Total;
            }

            await ImportRunSupport.WriteErrorsAsync(job.Services, batchId, errors, "validate", null, null, cancellationToken).ConfigureAwait(false);
            var tenant = job.Services.GetRequiredService<ITenantConnectionProvider>();
            var now = job.Services.GetRequiredService<IClock>().UtcNow;
            await using (var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false))
            await using (var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                await IdentityDb.ExecuteAsync(c, tx, "update itg.import_batches set status = 'validated', ok_rows = $2, error_rows = $3, validated_at = $4, updated_at = $4, row_version = row_version + 1 where id = $1 and status = 'validating'",
                    cancellationToken, batchId, ok, bad, now).ConfigureAwait(false);
                await ImportRunSupport.AuditStatusAsync(job.Services, c, tx, batchId, data.CompanyId, "validating", "validated", cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{ok} rows are good, {bad} have problems; nothing was saved"), cancellationToken).ConfigureAwait(false);
        }
        catch (JobStoppedException)
        {
            await ImportRunSupport.SetStatusAsync(job.Services, batchId, "uploaded", "validating", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await ImportRunSupport.SetStatusAsync(job.Services, batchId, "failed", "validating", CancellationToken.None).ConfigureAwait(false);
            await job.LogAsync("error", e is ImportRefusedException or ImportFileException ? e.Message : "The check stopped: " + e.GetType().Name, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// The commit of an import batch (slice S1.16a, FR-KRN-INT-04): rows are written in chunks, each chunk together with its checkpoint, the
/// records it wrote and its problems in one transaction, so a worker that stops and starts again finishes the batch with no row done
/// twice. Audit source <c>import</c>. Rows that had problems in the validation run are not written. After the last chunk a type with a
/// post-commit step (users: the invitation e-mail) does it for the records the batch created.
/// </summary>
public sealed class ImportCommitJob : IJobHandler
{
    private const int ChunkRows = 200;

    public string Code => ImportJobCodes.Commit;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var batchId = ImportRunSupport.BatchIdOf(job);
        var data = await ImportRunSupport.LoadAsync(job.Services, batchId, "committing", cancellationToken).ConfigureAwait(false);
        var resumed = false;
        if (data is null)
        {
            // A run taken over after the batch was finished still owes its post-commit step.
            data = await ImportRunSupport.LoadAsync(job.Services, batchId, "committed", cancellationToken).ConfigureAwait(false);
            resumed = data is not null;
            if (data is null)
            {
                await job.LogAsync("warning", "The batch is not waiting for a commit; nothing done.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            var state = new ImportCommitState(job.Checkpoint);
            if (!resumed && !state.Done)
            {
                await WriteChunksAsync(job, data, state, cancellationToken).ConfigureAwait(false);
            }

            await PostCommitAsync(job, data, state, cancellationToken).ConfigureAwait(false);
        }
        catch (JobStoppedException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await ImportRunSupport.SetStatusAsync(job.Services, batchId, "failed", "committing", CancellationToken.None).ConfigureAwait(false);
            await job.LogAsync("error", e is ImportRefusedException or ImportFileException ? e.Message : "The commit stopped: " + e.GetType().Name, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteChunksAsync(IJobExecution job, ImportRunData data, ImportCommitState state, CancellationToken ct)
    {
        var content = await job.Services.GetRequiredService<ImportFiles>().ReadAsync(data.FileId, ct).ConfigureAwait(false);
        var tenant = job.Services.GetRequiredService<ITenantConnectionProvider>();
        HashSet<int> skip;
        await using (var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false))
        {
            skip = [.. await OpsAlerts.ReadAsync(c, "select distinct row_no from itg.import_batch_errors where batch_id = $1 and phase = 'validate'", r => r.GetInt32(0), ct, data.BatchId).ConfigureAwait(false)];
        }

        using var sheet = ImportFileReader.Open(content, ImportFileReader.Detect(content, data.FileName) ?? data.Format, data.Mapping);
        using var rows = sheet.Rows().GetEnumerator();
        var more = true;
        while (more)
        {
            var chunk = new List<(int RowNo, string?[] Cells)>();
            var last = state.LastRow;
            while (chunk.Count < ChunkRows && (more = rows.MoveNext()))
            {
                var (rowNo, cells) = rows.Current;
                last = Math.Max(last, rowNo);
                if (rowNo > state.LastRow && !skip.Contains(rowNo))
                {
                    chunk.Add((rowNo, cells));
                }
            }

            if (chunk.Count == 0 && !more)
            {
                break;
            }

            if (chunk.Count == 0)
            {
                continue;
            }

            var next = state.Clone();
            next.LastRow = last;
            await job.CommitChunkAsync(async (conn, tx, token) => await ApplyChunkAsync(job, data, sheet.Headers, chunk, next, conn, tx, token).ConfigureAwait(false), next,
                data.Total == 0 ? 0 : Math.Min(99m, (state.Seen + chunk.Count) * 100m / Math.Max(1, data.OkRows)), ct).ConfigureAwait(false);
            state.CopyFrom(next);
        }

        var final = state.Clone();
        final.Done = true;
        await job.CommitChunkAsync(async (conn, tx, token) =>
        {
            await IdentityDb.ExecuteAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, """
                update itg.import_batches set status = 'committed', created_rows = $2, updated_rows = $3, ok_rows = $4, error_rows = error_rows + $5, committed_at = $6, updated_at = $6, row_version = row_version + 1
                where id = $1 and status = 'committing'
                """, token, data.BatchId, final.Created, final.Updated, final.Created + final.Updated, final.Errors, job.Services.GetRequiredService<IClock>().UtcNow).ConfigureAwait(false);
            await ImportRunSupport.AuditStatusAsync(job.Services, conn, tx, data.BatchId, data.CompanyId, "committing", "committed", token).ConfigureAwait(false);
        }, final, 100m, ct).ConfigureAwait(false);
        state.CopyFrom(final);
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{state.Created} records created, {state.Updated} updated, {state.Errors} rows not saved"), ct).ConfigureAwait(false);
    }

    private static async Task ApplyChunkAsync(IJobExecution job, ImportRunData data, IReadOnlyList<string> headers, List<(int RowNo, string?[] Cells)> chunk, ImportCommitState state, DbConnection conn, DbTransaction tx, CancellationToken ct)
    {
        await using var run = ImportRunScope.Create(job.Services);
        var db = run.Db;
        db.Database.SetDbConnection(conn);
        await db.Database.UseTransactionAsync(tx, ct).ConfigureAwait(false);
        var pipeline = new ImportRowPipeline(data.Type, data.Mapping, data.Mode, headers, ImportWriters.Generated(run.Services, data.Type.Writer ?? data.Type.Table), db, run.Services.GetRequiredService<ICallerAccess>());
        var clock = run.Services.GetRequiredService<IClock>();
        var by = run.Services.GetRequiredService<ICurrentActor>().UserId;
        var problems = new List<(int, ImportRowError)>();
        foreach (var (rowNo, cells) in chunk)
        {
            state.Seen++;
            var result = await pipeline.ProcessAsync(rowNo, cells, dryRun: false, mapOnly: false, ct).ConfigureAwait(false);
            if (!result.Ok || result.Outcome is null)
            {
                state.Errors++;
                problems.AddRange(result.Errors.Select(e => (rowNo, e)));
                continue;
            }

            foreach (var record in result.Outcome.Records)
            {
                var version = await IdentityDb.ScalarAsync<long?>((NpgsqlConnection)conn, (NpgsqlTransaction)tx, $"select row_version from {ImportService.Safe(record.Table)} where id = $1", ct, record.Id).ConfigureAwait(false);
                await IdentityDb.ExecuteAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, """
                    insert into itg.import_batch_rows (id, batch_id, row_no, table_name, record_id, action, record_version, created_at, created_by) values ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                    """, ct, Guid.CreateVersion7(clock.UtcNow), data.BatchId, rowNo, record.Table, record.Id, result.Outcome.Action ?? "created", version is { } v ? (int?)v : null, clock.UtcNow, by).ConfigureAwait(false);
            }

            if (result.Outcome.Action == "updated")
            {
                state.Updated++;
            }
            else
            {
                state.Created++;
            }
        }

        await ImportRunSupport.WriteErrorsAsync(job.Services, data.BatchId, problems, "commit", conn, tx, ct).ConfigureAwait(false);
    }

    private static async Task PostCommitAsync(IJobExecution job, ImportRunData data, ImportCommitState state, CancellationToken ct)
    {
        if (data.Type.PostCommit != "invite")
        {
            return;
        }

        var permissions = job.Services.GetRequiredService<IPermissionChecker>();
        if (!await permissions.HasPermissionAsync("iam.users.send_invitation", ct).ConfigureAwait(false))
        {
            await job.LogAsync("warning", "The person who started the import may not send invitations; the new users stay invited and can be sent theirs from the user list.", ct).ConfigureAwait(false);
            return;
        }

        var tenant = job.Services.GetRequiredService<ITenantConnectionProvider>();
        List<Guid> users;
        await using (var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false))
        {
            users = await OpsAlerts.ReadAsync(c, "select record_id from itg.import_batch_rows where batch_id = $1 and table_name = 'iam.users' and action = 'created' order by row_no, created_at, id", r => r.GetGuid(0), ct, data.BatchId).ConfigureAwait(false);
        }

        var email = job.Services.GetRequiredService<IIdentityEmailService>();
        for (var i = state.Invited; i < users.Count; i++)
        {
            var sent = await email.SendInvitationAsync(users[i], ct).ConfigureAwait(false);
            if (sent.Outcome != AdminOutcome.Done)
            {
                await job.LogAsync("warning", string.Create(CultureInfo.InvariantCulture, $"Invitation of record {users[i]} was not sent: {sent.Error}"), ct).ConfigureAwait(false);
            }

            state.Invited = i + 1;
            if (state.Invited % 50 == 0)
            {
                await job.CommitChunkAsync(null, state, 100m, ct).ConfigureAwait(false);
            }
        }

        await job.CommitChunkAsync(null, state, 100m, ct).ConfigureAwait(false);
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{users.Count} invitations handled"), ct).ConfigureAwait(false);
    }

}

/// <summary>The progress a commit run keeps with each chunk: the last file row handled and the counts so far.</summary>
internal sealed class ImportCommitState
{
    public ImportCommitState(JsonElement? checkpoint)
    {
        if (checkpoint is { ValueKind: JsonValueKind.Object } c)
        {
            LastRow = Get(c, "lastRow");
            Created = Get(c, "created");
            Updated = Get(c, "updated");
            Errors = Get(c, "errors");
            Seen = Get(c, "seen");
            Invited = Get(c, "invited");
            Done = c.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
        }
    }

    private ImportCommitState()
    {
    }

    [JsonPropertyName("lastRow")]
    public int LastRow { get; set; }

    [JsonPropertyName("created")]
    public int Created { get; set; }

    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    [JsonPropertyName("seen")]
    public int Seen { get; set; }

    [JsonPropertyName("invited")]
    public int Invited { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    public ImportCommitState Clone() => new() { LastRow = LastRow, Created = Created, Updated = Updated, Errors = Errors, Seen = Seen, Invited = Invited, Done = Done };

    public void CopyFrom(ImportCommitState other)
    {
        LastRow = other.LastRow;
        Created = other.Created;
        Updated = other.Updated;
        Errors = other.Errors;
        Seen = other.Seen;
        Invited = other.Invited;
        Done = other.Done;
    }

    private static int Get(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
}
