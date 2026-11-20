using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Jobs;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications.Email;

/// <summary>
/// The e-mail sender (slice S1.11b2 Build items 12 and 14; KRN-NTF-05, KRN-NTF-07, HRM-NTF-16; plan points P3 to P7): every
/// minute it sends the queued messages that are due, at most <c>ntf.email.per_minute</c> a minute, each in its own short
/// transaction that locks the row, so a message is never sent twice by two runs. A temporary refusal is tried again after
/// 1, 5, 15, 60 and 240 minutes and then fails with the reason; a permanent refusal fails at once; a message that cannot be
/// built (no address, the person left) is <c>dead</c>. Delivery results of events marked audited go into the change log.
/// The secret of an identity e-mail is decrypted only here and wiped when the message ends.
/// </summary>
public sealed class EmailSendJob(MailServerResolver servers, IEmailTransport transport, NotificationCatalog catalog, ControlPlaneOptions controlPlane, IClock clock) : IJobHandler
{
    public const string JobCode = "kernel.email_send";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var tenantCode = job.Services.GetRequiredService<ITenantContext>().TenantCode;
        var outcome = await SendDueAsync(job.Services, tenantCode, job.RunId, cancellationToken).ConfigureAwait(false);
        if (outcome.ReplyToRefused)
        {
            await job.LogAsync("warning", "the reply-to address (ntf.email.reply_to) is not a valid single address; e-mails are sent without it", cancellationToken).ConfigureAwait(false);
        }

        if (outcome.NoServer)
        {
            await job.LogAsync("warning", "no mail server is set up for this customer (docs/runbooks/mail-setup.md); e-mails wait in the queue", cancellationToken).ConfigureAwait(false);
        }
        else if (outcome.Sent + outcome.Retried + outcome.Failed + outcome.Dead > 0)
        {
            await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{outcome.Sent} sent, {outcome.Retried} to try again, {outcome.Failed} failed, {outcome.Dead} could not be built"), cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed record Outcome(int Sent, int Retried, int Failed, int Dead, bool NoServer, bool ReplyToRefused = false);

    /// <summary>Identity e-mails sent in one run at most, outside the per-minute budget.</summary>
    public const int IdentityBatch = 100;

    private sealed record Row(Guid Id, DateTimeOffset CreatedAt, Guid? UserId, string Recipient, string? EventCode, Guid? CompanyId, string Payload, int Attempts, byte[]? Secret);

    public async Task<Outcome> SendDueAsync(IServiceProvider services, string tenantCode, Guid? runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var now = clock.UtcNow;
        await using var c = (NpgsqlConnection)await services.GetRequiredService<ITenantConnectionProvider>().DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var settings = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.NtfEmailPerMinute, KernelSettingKeys.NtfEmailSenderName, KernelSettingKeys.NtfEmailReplyTo], null, null, today, ct).ConfigureAwait(false);
        var perMinute = settings.TryGetValue(KernelSettingKeys.NtfEmailPerMinute, out var pm) && pm.Value.ValueKind == JsonValueKind.Number && pm.Value.TryGetInt32(out var n) && n > 0 ? n : 60;
        var senderName = settings.TryGetValue(KernelSettingKeys.NtfEmailSenderName, out var sn) && sn.Value.ValueKind == JsonValueKind.String ? sn.Value.GetString()! : string.Empty;
        var replyTo = settings.TryGetValue(KernelSettingKeys.NtfEmailReplyTo, out var rt) && rt.Value.ValueKind == JsonValueKind.String && rt.Value.GetString() is { Length: > 0 } reply ? reply : null;

        // A reply-to the mail library cannot use is left out rather than stopping every e-mail (S1.11b2 review finding 6).
        var replyToRefused = replyTo is not null && !(MailboxAddress.TryParse(replyTo, out var parsedReply) && string.Equals(parsedReply.Address, replyTo, StringComparison.Ordinal));
        if (replyToRefused)
        {
            replyTo = null;
        }

        // Rate limit (KRN-NTF-05, ruling Q10): what was sent in the last minute counts against this minute.
        var recent = await IdentityDb.ScalarAsync<long>(c, null, """
            select count(*) from ntf.outbound_messages where channel = 'email' and status = 'sent' and updated_at > $1 and created_at > $2
            """, ct, now.AddMinutes(-1), now.AddDays(-31)).ConfigureAwait(false);
        var budget = Math.Max(0, perMinute - (int)recent);

