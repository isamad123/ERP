using System.Text;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.Notifications.Email;
using MimeKit;

namespace Erp.UnitTests.Notifications;

/// <summary>The delivery rules of slice S1.11b2 (KRN-NTF-05, KRN-NTF-07, GHR-06): retry timing, the e-mail layout and bounce reports.</summary>
public sealed class EmailRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly EmailLayoutTexts Texts = new("Open", "Sent automatically.");

    [Fact]
    public void S111b2_KRN_NTF_05_a_refused_email_waits_1_5_15_60_and_240_minutes_and_has_six_attempts_in_all()
    {
        Assert.Equal(6, EmailRetry.MaxAttempts);
        int[] waits = [1, 5, 15, 60, 240];
        for (var done = 1; done <= 5; done++)
        {
            Assert.Equal(Now.AddMinutes(waits[done - 1]), EmailRetry.Next(done, Now));
        }

        Assert.Null(EmailRetry.Next(6, Now));
        Assert.True(EmailRetry.IsPermanent(550));
        Assert.True(EmailRetry.IsPermanent(554));
        Assert.False(EmailRetry.IsPermanent(451));
        Assert.False(EmailRetry.IsPermanent(421));
    }

    [Fact]
    public void S111b2_KRN_WFL_08_GHR_06_the_email_carries_the_full_link_every_value_is_html_encoded_and_urdu_is_right_to_left()
    {
        var link = EmailLayout.Absolute("/approvals/task-1?x=1&y=2", "https://acme.erp.test");
        Assert.Equal("https://acme.erp.test/approvals/task-1?x=1&y=2", link);
        Assert.Null(EmailLayout.Absolute("https://evil.example/", "https://acme.erp.test"));
        Assert.Null(EmailLayout.Absolute("//evil.example/x", "https://acme.erp.test"));

        var html = EmailLayout.Html("<b>Hi</b>", "Line one\nline two\n\n<script>alert(1)</script>", link, "ur", Texts);
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Hi&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://acme.erp.test/approvals/task-1?x=1&amp;y=2\"", html, StringComparison.Ordinal);
        Assert.Contains("Line one<br>line two", html, StringComparison.Ordinal);
        Assert.Contains("dir=\"ltr\"", EmailLayout.Html("s", "b", null, "en", Texts), StringComparison.Ordinal);

        var text = EmailLayout.Text("Body", link, Texts);
        Assert.Equal("Body\n\nOpen: " + link + "\n\n--\nSent automatically.\n", text);
    }

    [Fact]
    public void S111b2_KRN_NTF_07_a_delivery_status_report_gives_the_message_and_the_reason_only_for_its_own_customer_and_not_when_merely_delayed()
    {
        var id = Guid.CreateVersion7();
        var messageId = EmailSendJob.MessageId(id, "acme", "no-reply@erp.test");
        Assert.Equal(id.ToString("N") + ".acme@erp.test", messageId);

        var bounce = BounceParser.Parse(Load(Report(messageId, "failed", "5.1.1", "smtp; 550 5.1.1 user unknown")), "acme");
        Assert.NotNull(bounce);
        Assert.Equal(id, bounce.MessageId);
        Assert.Equal("bounced: 5.1.1 smtp; 550 5.1.1 user unknown", bounce.Reason);

        Assert.Null(BounceParser.Parse(Load(Report(messageId, "failed", "5.1.1", "x")), "other"));
        Assert.Null(BounceParser.Parse(Load(Report(messageId, "delayed", "4.4.7", "x")), "acme"));
        Assert.Null(BounceParser.Parse(Load("From: a@b.test\r\nSubject: hello\r\n\r\nnot a bounce\r\n"), "acme"));

        // A plain returned message without a delivery-status part still counts, by our Message-ID in its text.
        var plain = BounceParser.Parse(Load($"From: postmaster@b.test\r\nSubject: Returned mail\r\n\r\nYour message <{messageId}> was returned.\r\n"), "acme");
        Assert.Equal("bounced: Returned mail", plain?.Reason);
    }

    private static MimeMessage Load(string raw) => MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));

    private static string Report(string messageId, string action, string status, string diagnostic) =>
        "From: MAILER-DAEMON@relay.test\r\nSubject: Undelivered\r\nMIME-Version: 1.0\r\n" +
        "Content-Type: multipart/report; report-type=delivery-status; boundary=\"b1\"\r\n\r\n" +
        "--b1\r\nContent-Type: text/plain\r\n\r\nNot delivered.\r\n\r\n" +
        "--b1\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; relay.test\r\n\r\n" +
        $"Final-Recipient: rfc822; x@mail.test\r\nAction: {action}\r\nStatus: {status}\r\nDiagnostic-Code: {diagnostic}\r\n\r\n" +
        $"--b1\r\nContent-Type: text/rfc822-headers\r\n\r\nMessage-ID: <{messageId}>\r\n\r\n--b1--\r\n";
}
