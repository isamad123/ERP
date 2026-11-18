using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Notifications;
using Erp.Kernel.Contracts.Notifications;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications;

/// <summary>
/// The notification centre of the signed-in person (HRM-NTF-09, KRN-NTF-04, HRM-NTF-04, ADM-USR-10 API part; slice S1.11
/// Build items 11 and 13): the person's own items with unread count, search and read state, and their preferences. The
/// person's id comes from the token, never from the request, so nobody can read or change another person's items. In-app
/// items cannot be switched off (ruling Q11); e-mail can be switched off per event group and held in quiet hours, except
/// for mandatory notifications.
/// </summary>
public sealed class NotificationCentreService(ITenantConnectionProvider tenant, IClock clock, ICurrentActor actor, IPermissionChecker permissions, NotificationCatalog catalog) : INotificationCentre
{
    private const int MaxLimit = 200;

    /// <summary>The preference row that holds a person's quiet hours (group <c>*</c>, channel e-mail).</summary>
    public const string QuietGroup = "*";

    public async Task<NotificationPage> ItemsAsync(NotificationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await RequireAsync(NotificationPermissions.ItemsRead, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit ?? 50, 1, MaxLimit);
        var offset = Math.Max(0, query.Offset ?? 0);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : NotificationText.LikePattern(query.Search);
        var rows = await NotificationConsumer.ReadAsync(c, null, """
            select id, event_code, event_group, title, body, link, created_at, read_at, count(*) over ()
            from ntf.in_app_items
            where user_id = $1 and ($2 = false or read_at is null) and ($3::text is null or title ilike $3 or body ilike $3) and ($4::text is null or event_group = $4)
            order by created_at desc, id desc limit $5 offset $6
            """, r => (Item: new NotificationItem(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)), Total: (int)r.GetInt64(8)),
            cancellationToken, actor.UserId, query.UnreadOnly, search, query.Group, limit, offset).ConfigureAwait(false);
        var unread = await UnreadAsync(c, cancellationToken).ConfigureAwait(false);
        return new NotificationPage([.. rows.Select(r => r.Item)], rows.Count == 0 ? 0 : rows[0].Total, unread);
    }

    public async Task<int> UnreadCountAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.ItemsRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await UnreadAsync(c, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkReadAsync(Guid itemId, bool read, CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.ItemsUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var changed = await IdentityDb.ExecuteAsync(c, null, """
            update ntf.in_app_items set read_at = $3, updated_at = $4, updated_by = $2, row_version = row_version + 1 where id = $1 and user_id = $2
            """, cancellationToken, itemId, actor.UserId, read ? now : null, now).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new NotificationException(NotificationErrors.NotFound, 404);
        }
    }

    public async Task<int> MarkAllReadAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.ItemsUpdate, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        return await IdentityDb.ExecuteAsync(c, null, """
            update ntf.in_app_items set read_at = $2, updated_at = $2, updated_by = $1, row_version = row_version + 1 where user_id = $1 and read_at is null
            """, cancellationToken, actor.UserId, now).ConfigureAwait(false);
    }

    public async Task<PreferenceView> PreferencesAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(NotificationPermissions.PreferencesRead, cancellationToken).ConfigureAwait(false);
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(c, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreferenceView> UpdatePreferencesAsync(PreferenceUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await RequireAsync(NotificationPermissions.PreferencesUpdate, cancellationToken).ConfigureAwait(false);
        foreach (var change in update.Preferences ?? [])
        {
            if (!NotificationCatalog.Groups.Contains(change.EventGroup, StringComparer.Ordinal))
            {
                throw new NotificationException(NotificationErrors.Invalid, 400, [$"event_group {change.EventGroup}"]);
            }

            if (change.Channel == NotificationChannels.InApp && !change.Enabled)
            {
                throw new NotificationException(NotificationErrors.MandatoryPreference, 400); // the centre is the record: in-app items are always delivered
            }

            if (!NotificationChannels.All.Contains(change.Channel, StringComparer.Ordinal))
            {
                throw new NotificationException(NotificationErrors.ChannelUnknown, 400);
            }
        }

        if (update.QuietHours is { Enabled: true } q && (!NotificationText.TryTime(q.From, out var from) || !NotificationText.TryTime(q.To, out var to) || from == to))
        {
            throw new NotificationException(NotificationErrors.QuietHoursInvalid, 400);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in update.Preferences ?? [])
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into ntf.preferences (user_id, event_group, channel, enabled) values ($1, $2, $3, $4)
                on conflict (user_id, event_group, channel) do update set enabled = excluded.enabled
                """, cancellationToken, actor.UserId, change.EventGroup, change.Channel, change.Enabled).ConfigureAwait(false);
        }

        if (update.QuietHours is { } quiet)
        {
            var json = JsonSerializer.Serialize(new { enabled = quiet.Enabled, from = quiet.From, to = quiet.To });
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into ntf.preferences (user_id, event_group, channel, enabled, quiet_hours) values ($1, $2, 'email', true, $3)
                on conflict (user_id, event_group, channel) do update set quiet_hours = excluded.quiet_hours
                """, cancellationToken, actor.UserId, QuietGroup, new JsonPayload(json)).ConfigureAwait(false);
        }

        var view = await ViewAsync(c, tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return view;
    }

    private async Task<PreferenceView> ViewAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var rows = await NotificationConsumer.ReadAsync(c, tx, "select event_group, channel, enabled, quiet_hours::text from ntf.preferences where user_id = $1",
            r => (Group: r.GetString(0), Channel: r.GetString(1), Enabled: r.GetBoolean(2), Quiet: r.IsDBNull(3) ? null : r.GetString(3)), ct, actor.UserId).ConfigureAwait(false);
        var list = new List<NotificationPreference>();
        foreach (var group in NotificationCatalog.Groups)
        {
            var hasMandatory = catalog.All.Any(e => e.Group == group && (e.Mandatory || e.MandatoryWhen.Count > 0));
            list.Add(new NotificationPreference(group, NotificationChannels.InApp, true, true));
            var chosen = rows.FirstOrDefault(r => r.Group == group && r.Channel == NotificationChannels.Email);
            list.Add(new NotificationPreference(group, NotificationChannels.Email, chosen.Group is null || chosen.Enabled, hasMandatory));
        }

        var quiet = new QuietHours(false, "22:00", "07:00");
        if (rows.FirstOrDefault(r => r.Group == QuietGroup && r.Quiet is not null).Quiet is { } text)
        {
            using var doc = JsonDocument.Parse(text);
            quiet = new QuietHours(doc.RootElement.GetProperty("enabled").GetBoolean(), doc.RootElement.GetProperty("from").GetString()!, doc.RootElement.GetProperty("to").GetString()!);
        }

        return new PreferenceView(list, quiet);
    }

    private async Task<int> UnreadAsync(NpgsqlConnection c, CancellationToken ct) =>
        (int)await IdentityDb.ScalarAsync<long>(c, null, "select count(*) from ntf.in_app_items where user_id = $1 and read_at is null", ct, actor.UserId).ConfigureAwait(false);

    private async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            throw new NotificationException("errors.access.forbidden", 403);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
}
