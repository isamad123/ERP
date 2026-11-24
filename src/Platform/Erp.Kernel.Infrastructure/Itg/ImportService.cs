using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Itg;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Erp.Kernel.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sylvan.Data.Excel;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// The import centre (slice S1.16a, FR-ADM-DAT-01, FR-KRN-INT-03, -04, FR-ADM-INT-04): templates, upload, the validation run and
/// the commit (both background jobs that act as the person who asked), rollback and the problem lists. Everything a row does goes
/// through the type's writer, so the rules are those of manual entry; this service only decides who may start what and in which state.
/// </summary>
public sealed class ImportService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IAuditContext audit,
    IPermissionChecker permissions,
    ImportCatalog catalog,
    ImportFiles files,
    ICallerAccess access,
    IServiceProvider services) : IImportService
{
    private const int MaxTestRows = 200;
    private const int MaxTestRead = 1000;
    private const string BatchSelect = """
        select b.id, b.template_id, t.name_i18n::text, t.entity_type, coalesce(b.file_name, ''), b.status, b.mode, b.total_rows, b.ok_rows, b.error_rows, b.created_rows, b.updated_rows,
               r.progress_pct, b.job_run_id, b.created_at, b.created_by, b.validated_at, b.committed_at, b.rolled_back_at, b.company_id, b.updated_at
        from itg.import_batches b
        join itg.import_templates t on t.id = b.template_id
        left join job.runs r on r.id = b.job_run_id
        """;

    // ------------------------------------------------------------------ types and templates

    public async Task<IReadOnlyList<ImportTypeView>> TypesAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ImportPermissions.Manage, cancellationToken).ConfigureAwait(false);
        var views = new List<ImportTypeView>();
        foreach (var type in catalog.All.OrderBy(t => t.Code, StringComparer.Ordinal))
        {
            views.Add(new ImportTypeView(type.Code, type.Module, type.Name, type.Permission, type.CanUpdate,
                await permissions.HasPermissionAsync(type.Permission, cancellationToken).ConfigureAwait(false),
                [.. type.Columns.Select(c => new ImportColumnView(c.Name, c.Field, c.Type, c.Required, c.Label, c.Lookup, c.Options, c.Example))]));
        }

        return views;
    }

    public async Task<IReadOnlyList<ImportTemplateView>> TemplatesAsync(string? type, CancellationToken cancellationToken)
    {
        await RequireAnyAsync([ImportPermissions.Manage, ImportPermissions.Design], cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await OpsAlerts.ReadAsync(c, TemplateSelect + " where ($1::text is null or t.entity_type = $1) order by t.is_default desc, t.code", ReadTemplate, cancellationToken, type).ConfigureAwait(false);
    }

    public async Task<ImportTemplateView> CreateTemplateAsync(ImportTemplateWrite request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(ImportPermissions.Design, cancellationToken).ConfigureAwait(false);
        var type = catalog.Find(request.EntityType ?? string.Empty) ?? throw new ImportException("errors.import.type_unknown", 400);
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 40 || !code.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))
        {
            throw new ImportException("errors.import.template_invalid", 400, "code");
        }

        if (code.StartsWith("default.", StringComparison.Ordinal))
        {
            // The prefix belongs to the shipped templates: a customer template with the code of a future one would make the setup skip it (finding 17).
            throw new ImportException("errors.import.template_invalid", 400, "code");
        }

        var (format, mode, name, mapping) = Checked(request, type, null);
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into itg.import_templates (id, code, name_i18n, entity_type, format, mapping, version, mode, is_default, is_active, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, $5, $6, 1, $7, false, true, $8, $9, $8, $9, 1)
                """, cancellationToken, id, code, new JsonPayload(name), type.Code, format, new JsonPayload(mapping), mode, now, actor.UserId).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ImportException("errors.import.template_exists", 409);
        }

        await LogTemplateAsync(c, tx, id, "create", [("code", null, code), ("entity_type", null, type.Code), ("mode", null, mode), ("version", null, 1)], now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await OpsAlerts.ReadAsync(c, TemplateSelect + " where t.id = $1", ReadTemplate, cancellationToken, id).ConfigureAwait(false)).Single();
    }

    public async Task<ImportTemplateView> UpdateTemplateAsync(Guid id, ImportTemplateWrite request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(ImportPermissions.Design, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = (await OpsAlerts.ReadAsync(c, TemplateSelect + " where t.id = $1", ReadTemplate, cancellationToken, id).ConfigureAwait(false)).SingleOrDefault() ?? throw new ImportException("errors.import.not_found", 404);
        var type = catalog.Find(current.EntityType) ?? throw new ImportException("errors.import.type_unknown", 400);
        if (request.RowVersion is null)
        {
            throw new ImportException("errors.import.template_invalid", 400, "rowVersion");
        }

        var (format, mode, name, mapping) = Checked(request, type, current);
        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var changed = await IdentityDb.ExecuteAsync(c, tx, """
            update itg.import_templates set name_i18n = $2, format = $3, mapping = $4, mode = $5, version = version + 1, updated_at = $6, updated_by = $7, row_version = row_version + 1
            where id = $1 and row_version = $8
            """, cancellationToken, id, new JsonPayload(name), format, new JsonPayload(mapping), mode, now, actor.UserId, request.RowVersion.Value).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new ImportException("errors.data.conflict", 409);
        }

        await LogTemplateAsync(c, tx, id, "update", [("mode", current.Mode, mode), ("version", current.Version, current.Version + 1), ("mapping", null, "changed")], now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await OpsAlerts.ReadAsync(c, TemplateSelect + " where t.id = $1", ReadTemplate, cancellationToken, id).ConfigureAwait(false)).Single();
    }

    public async Task<ImportFileContent> TemplateFileAsync(Guid id, string? format, CancellationToken cancellationToken)
    {
        await RequireAnyAsync([ImportPermissions.Manage, ImportPermissions.Design], cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var template = (await OpsAlerts.ReadAsync(c, TemplateSelect + " where t.id = $1", ReadTemplate, cancellationToken, id).ConfigureAwait(false)).SingleOrDefault() ?? throw new ImportException("errors.import.not_found", 404);
        var type = catalog.Find(template.EntityType) ?? throw new ImportException("errors.import.type_unknown", 400);
        var mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(template.Mapping));
        var chosen = string.IsNullOrWhiteSpace(format) ? template.Format : format.Trim().ToLowerInvariant();
        if (chosen is not (ImportFileReader.Csv or ImportFileReader.Xlsx))
        {
            throw new ImportException("errors.import.unsupported_file", 400);
        }

        var headers = new List<string>();
        var example = new List<string>();
        foreach (var column in type.Columns)
        {
            var m = mapping.Columns.FirstOrDefault(x => string.Equals(x.Field, column.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Field, column.Field, StringComparison.OrdinalIgnoreCase));
            if (m is null || string.IsNullOrWhiteSpace(m.Source))
            {
                continue;
            }

            headers.Add(m.Source);
            example.Add(column.Example ?? string.Empty);
        }

        var baseName = template.Code.Replace('.', '-');
        if (chosen == ImportFileReader.Csv)
        {
            var delimiter = mapping.Delimiter ?? ',';
            var text = new StringBuilder().Append(string.Join(delimiter, headers.Select(h => CsvCell(h, delimiter)))).Append("\r\n").Append(string.Join(delimiter, example.Select(e => CsvCell(e, delimiter)))).Append("\r\n").ToString();
            return new ImportFileContent(baseName + ".csv", "text/csv; charset=utf-8", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());
        }

        using var sheet = new DataTable("Template");
        foreach (var h in headers)
        {
            sheet.Columns.Add(h, typeof(string));
        }

        sheet.Rows.Add([.. example.Cast<object>()]);
        using var allowed = new DataTable("Allowed values");
        allowed.Columns.Add("Column", typeof(string));
        allowed.Columns.Add("Allowed values", typeof(string));
        foreach (var column in type.Columns.Where(col => col.Options is { Count: > 0 } || col.Type == "bool" || col.Type == "date" || col.Lookup is not null))
        {
            allowed.Rows.Add(column.Name, column.Options is { Count: > 0 } ? string.Join(", ", column.Options) : column.Type == "bool" ? "yes, no" : column.Type == "date" ? "yyyy-mm-dd" : "a code of " + column.Lookup);
        }

        using var stream = new MemoryStream();
        using (var writer = ExcelDataWriter.Create(stream, ExcelWorkbookType.ExcelXml))
        {
            using (var reader = sheet.CreateDataReader())
            {
                writer.Write(reader, "Template");
            }

            using var allowedReader = allowed.CreateDataReader();
            writer.Write(allowedReader, "Allowed values");
        }

        return new ImportFileContent(baseName + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", stream.ToArray());
    }

    public async Task<ImportTestResult> TestTemplateAsync(Guid id, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        await RequireAnyAsync([ImportPermissions.Manage, ImportPermissions.Design], cancellationToken).ConfigureAwait(false);
        ImportTemplateView template;
        await using (var c = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            template = (await OpsAlerts.ReadAsync(c, TemplateSelect + " where t.id = $1", ReadTemplate, cancellationToken, id).ConfigureAwait(false)).SingleOrDefault() ?? throw new ImportException("errors.import.not_found", 404);
        }

        var type = catalog.Find(template.EntityType) ?? throw new ImportException("errors.import.type_unknown", 400);
        await RequireTypeAsync(type, cancellationToken).ConfigureAwait(false);
        Guid company;
        await using (var c = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            company = await DefaultCompanyAsync(c, cancellationToken).ConfigureAwait(false);
        }

        // The sample goes through the same gate as an upload (finding 3): size, kind of file, virus scan; then the reader's limits.
        string format;
        ImportLimits limits;
        try
        {
            format = await files.GateAsync(content, fileName, company, cancellationToken).ConfigureAwait(false);
            limits = await files.LimitsAsync(company, cancellationToken).ConfigureAwait(false);
        }
        catch (ImportRefusedException e)
        {
            throw new ImportException(e.Message, e.Status);
        }

        var mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(template.Mapping));
        var errors = new List<ImportErrorView>();
        var sample = new List<ImportSampleRow>();
        var total = 0;
        var bad = 0;
        try
        {
            using var sheet = ImportFileReader.Open(content, format, mapping, limits);
            foreach (var missing in ImportRowPipeline.MissingHeaders(type, mapping, sheet.Headers))
            {
                errors.Add(new ImportErrorView(1, missing, "errors.import.header_missing", "validate"));
            }

            if (errors.Count > 0)
            {
                return new ImportTestResult(0, 0, errors, []);
            }

            var db = services.GetRequiredService<KernelDbContext>();
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pipeline = new ImportRowPipeline(type, mapping, template.Mode, sheet.Headers, ImportWriterFor(type), db, services.GetRequiredService<ICallerAccess>());
                foreach (var (rowNo, cells) in sheet.Rows())
                {
                    total++;
                    if (total >= MaxTestRead)
                    {
                        break; // the sample is read to a fixed number of rows, whatever the file holds
                    }

                    if (total > MaxTestRows)
                    {
                        continue;
                    }

                    var result = await pipeline.ProcessAsync(rowNo, cells, dryRun: true, mapOnly: false, cancellationToken).ConfigureAwait(false);
                    if (sample.Count < 5)
                    {
                        sample.Add(new ImportSampleRow(rowNo, result.Values));
                    }

                    if (!result.Ok)
                    {
                        bad++;
                        errors.AddRange(result.Errors.Take(3).Select(e => new ImportErrorView(rowNo, e.Field, e.Message, "validate")));
                    }
                }
            }
            finally
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ImportFileException e)
        {
            throw new ImportException(e.Message, FileStatus(e.Message));
        }

        return new ImportTestResult(total, bad, [.. errors.Take(100)], sample);
    }

    // ------------------------------------------------------------------ batches

    public async Task<ImportBatchView> UploadAsync(Guid templateId, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        await RequireAsync(ImportPermissions.Manage, cancellationToken).ConfigureAwait(false);
        ImportTemplateView template;
        await using (var c0 = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            template = (await OpsAlerts.ReadAsync(c0, TemplateSelect + " where t.id = $1 and t.is_active", ReadTemplate, cancellationToken, templateId).ConfigureAwait(false)).SingleOrDefault() ?? throw new ImportException("errors.import.not_found", 404);
        }

        var type = catalog.Find(template.EntityType) ?? throw new ImportException("errors.import.type_unknown", 400);
        await RequireTypeAsync(type, cancellationToken).ConfigureAwait(false);
        var mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(template.Mapping));
        var now = clock.UtcNow;
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var company = await DefaultCompanyAsync(c, cancellationToken).ConfigureAwait(false);
        var batchId = Guid.CreateVersion7(now);
        Guid fileId;
        string format;
        try
        {
            (fileId, format) = await files.StoreAsync(content, fileName, company, batchId, cancellationToken).ConfigureAwait(false);
        }
        catch (ImportRefusedException e)
        {
            throw new ImportException(e.Message, e.Status);
        }

        try
        {
            // The upload only looks at the headers and the package (finding 2): the rows are counted by the check run, never in this request.
            var limits = await files.LimitsAsync(company, cancellationToken).ConfigureAwait(false);
            using var sheet = ImportFileReader.Open(content, format, mapping, limits);
            var missing = ImportRowPipeline.MissingHeaders(type, mapping, sheet.Headers);
            if (missing.Count > 0)
            {
                throw new ImportException("errors.import.header_missing", 400, string.Join(", ", missing));
            }
        }
        catch (Exception e) when (e is ImportException or ImportFileException)
        {
            await DiscardFileAsync(c, fileId, cancellationToken).ConfigureAwait(false);
            throw e as ImportException ?? new ImportException(e.Message, FileStatus(e.Message));
        }

        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into itg.import_batches (id, company_id, template_id, file_id, total_rows, status, file_name, mode, mapping, template_version, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, null, 'uploaded', $5, $6, $7, $8, $9, $10, $9, $10, 1)
            """, cancellationToken, batchId, company, templateId, fileId, fileName.Trim(), template.Mode, new JsonPayload(template.Mapping?.ToJsonString() ?? "{}"), template.Version, now, actor.UserId).ConfigureAwait(false);
        await LogBatchAsync(c, tx, batchId, company, "create", [("template_id", null, templateId), ("file_name", null, fileName.Trim()), ("template_version", null, template.Version), ("status", null, "uploaded")], now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportBatchView> ValidateAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await StartableAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch.Status != "uploaded")
        {
            throw new ImportException("errors.import.wrong_state", 409);
        }

        await QueueAsync(batchId, ImportJobCodes.Validate, "validating", "uploaded", cancellationToken).ConfigureAwait(false);
        return await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportBatchView> CommitAsync(Guid batchId, bool skipErrors, CancellationToken cancellationToken)
    {
        var batch = await StartableAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch.Status != "validated")
        {
            throw new ImportException("errors.import.wrong_state", 409);
        }

        if ((batch.OkRows ?? 0) == 0)
        {
            throw new ImportException("errors.import.nothing_to_commit", 409);
        }

        if ((batch.ErrorRows ?? 0) > 0 && !skipErrors)
        {
            throw new ImportException("errors.import.has_errors", 409);
        }

        await QueueAsync(batchId, ImportJobCodes.Commit, "committing", "validated", cancellationToken).ConfigureAwait(false);
        return await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportBatchView> RollbackAsync(Guid batchId, string? reason, CancellationToken cancellationToken)
    {
        var batch = await StartableAsync(batchId, cancellationToken).ConfigureAwait(false);
        var type = catalog.Find(batch.EntityType) ?? throw new ImportException("errors.import.type_unknown", 400);
        var now = clock.UtcNow;
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var locked = await IdentityDb.ScalarAsync<string?>(c, tx, "select status from itg.import_batches where id = $1 for update", cancellationToken, batchId).ConfigureAwait(false);
        if (locked is not ("committed" or "failed"))
        {
            throw new ImportException("errors.import.rollback_not_allowed", 409);
        }

        // The check locks the records (for update): a change made between the check and the delete waits for this transaction (finding 15).
        var blocked = await RollbackBlockedAsync(c, tx, type, batch, now, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            throw new ImportException(blocked, 409);
        }

        var rows = await OpsAlerts.ReadAsync(c, "select table_name, record_id from itg.import_batch_rows where batch_id = $1 and action = 'created' order by row_no desc, created_at desc", r => (Table: r.GetString(0), Id: r.GetGuid(1)), cancellationToken, batchId).ConfigureAwait(false);
        var shapes = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            foreach (var (table, id) in rows)
            {
                if (!shapes.TryGetValue(table, out var soft))
                {
                    shapes[table] = soft = await HasColumnAsync(c, table, "is_deleted", cancellationToken).ConfigureAwait(false);
                }

                var company = await TryCompanyAsync(c, tx, table, id, cancellationToken).ConfigureAwait(false);
                await IdentityDb.ExecuteAsync(c, tx, soft
                    ? $"update {Safe(table)} set is_deleted = true, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1"
                    : $"delete from {Safe(table)} where id = $1", cancellationToken, [.. soft ? new object?[] { id, now, actor.UserId } : [id]]).ConfigureAwait(false);
                await AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, actor.UserId, audit.ActorKind, table, id, company, "delete",
                    new JsonArray(new JsonObject { ["field"] = "import_batch", ["after"] = batchId.ToString() }), null, batchId, audit.IpAddress, AuditSources.Import, actor.UserId)], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // A reference the database knows (and the check above did not foresee) is a refusal, never a 500 (finding 5).
            throw new ImportException("errors.import.rollback_in_use", 409);
        }

        await IdentityDb.ExecuteAsync(c, tx, "update itg.import_batches set status = 'rolled_back', rolled_back_at = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", cancellationToken, batchId, now, actor.UserId).ConfigureAwait(false);
        await LogBatchAsync(c, tx, batchId, batch.CompanyId, "status", [("status", locked, "rolled_back"), ("reason", null, reason?.Trim())], now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportBatchView> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await RequireAsync(ImportPermissions.Manage, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var batch = (await OpsAlerts.ReadAsync(c, BatchSelect + " where b.id = $1", ReadBatch, cancellationToken, batchId).ConfigureAwait(false)).SingleOrDefault();
        if (batch is null || !await MayReadAsync(batch, cancellationToken).ConfigureAwait(false))
        {
            throw new ImportException("errors.import.not_found", 404);
        }

        return await ViewAsync(c, batch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportPage<ImportBatchView>> ListAsync(string? type, string? status, string? cursor, int limit, CancellationToken cancellationToken)
    {
        await RequireAsync(ImportPermissions.Manage, cancellationToken).ConfigureAwait(false);
        var page = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        // Other people's batches are listed only for the types the person may read themselves, and only in the companies the read right covers (finding 6).
        var readable = new List<string>();
        if (await permissions.HasPermissionAsync("itg.import_batches.read", cancellationToken).ConfigureAwait(false))
        {
            foreach (var t in catalog.All)
            {
                if (await permissions.HasPermissionAsync(t.ReadRight, cancellationToken).ConfigureAwait(false))
                {
                    readable.Add(t.Code);
                }
            }
        }
        (DateTimeOffset At, Guid Id)? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = cursor.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParse(parts[1], out var cid))
            {
                throw new ImportException("errors.data.invalid_request", 400);
            }

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), cid);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await OpsAlerts.ReadAsync(c, BatchSelect + """
             where ($1::text is null or t.entity_type = $1) and ($2::text is null or b.status = $2) and (b.created_by = $4 or t.entity_type = any($3))
               and ($5::timestamptz is null or (b.created_at, b.id) < ($5, $6))
             order by b.created_at desc, b.id desc limit $7
            """, ReadBatch, cancellationToken, type, status, readable.ToArray(), actor.UserId, after?.At, after?.Id, page + 1).ConfigureAwait(false);
        var items = new List<ImportBatchView>();
        foreach (var row in rows.Take(page))
        {
            if (await MayReadAsync(row, cancellationToken).ConfigureAwait(false))
            {
                items.Add(await ViewAsync(c, row, cancellationToken).ConfigureAwait(false));
            }
        }

        var next = rows.Count > page ? rows[page - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + rows[page - 1].Id : null;
        return new ImportPage<ImportBatchView>(items, next);
    }

    public async Task<ImportPage<ImportErrorView>> ErrorsAsync(Guid batchId, int limit, string? cursor, CancellationToken cancellationToken)
    {
        await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
        var page = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
        // The cursor is the last problem's place in the list: row, time and id (a row with several problems may be split across pages, finding 12).
        var afterRow = 0;
        var afterAt = DateTimeOffset.MinValue;
        var afterId = Guid.Empty;
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = cursor.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out afterRow) || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParse(parts[2], out afterId) || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                throw new ImportException("errors.data.invalid_request", 400);
            }

            afterAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await OpsAlerts.ReadAsync(c, """
            select row_no, field, message, phase, created_at, id from itg.import_batch_errors where batch_id = $1 and (row_no, created_at, id) > ($2, $3, $4) order by row_no, created_at, id limit $5
            """, r => (View: new ImportErrorView(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3)), At: r.GetFieldValue<DateTimeOffset>(4), Id: r.GetGuid(5)),
            cancellationToken, batchId, afterRow, afterAt, afterId, page + 1).ConfigureAwait(false);
        var last = rows.Count > page ? rows[page - 1] : default;
        return new ImportPage<ImportErrorView>(rows.Take(page).Select(r => r.View).ToList(),
            rows.Count > page ? string.Create(CultureInfo.InvariantCulture, $"{last.View.RowNo}|{last.At.UtcTicks}|{last.Id}") : null);
    }

    public async Task<ImportFileContent> ErrorFileAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (fileId, batchMapping) = (await OpsAlerts.ReadAsync(c, "select file_id, mapping::text from itg.import_batches where id = $1", r => (File: r.GetGuid(0), Mapping: r.IsDBNull(1) ? null : r.GetString(1)), cancellationToken, batchId).ConfigureAwait(false)).Single();
        var template = (await OpsAlerts.ReadAsync(c, TemplateSelect + " where t.id = $1", ReadTemplate, cancellationToken, batch.TemplateId).ConfigureAwait(false)).Single();
        // The batch's own copy of the mapping, as the check and the commit used it (finding 4).
        var mapping = TemplateMapping.Parse(batchMapping is null ? JsonSerializer.SerializeToElement(template.Mapping) : JsonDocument.Parse(batchMapping).RootElement);
        var language = await IdentityDb.ScalarAsync<string?>(c, null, "select language from iam.users where id = $1", cancellationToken, actor.UserId).ConfigureAwait(false) ?? "en";
        var errors = (await OpsAlerts.ReadAsync(c, "select row_no, coalesce(field, ''), message from itg.import_batch_errors where batch_id = $1 order by row_no, created_at, id", r => (Row: r.GetInt32(0), Field: r.GetString(1), Message: r.GetString(2)), cancellationToken, batchId).ConfigureAwait(false))
            .GroupBy(e => e.Row).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(e => (e.Field.Length > 0 ? e.Field + ": " : string.Empty) + ImportTexts.Text(e.Message, language))));
        byte[] content;
        ImportLimits limits;
        try
        {
            content = await files.ReadAsync(fileId, batchId, cancellationToken).ConfigureAwait(false);
            limits = await files.LimitsAsync(await IdentityDb.ScalarAsync<Guid>(c, null, "select company_id from itg.import_batches where id = $1", cancellationToken, batchId).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (ImportRefusedException e)
        {
            throw new ImportException(e.Message, e.Status);
        }

        var format = ImportFileReader.Detect(content, batch.FileName) ?? template.Format;
        var text = new StringBuilder();
        using var sheet = ImportFileReader.Open(content, format, mapping, limits);
        text.Append(string.Join(',', sheet.Headers.Select(h => CsvCell(h, ',')))).Append(",message\r\n");
        foreach (var (rowNo, cells) in sheet.Rows())
        {
            if (errors.TryGetValue(rowNo, out var message))
            {
                text.Append(string.Join(',', cells.Select(x => CsvCell(x ?? string.Empty, ',')))).Append(',').Append(CsvCell(message, ',')).Append("\r\n");
            }
        }

        return new ImportFileContent("errors-" + batch.FileName.Replace('.', '-') + ".csv", "text/csv; charset=utf-8", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray());
    }

    public async Task<long> UploadLimitAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ImportPermissions.Manage, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await files.MaxBytesAsync(await DefaultCompanyAsync(c, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The HTTP status of a refusal the file reader raised: a file that is too big or has too many rows is 413, any other unreadable file 400.</summary>
    private static int FileStatus(string key) => key is "errors.import.too_large" or "errors.import.too_many_rows" ? 413 : 400;

    internal static string CsvCell(string value, char delimiter)
    {
        // A cell that a spreadsheet program would run as a formula is written as text (a leading apostrophe; tab and carriage return too,
        // finding 11). A plain number such as -5 or +923001234567 is left alone, so a corrected file loads back as it was.
        var risky = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsPlainNumber(value);
        var safe = risky ? "'" + value : value;
        return safe.Contains(delimiter, StringComparison.Ordinal) || safe.Contains('"', StringComparison.Ordinal) || safe.Contains('\n', StringComparison.Ordinal) || safe.Contains('\r', StringComparison.Ordinal)
            ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    private static bool IsPlainNumber(string value) =>
        value.Length > 1 && value[0] is '+' or '-' && value.Skip(1).All(ch => ch is >= '0' and <= '9' or '.') && value.Skip(1).Any(char.IsAsciiDigit) && value.Count(ch => ch == '.') <= 1;

    private IImportRowWriter ImportWriterFor(ImportTypeDefinition type) => ImportWriters.Generated(services, type.Writer ?? type.Table);

    private async Task<BatchRead> StartableAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await RequireAsync(ImportPermissions.Manage, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var batch = (await OpsAlerts.ReadAsync(c, BatchSelect + " where b.id = $1", ReadBatch, cancellationToken, batchId).ConfigureAwait(false)).SingleOrDefault();
        if (batch is null || !await MayReadAsync(batch, cancellationToken).ConfigureAwait(false))
        {
            throw new ImportException("errors.import.not_found", 404);
        }

        await RequireTypeAsync(catalog.Find(batch.EntityType) ?? throw new ImportException("errors.import.type_unknown", 400), cancellationToken).ConfigureAwait(false);
        return batch;
    }

    /// <summary>
    /// The person who started a batch reads it. Anyone else needs the right to read import batches, the right to read the records of the type
    /// (the problem file holds the original rows), and that right over the batch's company (finding 6).
    /// </summary>
    private async Task<bool> MayReadAsync(BatchRead batch, CancellationToken ct)
    {
        if (batch.CreatedBy == actor.UserId)
        {
            return true;
        }

        return catalog.Find(batch.EntityType) is { } type
            && await permissions.HasPermissionAsync("itg.import_batches.read", ct).ConfigureAwait(false)
            && await permissions.HasPermissionAsync(type.ReadRight, ct).ConfigureAwait(false)
            && await access.CoversCompanyAsync("itg.import_batches.read", batch.CompanyId, ct).ConfigureAwait(false);
    }

    private async Task<Guid> DefaultCompanyAsync(NpgsqlConnection c, CancellationToken ct) =>
        await IdentityDb.ScalarAsync<Guid?>(c, null, """
            select coalesce((select default_company_id from iam.users where id = $1), (select id from org.companies where not is_deleted order by created_at limit 1))
            """, ct, actor.UserId).ConfigureAwait(false) ?? throw new ImportException("errors.import.not_found", 404);

    private async Task RequireTypeAsync(ImportTypeDefinition type, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(type.Permission, ct).ConfigureAwait(false))
        {
            throw new ImportException("errors.import.type_forbidden", 403);
        }
    }

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new ImportException("errors.access.forbidden", 403);
        }
    }

    private async Task RequireAnyAsync(string[] any, CancellationToken ct)
    {
        foreach (var p in any)
        {
            if (await permissions.HasPermissionAsync(p, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ImportException("errors.access.forbidden", 403);
    }

    private Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => IdentityDb.OpenAsync(tenant.DataSource, ct);

    private async Task QueueAsync(Guid batchId, string jobCode, string toStatus, string fromStatus, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var runId = Guid.CreateVersion7(now);
        var inserted = await IdentityDb.ExecuteAsync(c, tx, """
            insert into job.runs (id, definition_id, company_id, status, priority, attempt, parameters, requested_by, actor_user_id, created_at, created_by, updated_at, updated_by, row_version)
            select $1, d.id, null, 'queued', d.priority, 0, $2, $3, $3, $4, $3, $4, $3, 1 from job.definitions d where d.code = $5
            """, ct, runId, new JsonPayload(new JsonObject { ["batchId"] = batchId.ToString() }.ToJsonString()), actor.UserId, now, jobCode).ConfigureAwait(false);
        if (inserted == 0)
        {
            throw new ImportException("errors.job.not_found", 500);
        }

        var queued = await IdentityDb.ExecuteAsync(c, tx, """
            update itg.import_batches set status = $2, job_run_id = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1 and status = $6
            """, ct, batchId, toStatus, runId, now, actor.UserId, fromStatus).ConfigureAwait(false);
        if (queued == 0)
        {
            throw new ImportException("errors.import.wrong_state", 409);
        }

        await JobStore.LogAsync(c, tx, runId, "info", "queued by " + actor.UserId.ToString(), now, ct).ConfigureAwait(false);
        await JobStore.RaiseAsync(c, tx, JobEvents.RunRequested, "job.run", runId, now, new { run_id = runId, code = jobCode, company_id = (Guid?)null, by = actor.UserId }, ct).ConfigureAwait(false);
        var company = await IdentityDb.ScalarAsync<Guid>(c, tx, "select company_id from itg.import_batches where id = $1", ct, batchId).ConfigureAwait(false);
        await LogBatchAsync(c, tx, batchId, company, "status", [("status", fromStatus, toStatus)], now, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The key of the reason a batch cannot be rolled back, or null when it can (Q5, review findings 1, 5, 8, 15). A committed batch, or a
    /// failed one that wrote only created records, may be rolled back within <c>itg.import.rollback_days</c> while none of its records changed,
    /// nothing live refers to them, the person may delete what the type wrote, and every record is in a table the type declares. With a
    /// transaction the records are locked (<c>for update</c>) until it ends.
    /// </summary>
    internal async Task<string?> RollbackBlockedAsync(NpgsqlConnection c, NpgsqlTransaction? tx, ImportTypeDefinition type, BatchRead batch, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await OpsAlerts.ReadAsync(c, "select table_name, record_id, record_version, action from itg.import_batch_rows where batch_id = $1", r => (Table: r.GetString(0), Id: r.GetGuid(1), Version: r.IsDBNull(2) ? (int?)null : r.GetInt32(2), Action: r.GetString(3)), ct, batch.Id).ConfigureAwait(false);
        if (rows.Any(r => r.Action == "updated") || (batch.UpdatedRows ?? 0) > 0)
        {
            return "errors.import.rollback_has_updates";
        }

        var created = rows.Where(r => r.Action == "created").ToList();
        if (created.Count == 0 || created.Any(r => !type.WrittenTables.Contains(r.Table, StringComparer.Ordinal)))
        {
            return "errors.import.rollback_not_allowed";
        }

        var days = await RollbackDaysAsync(c, now, ct).ConfigureAwait(false);
        var since = batch.CommittedAt ?? batch.UpdatedAt;
        if (now > since.AddDays(days))
        {
            return "errors.import.rollback_expired";
        }

        // The person needs the right to delete what the type wrote, over the company of every record (the check is the table's delete rule, finding 5).
        if (!await permissions.HasPermissionAsync(type.RollbackRight, ct).ConfigureAwait(false))
        {
            return "errors.import.rollback_forbidden";
        }

        var shapes = new Dictionary<string, (bool Company, bool Soft)>(StringComparer.Ordinal);
        foreach (var group in created.GroupBy(r => r.Table))
        {
            if (!shapes.TryGetValue(group.Key, out var shape))
            {
                shapes[group.Key] = shape = (await HasColumnAsync(c, group.Key, "company_id", ct).ConfigureAwait(false), await HasColumnAsync(c, group.Key, "is_deleted", ct).ConfigureAwait(false));
            }

            var current = await OpsAlerts.ReadAsync(c, $"select id, row_version{(shape.Company ? ", company_id" : ", null::uuid")} from {Safe(group.Key)} where id = any($1)" + (shape.Soft ? " and not is_deleted" : string.Empty) + (tx is not null ? " for update" : string.Empty),
                r => (Id: r.GetGuid(0), Version: r.GetInt64(1), Company: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2)), ct, group.Select(g => g.Id).ToArray()).ConfigureAwait(false);
            var map = current.ToDictionary(x => x.Id);
            foreach (var row in group)
            {
                if (!map.TryGetValue(row.Id, out var found) || (row.Version is { } recorded && found.Version != recorded))
                {
                    return "errors.import.rollback_changed";
                }
            }

            foreach (var company in current.Select(x => x.Company).Where(x => x is not null).Distinct())
            {
                if (!await access.CoversCompanyAsync(type.RollbackRight, company, ct).ConfigureAwait(false))
                {
                    return "errors.import.rollback_forbidden";
                }
            }
        }

        if (await ReferencedAsync(c, created.Select(r => (r.Table, r.Id)).ToList(), type.RollbackDependents ?? [], ct).ConfigureAwait(false))
        {
            return "errors.import.rollback_in_use";
        }

        var records = created.Select(r => new ImportCreatedRecord(r.Table, r.Id)).ToList();
        foreach (var guard in services.GetServices<IImportRollbackGuard>())
        {
            if (await guard.BlockedAsync(records, ct).ConfigureAwait(false) is { } key)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// True when a live record outside the batch's own records refers (by a foreign key of the database) to one of the created records:
    /// an organization unit that a later unit sits under, a calendar a holiday belongs to. References from other modules are the guards' business.
    /// </summary>
    private static async Task<bool> ReferencedAsync(NpgsqlConnection c, IReadOnlyList<(string Table, Guid Id)> created, IReadOnlyList<string> dependents, CancellationToken ct)
    {
        var own = created.GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray(), StringComparer.Ordinal);
        foreach (var (table, ids) in own)
        {
            var refs = await OpsAlerts.ReadAsync(c, """
                select ns.nspname || '.' || cl.relname, a.attname
                from pg_constraint k
                join pg_class cl on cl.oid = k.conrelid
                join pg_namespace ns on ns.oid = cl.relnamespace
                join pg_class target on target.oid = k.confrelid
                join pg_namespace tn on tn.oid = target.relnamespace
                join pg_attribute a on a.attrelid = k.conrelid and a.attnum = k.conkey[1]
                where k.contype = 'f' and k.conparentid = 0 and array_length(k.conkey, 1) = 1 and tn.nspname = $1 and target.relname = $2
                """, r => (Table: r.GetString(0), Column: r.GetString(1)), ct, SchemaOf(table), NameOf(table)).ConfigureAwait(false);
            foreach (var (refTable, column) in refs)
            {
                if (dependents.Contains(refTable, StringComparer.Ordinal))
                {
                    continue; // what the import itself makes along with the record (an invitation's token and message) goes away with it
                }

                var hasId = await HasColumnAsync(c, refTable, "id", ct).ConfigureAwait(false);
                var soft = await HasColumnAsync(c, refTable, "is_deleted", ct).ConfigureAwait(false);
                var ownIds = own.TryGetValue(refTable, out var mine) && hasId ? mine : [];
                var sql = $"select exists (select 1 from {Safe(refTable)} r where r.{Safe("x." + column).Split('.')[1]} = any($1)" + (hasId ? " and not (r.id = any($2))" : string.Empty) + (soft ? " and not r.is_deleted" : string.Empty) + ")";
                var used = hasId
                    ? await IdentityDb.ScalarAsync<bool>(c, null, sql, ct, ids, ownIds).ConfigureAwait(false)
                    : await IdentityDb.ScalarAsync<bool>(c, null, sql, ct, ids).ConfigureAwait(false);
                if (used)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<bool> HasColumnAsync(NpgsqlConnection c, string table, string column, CancellationToken ct) =>
        await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from information_schema.columns where table_schema = $1 and table_name = $2 and column_name = $3)", ct, SchemaOf(table), NameOf(table), column).ConfigureAwait(false);

    private async Task<ImportBatchView> ViewAsync(NpgsqlConnection c, BatchRead b, CancellationToken ct)
    {
        string? blocked = null;
        var canRollback = false;
        if (b.Status is "committed" or "failed" && catalog.Find(b.EntityType) is { } type)
        {
            blocked = await RollbackBlockedAsync(c, null, type, b, clock.UtcNow, ct).ConfigureAwait(false);
            if (b.Status == "failed" && blocked == "errors.import.rollback_not_allowed")
            {
                blocked = null; // a failed check wrote nothing, so there is nothing to roll back and nothing to explain
            }
            else
            {
                canRollback = blocked is null;
            }
        }

        return new ImportBatchView(b.Id, b.TemplateId, b.TemplateName, b.EntityType, b.FileName, b.Status, b.Mode, b.TotalRows, b.OkRows, b.ErrorRows, b.CreatedRows, b.UpdatedRows, b.Progress, b.JobRunId,
            b.CreatedAt, b.CreatedBy, b.ValidatedAt, b.CommittedAt, b.RolledBackAt, b.Status == "validated" && (b.OkRows ?? 0) > 0, canRollback, blocked);
    }

    private static async Task<int> RollbackDaysAsync(NpgsqlConnection c, DateTimeOffset now, CancellationToken ct)
    {
        var values = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.ItgImportRollbackDays], null, null, DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false);
        return values.TryGetValue(KernelSettingKeys.ItgImportRollbackDays, out var v) && v.Value.TryGetInt32(out var n) && n > 0 ? n : 7;
    }

    internal static async Task<int> MaxRowsAsync(NpgsqlConnection c, Guid company, DateTimeOffset now, CancellationToken ct)
    {
        var values = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.ItgImportMaxRows], company, null, DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false);
        return values.TryGetValue(KernelSettingKeys.ItgImportMaxRows, out var v) && v.Value.TryGetInt32(out var n) && n > 0 ? Math.Min(n, 50_000) : 50_000;
    }

    private async Task DiscardFileAsync(NpgsqlConnection c, Guid fileId, CancellationToken ct) =>
        await IdentityDb.ExecuteAsync(c, null, "update doc.files set status = 'deleted', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1", ct, fileId, clock.UtcNow, actor.UserId).ConfigureAwait(false);

    private static (string Format, string Mode, string Name, string Mapping) Checked(ImportTemplateWrite request, ImportTypeDefinition type, ImportTemplateView? current)
    {
        var format = (request.Format ?? current?.Format ?? "csv").Trim().ToLowerInvariant();
        var mode = (request.Mode ?? current?.Mode ?? "create").Trim().ToLowerInvariant();
        if (format is not ("csv" or "xlsx") || mode is not ("create" or "create_or_update") || (mode == "create_or_update" && !type.CanUpdate))
        {
            throw new ImportException("errors.import.template_invalid", 400, mode == "create_or_update" && !type.CanUpdate ? "mode" : "format");
        }

        var names = request.Name ?? (current?.Name.ToDictionary(p => p.Key, p => p.Value));
        if (names is null || !names.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en) || names.Values.Any(v => v.Length > 200))
        {
            throw new ImportException("errors.import.template_invalid", 400, "name");
        }

        var node = request.Mapping ?? current?.Mapping ?? throw new ImportException("errors.import.template_invalid", 400, "mapping");
        TemplateMapping mapping;
        try
        {
            mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(node));
        }
        catch (InvalidDataException)
        {
            throw new ImportException("errors.import.template_invalid", 400, "mapping");
        }

        var problems = mapping.Check(type);
        if (problems.Count > 0)
        {
            throw new ImportException("errors.import.template_invalid", 400, string.Join("; ", problems.Take(5)));
        }

        return (format, mode, JsonSerializer.Serialize(names), node.ToJsonString());
    }

    private Task<int> LogTemplateAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes, DateTimeOffset now, CancellationToken ct) =>
        LogAsync(c, tx, "itg.import_templates", id, null, action, changes, now, ct);

    private Task<int> LogBatchAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid company, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes, DateTimeOffset now, CancellationToken ct) =>
        LogAsync(c, tx, "itg.import_batches", id, company, action, changes, now, ct);

    private Task<int> LogAsync(NpgsqlConnection c, NpgsqlTransaction tx, string entity, Guid id, Guid? company, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes, DateTimeOffset now, CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var (field, before, after) in changes)
        {
            var node = new JsonObject { ["field"] = field };
            if (before is not null)
            {
                node["before"] = JsonSerializer.SerializeToNode(before);
            }

            node["after"] = after is null ? null : JsonSerializer.SerializeToNode(after);
            array.Add(node);
        }

        return AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, actor.UserId, audit.ActorKind, entity, id, company, action, array, null, audit.CorrelationId, audit.IpAddress, audit.Source, actor.UserId)], ct);
    }

    private static async Task<Guid?> TryCompanyAsync(NpgsqlConnection c, NpgsqlTransaction tx, string table, Guid id, CancellationToken ct) =>
        await HasColumnAsync(c, table, "company_id", ct).ConfigureAwait(false) ? await IdentityDb.ScalarAsync<Guid?>(c, tx, $"select company_id from {Safe(table)} where id = $1", ct, id).ConfigureAwait(false) : null;

    internal static string Safe(string table) => table.Split('.') is [var a, var b] && a.All(ch => ch is >= 'a' and <= 'z' or '_') && b.All(ch => ch is >= 'a' and <= 'z' or '_') ? table : throw new InvalidDataException("Not a table name.");

    private static string SchemaOf(string table) => Safe(table).Split('.')[0];

    private static string NameOf(string table) => Safe(table).Split('.')[1];

    private const string TemplateSelect = "select t.id, t.code, t.name_i18n::text, t.entity_type, t.format, t.mode, t.is_default, t.version, t.is_active, t.row_version, t.mapping::text from itg.import_templates t";

    private static ImportTemplateView ReadTemplate(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [], r.GetString(3), r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetInt32(7), r.GetBoolean(8), r.GetInt64(9), JsonNode.Parse(r.GetString(10)));

    internal sealed record BatchRead(Guid Id, Guid TemplateId, IReadOnlyDictionary<string, string> TemplateName, string EntityType, string FileName, string Status, string Mode, int? TotalRows, int? OkRows, int? ErrorRows, int? CreatedRows,
        int? UpdatedRows, decimal? Progress, Guid? JobRunId, DateTimeOffset CreatedAt, Guid CreatedBy, DateTimeOffset? ValidatedAt, DateTimeOffset? CommittedAt, DateTimeOffset? RolledBackAt, Guid CompanyId, DateTimeOffset UpdatedAt);

    private static BatchRead ReadBatch(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [], r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        Int(r, 7), Int(r, 8), Int(r, 9), Int(r, 10), Int(r, 11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetGuid(13), r.GetFieldValue<DateTimeOffset>(14), r.GetGuid(15),
        Time(r, 16), Time(r, 17), Time(r, 18), r.GetGuid(19), r.GetFieldValue<DateTimeOffset>(20));

    private static int? Int(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static DateTimeOffset? Time(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

}

/// <summary>The texts of the import error keys in a language, for the files the centre writes (the error file is read outside the screen, finding 13).</summary>
internal static class ImportTexts
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Catalogues = new(() =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal) { ["en"] = Load("en"), ["ur"] = Load("ur") });

    /// <summary>The text of a key in the language (English when the language or the key is unknown); a text the catalogue lacks stays as it is.</summary>
    public static string Text(string key, string? language)
    {
        var lang = language is { Length: >= 2 } l ? l[..2].ToLowerInvariant() : "en";
        if (Catalogues.Value.TryGetValue(lang, out var own) && own.TryGetValue(key, out var text))
        {
            return text;
        }

        return Catalogues.Value["en"].TryGetValue(key, out var english) ? english : key;
    }

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(ImportTexts).Assembly.GetManifestResourceStream($"Erp.Kernel.Import.{language}.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
