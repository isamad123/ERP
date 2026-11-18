using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications.Email;

/// <summary>
/// One e-mail to put in the queue (slice S1.11b2 Build item 12). <see cref="Values"/> are the event's template values;
/// <see cref="Secret"/> holds the link or code of an identity e-mail, which is stored encrypted, filled into the text only
/// when the e-mail is sent, and wiped once the message is sent or ends (plan points P1 and P5). <see cref="CreatedAt"/> is
/// the row time: the event's time for notifications, so the unique (dedup key, time) holds when the same event is handled
/// twice (plan point P4). <see cref="SendAt"/>: not before this time (quiet hours), or null for at once.
/// </summary>
public sealed record QueuedEmail(
    Guid? UserId,
    string Recipient,
    string EventCode,
    Guid? CompanyId,
    string Language,
    IReadOnlyDictionary<string, JsonElement> Values,
    string? Link,
    string? DedupKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SendAt,
    IReadOnlyDictionary<string, string>? Secret = null);

/// <summary>What a queued message holds in <c>payload</c>: the rendered subject and body, the relative deep link and the language.</summary>
public sealed record EmailPayload(string Subject, string Body, string? Link, string Language, Guid? ResendOf = null);

/// <summary>Writes rows of <c>ntf.outbound_messages</c> in the caller's transaction (the dispatcher's, or an identity service's).</summary>
public static class EmailOutbox
{
    public const string SecretPurpose = "ntf.outbound_messages.secret";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <returns>The new message id, or null when a message with the same dedup key and time already exists.</returns>
    public static async Task<Guid?> QueueAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationEventSpec spec, QueuedEmail message, IFieldEncryptor? encryptor,
        Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(message);
        var values = new Dictionary<string, JsonElement>(message.Values, StringComparer.Ordinal);
        if (message.Secret is { Count: > 0 })
        {
            // A secret value stays a placeholder in the stored text; the sender fills it in from the encrypted secret.
            foreach (var key in message.Secret.Keys)
            {
                values[key] = JsonSerializer.SerializeToElement("{" + key + "}");
            }
        }

        var template = await TextAsync(c, tx, spec, message.Language, ct).ConfigureAwait(false);
        var payload = new EmailPayload(
            NotificationText.Render(template.Subject, values, template.Language),
            NotificationText.Render(template.Body, values, template.Language),
            message.Link,
            template.Language);
        byte[]? secret = null;
        if (message.Secret is { Count: > 0 })
        {
            secret = (encryptor ?? throw new InvalidOperationException("An e-mail with a secret needs the customer's field encryptor."))
                .Encrypt(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message.Secret)), SecretPurpose);
        }

        return await InsertAsync(c, tx, message.UserId, message.Recipient, spec.Code, message.CompanyId, template.Id, payload, message.DedupKey, message.CreatedAt, message.SendAt, secret, actor, now, ct).ConfigureAwait(false);
    }

    internal static async Task<Guid?> InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid? userId, string recipient, string? eventCode, Guid? companyId, Guid? templateId,
        EmailPayload payload, string? dedupKey, DateTimeOffset createdAt, DateTimeOffset? sendAt, byte[]? secret, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7(now);
        var inserted = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
            insert into ntf.outbound_messages (id, created_at, channel, recipient, template_id, payload, dedup_key, attempts, next_attempt_at, status,
                user_id, event_code, company_id, secret, created_by, updated_at, updated_by, row_version)
            values ($1, $2, 'email', $3, $4, $5, $6, 0, $7, 'queued', $8, $9, $10, $11, $12, $13, $12, 1)
            on conflict do nothing
            returning id
            """, ct, id, createdAt, recipient, templateId, new JsonPayload(JsonSerializer.Serialize(payload, Json)), dedupKey, sendAt, userId, eventCode, companyId, secret, actor, now).ConfigureAwait(false);
        return inserted;
    }

    /// <summary>
    /// Asks for a run of the sender job now (sign-in links and codes, resend, test send), unless one is already waiting, so
    /// such an e-mail leaves within seconds instead of at the next minute (plan point P3).
    /// </summary>
    public static Task RequestSendSoonAsync(NpgsqlConnection c, NpgsqlTransaction tx, DateTimeOffset now, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            insert into job.runs (id, definition_id, status, priority, attempt, parameters, created_at, created_by, updated_at, updated_by, row_version)
            select $1, d.id, 'queued', d.priority, 0, '{}'::jsonb, $2, $3, $2, $3, 1 from job.definitions d
            where d.code = $4 and not exists (select 1 from job.runs w where w.definition_id = d.id and w.status = 'queued')
            """, ct, Guid.CreateVersion7(now), now, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId, EmailSendJob.JobCode);

    /// <summary>The active e-mail text in the language (English when there is none), or the catalogue's product text.</summary>
    internal static async Task<(Guid? Id, string? Subject, string Body, string Language)> TextAsync(NpgsqlConnection c, NpgsqlTransaction? tx, NotificationEventSpec spec, string language, CancellationToken ct)
    {
        foreach (var candidate in language == "en" ? new[] { "en" } : [language, "en"])
        {
            var found = (await NotificationConsumer.ReadAsync(c, tx, """
                select id, subject, body from ntf.templates where event_code = $1 and channel = 'email' and language = $2 and status = 'active' order by version desc limit 1
                """, r => (Id: r.GetGuid(0), Subject: r.IsDBNull(1) ? null : r.GetString(1), Body: r.GetString(2)), ct, spec.Code, candidate).ConfigureAwait(false)).FirstOrDefault();
            if (found.Body is not null)
            {
                return (found.Id, found.Subject, found.Body, candidate);
            }
        }

        var text = spec.Texts[language == "ur" ? "ur" : "en"];
        return (null, text.MailSubject, text.MailBody, language == "ur" ? "ur" : "en");
    }

    /// <summary>The person's e-mail choices for one event group: group switched on (no row = on) and quiet hours, if set.</summary>
    internal static async Task<(bool GroupEnabled, TimeOnly? QuietFrom, TimeOnly? QuietTo)> PreferencesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, string group, CancellationToken ct)
    {
        var rows = await NotificationConsumer.ReadAsync(c, tx, """
            select event_group, enabled, quiet_hours::text from ntf.preferences where user_id = $1 and channel = 'email' and event_group in ($2, $3)
            """, r => (Group: r.GetString(0), Enabled: r.GetBoolean(1), Quiet: r.IsDBNull(2) ? null : r.GetString(2)), ct, userId, group, NotificationCentreService.QuietGroup).ConfigureAwait(false);
        var enabled = rows.FirstOrDefault(r => r.Group == group) is not { Group: not null } row || row.Enabled;
        if (rows.FirstOrDefault(r => r.Group == NotificationCentreService.QuietGroup && r.Quiet is not null).Quiet is { } text)
        {
            using var doc = JsonDocument.Parse(text);
            var q = doc.RootElement;
            if (q.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True
                && NotificationText.TryTime(q.GetProperty("from").GetString(), out var from) && NotificationText.TryTime(q.GetProperty("to").GetString(), out var to))
            {
                return (enabled, from, to);
            }
        }

        return (enabled, null, null);
    }
}
