using System.Net;
using System.Text;

namespace Erp.Kernel.Domain.Notifications;

/// <summary>The statuses of <c>ntf.outbound_messages</c> (slice S1.11b2, plan point P3).</summary>
public static class DeliveryStatuses
{
    /// <summary>Waiting to be sent (first try, a retry, or held by quiet hours until <c>next_attempt_at</c>).</summary>
    public const string Queued = "queued";

    /// <summary>The mail server accepted the message.</summary>
    public const string Sent = "sent";

    /// <summary>Not delivered: refused for good, bounced later, or every retry used up. The reason is in <c>error</c>.</summary>
    public const string Failed = "failed";

    /// <summary>Could not be built: the person has no e-mail address, has left, or no mail server is set up.</summary>
    public const string Dead = "dead";
}

/// <summary>
/// When a refused e-mail is tried again (KRN-NTF-05, plan point P3): after 1, 5, 15, 60 and 240 minutes; after the sixth
/// attempt the message is failed with the last reason. A permanent refusal (an SMTP 5xx reply) fails it at once.
/// </summary>
public static class EmailRetry
{
    private static readonly int[] WaitMinutes = [1, 5, 15, 60, 240];

    /// <summary>Attempts in all, the first included.</summary>
    public static int MaxAttempts => WaitMinutes.Length + 1;

    /// <summary>When to try again after <paramref name="attemptsDone"/> attempts, or null when no attempt is left.</summary>
    public static DateTimeOffset? Next(int attemptsDone, DateTimeOffset utcNow) =>
        attemptsDone >= 1 && attemptsDone <= WaitMinutes.Length ? utcNow.AddMinutes(WaitMinutes[attemptsDone - 1]) : null;

    /// <summary>An SMTP reply code that ends the message at once (5xx: the server will never take it).</summary>
    public static bool IsPermanent(int smtpStatusCode) => smtpStatusCode is >= 500 and < 600;
}

/// <summary>The fixed words around a notification e-mail in one language (from the notification localization file).</summary>
/// <param name="OpenButton">The label of the link button.</param>
/// <param name="Footer">The line under every notification e-mail.</param>
public sealed record EmailLayoutTexts(string OpenButton, string Footer);

/// <summary>
/// The body of a notification e-mail as plain text and as simple HTML (plan point P5): the text, a button with the full
/// link, and the footer. Every value is HTML-encoded; Urdu is laid out right to left.
/// </summary>
public static class EmailLayout
{
    public static bool IsRightToLeft(string language) => language == "ur";

    public static string Text(string body, string? link, EmailLayoutTexts texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var b = new StringBuilder(body.Trim());
        if (!string.IsNullOrEmpty(link))
        {
            b.Append("\n\n").Append(texts.OpenButton).Append(": ").Append(link);
        }

        return b.Append("\n\n--\n").Append(texts.Footer).Append('\n').ToString();
    }

    public static string Html(string subject, string body, string? link, string language, EmailLayoutTexts texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var dir = IsRightToLeft(language) ? "rtl" : "ltr";
        var align = IsRightToLeft(language) ? "right" : "left";
        var b = new StringBuilder();
        b.Append("<!DOCTYPE html><html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\" dir=\"").Append(dir).Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(subject)).Append("</title></head><body style=\"font-family:Arial,'Noto Nastaliq Urdu',sans-serif;text-align:").Append(align).Append("\">");
        foreach (var paragraph in body.Trim().Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            b.Append("<p>").Append(WebUtility.HtmlEncode(paragraph).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
        }

        if (!string.IsNullOrEmpty(link))
        {
            b.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(link))
                .Append("\" style=\"display:inline-block;padding:10px 18px;background:#1f5fbf;color:#ffffff;text-decoration:none;border-radius:4px\">")
                .Append(WebUtility.HtmlEncode(texts.OpenButton)).Append("</a></p>");
        }

        return b.Append("<hr><p style=\"color:#666666;font-size:12px\">").Append(WebUtility.HtmlEncode(texts.Footer)).Append("</p></body></html>").ToString();
    }

    /// <summary>The full address of a deep link: relative paths get the customer's base address; anything else is dropped.</summary>
    public static string? Absolute(string? link, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(link) || !link.StartsWith('/') || link.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        return baseUrl.TrimEnd('/') + link;
    }
}
