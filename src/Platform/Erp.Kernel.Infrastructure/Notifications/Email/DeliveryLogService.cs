using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Notifications;
using Erp.Kernel.Contracts.Notifications;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications.Email;

/// <summary>
/// The delivery log (KRN-NTF-05, ADM-OPS-04, ADM-OPS-05; slice S1.11b2 plan point P8): messages with status, attempts and
/// the failure reason, filtered by status, person, event, company, address and time; resend; the numbers of the operations
/// screen; and "test send to myself" of a text (KRN-NTF-02). Messages are scoped by company like other company data: a
/// message of a company needs a role covering it, one without a company (sign-in, licence) a role for all companies.
/// </summary>
public sealed class DeliveryLogService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess access,
    NotificationCatalog catalog,
    Erp.Kernel.Contracts.Settings.ISettings settings) : IDeliveryLog
{
    private const int MaxLimit = 200;

    public async Task<IReadOnlyList<DeliveryView>> ListAsync(DeliveryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await RequireAsync(NotificationPermissions.DeliveriesRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (all, companies) = await CoverageAsync(c, NotificationPermissions.DeliveriesRead, cancellationToken).ConfigureAwait(false);
        return await NotificationConsumer.ReadAsync(c, null, DeliveryLog.Select + """
            where ($1::text is null or m.status = $1) and ($2::uuid is null or m.user_id = $2) and ($3::text is null or m.event_code = $3)
              and ($4::uuid is null or m.company_id = $4) and ($5::text is null or lower(m.recipient) = lower($5))
              and ($6::timestamptz is null or m.created_at >= $6) and ($7::timestamptz is null or m.created_at < $7)
              and ($8 or m.company_id = any($9))
            order by m.created_at desc, m.id desc limit $10 offset $11
            """, DeliveryLog.Read, cancellationToken, query.Status, query.UserId, query.EventCode, query.CompanyId, query.Recipient, query.From, query.To, all, companies,
            Math.Clamp(query.Limit ?? 50, 1, MaxLimit), Math.Max(0, query.Offset ?? 0)).ConfigureAwait(false);
    }

    public async Task<DeliveryDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.DeliveriesRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = (await NotificationConsumer.ReadAsync(c, null, DeliveryLog.Select.Replace("from ntf.outbound_messages m", ", m.payload::text from ntf.outbound_messages m", StringComparison.Ordinal)
            + "where m.id = $1", r => (View: DeliveryLog.Read(r), Payload: r.GetString(DeliveryLog.Columns)), cancellationToken, id).ConfigureAwait(false)).SingleOrDefault();
        if (row.View is null || !await access.CoversCompanyAsync(NotificationPermissions.DeliveriesRead, row.View.CompanyId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<EmailPayload>(row.Payload, EmailOutbox.Json)!;
        // GHR-06: the text of identity e-mails (links, codes) and of salary messages is never shown in the log.
        return DeliveryLog.IsBodyWithheld(row.View.EventCode)
            ? new DeliveryDetail(row.View, string.Empty, null, payload.Language, payload.ResendOf, BodyWithheld: true)
            : new DeliveryDetail(row.View, payload.Body, payload.Link, payload.Language, payload.ResendOf);
    }

    public async Task<DeliveryView> ResendAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.DeliveriesResend, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var view = (await NotificationConsumer.ReadAsync(c, null, DeliveryLog.Select + "where m.id = $1", DeliveryLog.Read, cancellationToken, id).ConfigureAwait(false)).SingleOrDefault();
        if (view is null || !await access.CoversCompanyAsync(NotificationPermissions.DeliveriesResend, view.CompanyId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotificationException(NotificationErrors.NotFound, 404);
        }

        var copy = await DeliveryLog.ResendAsync(c, id, actor.UserId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return (await NotificationConsumer.ReadAsync(c, null, DeliveryLog.Select + "where m.id = $1", DeliveryLog.Read, cancellationToken, copy!.Value).ConfigureAwait(false)).Single();
    }

    public async Task<DeliverySummary> SummaryAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.DeliveriesRead, cancellationToken).ConfigureAwait(false);
        if (!await access.CoversCompanyAsync(NotificationPermissions.DeliveriesRead, null, cancellationToken).ConfigureAwait(false))
        {
            throw new NotificationException("errors.access.forbidden", 403);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var since = clock.UtcNow.AddHours(-24);
        return (await NotificationConsumer.ReadAsync(c, null, """
            select count(*) filter (where status = 'queued'), count(*) filter (where status = 'queued' and attempts > 0),
                   count(*) filter (where status = 'sent' and updated_at > $1), count(*) filter (where status = 'failed' and updated_at > $1),
                   count(*) filter (where status = 'dead' and updated_at > $1), min(created_at) filter (where status = 'queued')
            from ntf.outbound_messages
            """, r => new DeliverySummary((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)), cancellationToken, since).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<DeliveryDay>> DailyAsync(int? days, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.DeliveriesRead, cancellationToken).ConfigureAwait(false);
        if (!await access.CoversCompanyAsync(NotificationPermissions.DeliveriesRead, null, cancellationToken).ConfigureAwait(false))
        {
            throw new NotificationException("errors.access.forbidden", 403);
        }

        var count = Math.Clamp(days ?? 14, 1, 31);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var setting = (await settings.GetAsync("org.time_zone", Erp.Kernel.Contracts.Settings.SettingContext.Customer, cancellationToken).ConfigureAwait(false)).Value;
        var zone = setting.ValueKind == JsonValueKind.String && TimeZoneInfo.TryFindSystemTimeZoneById(setting.GetString()!, out _) ? setting.GetString()! : "Asia/Karachi";
        var since = clock.UtcNow.AddDays(-count);
        return await NotificationConsumer.ReadAsync(c, null, """
            select (m.updated_at at time zone $1)::date as day,
                   count(*) filter (where m.status = 'sent'), count(*) filter (where m.status = 'failed'), count(*) filter (where m.status = 'dead'),
                   count(*) filter (where m.status = 'queued')
            from ntf.outbound_messages m where m.updated_at >= $2 group by 1 order by 1 desc
            """, r => new DeliveryDay(DateOnly.FromDateTime(r.GetDateTime(0)), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4)),
            cancellationToken, zone, since).ConfigureAwait(false);
    }

    public async Task<DeliveryView> TestSendAsync(TestSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(NotificationPermissions.TemplatesTestSend, cancellationToken).ConfigureAwait(false);
        var spec = catalog.Find(request.EventCode) ?? throw new NotificationException(NotificationErrors.EventUnknown, 404);
        if (request.Language is not ("en" or "ur"))
        {
            throw new NotificationException(NotificationErrors.LanguageUnknown);
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new NotificationException(NotificationErrors.TemplateEmpty);
        }

        var problems = NotificationText.Check(request.Subject, spec.FieldMap, NotificationChannels.Email).Concat(NotificationText.Check(request.Body, spec.FieldMap, NotificationChannels.Email))
            .Distinct(StringComparer.Ordinal).ToList();
        if (problems.Count > 0)
        {
            throw new NotificationException(problems[0], 400, problems);
        }

        var now = clock.UtcNow;
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var email = await IdentityDb.ScalarAsync<string?>(c, null, "select email from iam.users where id = $1 and status = 'active'", cancellationToken, actor.UserId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new NotificationException(NotificationErrors.NoEmailAddress);
        }

        // Sample values only for the fields the event offers (the same values a preview uses).
        var values = (request.Sample ?? []).Where(v => spec.FieldMap.ContainsKey(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var payload = new EmailPayload("[TEST] " + NotificationText.Render(request.Subject ?? spec.Texts[request.Language].MailSubject, values, request.Language),
            NotificationText.Render(request.Body, values, request.Language), null, request.Language);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = await EmailOutbox.InsertAsync(c, tx, actor.UserId, email, spec.Code, null, null, payload, null, now, null, null, actor.UserId, now, cancellationToken).ConfigureAwait(false);
        await EmailOutbox.RequestSendSoonAsync(c, tx, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await NotificationConsumer.ReadAsync(c, null, DeliveryLog.Select + "where m.id = $1", DeliveryLog.Read, cancellationToken, id!.Value).ConfigureAwait(false)).Single();
    }

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new NotificationException("errors.access.forbidden", 403);
        }
    }

    /// <summary>Whether the caller sees every message, else the companies whose messages they see.</summary>
    private async Task<(bool All, Guid[] Companies)> CoverageAsync(NpgsqlConnection c, string permission, CancellationToken ct)
    {
        if (await access.CoversCompanyAsync(permission, null, ct).ConfigureAwait(false))
        {
            return (true, []);
        }

        var covered = new List<Guid>();
        foreach (var company in await NotificationConsumer.ReadAsync(c, null, "select id from org.companies", r => r.GetGuid(0), ct).ConfigureAwait(false))
        {
            if (await access.CoversCompanyAsync(permission, company, ct).ConfigureAwait(false))
            {
                covered.Add(company);
            }
        }

        return (false, [.. covered]);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
}

/// <summary>The SQL of the delivery log shared by the service, the resend and the tests.</summary>
public static class DeliveryLog
{
    internal const int Columns = 14;

    internal const string Select = """
        select m.id, m.created_at, m.channel, m.status, m.recipient, m.user_id, m.event_code, m.company_id, m.payload->>'subject', m.attempts, m.next_attempt_at,
               m.error, m.provider_ref, m.updated_at
        from ntf.outbound_messages m

        """;

    internal static DeliveryView Read(NpgsqlDataReader r)
    {
        var code = r.IsDBNull(6) ? null : r.GetString(6);
        return new DeliveryView(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5), code,
            r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? string.Empty : r.GetString(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
            r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), IsResendable(code));
    }

    /// <summary>Identity e-mails carried a link or code; they are never sent again (plan point P4): a new link is sent instead.</summary>
    /// <summary>The text of these messages is not shown in the log: identity e-mails carry links and codes, salary messages carry pay data (GHR-06).</summary>
    public static bool IsBodyWithheld(string? eventCode) =>
        eventCode is not null && (eventCode.StartsWith("identity.", StringComparison.Ordinal) || eventCode.StartsWith("payroll.", StringComparison.Ordinal)
            || eventCode.Contains(".payslip", StringComparison.Ordinal) || eventCode.Contains(".salary", StringComparison.Ordinal));

    public static bool IsResendable(string? eventCode) => eventCode is null || !eventCode.StartsWith("identity.", StringComparison.Ordinal);

    /// <summary>
    /// Queues a copy of a finished message as a new message (KRN-NTF-05 "resend"): the same text and person, the person's
    /// current address, no dedup key, sent soon. Throws <see cref="NotificationException"/> for an identity e-mail (400)
    /// or a message still waiting (409); null when the message does not exist.
    /// </summary>
    public static async Task<Guid?> ResendAsync(NpgsqlConnection c, Guid id, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = (await NotificationConsumer.ReadAsync(c, tx, """
            select m.status, m.event_code, m.user_id, coalesce(u.email, m.recipient), m.company_id, m.template_id, m.payload::text
            from ntf.outbound_messages m left join iam.users u on u.id = m.user_id where m.id = $1
            """, r => (Status: r.GetString(0), Code: r.IsDBNull(1) ? null : r.GetString(1), User: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), Recipient: r.GetString(3),
                Company: r.IsDBNull(4) ? (Guid?)null : r.GetGuid(4), Template: r.IsDBNull(5) ? (Guid?)null : r.GetGuid(5), Payload: r.GetString(6)), ct, id).ConfigureAwait(false)).SingleOrDefault();
        if (row.Status is null)
        {
            return null;
        }

        if (!IsResendable(row.Code))
        {
            throw new NotificationException(NotificationErrors.NotResendable, 400);
        }

        if (row.Status == DeliveryStatuses.Queued)
        {
            throw new NotificationException("errors.notification.delivery_waiting", 409);
        }

        var payload = JsonSerializer.Deserialize<EmailPayload>(row.Payload, EmailOutbox.Json)! with { ResendOf = id };
        var copy = await EmailOutbox.InsertAsync(c, tx, row.User, row.Recipient, row.Code, row.Company, row.Template, payload, null, now, null, null, actor, now, ct).ConfigureAwait(false);
        await EmailOutbox.RequestSendSoonAsync(c, tx, now, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return copy;
    }
}
