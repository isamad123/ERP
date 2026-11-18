using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Domain.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications.Email;

/// <summary>
/// How the worker reaches a mail server: host, port, transport security (<c>starttls</c>, the default, <c>ssl</c> or
/// <c>none</c>), the login (password from the secret store) and the sender address.
/// </summary>
public sealed record MailServer(string Host, int Port, string Security, string? Username, string? Password, string FromAddress);

/// <summary>A mailbox the worker reads bounce messages from (IMAP; ruling Q10).</summary>
public sealed record BounceMailbox(string Host, int Port, string Security, string Username, string? Password, string Folder);

/// <summary>
/// The operator's mail settings of the worker host (slice S1.11b2, plan points P2, P6 and P13; environment variables, see
/// docs/runbooks/mail-setup.md): <c>Mail:Smtp:Host</c>, <c>Mail:Smtp:Port</c> (587), <c>Mail:Smtp:Security</c>
/// (starttls), <c>Mail:Smtp:Username</c>, <c>Mail:Smtp:PasswordRef</c> (a secret-store reference), <c>Mail:From</c> (the
/// relay's sender address); <c>Mail:Bounce:Host</c>, <c>:Port</c> (993), <c>:Security</c> (ssl), <c>:Username</c>,
/// <c>:PasswordRef</c>, <c>:Folder</c> (INBOX); <c>Alerts:OperatorEmail</c>.
/// </summary>
public sealed record MailOptions(
    string? SmtpHost,
    int SmtpPort,
    string SmtpSecurity,
    string? SmtpUsername,
    string? SmtpPasswordRef,
    string? FromAddress,
    string? BounceHost,
    int BouncePort,
    string BounceSecurity,
    string? BounceUsername,
    string? BouncePasswordRef,
    string BounceFolder,
    string? OperatorEmail)
{
    public static MailOptions None { get; } = new(null, 587, "starttls", null, null, null, null, 993, "ssl", null, null, "INBOX", null);
}

/// <summary>
/// Which mail server sends a customer's e-mails (ruling Q10, plan point P2). The customer's own server when the operator
/// stored one in <c>itg.connectors</c> (kind <c>email</c>, provider <c>smtp</c>; on-premises), otherwise the operator's
/// relay; a relay connector (provider <c>relay</c>) only gives the sender address the operator checked for the customer's
/// domain. Null when neither exists: the e-mails then wait in the queue.
/// </summary>
public sealed class MailServerResolver(MailOptions options, ISecretStore secrets)
{
    public const string ConnectorCode = "email";

    public MailOptions Options => options;

    public async Task<MailServer?> ResolveAsync(NpgsqlConnection c, CancellationToken ct)
    {
        var connector = (await NotificationConsumer.ReadAsync(c, null, """
            select provider, config::text, credentials_ref from itg.connectors where kind = 'email' and status = 'active' and lower(code) = $1 limit 1
            """, r => (Provider: r.GetString(0), Config: r.GetString(1), Ref: r.IsDBNull(2) ? null : r.GetString(2)), ct, ConnectorCode).ConfigureAwait(false)).FirstOrDefault();
        string? from = null;
        if (connector.Provider is not null)
        {
            using var doc = JsonDocument.Parse(connector.Config);
            var config = doc.RootElement;
            from = Text(config, "from_address");
            if (connector.Provider == "smtp" && Text(config, "host") is { } host && from is not null)
            {
                var password = connector.Ref is null ? null : await secrets.GetAsync(connector.Ref, ct).ConfigureAwait(false);
                var port = config.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 587;
                return new MailServer(host, port, Text(config, "security") ?? "starttls", Text(config, "username"), password, from);
            }
        }

        if (options.SmtpHost is null || (from ?? options.FromAddress) is not { } sender)
        {
            return null;
        }

        var relayPassword = options.SmtpPasswordRef is null ? null : await secrets.GetAsync(options.SmtpPasswordRef, ct).ConfigureAwait(false);
        return new MailServer(options.SmtpHost, options.SmtpPort, options.SmtpSecurity, options.SmtpUsername, relayPassword, sender);
    }

    public async Task<BounceMailbox?> BounceMailboxAsync(CancellationToken ct)
    {
        if (options.BounceHost is null || options.BounceUsername is null)
        {
            return null;
        }

        var password = options.BouncePasswordRef is null ? null : await secrets.GetAsync(options.BouncePasswordRef, ct).ConfigureAwait(false);
        return new BounceMailbox(options.BounceHost, options.BouncePort, options.BounceSecurity, options.BounceUsername, password, options.BounceFolder);
    }

    internal static SecureSocketOptions Socket(string security) => security switch
    {
        "ssl" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}

/// <summary>One e-mail ready to send.</summary>
public sealed record OutgoingEmail(string FromName, string FromAddress, string? ReplyTo, string To, string Subject, string Text, string Html, string MessageId);

/// <summary>The mail server's answer: sent (with its reference), refused for now (try again) or refused for good.</summary>
public sealed record EmailSendResult(bool Sent, bool Permanent, string? Error, string? ProviderRef);

/// <summary>
/// The provider contract of e-mail (KRN-NTF-01, HRM-NTF-08): business code never names a provider, it queues messages; the
/// sender job hands them to this adapter. One session sends a batch over one connection.
/// </summary>
public interface IEmailTransport
{
    Task<IEmailSession> OpenAsync(MailServer server, CancellationToken cancellationToken);
}

public interface IEmailSession : IAsyncDisposable
{
    Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

/// <summary>The SMTP adapter (MailKit, MIT; ruling Q10).</summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    public Task<IEmailSession> OpenAsync(MailServer server, CancellationToken cancellationToken) =>
        Task.FromResult<IEmailSession>(new Session(server));

    private sealed class Session(MailServer server) : IEmailSession
    {
        private readonly SmtpClient _client = new() { Timeout = 30_000 };

        public async Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(server.Host, server.Port, MailServerResolver.Socket(server.Security), cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(server.Username))
                    {
                        await _client.AuthenticateAsync(server.Username, server.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    }
                }

                using var message = Build(email);
                var reply = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                return new EmailSendResult(true, false, null, Limit(reply));
            }
            catch (SmtpCommandException e)
            {
                var code = (int)e.StatusCode;
                return new EmailSendResult(false, EmailRetry.IsPermanent(code), Limit(string.Create(CultureInfo.InvariantCulture, $"{code} {e.Message}")), null);
            }
            catch (Exception e) when (e is SmtpProtocolException or IOException or SocketException or ServiceNotConnectedException or AuthenticationException or TimeoutException)
            {
                // The connection itself failed: nothing was refused, so the message is tried again later.
                await DisconnectAsync().ConfigureAwait(false);
                return new EmailSendResult(false, false, Limit(e.GetType().Name + ": " + e.Message), null);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _client.Dispose();
        }

        private async Task DisconnectAsync()
        {
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or SmtpProtocolException or SmtpCommandException)
            {
                // the connection is gone already
            }
        }

        private static MimeMessage Build(OutgoingEmail email)
        {
            var message = new MimeMessage { Subject = email.Subject, MessageId = email.MessageId };
            message.From.Add(new MailboxAddress(email.FromName, email.FromAddress));
            message.To.Add(MailboxAddress.Parse(email.To));
            if (!string.IsNullOrEmpty(email.ReplyTo))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(email.ReplyTo));
            }

            message.Body = new BodyBuilder { TextBody = email.Text, HtmlBody = email.Html }.ToMessageBody();
            return message;
        }
    }

    internal static string? Limit(string? text) => text is null ? null : text.Length <= 1000 ? text : text[..1000];
}