        // Identity e-mails (sign-in codes, reset and activation links, the e-mail change notice) go first and outside the
        // budget: a code lives 10 minutes and must not wait behind a burst of notifications (S1.11b2 review finding 4).
        var due = await NotificationConsumer.ReadAsync(c, null, """
            (select id, created_at from ntf.outbound_messages
             where status = 'queued' and channel = 'email' and (next_attempt_at is null or next_attempt_at <= $1) and event_code like 'identity.%'
             order by coalesce(next_attempt_at, created_at), id limit $3)
            union all
            (select id, created_at from ntf.outbound_messages
             where status = 'queued' and channel = 'email' and (next_attempt_at is null or next_attempt_at <= $1) and (event_code is null or event_code not like 'identity.%')
             order by coalesce(next_attempt_at, created_at), id limit $2)
            """, r => (Id: r.GetGuid(0), CreatedAt: r.GetFieldValue<DateTimeOffset>(1)), ct, now, budget, IdentityBatch).ConfigureAwait(false);
        if (due.Count == 0)
        {
            return new Outcome(0, 0, 0, 0, false, replyToRefused);
        }

        var server = await servers.ResolveAsync(c, tenantCode, ct).ConfigureAwait(false);
        if (server is null)
        {
            return new Outcome(0, 0, 0, 0, true, replyToRefused);
        }

