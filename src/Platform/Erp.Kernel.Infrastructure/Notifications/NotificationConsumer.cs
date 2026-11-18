using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Notifications.Email;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications;

/// <summary>
/// The notification consumer (slice S1.11 Build items 8 to 11; KRN-NTF-02, KRN-NTF-03, KRN-NTF-05, HRM-NTF-02, HRM-NTF-03,
/// HRM-NTF-09, KRN-LOC-07, KRN-WFL-08). For each catalogue event an outbox event can give, it works out who is told
/// (the people the event names, plus the customer's rules whose event, company and condition fit), writes one in-app
/// item per person in the recipient's language, English when there is no text in it, and never twice: the item carries
/// the event id as its dedup key, so even a second dispatch of the same event adds nothing. It runs in the dispatcher's
/// transaction, so the items and the delivery record stand or fall together. In-app items are always delivered (ruling
/// Q11). From the same recipient list it queues one e-mail per person (slice S1.11b2 Build item 12), unless the person
/// switched the event group off; quiet hours hold it to their end; a mandatory event ignores both (ruling Q11). The e-mail
/// row carries the event id and person as its dedup key and the event's time as its row time, so it is written once too
/// (plan point P4). The <c>worker_stopped</c> alert is also e-mailed to the operator's address when one is set (P13).
/// </summary>
public sealed class NotificationConsumer(NotificationCatalog catalog, IClock clock, MailOptions mail) : IEventConsumer
{
    public const string ConsumerName = "ntf.notifications";

    public string Name => ConsumerName;

    public bool Accepts(string eventType) => catalog.Accepts(eventType);

