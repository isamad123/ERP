using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Notifications;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>
/// Document templates and generated documents (slice S1.11c2 Build item 20; KRN-DOC-05, HRM-DOC-08, rulings Q17, Q19, Q26).
/// A template has versions (draft, active, retired); the active one is never changed, a change is a new draft. A body is checked
/// when it is saved (syntax, size, and every name it uses must be a declared field of the record type). A document is made from
/// the data the owning module supplies; one that uses salary-class fields needs the salary right and is written to the access
/// log; a PDF is stored against the record like an attachment, so it is encrypted, scanned and kept by the same rules.
/// Every change is in the audit log.
/// </summary>
public sealed partial class TemplateService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IAuditContext audit,
    IPermissionChecker permissions,
    ICallerAccess access,
    IEnumerable<ITemplateDataProvider> providers,
    IAttachmentService attachments,
    IDocumentRenderer renderer) : ITemplateService
{
    private const string TemplateColumns = "id, code, name_i18n::text, kind, entity_type, company_id, language, source, active_version_id, is_active, row_version";
    private const string VersionColumns = "id, template_id, version, status, output_format, body, row_version";

    public async Task<TemplateView> CreateAsync(TemplateCreate create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);
        await RequireAsync(TemplatePermissions.TemplatesCreate, cancellationToken).ConfigureAwait(false);
        await RequireCompanyAsync(TemplatePermissions.TemplatesCreate, create.CompanyId, cancellationToken).ConfigureAwait(false);
        var code = (create.Code ?? string.Empty).Trim();
        if (!CodePattern().IsMatch(code) || !TemplateKinds.All.Contains(create.Kind, StringComparer.Ordinal) || create.Language is not ("en" or "ur")
            || string.IsNullOrWhiteSpace(create.EntityType) || create.Name is null || create.Name.Values.All(string.IsNullOrWhiteSpace))
        {
            throw new DocumentException(TemplateErrors.Invalid);
        }

        var isPart = create.Kind == TemplateKinds.Part;
        var provider = FindProvider(create.EntityType);
        if (provider is null && !isPart)
        {
            throw new DocumentException(TemplateErrors.UnknownEntity, 400);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var limits = await LimitsAsync(c, create.CompanyId, now, cancellationToken).ConfigureAwait(false);
        CheckBody(create.Body, create.OutputFormat, create.Language, provider?.Fields ?? [], isPart, limits.MaxKb, now, cancellationToken);
        var id = Guid.CreateVersion7(now);
        var versionId = Guid.CreateVersion7(now);
        try
        {
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into doc.templates (id, code, name_i18n, kind, entity_type, company_id, language, source, active_version_id, is_active, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3::jsonb, $4, $5, $6, $7, 'tenant', null, true, $8, $9, $8, $9, 1)
                """, cancellationToken, id, code, JsonSerializer.Serialize(create.Name), create.Kind, create.EntityType.Trim(), create.CompanyId, create.Language, now, actor.UserId).ConfigureAwait(false);
            await InsertVersionAsync(c, tx, versionId, id, 1, create.Body, create.OutputFormat, now, cancellationToken).ConfigureAwait(false);
            await AuditDb.AppendChangesAsync(c, tx,
            [
                Change("doc.templates", id, "create", new JsonObject { ["field"] = "code", ["before"] = null, ["after"] = code }, now, create.CompanyId),
                Change("doc.template_versions", versionId, "create", new JsonObject { ["field"] = "version", ["before"] = null, ["after"] = 1 }, now, create.CompanyId),
            ], cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DocumentException(TemplateErrors.Conflict, 409);
        }

        return (await FindTemplateAsync(c, id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<TemplateVersionView>> VersionsAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await RequireAsync(TemplatePermissions.VersionsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var template = await FindTemplateAsync(c, templateId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        await RequireCompanyAsync(TemplatePermissions.VersionsRead, template.CompanyId, cancellationToken).ConfigureAwait(false);
        return await NotificationConsumer.ReadAsync(c, null, $"select {VersionColumns} from doc.template_versions where template_id = $1 order by version desc", ReadVersion, cancellationToken, templateId).ConfigureAwait(false);
    }

    public async Task<TemplateVersionView> NewVersionAsync(Guid templateId, TemplateVersionEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await RequireAsync(TemplatePermissions.VersionsCreate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var template = await FindTemplateAsync(c, templateId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        await RequireCompanyAsync(TemplatePermissions.VersionsCreate, template.CompanyId, cancellationToken).ConfigureAwait(false);
        if (!template.IsActive)
        {
            throw new DocumentException(TemplateErrors.NotFound, 404);
        }

        var now = clock.UtcNow;
        var limits = await LimitsAsync(c, template.CompanyId, now, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('doc.template_versions'))", cancellationToken).ConfigureAwait(false);
        var versions = await NotificationConsumer.ReadAsync(c, tx, $"select {VersionColumns} from doc.template_versions where template_id = $1 order by version desc", ReadVersion, cancellationToken, templateId).ConfigureAwait(false);
        if (versions.Any(v => v.Status == "draft"))
        {
            throw new DocumentException(TemplateErrors.Conflict, 409); // edit the draft that exists
        }

        var format = edit.OutputFormat ?? versions[0].OutputFormat;
        CheckBody(edit.Body, format, template.Language, FindProvider(template.EntityType)?.Fields ?? [], template.Kind == TemplateKinds.Part, limits.MaxKb, now, cancellationToken);
        var id = Guid.CreateVersion7(now);
        var number = versions[0].Version + 1;
        await InsertVersionAsync(c, tx, id, templateId, number, edit.Body, format, now, cancellationToken).ConfigureAwait(false);
        await AuditDb.AppendChangesAsync(c, tx, [Change("doc.template_versions", id, "create", new JsonObject { ["field"] = "version", ["before"] = null, ["after"] = number }, now, template.CompanyId)], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await FindVersionAsync(c, null, id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<TemplateVersionView> UpdateDraftAsync(Guid versionId, TemplateVersionEdit edit, long rowVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await RequireAsync(TemplatePermissions.VersionsUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var version = await FindVersionAsync(c, null, versionId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        var template = (await FindTemplateAsync(c, version.TemplateId, cancellationToken).ConfigureAwait(false))!;
        await RequireCompanyAsync(TemplatePermissions.VersionsUpdate, template.CompanyId, cancellationToken).ConfigureAwait(false);
        if (version.Status != "draft")
        {
            throw new DocumentException(TemplateErrors.NotDraft, 409);
        }

        var now = clock.UtcNow;
        var limits = await LimitsAsync(c, template.CompanyId, now, cancellationToken).ConfigureAwait(false);
        var format = edit.OutputFormat ?? version.OutputFormat;
        CheckBody(edit.Body, format, template.Language, FindProvider(template.EntityType)?.Fields ?? [], template.Kind == TemplateKinds.Part, limits.MaxKb, now, cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var changed = await IdentityDb.ExecuteAsync(c, tx, "update doc.template_versions set body = $2, output_format = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1 and status = 'draft' and row_version = $6",
            cancellationToken, versionId, edit.Body, format, now, actor.UserId, rowVersion).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new DocumentException(TemplateErrors.Conflict, 409);
        }

        await AuditDb.AppendChangesAsync(c, tx, [Change("doc.template_versions", versionId, "update", new JsonObject { ["field"] = "body", ["before"] = null, ["after"] = "changed" }, now, template.CompanyId)], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await FindVersionAsync(c, null, versionId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<TemplateVersionView> ActivateAsync(Guid versionId, long rowVersion, CancellationToken cancellationToken)
    {
        await RequireAsync(TemplatePermissions.VersionsUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var version = await FindVersionAsync(c, null, versionId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        var template = (await FindTemplateAsync(c, version.TemplateId, cancellationToken).ConfigureAwait(false))!;
        await RequireCompanyAsync(TemplatePermissions.VersionsUpdate, template.CompanyId, cancellationToken).ConfigureAwait(false);
        if (version.Status != "draft" || !template.IsActive)
        {
            throw new DocumentException(TemplateErrors.NotDraft, 409);
        }

        var now = clock.UtcNow;
        var limits = await LimitsAsync(c, template.CompanyId, now, cancellationToken).ConfigureAwait(false);
        CheckBody(version.Body, version.OutputFormat, template.Language, FindProvider(template.EntityType)?.Fields ?? [], template.Kind == TemplateKinds.Part, limits.MaxKb, now, cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('doc.template_versions'))", cancellationToken).ConfigureAwait(false);
        var retired = await NotificationConsumer.ReadAsync(c, tx, """
            update doc.template_versions set status = 'retired', updated_at = $2, updated_by = $3, row_version = row_version + 1
            where template_id = $1 and status = 'active' returning id
            """, r => r.GetGuid(0), cancellationToken, template.Id, now, actor.UserId).ConfigureAwait(false);
        var activated = await IdentityDb.ExecuteAsync(c, tx, "update doc.template_versions set status = 'active', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and status = 'draft' and row_version = $4",
            cancellationToken, versionId, now, actor.UserId, rowVersion).ConfigureAwait(false);
        if (activated != 1)
        {
            throw new DocumentException(TemplateErrors.Conflict, 409);
        }

        await IdentityDb.ExecuteAsync(c, tx, "update doc.templates set active_version_id = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", cancellationToken, template.Id, versionId, now, actor.UserId).ConfigureAwait(false);
        var entries = retired.Select(id => Change("doc.template_versions", id, "status", new JsonObject { ["field"] = "status", ["before"] = "active", ["after"] = "retired" }, now, template.CompanyId)).ToList();
        entries.Add(Change("doc.template_versions", versionId, "status", new JsonObject { ["field"] = "status", ["before"] = "draft", ["after"] = "active" }, now, template.CompanyId));
        await AuditDb.AppendChangesAsync(c, tx, entries, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await FindVersionAsync(c, null, versionId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<TemplateView> RetireAsync(Guid templateId, long rowVersion, CancellationToken cancellationToken)
    {
        await RequireAsync(TemplatePermissions.TemplatesUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var template = await FindTemplateAsync(c, templateId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        await RequireCompanyAsync(TemplatePermissions.TemplatesUpdate, template.CompanyId, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var retired = await NotificationConsumer.ReadAsync(c, tx, """
            update doc.template_versions set status = 'retired', updated_at = $2, updated_by = $3, row_version = row_version + 1 where template_id = $1 and status in ('active', 'draft') returning id
            """, r => r.GetGuid(0), cancellationToken, templateId, now, actor.UserId).ConfigureAwait(false);
        var changed = await IdentityDb.ExecuteAsync(c, tx, "update doc.templates set is_active = false, active_version_id = null, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and row_version = $4 and is_active",
            cancellationToken, templateId, now, actor.UserId, rowVersion).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new DocumentException(TemplateErrors.Conflict, 409);
        }

        var entries = retired.Select(id => Change("doc.template_versions", id, "status", new JsonObject { ["field"] = "status", ["before"] = null, ["after"] = "retired" }, now, template.CompanyId)).ToList();
        entries.Add(Change("doc.templates", templateId, "status", new JsonObject { ["field"] = "is_active", ["before"] = true, ["after"] = false }, now, template.CompanyId));
        await AuditDb.AppendChangesAsync(c, tx, entries, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await FindTemplateAsync(c, templateId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<TemplatePreview> PreviewAsync(TemplatePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(TemplatePermissions.TemplatesRead, cancellationToken).ConfigureAwait(false);
        await RequireCompanyAsync(TemplatePermissions.TemplatesRead, request.CompanyId, cancellationToken).ConfigureAwait(false);
        if (request.Language is not ("en" or "ur"))
        {
            throw new DocumentException(TemplateErrors.Invalid);
        }

        var provider = FindProvider(request.EntityType);
        var fields = provider?.Fields ?? [];
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var limits = await LimitsAsync(c, request.CompanyId, now, cancellationToken).ConfigureAwait(false);
        CheckBody(request.Body, request.OutputFormat, request.Language, fields, false, limits.MaxKb, now, cancellationToken, strict: request.SampleData is null && provider is not null);
        var values = request.SampleData is { ValueKind: JsonValueKind.Object } sample ? JsonValues(sample) : TemplateSandbox.SampleValues(fields);
        var rendered = await ComposeAsync(c, request.Body, request.OutputFormat, request.Language, request.CompanyId, values, now, cancellationToken).ConfigureAwait(false);
        var pdf = request.OutputFormat == TemplateFormats.Pdf ? await PdfAsync(rendered, limits.MaxPages, cancellationToken).ConfigureAwait(false) : null;
        return new TemplatePreview(rendered, pdf, [.. TemplateSandbox.UsedFields(request.Body, fields).Select(f => f.Path)]);
    }

    public async Task<GeneratedDocumentInfo> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(TemplatePermissions.GeneratedCreate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var template = await FindTemplateAsync(c, request.TemplateId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        if (!template.IsActive || template.ActiveVersionId is not { } versionId || template.Kind == TemplateKinds.Part)
        {
            throw new DocumentException(TemplateErrors.NoActiveVersion, 409);
        }

        var version = (await FindVersionAsync(c, null, versionId, cancellationToken).ConfigureAwait(false))!;
        var provider = FindProvider(template.EntityType) ?? throw new DocumentException(TemplateErrors.UnknownEntity, 400);
        var data = await provider.LoadAsync(request.OwnerId, cancellationToken).ConfigureAwait(false) ?? throw new DocumentException(TemplateErrors.NotFound, 404);
        if (template.CompanyId is { } own && own != data.CompanyId)
        {
            throw new DocumentException(TemplateErrors.NotFound, 404); // a company's own template is not used for another company's record
        }

        if (version.OutputFormat == TemplateFormats.Xlsx)
        {
            throw new DocumentException(TemplateErrors.FormatNotSupported);
        }

        var used = TemplateSandbox.UsedFields(version.Body, provider.Fields);
        var usesSalary = used.Any(f => f.FieldClass == TemplateFieldClasses.Salary);
        if (usesSalary && !await permissions.HasPermissionAsync(TemplatePermissions.GenerateSalary, cancellationToken).ConfigureAwait(false))
        {
            throw new DocumentException(DocumentErrors.Forbidden, 403);
        }

        var now = clock.UtcNow;
        var limits = await LimitsAsync(c, data.CompanyId, now, cancellationToken).ConfigureAwait(false);
        var rendered = await ComposeAsync(c, version.Body, version.OutputFormat, template.Language, data.CompanyId, data.Values, now, cancellationToken).ConfigureAwait(false);
        var id = Guid.CreateVersion7(now);
        var sensitive = used.Where(f => f.FieldClass != TemplateFieldClasses.General).ToList();
        if (sensitive.Count > 0)
        {
            // The access log is written before the document leaves the server; if it cannot be written the request fails (Q26).
            await AuditDb.AppendAccessAsync(c, actor.UserId, "doc.generated_documents", id, [.. sensitive.Select(f => f.Path)], "print", 1, audit.CorrelationId, now, cancellationToken).ConfigureAwait(false);
        }

        if (version.OutputFormat != TemplateFormats.Pdf)
        {
            return new GeneratedDocumentInfo(id, version.Id, template.EntityType, request.OwnerId, null, version.OutputFormat, rendered, now);
        }

        var pdf = await PdfAsync(rendered, limits.MaxPages, cancellationToken).ConfigureAwait(false);
        var name = string.Create(CultureInfo.InvariantCulture, $"{template.Code}-{request.OwnerId.ToString("N")[..8]}-{now:yyyyMMdd}.pdf");
        var file = await attachments.UploadAsync(new AttachmentUpload(template.EntityType, request.OwnerId, name, pdf, template.Code, null, sensitive.Count > 0, null), cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, null, """
            insert into doc.generated_documents (id, company_id, template_version_id, owner_type, owner_id, file_id, is_password_protected, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, false, $7, $8)
            """, cancellationToken, id, data.CompanyId, version.Id, template.EntityType, request.OwnerId, file.Id, now, actor.UserId).ConfigureAwait(false);
        return new GeneratedDocumentInfo(id, version.Id, template.EntityType, request.OwnerId, file.Id, TemplateFormats.Pdf, null, now);
    }

    public async Task<IReadOnlyList<GeneratedDocumentInfo>> GeneratedAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        await RequireAsync(TemplatePermissions.GeneratedRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await NotificationConsumer.ReadAsync(c, null, "select id, template_version_id, owner_type, owner_id, file_id, created_at from doc.generated_documents where owner_type = $1 and owner_id = $2 order by created_at desc, id",
            r => new GeneratedDocumentInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), TemplateFormats.Pdf, null, r.GetFieldValue<DateTimeOffset>(5)), cancellationToken, ownerType, ownerId).ConfigureAwait(false);
        var visible = new List<GeneratedDocumentInfo>();
        foreach (var row in rows)
        {
            // The record's own rules decide: a file the caller may not read (a sensitive one, or a record out of scope) is not listed.
            if (row.FileId is { } file && await attachments.GetAsync(file, cancellationToken).ConfigureAwait(false) is not null)
            {
                visible.Add(row);
            }
        }

        return visible;
    }

    // ---- rendering ----

    private static async Task<string> ComposeAsync(NpgsqlConnection c, string body, string format, string language, Guid? companyId, IReadOnlyDictionary<string, object?> values, DateTimeOffset now, CancellationToken ct)
    {
        var content = TemplateSandbox.Render(body, values, format, language, now, strict: false, ct);
        if (format is not (TemplateFormats.Pdf or TemplateFormats.Html))
        {
            return content;
        }

        var letterhead = await LetterheadAsync(c, companyId, language, ct).ConfigureAwait(false);
        if (letterhead is not null)
        {
            var withContent = new Dictionary<string, object?>(values, StringComparer.Ordinal) { ["content"] = new RawHtml(content) };
            content = TemplateSandbox.Render(letterhead, withContent, format, language, now, strict: false, ct);
        }

        return DocumentHtml.Page(content, language);
    }

    private async Task<byte[]> PdfAsync(string html, int maxPages, CancellationToken ct)
    {
        var pdf = await renderer.PdfAsync(html, ct).ConfigureAwait(false);
        if (ChromiumPdfRenderer.PageCount(pdf) > maxPages)
        {
            throw new DocumentException(TemplateErrors.TooManyPages, 400);
        }

        return pdf;
    }

    /// <summary>The company's own active letterhead part for the language, else the one for all companies (null when there is none).</summary>
    private static async Task<string?> LetterheadAsync(NpgsqlConnection c, Guid? companyId, string language, CancellationToken ct) =>
        (await NotificationConsumer.ReadAsync(c, null, """
            select v.body from doc.templates t join doc.template_versions v on v.template_id = t.id and v.id = t.active_version_id
            where t.kind = 'part' and lower(t.code) = 'letterhead' and t.language = $2 and t.is_active and (t.company_id = $1 or t.company_id is null)
            order by (t.company_id is not null) desc limit 1
            """, r => r.GetString(0), ct, companyId, language).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Size, format, syntax and field check of a body. A name the record type does not offer is refused (checked by trying the body against sample values).</summary>
    private static void CheckBody(string? body, string format, string language, IReadOnlyList<TemplateFieldDefinition> fields, bool isPart, int maxKb, DateTimeOffset now, CancellationToken ct, bool strict = true)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new DocumentException(TemplateErrors.Invalid);
        }

        if (body.Length > maxKb * 1024L)
        {
            throw new DocumentException(TemplateErrors.TooLarge, 413);
        }

        if (!TemplateFormats.Supported.Contains(format, StringComparer.Ordinal))
        {
            throw new DocumentException(TemplateErrors.FormatNotSupported);
        }

        var sample = TemplateSandbox.SampleValues(fields);
        if (isPart)
        {
            sample["content"] = new RawHtml(string.Empty);
        }

        TemplateSandbox.Render(body, sample, format, language, now, strict, ct);
    }

    private static Dictionary<string, object?> JsonValues(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = JsonValue(property.Value);
        }

        return result;
    }

    private static object? JsonValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => JsonValues(e),
        JsonValueKind.Array => e.EnumerateArray().Select(JsonValue).ToList(),
        JsonValueKind.String => e.TryGetDateTime(out var at) && e.GetString()!.Length >= 10 && char.IsDigit(e.GetString()![0]) ? at : e.GetString(),
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // ---- data ----

    private ITemplateDataProvider? FindProvider(string entityType) => providers.FirstOrDefault(p => string.Equals(p.EntityType, entityType, StringComparison.Ordinal));

    private static async Task<(int MaxKb, int MaxPages)> LimitsAsync(NpgsqlConnection c, Guid? companyId, DateTimeOffset now, CancellationToken ct)
    {
        var values = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.DocTemplateMaxKb, KernelSettingKeys.DocPdfMaxPages], companyId, null, DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false);
        static int Number(Dictionary<string, ResolvedSetting> v, string key, int fallback) => v.TryGetValue(key, out var s) && s.Value.TryGetInt32(out var n) && n > 0 ? n : fallback;
        return (Number(values, KernelSettingKeys.DocTemplateMaxKb, 200), Number(values, KernelSettingKeys.DocPdfMaxPages, 50));
    }

    private Task<int> InsertVersionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid templateId, int number, string body, string format, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            insert into doc.template_versions (id, template_id, version, body, output_format, status, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, 'draft', $6, $7, $6, $7, 1)
            """, ct, id, templateId, number, body, format, now, actor.UserId);

    private static TemplateView ReadTemplate(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [], r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetGuid(5), r.GetString(6).Trim(), r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.GetBoolean(9), r.GetInt64(10));

    private static TemplateVersionView ReadVersion(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6));

    private static async Task<TemplateView?> FindTemplateAsync(NpgsqlConnection c, Guid id, CancellationToken ct) =>
        (await NotificationConsumer.ReadAsync(c, null, $"select {TemplateColumns} from doc.templates where id = $1", ReadTemplate, ct, id).ConfigureAwait(false)).FirstOrDefault();

    private static async Task<TemplateVersionView?> FindVersionAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
        (await NotificationConsumer.ReadAsync(c, tx, $"select {VersionColumns} from doc.template_versions where id = $1", ReadVersion, ct, id).ConfigureAwait(false)).FirstOrDefault();

    private ChangeEntry Change(string table, Guid id, string action, JsonObject change, DateTimeOffset now, Guid? company) =>
        new(Guid.CreateVersion7(now), now, audit.ActorUserId, audit.ActorKind, table, id, company, action, [change], null, audit.CorrelationId, audit.IpAddress, audit.Source, actor.UserId);

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new DocumentException(DocumentErrors.Forbidden, 403);
        }
    }

    private async Task RequireCompanyAsync(string permission, Guid? company, CancellationToken ct)
    {
        if (!await access.CoversCompanyAsync(permission, company, ct).ConfigureAwait(false))
        {
            throw new DocumentException(DocumentErrors.Forbidden, 403);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodePattern();
}
