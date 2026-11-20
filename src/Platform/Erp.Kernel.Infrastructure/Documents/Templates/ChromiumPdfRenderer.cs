using System.Text.RegularExpressions;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using PuppeteerSharp;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>
/// How the server's Chromium is found and run (<c>Pdf:*</c> settings of both hosts, runbook <c>docs/runbooks/pdf-rendering.md</c>).
/// <c>NoSandbox</c> is for containers that run as root; <c>Parallel</c> limits browsers started at the same time.
/// </summary>
public sealed record PdfRendererOptions(string? ChromiumPath, bool NoSandbox, int TimeoutSeconds, int Parallel)
{
    public static PdfRendererOptions From(Func<string, string?> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        static int Number(string? text, int fallback) => int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;
        return new PdfRendererOptions(string.IsNullOrWhiteSpace(setting("Pdf:ChromiumPath")) ? null : setting("Pdf:ChromiumPath")!.Trim(),
            string.Equals(setting("Pdf:NoSandbox"), "true", StringComparison.OrdinalIgnoreCase), Number(setting("Pdf:TimeoutSeconds"), 60), Number(setting("Pdf:Parallel"), 2));
    }
}

/// <summary>
/// Prints a page to PDF with headless Chromium driven by PuppeteerSharp (ruling Q17). Chromium handles joined Urdu script and
/// right-to-left text. The page can run no script and fetch nothing from outside: every request except embedded data is
/// refused, so a template cannot read an internal address (S1.11c2).
/// </summary>
public sealed class ChromiumPdfRenderer(PdfRendererOptions options) : IDocumentRenderer, IDisposable
{
    private static readonly string[] CommonPaths = ["/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/snap/bin/chromium", "/opt/pw-browsers/chromium"];
    private readonly SemaphoreSlim gate = new(Math.Max(1, options.Parallel));

    public void Dispose() => gate.Dispose();

    public async Task<byte[]> PdfAsync(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        var path = ResolvePath();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var args = new List<string> { "--disable-gpu", "--font-render-hinting=none", "--disable-dev-shm-usage" };
            if (options.NoSandbox)
            {
                args.Add("--no-sandbox");
            }

            try
            {
                await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = path, Args = [.. args], Timeout = options.TimeoutSeconds * 1000 }).ConfigureAwait(false);
                await using var page = await browser.NewPageAsync().ConfigureAwait(false);
                await page.SetRequestInterceptionAsync(true).ConfigureAwait(false);
                page.Request += async (_, e) =>
                {
                    if (e.Request.Url.StartsWith("data:", StringComparison.Ordinal) || e.Request.Url == "about:blank")
                    {
                        await e.Request.ContinueAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await e.Request.AbortAsync().ConfigureAwait(false);
                    }
                };
                await page.SetContentAsync(html, new SetContentOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = options.TimeoutSeconds * 1000 }).ConfigureAwait(false);
                return await page.PdfDataAsync(new PdfOptions { PrintBackground = true, PreferCSSPageSize = true }).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not DocumentException)
            {
                throw new DocumentException(TemplateErrors.RenderFailed, 500) { Data = { ["detail"] = e.Message } };
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The number of pages of a PDF made by Chromium (its page objects are written in the clear).</summary>
    public static int PageCount(byte[] pdf) => Regex.Count(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![A-Za-z])", RegexOptions.None, TimeSpan.FromSeconds(10));

    private string ResolvePath()
    {
        if (options.ChromiumPath is { } configured)
        {
            return File.Exists(configured) ? configured : throw new DocumentException(TemplateErrors.PdfUnavailable, 503);
        }

        return CommonPaths.FirstOrDefault(File.Exists) ?? throw new DocumentException(TemplateErrors.PdfUnavailable, 503);
    }
}