    public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var c = (NpgsqlConnection)connection;
        var tx = (NpgsqlTransaction)transaction;
        var now = clock.UtcNow;
        foreach (var spec in catalog.ForSource(message.EventType))
        {
            var values = Values(spec, message.Payload);
            if (!Matches(spec, message.Payload, values))
            {
                continue;
            }

            var company = spec.CompanyProperty is { } cp && Text(message.Payload, cp) is { } cs && Guid.TryParse(cs, out var g) ? g : (Guid?)null;
            var mandatory = spec.Mandatory || MandatoryNow(spec, values);
            var recipients = await RecipientsAsync(c, tx, spec, message.Payload, values, company, mandatory, now, cancellationToken).ConfigureAwait(false);
            foreach (var recipient in recipients.Where(r => r.InApp))
            {
                await WriteItemAsync(c, tx, spec, message, values, recipient.UserId, recipient.Language, now, cancellationToken).ConfigureAwait(false);
            }

            foreach (var recipient in recipients.Where(r => r.Email))
            {
                await QueueEmailAsync(c, tx, spec, message, values, recipient, company, now, cancellationToken).ConfigureAwait(false);
            }

            if (spec.Code == OperatorAlert && mail.OperatorEmail is { Length: > 0 } operatorAddress)
            {
                await EmailOutbox.QueueAsync(c, tx, spec, new QueuedEmail(null, operatorAddress, spec.Code, company, "en", values, LinkOf(spec, message, values, "en"),
                    message.Id.ToString("N") + ":operator", message.OccurredAt, null), null, BaselineSeed.SystemUserId, now, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The alert the operator also hears about by e-mail (<c>Alerts:OperatorEmail</c>, plan point P13).</summary>
    public const string OperatorAlert = "alert.worker_stopped";

    private static async Task QueueEmailAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationEventSpec spec, OutboxEvent message, Dictionary<string, JsonElement> values,
        Recipient recipient, Guid? company, DateTimeOffset now, CancellationToken ct)
    {
        var person = (await ReadAsync(c, tx, "select email, time_zone from iam.users where id = $1", r => (Email: r.GetString(0), Zone: r.GetString(1)), ct, recipient.UserId).ConfigureAwait(false)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(person.Email))
        {
            return;
        }

        var (enabled, from, to) = await EmailOutbox.PreferencesAsync(c, tx, recipient.UserId, spec.Group, ct).ConfigureAwait(false);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(person.Zone, out var z) ? z : TimeZoneInfo.Utc;
        var (timing, sendAt) = NotificationPolicy.Email(recipient.Mandatory, enabled, now, zone, from, to);
        if (timing == EmailTiming.Skip)
        {
            return;
        }

        await EmailOutbox.QueueAsync(c, tx, spec, new QueuedEmail(recipient.UserId, person.Email, spec.Code, company, recipient.Language, values,
            LinkOf(spec, message, values, recipient.Language), message.Id.ToString("N") + ":" + recipient.UserId.ToString("N"), message.OccurredAt,
            timing == EmailTiming.Held ? sendAt : null), null, BaselineSeed.SystemUserId, now, ct).ConfigureAwait(false);
    }

    private static string? LinkOf(NotificationEventSpec spec, OutboxEvent message, Dictionary<string, JsonElement> values, string language) =>
        spec.LinkProperty is { } lp ? Text(message.Payload, lp) : spec.LinkTemplate is { } template ? NotificationText.Render(template, values, language) : null;

    /// <summary>The values a template or condition may use: the declared fields found in the payload (or in the object named by <c>fields_from</c>).</summary>
    public static Dictionary<string, JsonElement> Values(NotificationEventSpec spec, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement? source = spec.FieldsFrom is { } name && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : null;
        foreach (var field in spec.Fields)
        {
            if (source is { } s && s.TryGetProperty(field.Name, out var v) || payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field.Name, out v))
            {
                if (v.ValueKind != JsonValueKind.Null)
                {
                    values[field.Name] = v.Clone();
                }
            }
        }

        return values;
    }

    private static bool Matches(NotificationEventSpec spec, JsonElement payload, Dictionary<string, JsonElement> values)
    {
        foreach (var (property, expected) in spec.When)
        {
            var actual = Text(payload, property) ?? (values.TryGetValue(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                ?? (spec.FieldsFrom is { } from && payload.TryGetProperty(from, out var inner) ? Text(inner, property) : null);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MandatoryNow(NotificationEventSpec spec, Dictionary<string, JsonElement> values) =>
        spec.MandatoryWhen.Any(w => values.TryGetValue(w.Key, out var v) && v.ValueKind == JsonValueKind.String && w.Value.Contains(v.GetString()!, StringComparer.Ordinal));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>One person to tell: their language, whether they get the in-app item and whether the event is mandatory for them.</summary>
    public sealed record Recipient(Guid UserId, string Language, bool InApp, bool Email, bool Mandatory);

    /// <summary>
    /// The recipients of an event: the people the catalogue entry names, plus the customer's rules (event, company,
    /// condition). Only people who can sign in (active, not the system users) are told. The e-mail flag is the list part S1.11b2 sends from.
    /// </summary>
    public static async Task<IReadOnlyList<Recipient>> RecipientsAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationEventSpec spec, JsonElement payload,
        IReadOnlyDictionary<string, JsonElement> values, Guid? company, bool mandatory, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var wanted = new Dictionary<Guid, (bool InApp, bool Email, bool Mandatory)>();

        void Add(IEnumerable<Guid> users, bool inApp, bool email, bool isMandatory)
        {
            foreach (var id in users)
            {
                var (i, e, m) = wanted.GetValueOrDefault(id);
                wanted[id] = (i || inApp, e || email, m || isMandatory);
            }
        }

        // 1: the people the event names by itself (in-app and e-mail, ruling Q14).
        Add(spec.Audience.UserProperties.SelectMany(p => Guids(payload, p, spec.FieldsFrom)), true, true, mandatory);
        var roleCodes = spec.Audience.Roles.Concat(spec.Audience.RoleProperty is { } rp ? Strings(payload, rp) : []).Distinct(StringComparer.Ordinal).ToArray();
        if (roleCodes.Length > 0)
        {
            Add(await RoleHoldersAsync(c, tx, roleCodes, company, now, ct).ConfigureAwait(false), true, true, mandatory);
        }

        // 2: the customer's rules.
        var rules = await ReadAsync(c, tx, """
            select id, audience::text, channels, is_mandatory, condition::text from ntf.subscriptions
            where event_code = $1 and (company_id is null or company_id = $2)
            order by created_at, id
            """, r => (Id: r.GetGuid(0), Audience: r.GetString(1), Channels: r.GetFieldValue<string[]>(2), Mandatory: r.GetBoolean(3), Condition: r.IsDBNull(4) ? null : r.GetString(4)), ct, spec.Code, company).ConfigureAwait(false);
        foreach (var rule in rules)
        {
            if (rule.Condition is { } text)
            {
                using var doc = JsonDocument.Parse(text);
                var (condition, problems) = RouteFormat.ParseCondition(doc.RootElement, spec.RouteFields);
                if (condition is null || problems.Count > 0 || !ConditionEvaluator.Evaluate(condition, values, spec.RouteFields))
                {
                    continue;
                }
            }

            using var audience = JsonDocument.Parse(rule.Audience);
            var users = Guids(audience.RootElement, "users", null).ToList();
            var groups = Guids(audience.RootElement, "groups", null).ToArray();
            if (groups.Length > 0)
            {
                users.AddRange(await ReadAsync(c, tx, "select user_id from iam.user_group_members where group_id = any($1)", r => r.GetGuid(0), ct, groups).ConfigureAwait(false));
            }

            var roles = Strings(audience.RootElement, "roles").ToArray();
            if (roles.Length > 0)
            {
                users.AddRange(await RoleHoldersAsync(c, tx, roles, company, now, ct).ConfigureAwait(false));
            }

            // "line_manager" is a relationship HR provides (S1.20); until then it names nobody.
            Add(users, rule.Channels.Contains("in_app", StringComparer.Ordinal), rule.Channels.Contains("email", StringComparer.Ordinal), rule.Mandatory || mandatory);
        }

        if (wanted.Count == 0)
        {
            return [];
        }

        var people = await ReadAsync(c, tx, """
            select id, language from iam.users where id = any($1) and status = 'active' and not is_deleted and not is_system
            """, r => (Id: r.GetGuid(0), Language: r.GetString(1)), ct, wanted.Keys.ToArray()).ConfigureAwait(false);
        return [.. people.OrderBy(p => p.Id).Select(p => new Recipient(p.Id, p.Language, wanted[p.Id].InApp, wanted[p.Id].Email, wanted[p.Id].Mandatory))];
    }

    private static async Task<List<Guid>> RoleHoldersAsync(NpgsqlConnection c, NpgsqlTransaction tx, string[] roleCodes, Guid? company, DateTimeOffset now, CancellationToken ct) =>
        await ReadAsync(c, tx, """
            select ur.user_id from iam.user_roles ur join iam.roles r on r.id = ur.role_id
            where lower(r.code) = any($1) and r.is_active and not r.is_deleted and ur.valid_from <= $2 and (ur.valid_to is null or ur.valid_to > $2)
              and ($3::uuid is null or ur.company_id is null or ur.company_id = $3)
            union
            select m.user_id from iam.user_group_members m
            join iam.user_group_roles gr on gr.group_id = m.group_id join iam.roles r on r.id = gr.role_id
            where lower(r.code) = any($1) and r.is_active and not r.is_deleted and gr.valid_from <= $2 and (gr.valid_to is null or gr.valid_to > $2)
              and ($3::uuid is null or gr.company_id is null or gr.company_id = $3)
            """, r => r.GetGuid(0), ct, roleCodes.Select(x => x.ToLowerInvariant()).ToArray(), now, company).ConfigureAwait(false);

    private static async Task WriteItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, NotificationEventSpec spec, OutboxEvent message, Dictionary<string, JsonElement> values,
        Guid userId, string language, DateTimeOffset now, CancellationToken ct)
    {
        var (subject, body, used) = await NotificationTemplates.ActiveAsync(c, tx, spec.Code, "in_app", language, ct).ConfigureAwait(false) is { } t
            ? t
            : (spec.Texts[language == "ur" ? "ur" : "en"].Title, spec.Texts[language == "ur" ? "ur" : "en"].Body, language);
        var title = NotificationText.Render(subject, values, used);
        var text = NotificationText.Render(body, values, used);
        var link = LinkOf(spec, message, values, used);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into ntf.in_app_items (id, user_id, event_code, title, body, link, event_group, dedup_key, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10, 1)
            on conflict do nothing
            """, ct, Guid.CreateVersion7(now), userId, spec.Code, title, text, link, spec.Group, message.Id.ToString("N"), now, BaselineSeed.SystemUserId).ConfigureAwait(false);
    }

    private static IEnumerable<Guid> Guids(JsonElement payload, string property, string? from)
    {
        var element = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var v) ? v
            : from is not null && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(from, out var inner) && inner.ValueKind == JsonValueKind.Object && inner.TryGetProperty(property, out var w) ? w
            : default;
        return element.ValueKind switch
        {
            JsonValueKind.String when Guid.TryParse(element.GetString(), out var id) => [id],
            JsonValueKind.Array => [.. element.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Guid.TryParse(x.GetString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)],
            _ => [],
        };
    }

    private static IEnumerable<string> Strings(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];

    internal static async Task<List<T>> ReadAsync<T>(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, Func<NpgsqlDataReader, T> read, CancellationToken ct, params object?[] values)
    {
        await using var command = IdentityDb.Command(c, tx, sql, values);
        var list = new List<T>();
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(read(r));
        }

        return list;
    }
}

/// <summary>Template look-up shared by the consumer and the administration: the active text, in the recipient's language or in English.</summary>
public static class NotificationTemplates
{
    /// <returns>Subject, body and the language that was used; null when neither the language nor English has an active text.</returns>
    public static async Task<(string? Subject, string Body, string Language)?> ActiveAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string eventCode, string channel, string language, CancellationToken ct)
    {
        foreach (var candidate in language == "en" ? new[] { "en" } : [language, "en"])
        {
            var found = (await NotificationConsumer.ReadAsync(c, tx, """
                select subject, body from ntf.templates where event_code = $1 and channel = $2 and language = $3 and status = 'active' order by version desc limit 1
                """, r => (Subject: r.IsDBNull(0) ? null : r.GetString(0), Body: r.GetString(1)), ct, eventCode, channel, candidate).ConfigureAwait(false)).FirstOrDefault();
            if (found.Body is not null)
            {
                return (found.Subject, found.Body, candidate);
            }
        }

        return null;
    }
}