        var host = await TenantHosts.PrimaryAsync(controlPlane, tenantCode, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Customer {tenantCode} has no host name.");
        var baseUrl = "https://" + host;
        var encryptor = await services.GetRequiredService<IFieldEncryptorProvider>().GetAsync(ct).ConfigureAwait(false);
        int sent = 0, retried = 0, failed = 0, dead = 0;
        await using var session = await transport.OpenAsync(server, ct).ConfigureAwait(false);
        foreach (var (id, createdAt) in due.Select(d => (d.Id, d.CreatedAt)))
        {
            await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            var row = (await NotificationConsumer.ReadAsync(c, tx, """
                select id, created_at, user_id, recipient, event_code, company_id, payload::text, attempts, secret from ntf.outbound_messages
                where id = $1 and created_at = $2 and status = 'queued' for update skip locked
                """, r => new Row(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetGuid(5), r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetFieldValue<byte[]>(8)), ct, id, createdAt).ConfigureAwait(false)).FirstOrDefault();
            if (row is null)
            {
                continue; // another run has it, or it changed since the list was read
            }

            var payload = JsonSerializer.Deserialize<EmailPayload>(row.Payload, EmailOutbox.Json)!;
            var recipient = await StillValidRecipientAsync(c, tx, row, ct).ConfigureAwait(false);
            if (recipient is null || !MailboxAddress.TryParse(recipient, out _))
            {
                await FinishAsync(c, tx, row, DeliveryStatuses.Dead, row.Attempts, null, "no valid e-mail address, or the person can no longer sign in", null, runId, now, ct).ConfigureAwait(false);
                dead++;
                await tx.CommitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var (subject, body, link) = (payload.Subject, payload.Body, payload.Link);
            if (row.Secret is { } secret)
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(encryptor.Decrypt(secret, EmailOutbox.SecretPurpose)))!;
                var map = values.Where(v => v.Key != "link").ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value), StringComparer.Ordinal);
                subject = NotificationText.Render(subject, map, payload.Language);
                body = NotificationText.Render(body, map, payload.Language);
                link = values.GetValueOrDefault("link") ?? link;
            }

            var absolute = EmailLayout.Absolute(link, baseUrl);
            var texts = LayoutTexts.For(payload.Language);
            var email = new OutgoingEmail(senderName, server.FromAddress, replyTo, recipient, subject, EmailLayout.Text(body, absolute, texts),
                EmailLayout.Html(subject, body, absolute, payload.Language, texts), MessageId(row.Id, tenantCode, server.FromAddress));
            var result = await session.SendAsync(email, ct).ConfigureAwait(false);
            var attempts = row.Attempts + 1;
            if (result.Sent)
            {
                await FinishAsync(c, tx, row, DeliveryStatuses.Sent, attempts, result.ProviderRef ?? email.MessageId, null, email.MessageId, runId, now, ct).ConfigureAwait(false);
                sent++;
            }
            else if (!result.Permanent && EmailRetry.Next(attempts, now) is { } next)
            {
                await IdentityDb.ExecuteAsync(c, tx, """
                    update ntf.outbound_messages set attempts = $3, next_attempt_at = $4, error = $5, updated_at = $6, updated_by = $7, row_version = row_version + 1
                    where id = $1 and created_at = $2
                    """, ct, row.Id, row.CreatedAt, attempts, next, result.Error, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
                retried++;
            }
            else
            {
                await FinishAsync(c, tx, row, DeliveryStatuses.Failed, attempts, null, result.Error, null, runId, now, ct).ConfigureAwait(false);
                failed++;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return new Outcome(sent, retried, failed, dead, false, replyToRefused);
    }

    /// <summary>
    /// The Message-ID of a message: its id and the customer code at the sender's domain, so a bounce finds its message, and
    /// its customer, again (plan point P6).
    /// </summary>
    public static string MessageId(Guid id, string tenantCode, string fromAddress)
    {
        ArgumentNullException.ThrowIfNull(fromAddress);
        var at = fromAddress.LastIndexOf('@');
        return id.ToString("N") + "." + tenantCode + "@" + (at >= 0 ? fromAddress[(at + 1)..] : "localhost");
    }

    /// <summary>The address to send to: the stored one, as long as a person's message still has an active account.</summary>
    private static async Task<string?> StillValidRecipientAsync(NpgsqlConnection c, NpgsqlTransaction tx, Row row, CancellationToken ct)
    {
        if (row.UserId is not { } user)
        {
            return string.IsNullOrWhiteSpace(row.Recipient) ? null : row.Recipient;
        }

        // An invitation goes to an invited person; a reset link also to an account under an automatic lock (it has an end
        // time: the rule of IdentityEmailService.MayReset, CR-049 (a); S1.11b2 review finding 3); the notice of an e-mail
        // change to an active or locked person; everything else to an active one.
        var ok = await IdentityDb.ScalarAsync<bool>(c, tx, """
            select exists (select 1 from iam.users u left join iam.user_credentials cr on cr.user_id = u.id
                           where u.id = $1 and not u.is_deleted and not u.is_system
                             and (u.status = 'active'
                                  or (u.status = 'invited' and $2 = 'identity.invitation')
                                  or (u.status = 'locked' and $2 = 'identity.password_reset' and cr.locked_until is not null)
                                  or (u.status = 'locked' and $2 = 'identity.email_changed')))
            """, ct, user, row.EventCode ?? string.Empty).ConfigureAwait(false);
        return ok && !string.IsNullOrWhiteSpace(row.Recipient) ? row.Recipient : null;
    }

    /// <summary>Ends a message (sent, failed or dead), wipes its secret and, for audited events, writes the delivery result to the change log.</summary>
    private async Task FinishAsync(NpgsqlConnection c, NpgsqlTransaction tx, Row row, string status, int attempts, string? providerRef, string? error, string? messageId, Guid? runId,
        DateTimeOffset now, CancellationToken ct)
    {
        await IdentityDb.ExecuteAsync(c, tx, """
            update ntf.outbound_messages set status = $3, attempts = $4, provider_ref = coalesce($5, provider_ref), error = $6, secret = null, next_attempt_at = null,
                updated_at = $7, updated_by = $8, row_version = row_version + 1
            where id = $1 and created_at = $2
            """, ct, row.Id, row.CreatedAt, status, attempts, providerRef ?? messageId, error, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
        if (status != DeliveryStatuses.Dead && row.EventCode is { } code && catalog.Find(code) is { Audited: true })
        {
            await DeliveryAudit.RecordAsync(c, tx, row.Id, row.CompanyId, code, row.UserId, status, error, runId, now, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The delivery audit (HRM-NTF-16, ruling Q12, plan point P7): one change-log entry per delivery result of an event marked
/// audited, with the time, the result and the reason, written by the job (source <c>job</c>). It joins the hash chain.
/// </summary>
public static class DeliveryAudit
{
    public static Task RecordAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid messageId, Guid? companyId, string eventCode, Guid? userId, string status, string? error, Guid? runId,
        DateTimeOffset now, CancellationToken ct, string before = DeliveryStatuses.Queued)
    {
        var changes = new JsonArray
        {
            new JsonObject { ["field"] = "status", ["before"] = before, ["after"] = status },
            new JsonObject { ["field"] = "event_code", ["after"] = eventCode },
            new JsonObject { ["field"] = "channel", ["after"] = "email" },
            new JsonObject { ["field"] = "user_id", ["after"] = userId?.ToString() },
        };
        if (error is not null)
        {
            changes.Add(new JsonObject { ["field"] = "error", ["after"] = error });
        }

        return AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, null, ActorKinds.Job, "ntf.outbound_messages", messageId, companyId, "status", changes,
            null, runId, null, AuditSources.Job, BaselineSeed.SystemUserId)], ct);
    }
}

/// <summary>The fixed words of notification e-mails per language, read from the notification localization files.</summary>
public static class LayoutTexts
{
    private static readonly Lazy<Dictionary<string, EmailLayoutTexts>> Texts = new(Load);

    public static EmailLayoutTexts For(string language) => Texts.Value.GetValueOrDefault(language) ?? Texts.Value["en"];

    private static Dictionary<string, EmailLayoutTexts> Load()
    {
        var map = new Dictionary<string, EmailLayoutTexts>(StringComparer.Ordinal);
        foreach (var language in new[] { "en", "ur" })
        {
            using var stream = typeof(LayoutTexts).Assembly.GetManifestResourceStream($"Erp.Kernel.Notifications.{language}.json")
                ?? throw new InvalidOperationException($"notifications.{language}.json is not embedded.");
            using var doc = JsonDocument.Parse(stream);
            map[language] = new EmailLayoutTexts(doc.RootElement.GetProperty("notifications.email.open_button").GetString()!, doc.RootElement.GetProperty("notifications.email.footer").GetString()!);
        }

        return map;
    }
}
