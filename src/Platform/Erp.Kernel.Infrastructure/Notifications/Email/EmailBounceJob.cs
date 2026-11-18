using System.Globalization;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications.Email;

/// <summary>A bounce found in the mailbox: the message it is about and the reason the receiving server gave.</summary>
public sealed record Bounce(Guid MessageId, string Reason);

/// <summary>
/// Reads bounce reports (KRN-NTF-07 "bounce handling", ruling Q10, plan point P6). A report is matched to its message by the
/// Message-ID the sender gave it (<c>&lt;message id.customer code@domain&gt;</c>), so in a mailbox shared by several
/// customers each customer's run takes only its own reports. A delivery-status report (RFC 3464) gives the status and the
/// server's words; a report that only delays the message is ignored.
/// </summary>
public static partial class BounceParser
{
    public static Bounce? Parse(MimeMessage message, string tenantCode)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(tenantCode);
        Guid? id = null;
        foreach (Match m in OurMessageId().Matches(message.ToString()))
        {
            if (string.Equals(m.Groups[2].Value, tenantCode, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(m.Groups[1].Value, "N", out var found))
            {
                id = found;
                break;
            }
        }

        if (id is null)
        {
            return null;
        }

        var report = message.BodyParts.OfType<MessageDeliveryStatus>().FirstOrDefault();
        if (report is null)
        {
            return new Bounce(id.Value, "bounced: " + Limit(message.Subject ?? "returned by the receiving mail server"));
        }

        foreach (var group in report.StatusGroups.Skip(1))
        {
            var action = group["Action"]?.Trim() ?? string.Empty;
            if (action.StartsWith("delayed", StringComparison.OrdinalIgnoreCase) || action.StartsWith("delivered", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("relayed", StringComparison.OrdinalIgnoreCase) || action.StartsWith("expanded", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var status = group["Status"]?.Trim();
            var diagnostic = group["Diagnostic-Code"]?.Trim();
            return new Bounce(id.Value, Limit(string.Create(CultureInfo.InvariantCulture, $"bounced: {status} {diagnostic}").Trim()));
        }

        return null;
    }

    private static string Limit(string text) => text.Length <= 1000 ? text : text[..1000];

    [GeneratedRegex(@"<?([0-9a-f]{32})\.([a-z0-9][a-z0-9-]*)@", RegexOptions.IgnoreCase)]
    private static partial Regex OurMessageId();
}

/// <summary>A mailbox of bounce reports: unread messages, and marking one read once handled.</summary>
public interface IBounceMailboxReader
{
    Task<IBounceSession> OpenAsync(BounceMailbox mailbox, CancellationToken cancellationToken);
}

public interface IBounceSession : IAsyncDisposable
{
    Task<IReadOnlyList<(string Key, MimeMessage Message)>> UnreadAsync(CancellationToken cancellationToken);

    Task MarkReadAsync(string key, CancellationToken cancellationToken);
}

/// <summary>The IMAP adapter of the bounce mailbox (MailKit, MIT).</summary>
public sealed class ImapBounceMailboxReader : IBounceMailboxReader
{
    public async Task<IBounceSession> OpenAsync(BounceMailbox mailbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        var client = new ImapClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(mailbox.Host, mailbox.Port, MailServerResolver.Socket(mailbox.Security), cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(mailbox.Username, mailbox.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
            var folder = string.Equals(mailbox.Folder, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(mailbox.Folder, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            return new Session(client, folder);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class Session(ImapClient client, IMailFolder folder) : IBounceSession
    {
        public async Task<IReadOnlyList<(string Key, MimeMessage Message)>> UnreadAsync(CancellationToken cancellationToken)
        {
            var list = new List<(string, MimeMessage)>();
            foreach (var uid in (await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false)).Take(500))
            {
                list.Add((uid.Id.ToString(CultureInfo.InvariantCulture), await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false)));
            }

            return list;
        }

        public Task MarkReadAsync(string key, CancellationToken cancellationToken) =>
            folder.StoreAsync(new UniqueId(uint.Parse(key, CultureInfo.InvariantCulture)), new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or ImapProtocolException or ImapCommandException)
            {
                // the connection is gone already
            }

            client.Dispose();
        }
    }
}

/// <summary>
/// The bounce job (<c>kernel.email_bounces</c>, every 15 minutes): reads the unread reports of the bounce mailbox the
/// operator set up, marks each sent message of this customer that came back as failed with the reason (and, for an audited
/// event, writes the result to the change log), and marks the report read. Reports of other customers stay unread.
/// </summary>
public sealed class EmailBounceJob(MailServerResolver servers, IBounceMailboxReader reader, NotificationCatalog catalog, IClock clock) : IJobHandler
{
    public const string JobCode = "kernel.email_bounces";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (await servers.BounceMailboxAsync(cancellationToken).ConfigureAwait(false) is not { } mailbox)
        {
            return; // no bounce mailbox: refusals at sending are still recorded (ruling Q10)
        }

        var tenantCode = job.Services.GetRequiredService<ITenantContext>().TenantCode;
        await using var c = (NpgsqlConnection)await job.Services.GetRequiredService<ITenantConnectionProvider>().DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var session = await reader.OpenAsync(mailbox, cancellationToken).ConfigureAwait(false);
        var marked = 0;
        foreach (var (key, message) in await session.UnreadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (BounceParser.Parse(message, tenantCode) is not { } bounce)
            {
                continue;
            }

            var now = clock.UtcNow;
            await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = (await NotificationConsumer.ReadAsync(c, tx, """
                update ntf.outbound_messages set status = 'failed', error = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1
                where id = $1 and status = 'sent' returning company_id, event_code, user_id
                """, r => (Company: r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), Code: r.IsDBNull(1) ? null : r.GetString(1), User: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2)),
                cancellationToken, bounce.MessageId, bounce.Reason, now, BaselineSeed.SystemUserId).ConfigureAwait(false)).FirstOrDefault();
            if (row.Code is { } code && catalog.Find(code) is { Audited: true })
            {
                await DeliveryAudit.RecordAsync(c, tx, bounce.MessageId, row.Company, code, row.User, DeliveryStatuses.Failed, bounce.Reason, job.RunId, now, cancellationToken,
                    before: DeliveryStatuses.Sent).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            await session.MarkReadAsync(key, cancellationToken).ConfigureAwait(false);
            marked++;
        }

        if (marked > 0)
        {
            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{marked} bounce report(s) handled"), cancellationToken).ConfigureAwait(false);
        }
    }
}
