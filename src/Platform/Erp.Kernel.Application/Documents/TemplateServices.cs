using Erp.Kernel.Contracts.Documents;

namespace Erp.Kernel.Application.Documents;

/// <summary>
/// Document templates (slice S1.11c2; KRN-DOC-05, HRM-DOC-08): create, version (draft, active, retired), preview and produce
/// documents. Every call checks the caller's permission; generating also asks the owning module's provider for the data.
/// </summary>
public interface ITemplateService
{
    Task<TemplateView> CreateAsync(TemplateCreate create, CancellationToken cancellationToken);

    Task<IReadOnlyList<TemplateVersionView>> VersionsAsync(Guid templateId, CancellationToken cancellationToken);

    /// <summary>Starts a new draft version (the active version is never changed in place).</summary>
    Task<TemplateVersionView> NewVersionAsync(Guid templateId, TemplateVersionEdit edit, CancellationToken cancellationToken);

    /// <summary>Changes the body of a draft version.</summary>
    Task<TemplateVersionView> UpdateDraftAsync(Guid versionId, TemplateVersionEdit edit, long rowVersion, CancellationToken cancellationToken);

    /// <summary>Makes a draft the active version; the version that was active becomes retired.</summary>
    Task<TemplateVersionView> ActivateAsync(Guid versionId, long rowVersion, CancellationToken cancellationToken);

    /// <summary>Takes a template out of use (its active version becomes retired).</summary>
    Task<TemplateView> RetireAsync(Guid templateId, long rowVersion, CancellationToken cancellationToken);

    Task<TemplatePreview> PreviewAsync(TemplatePreviewRequest request, CancellationToken cancellationToken);

    /// <summary>Produces the document; a PDF is stored against the record like an attachment.</summary>
    Task<GeneratedDocumentInfo> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneratedDocumentInfo>> GeneratedAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken);
}

/// <summary>Prints HTML to PDF with the server's headless Chromium (ruling Q17).</summary>
public interface IDocumentRenderer
{
    /// <summary>The PDF of a complete HTML page. Scripts and outside network requests are blocked.</summary>
    Task<byte[]> PdfAsync(string html, CancellationToken cancellationToken);
}
