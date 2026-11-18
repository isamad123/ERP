using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Notifications;
using Erp.Kernel.Contracts.Notifications;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications;

/// <summary>
/// Administration of notifications (KRN-NTF-02, KRN-NTF-03, HRM-NTF-02, HRM-NTF-03, HRM-NTF-14, GHR-06; slice S1.11 Build
/// items 8 to 10): the catalogue of events and the fields each offers, the texts per event, channel and language (each
/// customer edit a new version, "back to product text" possible, preview), and the audience rules. Every text is checked
/// before it is stored: a placeholder the event does not offer is refused, so is any salary-class field, and a
/// personal-class field in e-mail (ruling Q13). Every change is in the audit log.
/// </summary>
public sealed class NotificationAdminService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess access,
    NotificationCatalog catalog,
    IAuditContext audit) : INotificationAdmin
{
    private static readonly string[] Languages = ["en", "ur"];
    private static readonly string[] Relationships = ["line_manager"];

    public async Task<IReadOnlyList<CatalogueEvent>> CatalogueAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.CatalogueRead, cancellationToken).ConfigureAwait(false);
        return [.. catalog.All.OrderBy(e => e.Code, StringComparer.Ordinal).Select(e => new CatalogueEvent(e.Code, e.Module, e.Group, e.Mandatory || e.MandatoryWhen.Count > 0, e.Audited,
            [.. e.Fields.Select(f => new CatalogueField(f.Name, f.Type, f.FieldClass))], e.LinkProperty ?? e.LinkTemplate))];
    }

    public async Task<IReadOnlyList<TemplateView>> TemplatesAsync(string? eventCode, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.TemplatesRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await NotificationConsumer.ReadAsync(c, null, """
            select id, event_code, channel, language, subject, body, version, source, status, row_version from ntf.templates
            where ($1::text is null or event_code = $1) order by event_code, channel, language, version desc
            """, r => new TemplateView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetInt64(9)),
            cancellationToken, eventCode).ConfigureAwait(false);
    }

    public async Task<TemplateView> EditTemplateAsync(TemplateEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await RequireAsync(NotificationPermissions.TemplatesCreate, cancellationToken).ConfigureAwait(false);
        CheckText(edit.EventCode, edit.Channel, edit.Language, edit.Subject, edit.Body);
        var now = clock.UtcNow;
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('ntf.templates'))", cancellationToken).ConfigureAwait(false);
        var version = 1 + await IdentityDb.ScalarAsync<int>(c, tx, "select coalesce(max(version), 0) from ntf.templates where event_code = $1 and channel = $2 and language = $3", cancellationToken,
            edit.EventCode, edit.Channel, edit.Language).ConfigureAwait(false);
        var retired = await NotificationConsumer.ReadAsync(c, tx, """
            update ntf.templates set status = 'retired', updated_at = $4, updated_by = $5, row_version = row_version + 1
            where event_code = $1 and channel = $2 and language = $3 and status = 'active' returning id, version
            """, r => (Id: r.GetGuid(0), Version: r.GetInt32(1)), cancellationToken, edit.EventCode, edit.Channel, edit.Language, now, actor.UserId).ConfigureAwait(false);
        var id = Guid.CreateVersion7(now);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into ntf.templates (id, event_code, channel, language, subject, body, version, source, status, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, 'tenant', 'active', $8, $9, $8, $9, 1)
            """, cancellationToken, id, edit.EventCode, edit.Channel, edit.Language, edit.Subject?.Trim(), edit.Body.Trim(), version, now, actor.UserId).ConfigureAwait(false);
        var entries = retired.Select(r => Change("ntf.templates", r.Id, "update", new JsonObject { ["field"] = "status", ["before"] = "active", ["after"] = "retired" }, now)).ToList();
        entries.Add(Change("ntf.templates", id, "create", new JsonObject { ["field"] = "version", ["before"] = null, ["after"] = version }, now));
        await AuditDb.AppendChangesAsync(c, tx, entries, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(c, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateView> RestoreProductTextAsync(string eventCode, string channel, string language, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.TemplatesCreate, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('ntf.templates'))", cancellationToken).ConfigureAwait(false);
        var product = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select id from ntf.templates where event_code = $1 and channel = $2 and language = $3 and source = 'product' order by version limit 1",
            cancellationToken, eventCode, channel, language).ConfigureAwait(false);
        if (product is not { } productId)
        {
            throw new NotificationException(NotificationErrors.NoProductText, 404);
        }

        var changed = await NotificationConsumer.ReadAsync(c, tx, """
            update ntf.templates set status = case when id = $4 then 'active' else 'retired' end, updated_at = $5, updated_by = $6, row_version = row_version + 1
            where event_code = $1 and channel = $2 and language = $3 and ((id = $4 and status <> 'active') or (id <> $4 and status = 'active')) returning id, status
            """, r => (Id: r.GetGuid(0), Status: r.GetString(1)), cancellationToken, eventCode, channel, language, productId, now, actor.UserId).ConfigureAwait(false);
        await AuditDb.AppendChangesAsync(c, tx, [.. changed.Select(x => Change("ntf.templates", x.Id, "update",
            new JsonObject { ["field"] = "status", ["before"] = x.Status == "active" ? "retired" : "active", ["after"] = x.Status }, now))], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(c, productId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplatePreview> PreviewAsync(TemplatePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(NotificationPermissions.TemplatesRead, cancellationToken).ConfigureAwait(false);
        CheckText(request.EventCode, request.Channel, request.Language, request.Subject, request.Body);
        var spec = catalog.Find(request.EventCode)!;
        var values = Sample(spec, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        foreach (var (name, value) in request.Sample ?? [])
        {
            if (spec.FieldMap.ContainsKey(name) && spec.FieldMap[name].FieldClass == "general")
            {
                values[name] = value;
            }
        }

        return new TemplatePreview(request.Subject is null ? null : NotificationText.Render(request.Subject, values, request.Language), NotificationText.Render(request.Body, values, request.Language));
    }

    public async Task<IReadOnlyList<SubscriptionView>> SubscriptionsAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.SubscriptionsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var views = new List<SubscriptionView>();
        foreach (var row in await NotificationConsumer.ReadAsync(c, null, "select id from ntf.subscriptions order by created_at, id", r => r.GetGuid(0), cancellationToken).ConfigureAwait(false))
        {
            views.Add(await SubscriptionAsync(c, row, cancellationToken).ConfigureAwait(false) ?? throw new NotificationException(NotificationErrors.NotFound, 404));
        }

        return views;
    }

    public async Task<SubscriptionView> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(NotificationPermissions.SubscriptionsCreate, cancellationToken).ConfigureAwait(false);
        await CheckCompanyAsync(NotificationPermissions.SubscriptionsCreate, request.CompanyId, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (audience, condition) = await ValidateAsync(c, request, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into ntf.subscriptions (id, event_code, audience, channels, company_id, is_mandatory, name, condition, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $9, $10, 1)
            """, cancellationToken, id, request.EventCode, new JsonPayload(audience), request.Channels.Distinct(StringComparer.Ordinal).ToArray(), request.CompanyId, request.Mandatory,
            request.Name?.Trim(), condition is null ? null : new JsonPayload(condition), now, actor.UserId).ConfigureAwait(false);
        await AuditDb.AppendChangesAsync(c, tx, [Change("ntf.subscriptions", id, "create", new JsonObject { ["field"] = "event_code", ["before"] = null, ["after"] = request.EventCode }, now, request.CompanyId)], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await SubscriptionAsync(c, id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<SubscriptionView> UpdateSubscriptionAsync(Guid id, SubscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(NotificationPermissions.SubscriptionsUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = await SubscriptionAsync(c, id, cancellationToken).ConfigureAwait(false) ?? throw new NotificationException(NotificationErrors.NotFound, 404);
        await CheckCompanyAsync(NotificationPermissions.SubscriptionsUpdate, current.CompanyId, cancellationToken).ConfigureAwait(false);
        await CheckCompanyAsync(NotificationPermissions.SubscriptionsUpdate, request.CompanyId, cancellationToken).ConfigureAwait(false);
        var (audience, condition) = await ValidateAsync(c, request, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var changed = await IdentityDb.ExecuteAsync(c, tx, """
            update ntf.subscriptions set event_code = $2, audience = $3, channels = $4, company_id = $5, is_mandatory = $6, name = $7, condition = $8,
                updated_at = $9, updated_by = $10, row_version = row_version + 1
            where id = $1 and row_version = $11
            """, cancellationToken, id, request.EventCode, new JsonPayload(audience), request.Channels.Distinct(StringComparer.Ordinal).ToArray(), request.CompanyId, request.Mandatory,
            request.Name?.Trim(), condition is null ? null : new JsonPayload(condition), now, actor.UserId, request.RowVersion ?? -1).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new NotificationException(NotificationErrors.Changed, 409);
        }

        await AuditDb.AppendChangesAsync(c, tx, [Change("ntf.subscriptions", id, "update", new JsonObject { ["field"] = "rule", ["before"] = current.EventCode, ["after"] = request.EventCode }, now, request.CompanyId)], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await SubscriptionAsync(c, id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task DeleteSubscriptionAsync(Guid id, long rowVersion, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.SubscriptionsDelete, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = await SubscriptionAsync(c, id, cancellationToken).ConfigureAwait(false) ?? throw new NotificationException(NotificationErrors.NotFound, 404);
        await CheckCompanyAsync(NotificationPermissions.SubscriptionsDelete, current.CompanyId, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await IdentityDb.ExecuteAsync(c, tx, "delete from ntf.subscriptions where id = $1 and row_version = $2", cancellationToken, id, rowVersion).ConfigureAwait(false) == 0)
        {
            throw new NotificationException(NotificationErrors.Changed, 409);
        }

        await AuditDb.AppendChangesAsync(c, tx, [Change("ntf.subscriptions", id, "delete", new JsonObject { ["field"] = "event_code", ["before"] = current.EventCode, ["after"] = null }, now, current.CompanyId)], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The checks of a text before it is stored or previewed (the same for every language and channel).</summary>
    private void CheckText(string eventCode, string channel, string language, string? subject, string body)
    {
        if (catalog.Find(eventCode) is not { } spec)
        {
            throw new NotificationException(NotificationErrors.EventUnknown, 404);
        }

        if (!NotificationChannels.All.Contains(channel, StringComparer.Ordinal))
        {
            throw new NotificationException(NotificationErrors.ChannelUnknown);
        }

        if (!Languages.Contains(language, StringComparer.Ordinal))
        {
            throw new NotificationException(NotificationErrors.LanguageUnknown);
        }

        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(subject))
        {
            throw new NotificationException(NotificationErrors.TemplateEmpty);
        }

        var problems = NotificationText.Check(subject, spec.FieldMap, channel).Concat(NotificationText.Check(body, spec.FieldMap, channel)).Distinct(StringComparer.Ordinal).ToList();
        if (problems.Count > 0)
        {
            throw new NotificationException(problems[0], 400, problems);
        }
    }

    private static Dictionary<string, JsonElement> Sample(NotificationEventSpec spec, DateOnly today)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in spec.Fields.Where(f => f.FieldClass == "general"))
        {
            var json = field.Type switch
            {
                FieldTypes.Number or FieldTypes.Money => "1250",
                FieldTypes.Date => $"\"{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\"",
                FieldTypes.Bool => "true",
                FieldTypes.Choice when spec.Choices.TryGetValue(field.Name, out var choices) && choices.Count > 0 => JsonSerializer.Serialize(choices[0]),
                _ => "\"Sample\"",
            };
            using var doc = JsonDocument.Parse(json);
            values[field.Name] = doc.RootElement.Clone();
        }

        return values;
    }

    private async Task<(string Audience, string? Condition)> ValidateAsync(NpgsqlConnection c, SubscriptionRequest request, CancellationToken ct)
    {
        if (catalog.Find(request.EventCode) is not { } spec)
        {
            throw new NotificationException(NotificationErrors.EventUnknown, 404);
        }

        if (request.Channels is not { Count: > 0 } || request.Channels.Any(x => !NotificationChannels.All.Contains(x, StringComparer.Ordinal)))
        {
            throw new NotificationException(NotificationErrors.ChannelUnknown);
        }

        var a = request.Audience ?? throw new NotificationException(NotificationErrors.AudienceEmpty);
        var users = a.Users?.Distinct().ToArray() ?? [];
        var roles = a.Roles?.Select(r => r.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var groups = a.Groups?.Distinct().ToArray() ?? [];
        var relationships = a.Relationships?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (users.Length + roles.Length + groups.Length + relationships.Length == 0)
        {
            throw new NotificationException(NotificationErrors.AudienceEmpty);
        }

        if (relationships.Any(r => !Relationships.Contains(r, StringComparer.Ordinal))
            || (users.Length > 0 && await IdentityDb.ScalarAsync<long>(c, null, "select count(*) from iam.users where id = any($1) and not is_deleted and not is_system", ct, users).ConfigureAwait(false) != users.Length)
            || (roles.Length > 0 && await IdentityDb.ScalarAsync<long>(c, null, "select count(*) from iam.roles where lower(code) = any($1) and not is_deleted", ct, (object)roles).ConfigureAwait(false) != roles.Length)
            || (groups.Length > 0 && await IdentityDb.ScalarAsync<long>(c, null, "select count(*) from iam.user_groups where id = any($1) and not is_deleted", ct, groups).ConfigureAwait(false) != groups.Length))
        {
            throw new NotificationException(NotificationErrors.AudienceUnknown);
        }

        string? condition = null;
        if (request.Condition is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given)
        {
            var (parsed, problems) = RouteFormat.ParseCondition(given, spec.RouteFields);
            if (parsed is null)
            {
                throw new NotificationException(NotificationErrors.ConditionInvalid, 400, [.. problems.Select(p => $"{p.Path}: {p.ErrorKey}")]);
            }

            condition = given.GetRawText();
        }

        var audience = JsonSerializer.Serialize(new { users, roles, groups, relationships });
        return (audience, condition);
    }

    private static async Task<SubscriptionView?> SubscriptionAsync(NpgsqlConnection c, Guid id, CancellationToken ct)
    {
        var row = (await NotificationConsumer.ReadAsync(c, null, "select id, name, event_code, company_id, condition::text, audience::text, channels, is_mandatory, row_version from ntf.subscriptions where id = $1",
            r => (Id: r.GetGuid(0), Name: r.IsDBNull(1) ? null : r.GetString(1), Event: r.GetString(2), Company: r.IsDBNull(3) ? (Guid?)null : r.GetGuid(3), Condition: r.IsDBNull(4) ? null : r.GetString(4),
                Audience: r.GetString(5), Channels: r.GetFieldValue<string[]>(6), Mandatory: r.GetBoolean(7), Version: r.GetInt64(8)), ct, id).ConfigureAwait(false)).FirstOrDefault();
        if (row.Event is null)
        {
            return null;
        }

        using var audience = JsonDocument.Parse(row.Audience);
        JsonElement? condition = null;
        if (row.Condition is { } text)
        {
            using var doc = JsonDocument.Parse(text);
            condition = doc.RootElement.Clone();
        }

        return new SubscriptionView(row.Id, row.Name, row.Event, row.Company, condition,
            new SubscriptionAudience(Ids(audience.RootElement, "users"), Names(audience.RootElement, "roles"), Ids(audience.RootElement, "groups"), Names(audience.RootElement, "relationships")),
            row.Channels, row.Mandatory, row.Version);
    }

    private static List<Guid> Ids(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? [.. v.EnumerateArray().Select(x => x.GetGuid())] : [];

    private static List<string> Names(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? [.. v.EnumerateArray().Select(x => x.GetString()!)] : [];

    private static async Task<TemplateView> ViewAsync(NpgsqlConnection c, Guid id, CancellationToken ct) =>
        (await NotificationConsumer.ReadAsync(c, null, "select id, event_code, channel, language, subject, body, version, source, status, row_version from ntf.templates where id = $1",
            r => new TemplateView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetInt64(9)), ct, id).ConfigureAwait(false)).Single();

    private ChangeEntry Change(string table, Guid id, string action, JsonObject change, DateTimeOffset now, Guid? company = null) =>
        new(Guid.CreateVersion7(now), now, audit.ActorUserId, audit.ActorKind, table, id, company, action, [change], null, audit.CorrelationId, audit.IpAddress, audit.Source, actor.UserId);

    private async Task CheckCompanyAsync(string permission, Guid? company, CancellationToken ct)
    {
        if (!await access.CoversCompanyAsync(permission, company, ct).ConfigureAwait(false))
        {
            throw new NotificationException("errors.access.forbidden", 403);
        }
    }

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new NotificationException("errors.access.forbidden", 403);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
}
