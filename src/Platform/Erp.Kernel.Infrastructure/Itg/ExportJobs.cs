using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Itg;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// The background run of one export (slice S1.16b, FR-ADM-DAT-04), with the rights of the person who asked: it reads the list page by page through the
/// list's own reader (permission, data scope and field security per row), refuses a list over <c>itg.export.max_rows</c>, writes the file with its
/// watermark line and neutralised formula cells, keeps it in the file store for <c>itg.export.keep_days</c> and records the sensitive fields shown
/// unmasked in the access log (FR-KRN-AUD-02). A refusal that a retry cannot change ends the export as failed with its reason; any other error is retried.
/// </summary>
public sealed class ExportRunJob : IJobHandler
{
    public string Code => ExportCodes.RunJob;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var exportId = job.Parameters.ValueKind == JsonValueKind.Object && job.Parameters.TryGetProperty("exportId", out var e) && e.TryGetGuid(out var parsed)
            ? parsed : throw new InvalidOperationException("The run needs the parameter exportId.");
        var sp = job.Services;
        var tenant = sp.GetRequiredService<ITenantConnectionProvider>();
        var clock = sp.GetRequiredService<IClock>();
        var actor = sp.GetRequiredService<ICurrentActor>();
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var row = (await OpsAlerts.ReadAsync(c, "select company_id, entity_type, filter::text, format, status, created_by from itg.export_jobs where id = $1",
            r => (Company: r.GetGuid(0), Source: r.GetString(1), Filter: r.GetString(2), Format: r.GetString(3), Status: r.GetString(4), By: r.GetGuid(5)), cancellationToken, exportId).ConfigureAwait(false)).SingleOrDefault();
        if (row.Source is null || row.Status is not ("queued" or "running"))
        {
            await job.LogAsync("warning", "The export is not waiting for a run; nothing done.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var started = clock.UtcNow;
        await IdentityDb.ExecuteAsync(c, null, "update itg.export_jobs set status = 'running', started_at = coalesce(started_at, $2), updated_at = $2, row_version = row_version + 1 where id = $1", cancellationToken, exportId, started).ConfigureAwait(false);
        try
        {
            await ProduceAsync(job, c, exportId, row.Company, row.Source, row.Filter, row.Format, row.By, cancellationToken).ConfigureAwait(false);
        }
        catch (ExportException ex)
        {
            await job.LogAsync("warning", "Export refused: " + ex.ErrorKey, cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNow;
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, "update itg.export_jobs set status = 'failed', error_key = $2, finished_at = $3, updated_at = $3, row_version = row_version + 1 where id = $1", cancellationToken, exportId, ex.ErrorKey, now).ConfigureAwait(false);
            await AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, actor.UserId, sp.GetRequiredService<IAuditContext>().ActorKind, ExportCodes.OwnerType, exportId, row.Company, "status",
                new JsonArray(new JsonObject { ["field"] = "status", ["before"] = "running", ["after"] = "failed" }), null, sp.GetRequiredService<IAuditContext>().CorrelationId, null, AuditSources.Job, actor.UserId)], cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ProduceAsync(IJobExecution job, NpgsqlConnection c, Guid exportId, Guid company, string source, string filterJson, string format, Guid by, CancellationToken ct)
    {
        var sp = job.Services;
        var clock = sp.GetRequiredService<IClock>();
        var settings = sp.GetRequiredService<ISettings>();
        var permissions = sp.GetRequiredService<IPermissionChecker>();
        var reader = sp.GetServices<IExportRowReader>().FirstOrDefault(r => string.Equals(r.Code, source, StringComparison.Ordinal)) ?? throw new ExportException("errors.export.source_unknown", 400);
        if (!await permissions.HasPermissionAsync(reader.ReadPermission, ct).ConfigureAwait(false) || !await permissions.HasPermissionAsync(ExportPermissions.Run, ct).ConfigureAwait(false))
        {
            throw new ExportException("errors.access.forbidden", 403);
        }

        var maxRows = (await settings.GetAsync(KernelSettingKeys.ItgExportMaxRows, company, ct).ConfigureAwait(false)).Value is { ValueKind: JsonValueKind.Number } m && m.TryGetInt32(out var mr) ? mr : 50000;
        var keepDays = (await settings.GetAsync(KernelSettingKeys.ItgExportKeepDays, company, ct).ConfigureAwait(false)).Value is { ValueKind: JsonValueKind.Number } k && k.TryGetInt32(out var kd) ? kd : 7;
        var definition = JsonNode.Parse(filterJson)!.AsObject();
        var language = definition["language"]?.GetValue<string>() ?? "en";
        var requested = (definition["columns"] as JsonArray ?? []).OfType<JsonObject>().Select(o => (Field: o["field"]!.GetValue<string>(), Label: o["label"]?.GetValue<string>() ?? o["field"]!.GetValue<string>())).ToList();
        using var queryDocument = JsonDocument.Parse((definition["query"] ?? new JsonObject()).ToJsonString());
        var query = new ExportQuery(queryDocument.RootElement.Clone(), definition["search"]?.GetValue<string>(), definition["sort"]?.GetValue<string>());

        // The columns again, with the access the person has now (it may have changed since the request): hidden ones are left out.
        var known = (await reader.ColumnsAsync(company, ct).ConfigureAwait(false)).ToDictionary(x => x.Column.JsonName, x => x.Access, StringComparer.Ordinal);
        var columns = requested.Where(r => known.TryGetValue(r.Field.StartsWith("custom.", StringComparison.Ordinal) ? "custom" : r.Field, out var access) && access != "hidden").ToList();
        if (columns.Count == 0)
        {
            throw new ExportException("errors.export.no_columns", 400);
        }

        var rows = new List<ExportCell[]>();
        var present = new bool[columns.Count];
        var shown = new SortedSet<string>(StringComparer.Ordinal);
        Guid? after = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await reader.ReadPageAsync(query, after, ExportService.PageRows, ct).ConfigureAwait(false);
            foreach (var key in page.SensitiveShown)
            {
                shown.Add(key);
            }

            foreach (var item in page.Rows)
            {
                if (rows.Count >= maxRows)
                {
                    throw new ExportException("errors.export.too_many_rows", 400);
                }

                var cells = new ExportCell[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    var (node, has) = ValueOf(item, columns[i].Field);
                    present[i] |= has;
                    cells[i] = has ? ExportWriters.CellOf(node, language) : default;
                }

                rows.Add(cells);
            }

            after = page.Next;
        }
        while (after is not null);

        // A column hidden for every row the person's scope reached is left out, as in the list (field security per row).
        var keep = Enumerable.Range(0, columns.Count).Where(i => present[i] || rows.Count == 0).ToArray();
        if (keep.Length == 0)
        {
            throw new ExportException("errors.export.no_columns", 400);
        }

        var headers = keep.Select(i => columns[i].Label).ToList();
        var body = keep.Length == columns.Count ? rows : rows.Select(r => keep.Select(i => r[i]).ToArray()).ToList();
        var now = clock.UtcNow;
        var name = await c.QueryName(by, ct).ConfigureAwait(false);
        var watermark = ExportTexts.Text("export.watermark", language)
            .Replace("{name}", name, StringComparison.Ordinal)
            .Replace("{date}", now.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC", StringComparison.Ordinal)
            .Replace("{customer}", sp.GetRequiredService<ITenantContext>().TenantCode, StringComparison.Ordinal);
        var xlsx = string.Equals(format, "xlsx", StringComparison.Ordinal);
        var content = xlsx ? ExportWriters.Xlsx(watermark, headers, body) : ExportWriters.Csv(watermark, headers, body);
        var fileName = source.Replace('.', '-') + "-" + now.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + (xlsx ? ".xlsx" : ".csv");
        var expires = now.AddDays(keepDays);
        var files = sp.GetRequiredService<ExportFiles>();
        var fileId = await files.StoreAsync(content, fileName, xlsx ? ExportWriters.XlsxType : ExportWriters.CsvType, company, exportId, expires, ct).ConfigureAwait(false);

        // The access log first (FR-KRN-AUD-02): one row naming the sensitive fields that left unmasked; then the export is done.
        if (shown.Count > 0)
        {
            await sp.GetRequiredService<ISensitiveAccessLog>().RecordAsync(source, null, [.. shown], "export", rows.Count, ct).ConfigureAwait(false);
        }

        var audit = sp.GetRequiredService<IAuditContext>();
        var actor = sp.GetRequiredService<ICurrentActor>();
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, """
            update itg.export_jobs set status = 'done', file_id = $2, file_name = $3, row_count = $4, size_bytes = $5, expires_at = $6, finished_at = $7, error_key = null,
                updated_at = $7, row_version = row_version + 1 where id = $1
            """, ct, exportId, fileId, fileName, rows.Count, content.Length, expires, now).ConfigureAwait(false);
        await AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, actor.UserId, audit.ActorKind, ExportCodes.OwnerType, exportId, company, "status",
            new JsonArray(new JsonObject { ["field"] = "status", ["before"] = "running", ["after"] = "done" }, new JsonObject { ["field"] = "row_count", ["after"] = rows.Count }), null, audit.CorrelationId, null, AuditSources.Job, actor.UserId)], ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await job.LogAsync("info", $"Exported {rows.Count} rows of {source} as {fileName}.", ct).ConfigureAwait(false);
    }

    /// <summary>The value of a column in a shaped row, and whether the row has the column at all (a hidden field is not there).</summary>
    private static (JsonNode? Value, bool Has) ValueOf(JsonObject row, string field)
    {
        if (field.StartsWith("custom.", StringComparison.Ordinal))
        {
            return row["custom"] is JsonObject custom ? (custom[field["custom.".Length..]], true) : (null, false);
        }

        return row.ContainsKey(field) ? (row[field], true) : (null, false);
    }
}

