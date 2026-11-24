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
using Npgsql;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>What an export asks for, resolved against the list and the caller's access: the reader, the columns the person may see and their classes.</summary>
internal sealed record ExportPlan(IExportRowReader Reader, IReadOnlyList<(ExportColumn Column, string Label)> Columns, IReadOnlyList<string> Hidden, IReadOnlyList<string> SensitiveClasses);

/// <summary>
/// The export centre (slice S1.16b, FR-ADM-DAT-04, FR-KRN-IAM-12 to -14, FR-KRN-AUD-02, FR-KRN-ENT-04). An export is a background job over the
/// list's own query as the person who asked, so permission, data scope and field security apply row by row exactly as in the list. This service
/// decides who may ask, whether a reason is needed (a salary, personal or restricted field class in the columns), whether a second person must
/// approve (above <c>itg.export.approval_rows</c> or with sensitive classes, while <c>itg.export.requires_approval</c> is on) and who may see and
/// download which export (one's own file only; holders of <c>itg.export_jobs.read</c> see the log of their companies, never the files).
/// </summary>
public sealed class ExportService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IAuditContext audit,
    IPermissionChecker permissions,
    ICallerAccess access,
    ISettings settings,
    IHeldChanges held,
    IEnumerable<IExportRowReader> readers,
    ExportFiles files) : IExportService
{
    public const int MaxColumns = 200;
    internal const int PageRows = 200;
    private const string Select = """
        select e.id, e.entity_type, e.format, e.status, m.status, e.file_name, e.row_count, e.size_bytes, e.reason, e.sensitive_classes, e.company_id, e.created_by, u.display_name,
               e.created_at, e.finished_at, e.expires_at, e.error_key, e.held_request_id, e.file_id
        from itg.export_jobs e
        left join iam.maker_checker_requests m on m.id = e.held_request_id
        left join iam.users u on u.id = e.created_by
        """;

    private static readonly string[] Formats = ["csv", "xlsx"];

    public async Task<ExportCheck> CheckAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var companyId = await CompanyAsync(request, cancellationToken).ConfigureAwait(false);
        var approvalOn = await FlagAsync(KernelSettingKeys.ItgExportRequiresApproval, companyId, cancellationToken).ConfigureAwait(false);
        var approvalRows = await IntAsync(KernelSettingKeys.ItgExportApprovalRows, companyId, 5000, cancellationToken).ConfigureAwait(false);
        var maxRows = await IntAsync(KernelSettingKeys.ItgExportMaxRows, companyId, 50000, cancellationToken).ConfigureAwait(false);
        var rows = approvalOn ? await CountAsync(plan, request, approvalRows + 1, cancellationToken).ConfigureAwait(false) : 0;
        var needsApproval = approvalOn && (plan.SensitiveClasses.Count > 0 || rows > approvalRows);
        return new ExportCheck(plan.Reader.Code, plan.SensitiveClasses, plan.SensitiveClasses.Count > 0, needsApproval, approvalOn, rows, approvalOn && rows > approvalRows, maxRows, approvalRows,
            plan.Hidden, [.. plan.Columns.Select(c => c.Column.JsonName)]);
    }

    public async Task<ExportView> StartAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var companyId = await CompanyAsync(request, cancellationToken).ConfigureAwait(false);
        var format = (request.Format ?? string.Empty).Trim().ToLowerInvariant();
        if (!Formats.Contains(format))
        {
            throw new ExportException("errors.export.format_unsupported", 400);
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 500 })
        {
            throw new ExportException("errors.export.reason_too_long", 400);
        }

        if (plan.SensitiveClasses.Count > 0 && reason is null)
        {
            throw new ExportException("errors.export.reason_required", 400);
        }

        var approvalOn = await FlagAsync(KernelSettingKeys.ItgExportRequiresApproval, companyId, cancellationToken).ConfigureAwait(false);
        var approvalRows = await IntAsync(KernelSettingKeys.ItgExportApprovalRows, companyId, 5000, cancellationToken).ConfigureAwait(false);
        var needsApproval = approvalOn && (plan.SensitiveClasses.Count > 0 || await CountAsync(plan, request, approvalRows + 1, cancellationToken).ConfigureAwait(false) > approvalRows);

        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        var definition = new JsonObject
        {
            ["query"] = request.Query is { ValueKind: JsonValueKind.Object } q ? JsonNode.Parse(q.GetRawText()) : new JsonObject(),
            ["search"] = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
            ["sort"] = string.IsNullOrWhiteSpace(request.Sort) ? null : request.Sort.Trim(),
            ["language"] = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim(),
            ["columns"] = new JsonArray([.. plan.Columns.Select(c => (JsonNode)new JsonObject { ["field"] = c.Column.JsonName, ["label"] = c.Label })]),
        };
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into itg.export_jobs (id, company_id, entity_type, filter, format, status, reason, sensitive_classes, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10, 1)
            """, cancellationToken, id, companyId, plan.Reader.Code, new JsonPayload(definition.ToJsonString()), format, needsApproval ? "awaiting_approval" : "queued", reason,
            plan.SensitiveClasses.ToArray(), now, actor.UserId).ConfigureAwait(false);
        await LogAsync(c, tx, id, companyId, "create", [("entity_type", null, plan.Reader.Code), ("format", null, format), ("status", null, needsApproval ? "awaiting_approval" : "queued"),
            ("reason", null, reason), ("sensitive_classes", null, string.Join(",", plan.SensitiveClasses))], now, cancellationToken).ConfigureAwait(false);
        if (needsApproval)
        {
            var requestId = await held.SubmitAsync(new HeldChangeRequest(ExportCodes.HeldKind, ExportCodes.OwnerType, id, new JsonObject { ["exportId"] = id.ToString() }.ToJsonString(), reason), tx, cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, "update itg.export_jobs set held_request_id = $2 where id = $1", cancellationToken, id, requestId).ConfigureAwait(false);
        }
        else
        {
            await ExportQueue.QueueAsync(c, tx, id, actor.UserId, actor.UserId, now, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportPageResult> ListAsync(bool all, string? status, string? cursor, int limit, CancellationToken cancellationToken)
    {
        await RequireRunAsync(cancellationToken).ConfigureAwait(false);
        var take = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        _ = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var skip);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var companies = new List<Guid>();
        if (all && await permissions.HasPermissionAsync("itg.export_jobs.read", cancellationToken).ConfigureAwait(false))
        {
            foreach (var company in await OpsAlerts.ReadAsync(c, "select id from org.companies", r => r.GetGuid(0), cancellationToken).ConfigureAwait(false))
            {
                if (await access.CoversCompanyAsync("itg.export_jobs.read", company, cancellationToken).ConfigureAwait(false))
                {
                    companies.Add(company);
                }
            }
        }

        var now = clock.UtcNow;
        var rows = await OpsAlerts.ReadAsync(c, Select + """

            where (e.created_by = $1 or e.company_id = any($2))
            order by e.created_at desc, e.id desc
            offset $3 limit $4
            """, r => ReadRow(r), cancellationToken, actor.UserId, companies.ToArray(), skip, take + 1).ConfigureAwait(false);
        var views = rows.Take(take).Select(r => ToView(r, now)).Where(v => status is null || v.Status == status).ToList();
        return new ExportPageResult(views, rows.Count > take ? (skip + take).ToString(CultureInfo.InvariantCulture) : null);
    }

    public async Task<ExportView> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireRunAsync(cancellationToken).ConfigureAwait(false);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var row = await FindAsync(c, id, cancellationToken).ConfigureAwait(false);
        if (row is null || !await MayViewAsync(row, cancellationToken).ConfigureAwait(false))
        {
            throw new ExportException("errors.export.not_found", 404);
        }

        return ToView(row, clock.UtcNow);
    }

    public async Task<ExportFile> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireRunAsync(cancellationToken).ConfigureAwait(false);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var row = await FindAsync(c, id, cancellationToken).ConfigureAwait(false);
        if (row is null || !await MayViewAsync(row, cancellationToken).ConfigureAwait(false))
        {
            throw new ExportException("errors.export.not_found", 404);
        }

        if (row.CreatedBy != actor.UserId)
        {
            // Q9: the log of other people's exports is visible to holders of itg.export_jobs.read, their files are not.
            throw new ExportException("errors.export.not_yours", 403);
        }

        var now = clock.UtcNow;
        var view = ToView(row, now);
        switch (view.Status)
        {
            case "expired":
                throw new ExportException("errors.export.expired", 410);
            case "awaiting_approval":
                throw new ExportException("errors.export.waiting", 409);
            case "rejected":
                throw new ExportException("errors.export.rejected", 409);
            case not "done":
                throw new ExportException("errors.export.not_ready", 409);
        }

        // The right to read the list is checked again at download: a person who lost it can no longer fetch an old file.
        var reader = readers.FirstOrDefault(r => string.Equals(r.Code, row.Source, StringComparison.Ordinal));
        if (reader is null || !await permissions.HasPermissionAsync(reader.ReadPermission, cancellationToken).ConfigureAwait(false))
        {
            throw new ExportException("errors.access.forbidden", 403);
        }

        var content = await files.ReadAsync(row.FileId ?? Guid.Empty, id, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LogAsync(c, tx, id, row.CompanyId, "update", [("downloaded_at", null, now), ("downloaded_by", null, actor.UserId)], now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExportFile(content, string.Equals(row.Format, "xlsx", StringComparison.Ordinal) ? ExportWriters.XlsxType : ExportWriters.CsvType, row.FileName ?? "export." + row.Format);
    }

    // ------------------------------------------------------------------ planning

    /// <summary>The list, the columns the person may see and the field classes among them. Hidden columns are left out; a masked column stays (it is exported masked).</summary>
    internal async Task<ExportPlan> PlanAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireRunAsync(cancellationToken).ConfigureAwait(false);
        var reader = readers.FirstOrDefault(r => string.Equals(r.Code, request.Source?.Trim(), StringComparison.Ordinal)) ?? throw new ExportException("errors.export.source_unknown", 404);
        if (!await permissions.HasPermissionAsync(reader.ReadPermission, cancellationToken).ConfigureAwait(false))
        {
            throw new ExportException("errors.access.forbidden", 403);
        }

        var requested = request.Columns ?? [];
        if (requested.Count == 0)
        {
            throw new ExportException("errors.export.no_columns", 400);
        }

        if (requested.Count > MaxColumns)
        {
            throw new ExportException("errors.export.too_many_columns", 400);
        }

        var companyId = await CompanyAsync(request, cancellationToken).ConfigureAwait(false);
        var known = (await reader.ColumnsAsync(companyId, cancellationToken).ConfigureAwait(false)).ToDictionary(c => c.Column.JsonName, StringComparer.Ordinal);
        var kept = new List<(ExportColumn, string)>();
        var hidden = new List<string>();
        foreach (var column in requested)
        {
            var field = column.Field?.Trim() ?? string.Empty;
            var isCustom = field.StartsWith("custom.", StringComparison.Ordinal) && field.Length > "custom.".Length;
            var baseName = isCustom ? "custom" : field;
            if (!known.TryGetValue(baseName, out var entry))
            {
                throw new ExportException("errors.export.column_unknown", 400, field);
            }

            if (entry.Access == "hidden")
            {
                hidden.Add(field);
                continue;
            }

            if (kept.Any(k => k.Item1.JsonName == field))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(column.Label) ? field : column.Label.Trim();
            kept.Add((field == baseName ? entry.Column : entry.Column with { JsonName = field }, label.Length > 200 ? label[..200] : label));
        }

        if (kept.Count == 0)
        {
            throw new ExportException("errors.export.no_columns", 400);
        }

        var classes = kept.Select(k => k.Item1.FieldClass).Where(f => f is not null).Select(f => f!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new ExportPlan(reader, kept, hidden, classes);
    }

    /// <summary>Rows of the export's query, counted through the caller's own access up to <paramref name="cap"/>.</summary>
    private static async Task<int> CountAsync(ExportPlan plan, ExportRequest request, int cap, CancellationToken ct)
    {
        var query = new ExportQuery(request.Query is { ValueKind: JsonValueKind.Object } q ? q : JsonDocument.Parse("{}").RootElement, request.Search, request.Sort);
        var total = 0;
        Guid? after = null;
        while (total < cap)
        {
            var page = await plan.Reader.ReadPageAsync(query, after, Math.Min(PageRows, cap - total), ct).ConfigureAwait(false);
            total += page.Rows.Count;
            if (page.Next is null || page.Rows.Count == 0)
            {
                break;
            }

            after = page.Next;
        }

        return total;
    }

    /// <summary>The company the export is filed under: the company in hand, which the person must hold the export right in.</summary>
    private async Task<Guid> CompanyAsync(ExportRequest request, CancellationToken ct)
    {
        if (request.CompanyId is not { } company || company == Guid.Empty || !await access.CoversCompanyAsync(ExportPermissions.Run, company, ct).ConfigureAwait(false))
        {
            throw new ExportException("errors.export.company_required", 400);
        }

        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        if (await IdentityDb.ScalarAsync<long>(c, null, "select count(*) from org.companies where id = $1 and not is_deleted", ct, company).ConfigureAwait(false) == 0)
        {
            throw new ExportException("errors.export.company_required", 400);
        }

        return company;
    }

    private async Task RequireRunAsync(CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(ExportPermissions.Run, ct).ConfigureAwait(false))
        {
            throw new ExportException("errors.access.forbidden", 403);
        }
    }

    private async Task<bool> MayViewAsync(ExportRow row, CancellationToken ct) =>
        row.CreatedBy == actor.UserId
        || (await permissions.HasPermissionAsync("itg.export_jobs.read", ct).ConfigureAwait(false) && await access.CoversCompanyAsync("itg.export_jobs.read", row.CompanyId, ct).ConfigureAwait(false));

    private async Task<bool> FlagAsync(string key, Guid company, CancellationToken ct) =>
        (await settings.GetAsync(key, company, ct).ConfigureAwait(false)).Value.ValueKind == JsonValueKind.True;

    private async Task<int> IntAsync(string key, Guid company, int fallback, CancellationToken ct) =>
        (await settings.GetAsync(key, company, ct).ConfigureAwait(false)).Value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : fallback;

    // ------------------------------------------------------------------ rows

    internal sealed record ExportRow(Guid Id, string Source, string Format, string Status, string? HeldStatus, string? FileName, int? RowCount, int? SizeBytes, string? Reason, string[] Classes,
        Guid CompanyId, Guid CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, DateTimeOffset? ExpiresAt, string? ErrorKey, Guid? HeldRequestId, Guid? FileId);

    private static ExportRow ReadRow(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? [] : (string[])r.GetValue(9), r.GetGuid(10), r.GetGuid(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetGuid(17), r.IsDBNull(18) ? null : r.GetGuid(18));

    private static async Task<ExportRow?> FindAsync(NpgsqlConnection c, Guid id, CancellationToken ct) =>
        (await OpsAlerts.ReadAsync(c, Select + "\nwhere e.id = $1", r => ReadRow(r), ct, id).ConfigureAwait(false)).SingleOrDefault();

    /// <summary>The status as shown: a held export whose request was refused or lapsed is rejected; a finished one past its keeping date is expired.</summary>
    private ExportView ToView(ExportRow r, DateTimeOffset now)
    {
        var status = r.Status;
        string? error = r.ErrorKey;
        if (status == "awaiting_approval" && r.HeldStatus is "rejected" or "expired")
        {
            status = "rejected";
            error = "errors.export.rejected";
        }
        else if (status == "done" && r.ExpiresAt is { } expires && expires <= now)
        {
            status = "expired";
        }

        var mine = r.CreatedBy == actor.UserId;
        return new ExportView(r.Id, r.Source, r.Format, status, r.FileName, r.RowCount, r.SizeBytes, r.Reason, r.Classes, r.CompanyId, r.CreatedBy, r.CreatedByName, r.CreatedAt, r.FinishedAt, r.ExpiresAt,
            error, r.HeldRequestId, mine, mine && status == "done", null);
    }

    private Task<int> LogAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid company, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes, DateTimeOffset now, CancellationToken ct)
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

        return AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, actor.UserId, audit.ActorKind, ExportCodes.OwnerType, id, company, action, array, null, audit.CorrelationId, audit.IpAddress, audit.Source, actor.UserId)], ct);
    }
}

/// <summary>Queues the export's background run as the person who asked (shared by the request and by the approval of a held one).</summary>
internal static class ExportQueue
{
    public static async Task QueueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid exportId, Guid requester, Guid by, DateTimeOffset now, CancellationToken ct)
    {
        var runId = Guid.CreateVersion7(now);
        var inserted = await IdentityDb.ExecuteAsync(c, tx, """
            insert into job.runs (id, definition_id, company_id, status, priority, attempt, parameters, requested_by, actor_user_id, created_at, created_by, updated_at, updated_by, row_version)
            select $1, d.id, null, 'queued', d.priority, 0, $2, $3, $3, $4, $5, $4, $5, 1 from job.definitions d where d.code = $6
            """, ct, runId, new JsonPayload(new JsonObject { ["exportId"] = exportId.ToString() }.ToJsonString()), requester, now, by, ExportCodes.RunJob).ConfigureAwait(false);
        if (inserted == 0)
        {
            throw new ExportException("errors.job.not_found", 500);
        }

        await IdentityDb.ExecuteAsync(c, tx, "update itg.export_jobs set job_run_id = $2, status = 'queued', updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", ct, exportId, runId, now, by).ConfigureAwait(false);
        await JobStore.LogAsync(c, tx, runId, "info", "queued for " + requester.ToString(), now, ct).ConfigureAwait(false);
        await JobStore.RaiseAsync(c, tx, JobEvents.RunRequested, "job.run", runId, now, new { run_id = runId, code = ExportCodes.RunJob, company_id = (Guid?)null, by }, ct).ConfigureAwait(false);
    }
}
