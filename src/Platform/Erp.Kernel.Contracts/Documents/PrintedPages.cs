namespace Erp.Kernel.Contracts.Documents;

/// <summary>
/// Prints a module's own HTML to PDF through the document service's renderer (slice S1.22b, ruling Q7): the page gets the language and
/// direction (right to left for Urdu), the embedded fonts and an A4 page, portrait or landscape. Scripts and outside requests are blocked.
/// </summary>
public interface IPrintedPages
{
    /// <summary><paramref name="bodyHtml"/> is the content of the page body (already encoded); <paramref name="language"/> is <c>en</c> or <c>ur</c>.</summary>
    Task<byte[]> PdfAsync(string bodyHtml, string language, bool landscape, CancellationToken cancellationToken);
}