internal static class ExportRunSupport
{
    /// <summary>The display name of the person who asked, for the watermark.</summary>
    public static async Task<string> QueryName(this NpgsqlConnection c, Guid userId, CancellationToken ct) =>
        await IdentityDb.ScalarAsync<string>(c, null, "select display_name from iam.users where id = $1", ct, userId).ConfigureAwait(false) ?? userId.ToString();
}

/// <summary>
/// Deletes the files of exports past their keeping date (slice S1.16b, <c>itg.export.keep_days</c>) and marks the export expired; an export asked for
/// but never approved is left as the record. Runs as the system.
/// </summary>
public sealed class ExportExpireJob : IJobHandler
{
    public string Code => ExportCodes.ExpireJob;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var sp = job.Services;
        var tenant = sp.GetRequiredService<ITenantConnectionProvider>();
        var clock = sp.GetRequiredService<IClock>();
        var files = sp.GetRequiredService<ExportFiles>();
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var due = await OpsAlerts.ReadAsync(c, "select id, file_id, company_id from itg.export_jobs where status = 'done' and expires_at <= $1 order by expires_at limit 500",
            r => (Id: r.GetGuid(0), File: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), Company: r.GetGuid(2)), cancellationToken, now).ConfigureAwait(false);
        var system = Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        var expired = 0;
        foreach (var item in due)
        {
            if (item.File is { } file)
            {
                await files.PurgeAsync(file, system, cancellationToken).ConfigureAwait(false);
            }

            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var changed = await IdentityDb.ExecuteAsync(c, tx, "update itg.export_jobs set status = 'expired', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and status = 'done'", cancellationToken, item.Id, now, system).ConfigureAwait(false);
            if (changed == 1)
            {
                await AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, system, ActorKinds.Job, ExportCodes.OwnerType, item.Id, item.Company, "status",
                    new JsonArray(new JsonObject { ["field"] = "status", ["before"] = "done", ["after"] = "expired" }), null, sp.GetRequiredService<IAuditContext>().CorrelationId, null, AuditSources.Job, system)], cancellationToken).ConfigureAwait(false);
                expired++;
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await job.LogAsync("info", $"{expired} export files past their keeping date were deleted.", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Approval of a held export (kind <see cref="ExportCodes.HeldKind"/>, FR-KRN-IAM-14): the approver needs <see cref="ExportPermissions.Approve"/> in the
/// export's company; the kernel refuses the requester (FR-XM-32). Approving queues the run, which still reads only what the requester may read.
/// </summary>
public sealed class ExportChangeHandler(IServiceProvider services) : IHeldChangeHandler
{
    public string Kind => ExportCodes.HeldKind;

    public string ApprovePermission => ExportPermissions.Approve;

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var (exportId, company) = await FindAsync(change, transaction, cancellationToken).ConfigureAwait(false);
        return exportId is not null && await services.GetRequiredService<ICallerAccess>().CoversCompanyAsync(ExportPermissions.Approve, company, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var (exportId, _) = await FindAsync(change, transaction, cancellationToken).ConfigureAwait(false);
        return exportId is null ? new HeldChangeProblem("errors.export.request_changed") : null;
    }

    public async Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (exportId, _) = await FindAsync(change, transaction, cancellationToken).ConfigureAwait(false);
        if (exportId is not { } id)
        {
            return new HeldChangeProblem("errors.export.request_changed");
        }

        var tx = (NpgsqlTransaction)transaction;
        var by = services.GetRequiredService<ICurrentActor>().UserId;
        var now = services.GetRequiredService<IClock>().UtcNow;
        await ExportQueue.QueueAsync(tx.Connection!, tx, id, change.RequestedBy, by, now, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tx = (NpgsqlTransaction)transaction;
        var (exportId, _) = await FindAsync(change, transaction, cancellationToken).ConfigureAwait(false);
        var details = new List<HeldChangeDetail>();
        if (exportId is { } id)
        {
            var row = (await OpsAlerts.ReadAsync(tx.Connection!, "select entity_type, format, filter::text, sensitive_classes, company_id from itg.export_jobs where id = $1",
                r => (Source: r.GetString(0), Format: r.GetString(1), Filter: r.GetString(2), Classes: r.IsDBNull(3) ? [] : (string[])r.GetValue(3), Company: r.GetGuid(4)), cancellationToken, id).ConfigureAwait(false)).Single();
            var columns = (JsonNode.Parse(row.Filter)?["columns"] as JsonArray ?? []).OfType<JsonObject>().Select(o => o["label"]?.GetValue<string>() ?? string.Empty).ToList();
            details.Add(new HeldChangeDetail("export.held.detail.list", "text", Text: row.Source));
            details.Add(new HeldChangeDetail("export.held.detail.format", "text", Text: row.Format));
            details.Add(new HeldChangeDetail("export.held.detail.columns", "items", Items: columns));
            if (row.Classes.Length > 0)
            {
                details.Add(new HeldChangeDetail("export.held.detail.classes", "items", Items: row.Classes));
            }

            details.Add(new HeldChangeDetail("export.held.detail.company", "text", Text: await IdentityDb.ScalarAsync<string>(tx.Connection!, tx, "select code from org.companies where id = $1", cancellationToken, row.Company).ConfigureAwait(false)));
            return new HeldChangeDescription(new Dictionary<string, string> { ["en"] = row.Source, ["ur"] = row.Source }, details);
        }

        return new HeldChangeDescription(null, details);
    }

    private static async Task<(Guid? ExportId, Guid Company)> FindAsync(HeldChange change, DbTransaction transaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tx = (NpgsqlTransaction)transaction;
        Guid? id = null;
        try
        {
            if (JsonNode.Parse(change.Payload)?["exportId"]?.GetValue<string>() is { } text && Guid.TryParse(text, out var parsed))
            {
                id = parsed;
            }
        }
        catch (JsonException)
        {
            return (null, Guid.Empty);
        }

        if (id is null)
        {
            return (null, Guid.Empty);
        }

        var row = (await OpsAlerts.ReadAsync(tx.Connection!, "select company_id from itg.export_jobs where id = $1 and status = 'awaiting_approval' and held_request_id = $2", r => r.GetGuid(0), ct, id.Value, change.RequestId).ConfigureAwait(false));
        return row.Count == 1 ? (id, row[0]) : (null, Guid.Empty);
    }
}

/// <summary>The texts of the export keys in a language, for the watermark line of the files (a file is read outside the screen).</summary>
internal static class ExportTexts
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Catalogues = new(() =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal) { ["en"] = Load("en"), ["ur"] = Load("ur") });

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
        using var stream = typeof(ExportTexts).Assembly.GetManifestResourceStream($"Erp.Kernel.Export.{language}.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
