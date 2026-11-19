using System.Net;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>Wraps a rendered template in a complete page: language and direction, fonts, A4 page rules (right to left for Urdu).</summary>
public static class DocumentHtml
{
    public static string Page(string content, string language)
    {
        var rtl = string.Equals(language, "ur", StringComparison.OrdinalIgnoreCase);
        var family = rtl ? "'Noto Nastaliq Urdu','Noto Sans',serif" : "'Noto Sans','Noto Nastaliq Urdu',sans-serif";
        var lineHeight = rtl ? "2.2" : "1.45";
        return $"<!DOCTYPE html><html lang=\"{WebUtility.HtmlEncode(language)}\" dir=\"{(rtl ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\">"
            + $"<style>{DocumentFonts.Css}@page{{size:A4;margin:18mm}}html,body{{margin:0;padding:0}}body{{font-family:{family};font-size:11pt;line-height:{lineHeight};color:#111}}table{{border-collapse:collapse;width:100%}}th,td{{border:1px solid #999;padding:4px 8px;text-align:start}}</style></head><body>{content}</body></html>";
    }
}
