using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Notifications;
using Erp.Kernel.Contracts.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Notifications;

/// <summary>
/// The notification API (KRN-NTF-02, KRN-NTF-03, KRN-NTF-04, HRM-NTF-02, HRM-NTF-04, HRM-NTF-09, ADM-USR-10; slice S1.11
/// Build items 8 to 11 and 13; screens in S1.12, S1.15 and S1.26). Under <c>/api/ntf</c>: the signed-in person's items
/// (<c>/centre/items</c>) and preferences (<c>/centre/preferences</c>), and the administration of the catalogue, texts and
/// audience rules. The person is always the signed-in one; no id of a person is accepted. The generated
/// <c>/api/ntf/*</c> endpoints only read (CR-048). Error titles are localization keys (<c>notifications.*.json</c>).
/// </summary>
public static class NotificationEndpoints
{
    private const string Items = "ntf.in_app_items";
    private const string Preferences = "ntf.preferences";
    private const string Templates = "ntf.templates";
    private const string Subscriptions = "ntf.subscriptions";

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/ntf").WithTags("ntf.notifications");

        // The signed-in person's centre.
        Tag(g.MapGet("/centre/items", ([AsParameters] ItemParameters q, INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ItemsAsync(new NotificationQuery(q.Unread ?? false, q.Search, q.Group, q.Limit, q.Offset), ct).ConfigureAwait(false)))),
            "own_catalog", Items, NotificationPermissions.ItemsRead);
        Tag(g.MapGet("/centre/unread-count", (INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(new { unread = await s.UnreadCountAsync(ct).ConfigureAwait(false) }))), "own_catalog", Items, NotificationPermissions.ItemsRead);
        Tag(g.MapPost("/centre/items/{id:guid}/read", (Guid id, INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await s.MarkReadAsync(id, true, ct).ConfigureAwait(false);
                return Results.NoContent();
            })), "record_action", Items, NotificationPermissions.ItemsUpdate);
        Tag(g.MapPost("/centre/items/{id:guid}/unread", (Guid id, INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await s.MarkReadAsync(id, false, ct).ConfigureAwait(false);
                return Results.NoContent();
            })), "record_action", Items, NotificationPermissions.ItemsUpdate);
        Tag(g.MapPost("/centre/items/read-all", (INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(new { marked = await s.MarkAllReadAsync(ct).ConfigureAwait(false) }))), "own_catalog", Items, NotificationPermissions.ItemsUpdate);
        Tag(g.MapGet("/centre/preferences", (INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.PreferencesAsync(ct).ConfigureAwait(false)))), "own_catalog", Preferences, NotificationPermissions.PreferencesRead);
        Tag(g.MapPut("/centre/preferences", (PreferenceUpdate body, INotificationCentre s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.UpdatePreferencesAsync(body, ct).ConfigureAwait(false)))), "own_catalog", Preferences, NotificationPermissions.PreferencesUpdate);

        // Administration.
        Tag(g.MapGet("/catalogue", (INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.CatalogueAsync(ct).ConfigureAwait(false)))), "catalog", Templates, NotificationPermissions.CatalogueRead);
        Tag(g.MapGet("/text-templates", (string? eventCode, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.TemplatesAsync(eventCode, ct).ConfigureAwait(false)))), "catalog", Templates, NotificationPermissions.TemplatesRead);
        Tag(g.MapPost("/text-templates", (TemplateEdit body, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Created("/api/ntf/text-templates?eventCode=" + Uri.EscapeDataString(body.EventCode), await s.EditTemplateAsync(body, ct).ConfigureAwait(false)))),
            "catalog", Templates, NotificationPermissions.TemplatesCreate);
        Tag(g.MapPost("/text-templates/restore", (RestoreRequest body, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.RestoreProductTextAsync(body.EventCode, body.Channel, body.Language, ct).ConfigureAwait(false)))),
            "catalog", Templates, NotificationPermissions.TemplatesCreate);
        Tag(g.MapPost("/text-templates/preview", (TemplatePreviewRequest body, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.PreviewAsync(body, ct).ConfigureAwait(false)))), "catalog", Templates, NotificationPermissions.TemplatesRead);
        Tag(g.MapGet("/rules", (INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.SubscriptionsAsync(ct).ConfigureAwait(false)))), "catalog", Subscriptions, NotificationPermissions.SubscriptionsRead);
        Tag(g.MapPost("/rules", (SubscriptionRequest body, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var created = await s.CreateSubscriptionAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/ntf/rules/" + created.Id, created);
            })), "catalog", Subscriptions, NotificationPermissions.SubscriptionsCreate);
        Tag(g.MapPut("/rules/{id:guid}", (Guid id, SubscriptionRequest body, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.UpdateSubscriptionAsync(id, body, ct).ConfigureAwait(false)))), "record_action", Subscriptions, NotificationPermissions.SubscriptionsUpdate);
        Tag(g.MapDelete("/rules/{id:guid}", (Guid id, long rowVersion, INotificationAdmin s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await s.DeleteSubscriptionAsync(id, rowVersion, ct).ConfigureAwait(false);
                return Results.NoContent();
            })), "record_action", Subscriptions, NotificationPermissions.SubscriptionsDelete);
        return app;
    }

    /// <summary>Refusals become problem answers with their status (400, 403, 404, 409).</summary>
    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (NotificationException ex)
        {
            return Results.Problem(statusCode: ex.Status, title: ex.Message, extensions: ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new NotificationEndpointMetadata(kind, resource, permission));

    /// <summary>Query string of <c>GET /api/ntf/centre/items</c>.</summary>
    public sealed class ItemParameters
    {
        public bool? Unread { get; init; }

        public string? Search { get; init; }

        public string? Group { get; init; }

        public int? Limit { get; init; }

        public int? Offset { get; init; }
    }

    public sealed record RestoreRequest(string EventCode, string Channel, string Language);
}
