using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Documents;

/// <summary>
/// Permissions of document templates and generated documents (slice S1.11c2; KRN-DOC-05, HRM-DOC-08, rulings Q17, Q26). The
/// read codes are generated from the data design; the writes of these tables are closed there (templates change only through the
/// template service), so their codes are declared here, with the new salary right.
/// </summary>
public static class TemplatePermissions
{
    public const string TemplatesRead = "doc.templates.read";
    public const string TemplatesCreate = "doc.templates.create";
    public const string TemplatesUpdate = "doc.templates.update";
    public const string VersionsRead = "doc.template_versions.read";
    public const string VersionsCreate = "doc.template_versions.create";
    public const string VersionsUpdate = "doc.template_versions.update";
    public const string GeneratedRead = "doc.generated_documents.read";
    public const string GeneratedCreate = "doc.generated_documents.create";

    /// <summary>Produce a document from a template that uses salary-class fields (ruling Q26, HRM-DOC-08).</summary>
    public const string GenerateSalary = "doc.templates.generate_salary";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(TemplatesCreate, "doc", "permissions.doc.templates.create", false),
        new(TemplatesUpdate, "doc", "permissions.doc.templates.update", false),
        new(VersionsCreate, "doc", "permissions.doc.template_versions.create", false),
        new(VersionsUpdate, "doc", "permissions.doc.template_versions.update", false),
        new(GeneratedCreate, "doc", "permissions.doc.generated_documents.create", false),
        new(GenerateSalary, "doc", "permissions.doc.templates.generate_salary", true),
    ];
}

/// <summary>Field classes a template field can have (the same words as the notification catalogue and the access engine).</summary>
public static class TemplateFieldClasses
{
    public const string General = "general";
    public const string Personal = "personal";
    public const string Salary = "salary";
}

/// <summary>Error titles of templates (localization keys, <c>Documents/Localization/documents.*.json</c>).</summary>
public static class TemplateErrors
{
    public const string Invalid = "errors.template.invalid";
    public const string NotFound = "errors.template.not_found";
    public const string Syntax = "errors.template.syntax";
    public const string UnknownField = "errors.template.unknown_field";
    public const string TooLarge = "errors.template.too_large";
    public const string NotDraft = "errors.template.not_draft";
    public const string NoActiveVersion = "errors.template.no_active_version";
    public const string FormatNotSupported = "errors.template.format_not_supported";
    public const string RenderFailed = "errors.template.render_failed";
    public const string PdfUnavailable = "errors.template.pdf_unavailable";
    public const string TooManyPages = "errors.template.too_many_pages";
    public const string UnknownEntity = "errors.template.unknown_entity";
    public const string Conflict = "errors.template.conflict";
}

/// <summary>Output formats of a template version (ruling Q19: PDF and HTML in 1.0; text kinds pass through the template only).</summary>
public static class TemplateFormats
{
    public const string Pdf = "pdf";
    public const string Html = "html";
    public const string Text = "text";
    public const string Csv = "csv";
    public const string FixedWidth = "fixed_width";

    /// <summary>In the data design, not built in 1.0.</summary>
    public const string Xlsx = "xlsx";

    /// <summary>Formats a version can be saved with today; <c>xlsx</c> exists in the data design but is not built (Q19).</summary>
    public static IReadOnlyList<string> Supported { get; } = [Pdf, Html, Text, Csv, FixedWidth];
}

/// <summary>Kinds of template (the data design's enum). A <c>part</c> is a shared letterhead, header or footer.</summary>
public static class TemplateKinds
{
    public const string Part = "part";

    /// <summary>The code of the letterhead part: its body wraps the printed document and shows the letter's content where <c>{{ content }}</c> stands.</summary>
    public const string LetterheadCode = "letterhead";

    public static IReadOnlyList<string> All { get; } = ["letter", "payslip", "certificate", "form", "email", "bank_file", "report", Part];
}

/// <summary>One field a module offers to the templates of its records. The path is dotted; a segment ending in <c>[]</c> is a list.</summary>
public sealed record TemplateFieldDefinition(string Path, string Type, string FieldClass);

/// <summary>The data of one record for a template: its company and the values of the declared fields (nested by path).</summary>
public sealed record TemplateData(Guid CompanyId, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// What a module supplies so its records can be printed (ruling Q26): the fields with their class, and the values of one
/// record. The module applies its own permissions and data scopes: it returns null for a record the caller may not read.
/// The entity type is the table name, the same owner type the record's files use.
/// </summary>
public interface ITemplateDataProvider
{
    string EntityType { get; }

    IReadOnlyList<TemplateFieldDefinition> Fields { get; }

    Task<TemplateData?> LoadAsync(Guid ownerId, CancellationToken cancellationToken);
}

/// <summary>A printable record type with the fields its module offers to templates (the placeholder list of the editor).</summary>
public sealed record TemplateEntityFields(string EntityType, IReadOnlyList<TemplateFieldDefinition> Fields);

/// <summary>A template (the row of <c>doc.templates</c>).</summary>
public sealed record TemplateView(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Kind, string EntityType, Guid? CompanyId, string Language, string Source, Guid? ActiveVersionId, bool IsActive, long RowVersion);

/// <summary>One version of a template; the body is the Scriban text.</summary>
public sealed record TemplateVersionView(Guid Id, Guid TemplateId, int Version, string Status, string OutputFormat, string Body, long RowVersion);

/// <summary>Creates a template with its first (draft) version.</summary>
public sealed record TemplateCreate(string Code, IReadOnlyDictionary<string, string> Name, string Kind, string EntityType, Guid? CompanyId, string Language, string OutputFormat, string Body);

/// <summary>A change to a draft version, or a new draft version when it is sent to the versions endpoint.</summary>
public sealed record TemplateVersionEdit(string Body, string? OutputFormat);

/// <summary>Previews a body against sample values; nothing is stored. Without sample data the declared fields get invented values.</summary>
public sealed record TemplatePreviewRequest(string Body, string OutputFormat, string Language, string EntityType, Guid? CompanyId, JsonElement? SampleData);

/// <summary>The rendered HTML of a preview, and the PDF when the format is pdf.</summary>
public sealed record TemplatePreview(string Html, byte[]? Pdf, IReadOnlyList<string> UsedFields);

/// <summary>Produces one document of a template for a record. The language and company come from the template and the record.</summary>
public sealed record GenerateRequest(Guid TemplateId, Guid OwnerId);

/// <summary>A generated document: its row and, for PDF, the stored file.</summary>
public sealed record GeneratedDocumentInfo(Guid Id, Guid TemplateVersionId, string OwnerType, Guid OwnerId, Guid? FileId, string OutputFormat, string? Content, DateTimeOffset CreatedAt);
