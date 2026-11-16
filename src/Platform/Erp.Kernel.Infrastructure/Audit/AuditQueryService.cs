using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Audit;
using Erp.Kernel.Contracts.Audit;
using Erp.Kernel.Domain.Generated.Aud;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// Search, record history, access log, security events and export of the audit log (FR-KRN-AUD-04, FR-KRN-AUD-07,
/// FR-KRN-AUD-02, ADM-USR-04; slice S1.07 build item 6). Values pass through the field security of the record's table for
/// the viewer: hidden fields lose their values, masked ones are masked (a line manager sees no salary before or after).
/// Showing salary, personal or restricted values unmasked, and every export, writes one access-log row (ruling Q6).
/// </summary>
public sealed class AuditQueryService(
    KernelDbContext db,
    IServiceProvider services,
    ResourceCatalog resources,
    IDataScopeFilter scopes,
    IFieldAccess fields,
    IPermissionChecker permissions,
    IFieldEncryptorProvider encryptors,
    ITenantConnectionProvider tenant,
    IAuditContext audit,
    IClock clock) : IAuditQueryService
{
    /// <summary>The change log as a data-scoped resource: permission <c>aud.changes.view</c>, located by company.</summary>
    public static ScopeTarget<ChangeLog> ChangesScope { get; } = new(
        "aud.changes",
        new Dictionary<ScopeDimension, Expression<Func<ChangeLog, Guid?>>> { [ScopeDimension.Company] = e => e.CompanyId },
        new Dictionary<string, Expression<Func<ChangeLog, string?>>>());

    private static readonly string[] SignInEvents = ["iam.sign_in_succeeded", "iam.sign_in_failed"];

    public async Task<AuditPage<ChangeItem>> ChangesAsync(ChangeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, AuditLimits.MaxPage);
        var rows = await ChangeRowsAsync(query, limit + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        var (items, shown) = await PresentAsync(page, recordAccess: null, cancellationToken).ConfigureAwait(false);
        await LogShownAsync("aud.change_log", null, shown, "view", items.Count, cancellationToken).ConfigureAwait(false);
        return new AuditPage<ChangeItem>(items, rows.Count > limit ? Cursor(page[^1].OccurredAt, page[^1].Id) : null);
    }

    public async Task<AuditExport> ExportChangesAsync(ChangeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = await ChangeRowsAsync(query with { Cursor = null }, AuditLimits.ExportRows + 1, cancellationToken).ConfigureAwait(false);
        var truncated = rows.Count > AuditLimits.ExportRows;
        var (items, shown) = await PresentAsync(rows.Take(AuditLimits.ExportRows).ToList(), recordAccess: null, cancellationToken).ConfigureAwait(false);
        var csv = new StringBuilder("occurred_at,actor_user_id,actor_kind,entity_type,entity_id,company_id,action,source,correlation_id,ip_address,changes\r\n");
        foreach (var i in items)
        {
            csv.AppendJoin(',', [
                i.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), Csv(i.ActorUserId?.ToString()), i.ActorKind,
                Csv(i.EntityType), i.EntityId.ToString(), Csv(i.CompanyId?.ToString()), i.Action, Csv(i.Source), Csv(i.CorrelationId?.ToString()), Csv(i.IpAddress), Csv(i.Changes.ToJsonString())]);
            csv.Append("\r\n");
        }

        // Every export is logged, whatever it contains (build item 6).
        await LogShownAsync("aud.change_log", null, ["changes", .. shown], "export", items.Count, cancellationToken, always: true).ConfigureAwait(false);
        return new AuditExport(csv.ToString(), items.Count, truncated);
    }

    public async Task<IReadOnlyList<ChangeItem>?> HistoryAsync(string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        FieldAccessMap? recordAccess = null;
        if (resources.Find(entityType) is { } resource)
        {
            // The viewer must be able to read the record itself: permission, data scope, and its field security (Q10).
            if (resource.ReadAccess is null || !await permissions.HasPermissionAsync(entityType + ".read", cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            recordAccess = await resource.ReadAccess(services, entityId, cancellationToken).ConfigureAwait(false);
            if (recordAccess is null)
            {
                return null;
            }
        }
        else if (!await permissions.HasPermissionAsync(AuditPermissions.ViewChanges, cancellationToken).ConfigureAwait(false))
        {
            // Tables without their own read API (and event-only entities) are for auditors only.
            return null;
        }

        var rows = await db.Set<ChangeLog>().AsNoTracking()
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .OrderByDescending(e => e.OccurredAt).Take(AuditLimits.ExportRows)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var (items, shown) = await PresentAsync(rows, recordAccess, cancellationToken).ConfigureAwait(false);
        await LogShownAsync(entityType, entityId, shown, "view", items.Count, cancellationToken).ConfigureAwait(false);
        return items;
    }

    public async Task<AuditPage<AccessLogItem>> AccessLogAsync(AccessLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, AuditLimits.MaxPage);
        var q = db.Set<AccessLog>().AsNoTracking();
        if (query.UserId is { } user)
        {
            q = q.Where(e => e.ActorUserId == user);
        }

        if (query.EntityType is { } type)
        {
            q = q.Where(e => e.EntityType == type);
        }

        if (query.EntityId is { } id)
        {
            q = q.Where(e => e.EntityId == id);
        }

        q = Between(q, query.From, query.To);
        if (ParseCursor(query.Cursor) is var (at, after))
        {
            q = q.Where(e => e.OccurredAt < at || (e.OccurredAt == at && e.Id.CompareTo(after) < 0));
        }

        var rows = await q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).Select(e => new AccessLogItem(e.Id, e.OccurredAt, e.ActorUserId, e.EntityType, e.EntityId, e.Fields, e.Action, e.RowCount, e.CorrelationId)).ToList();
        return new AuditPage<AccessLogItem>(page, rows.Count > limit ? Cursor(page[^1].OccurredAt, page[^1].Id) : null);
    }

    public Task<AuditPage<SecurityEventItem>> SecurityEventsAsync(SecurityEventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = db.Set<SecurityEvent>().AsNoTracking();
        if (query.EventType is { } type)
        {
            q = q.Where(e => e.EventType == type);
        }

        if (query.Severity is { } severity)
        {
            q = q.Where(e => e.Severity == severity);
        }

        if (query.Actor is { } actor)
        {
            q = q.Where(e => e.Actor == actor);
        }

        if (query.Subject is { } subject)
        {
            q = q.Where(e => e.Subject == subject);
        }

        return EventPageAsync(q, query.From, query.To, query.Cursor, query.Limit, cancellationToken);
    }

    public async Task<AuditPage<SecurityEventItem>> MySignInsAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        var me = audit.ActorUserId ?? throw new AccessDeniedException();
        var user = await db.Set<Domain.Generated.Iam.User>().AsNoTracking().Where(u => u.Id == me).Select(u => new { u.Username, u.Email }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var logins = user is null ? [] : new[] { user.Username.Trim().ToLowerInvariant(), user.Email.Trim().ToLowerInvariant() };
        var id = me.ToString();
        var q = db.Set<SecurityEvent>().AsNoTracking().Where(e => SignInEvents.Contains(e.EventType) && (e.Actor == id || (e.Subject != null && logins.Contains(e.Subject))));
        return await EventPageAsync(q, null, null, cursor, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AuditPage<SecurityEventItem>> EventPageAsync(IQueryable<SecurityEvent> q, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, AuditLimits.MaxPage);
        if (from is { } f)
        {
            q = q.Where(e => e.OccurredAt >= f);
        }

        if (to is { } t)
        {
            q = q.Where(e => e.OccurredAt < t);
        }

        if (ParseCursor(cursor) is var (at, after))
        {
            q = q.Where(e => e.OccurredAt < at || (e.OccurredAt == at && e.Id.CompareTo(after) < 0));
        }

        var rows = await q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).Select(e => new SecurityEventItem(e.Id, e.OccurredAt, e.EventType, e.Actor, e.Subject, e.Severity, JsonNode.Parse(e.Details.RootElement.GetRawText()) as JsonObject ?? [])).ToList();
        return new AuditPage<SecurityEventItem>(page, rows.Count > limit ? Cursor(page[^1].OccurredAt, page[^1].Id) : null);
    }

    private async Task<List<ChangeLog>> ChangeRowsAsync(ChangeQuery query, int take, CancellationToken cancellationToken)
    {
        var q = await scopes.ApplyAsync(db.Set<ChangeLog>().AsNoTracking(), ChangesScope, "view", new BuildingBlocks.Infrastructure.Persistence.DbContextScopeSource(db), cancellationToken).ConfigureAwait(false);
        if (query.UserId is { } user)
        {
            q = q.Where(e => e.ActorUserId == user);
        }

        if (query.EntityType is { } type)
        {
            q = q.Where(e => e.EntityType == type);
        }

        if (query.EntityId is { } id)
        {
            q = q.Where(e => e.EntityId == id);
        }

        if (query.Action is { } action)
        {
            q = q.Where(e => e.Action == action);
        }

        if (query.Source is { } source)
        {
            q = q.Where(e => e.Source == source);
        }

        q = Between(q, query.From, query.To);
        if (ParseCursor(query.Cursor) is var (at, _))
        {
            q = q.Where(e => e.OccurredAt < at); // occurred_at is strictly increasing along the chain (P2)
        }

        return await q.OrderByDescending(e => e.OccurredAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<T> Between<T>(IQueryable<T> q, DateTimeOffset? from, DateTimeOffset? to)
        where T : class
    {
        if (from is { } f)
        {
            q = q.Where(e => EF.Property<DateTimeOffset>(e, "OccurredAt") >= f);
        }

        if (to is { } t)
        {
            q = q.Where(e => EF.Property<DateTimeOffset>(e, "OccurredAt") < t);
        }

        return q;
    }

    /// <summary>Decrypts protected values and applies the viewer's field security; returns the sensitive fields shown.</summary>
    private async Task<(List<ChangeItem> Items, List<string> Shown)> PresentAsync(List<ChangeLog> rows, FieldAccessMap? recordAccess, CancellationToken cancellationToken)
    {
        var shown = new SortedSet<string>(StringComparer.Ordinal);
        var items = new List<ChangeItem>(rows.Count);
        if (rows.Count == 0)
        {
            return (items, []);
        }

        var key = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var reader = new AuditValueReader(connection, key);
        var maps = new Dictionary<(string, Guid?), FieldAccessMap?>();
        foreach (var row in rows)
        {
            var resource = resources.Find(row.EntityType);
            FieldAccessMap? map = null;
            if (resource is not null)
            {
                if (recordAccess is not null)
                {
                    map = recordAccess;
                }
                else if (!maps.TryGetValue((row.EntityType, row.CompanyId), out map))
                {
                    map = await fields.ForAsync(resource.Fields, "read", row.CompanyId, cancellationToken).ConfigureAwait(false);
                    maps[(row.EntityType, row.CompanyId)] = map;
                }
            }

            var changes = new JsonArray();
            foreach (var stored in (JsonNode.Parse(row.Changes.RootElement.GetRawText()) as JsonArray ?? []).OfType<JsonObject>())
            {
                var field = (string?)stored["field"] ?? string.Empty;
                var known = resource?.Fields.Find(field);
                var level = known is null || map is null ? FieldAccessLevel.ReadOnly : map.Of(field);
                if (level == FieldAccessLevel.Hidden)
                {
                    changes.Add(new JsonObject { ["field"] = field, ["hidden"] = true });
                    continue;
                }

                var change = await reader.ReadAsync(row.EntityType, stored, row.SubjectPersonId, cancellationToken).ConfigureAwait(false);
                if (level == FieldAccessLevel.Masked)
                {
                    foreach (var side in new[] { "before", "after" })
                    {
                        if (change.ContainsKey(side))
                        {
                            change[side] = FieldShaping.Mask(change[side]);
                        }
                    }
                }
                else if (stored["class"] is not null && change["erased"] is null && (change.ContainsKey("before") || change.ContainsKey("after")))
                {
                    shown.Add(row.EntityType + "." + field);
                }

                changes.Add(change);
            }

            items.Add(new ChangeItem(row.Id, row.OccurredAt, row.ActorUserId, row.ActorKind, row.EntityType, row.EntityId, row.CompanyId, row.Action, row.Source,
                row.CorrelationId, row.IpAddress?.ToString(), changes));
        }

        return (items, [.. shown]);
    }

    private async Task LogShownAsync(string entityType, Guid? entityId, List<string> shown, string action, int rows, CancellationToken cancellationToken, bool always = false)
    {
        if (shown.Count == 0 && !always)
        {
            return;
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AuditDb.AppendAccessAsync(connection, audit.ActorUserId ?? BaselineSeed.SystemUserId, entityType, entityId, shown, action, rows, audit.CorrelationId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private static string Cursor(DateTimeOffset at, Guid id) =>
        ((at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10).ToString(CultureInfo.InvariantCulture) + "~" + id.ToString("N");

    /// <summary>A cursor from <see cref="Cursor"/>; anything else is refused as an invalid filter.</summary>
    public static (DateTimeOffset At, Guid Id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var parts = cursor.Split('~');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var micros) || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            throw new ArgumentException("errors.audit.filter_invalid");
        }

        return (DateTimeOffset.UnixEpoch.AddTicks(micros * 10), id);
    }

    private static string Csv(string? value) =>
        value is null ? string.Empty
        : value.IndexOfAny([',', '"', '\r', '\n']) < 0 && !value.StartsWith('=') && !value.StartsWith('+') && !value.StartsWith('-') && !value.StartsWith('@')
            ? value
            : "\"" + (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@') ? "'" : string.Empty) + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
