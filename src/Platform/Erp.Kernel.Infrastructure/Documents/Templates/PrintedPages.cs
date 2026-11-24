using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>The module-facing print service: the standard document page around the module's HTML, then the Chromium renderer (slice S1.22b).</summary>
public sealed class PrintedPages(IDocumentRenderer renderer) : IPrintedPages
{
    public Task<byte[]> PdfAsync(string bodyHtml, string language, bool landscape, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        var page = DocumentHtml.Page(bodyHtml, language);
        if (landscape)
        {
            page = page.Replace("@page{size:A4;", "@page{size:A4 landscape;", StringComparison.Ordinal);
        }

        return renderer.PdfAsync(page, cancellationToken);
    }
}
