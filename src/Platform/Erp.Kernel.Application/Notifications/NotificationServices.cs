using System.Text.Json;

namespace Erp.Kernel.Application.Notifications;

/// <summary>A notification item of a person's centre (HRM-NTF-09).</summary>
public sealed record NotificationItem(Guid Id, string EventCode, string? EventGroup, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>A page of items with the person's unread count.</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, int Total, int Unread);

/// <summary>Filters of the item list: unread only, free-text search in title and body, event group, paging.</summary>
public sealed record NotificationQuery(bool UnreadOnly = false, string? Search = null, string? Group = null, int? Limit = null, int? Offset = null);

/// <summary>One preference: a person's choice for an event group and channel (KRN-NTF-04). Quiet hours are per person.</summary>
public sealed record NotificationPreference(string EventGroup, string Channel, bool Enabled, bool Mandatory);

/// <summary>Quiet hours of a person: e-mails are held from <c>From</c> to <c>To</c> (local times of the person's zone, HH:mm).</summary>
public sealed record QuietHours(bool Enabled, string From, string To);

/// <summary>Everything a person has chosen: the groups of the catalogue (mandatory ones cannot be switched off) and the quiet hours.</summary>
public sealed record PreferenceView(IReadOnlyList<NotificationPreference> Preferences, QuietHours QuietHours);

public sealed record PreferenceUpdate(IReadOnlyList<PreferenceChange>? Preferences, QuietHours? QuietHours);

public sealed record PreferenceChange(string EventGroup, string Channel, bool Enabled);

/// <summary>An event a customer can notify on, as the catalogue declares it (HRM-NTF-02).</summary>
public sealed record CatalogueEvent(string Code, string Module, string Group, bool Mandatory, bool Audited, IReadOnlyList<CatalogueField> Fields, string? Link);

public sealed record CatalogueField(string Name, string Type, string FieldClass);

/// <summary>One text of a template: event, channel, language and the active version.</summary>
public sealed record TemplateView(Guid Id, string EventCode, string Channel, string Language, string? Subject, string Body, int Version, string Source, string Status, long RowVersion);

public sealed record TemplateEdit(string EventCode, string Channel, string Language, string? Subject, string Body);

public sealed record TemplatePreviewRequest(string EventCode, string Channel, string Language, string? Subject, string Body, Dictionary<string, JsonElement>? Sample);

public sealed record TemplatePreview(string? Subject, string Body);

/// <summary>An administrator rule that adds people to an event's audience (KRN-NTF-03, ruling Q14).</summary>
public sealed record SubscriptionView(Guid Id, string? Name, string EventCode, Guid? CompanyId, JsonElement? Condition, SubscriptionAudience Audience,
    IReadOnlyList<string> Channels, bool Mandatory, long RowVersion);

/// <summary>Who a rule adds: users, role codes, user groups (ids) and the relationship "line_manager" once HR provides it.</summary>
public sealed record SubscriptionAudience(IReadOnlyList<Guid>? Users, IReadOnlyList<string>? Roles, IReadOnlyList<Guid>? Groups, IReadOnlyList<string>? Relationships);

public sealed record SubscriptionRequest(string? Name, string EventCode, Guid? CompanyId, JsonElement? Condition, SubscriptionAudience Audience,
    IReadOnlyList<string> Channels, bool Mandatory, long? RowVersion);

/// <summary>A refusal of a notification service with its HTTP status and localization key.</summary>
public sealed class NotificationException : Exception
{
    public NotificationException(string errorKey, int status = 400, IReadOnlyList<string>? details = null)
        : base(errorKey)
    {
        Status = status;
        Details = details ?? [];
    }

    public NotificationException()
        : this("errors.notification.invalid")
    {
    }

    public NotificationException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public int Status { get; } = 400;

    public IReadOnlyList<string> Details { get; } = [];
}

/// <summary>The notification centre of the signed-in person (items and preferences; screens S1.12 or S1.26).</summary>
public interface INotificationCentre
{
    Task<NotificationPage> ItemsAsync(NotificationQuery query, CancellationToken cancellationToken);

    Task<int> UnreadCountAsync(CancellationToken cancellationToken);

    Task MarkReadAsync(Guid itemId, bool read, CancellationToken cancellationToken);

    /// <returns>The number of items marked.</returns>
    Task<int> MarkAllReadAsync(CancellationToken cancellationToken);

    Task<PreferenceView> PreferencesAsync(CancellationToken cancellationToken);

    Task<PreferenceView> UpdatePreferencesAsync(PreferenceUpdate update, CancellationToken cancellationToken);
}

/// <summary>Administration: catalogue, templates (versions, back to product text, preview) and audience rules.</summary>
public interface INotificationAdmin
{
    Task<IReadOnlyList<CatalogueEvent>> CatalogueAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TemplateView>> TemplatesAsync(string? eventCode, CancellationToken cancellationToken);

    /// <summary>A customer edit: a new tenant version, active at once; the former active version is retired.</summary>
    Task<TemplateView> EditTemplateAsync(TemplateEdit edit, CancellationToken cancellationToken);

    /// <summary>Retires every customer version of a text so the product text is used again.</summary>
    Task<TemplateView> RestoreProductTextAsync(string eventCode, string channel, string language, CancellationToken cancellationToken);

    Task<TemplatePreview> PreviewAsync(TemplatePreviewRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionView>> SubscriptionsAsync(CancellationToken cancellationToken);

    Task<SubscriptionView> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken cancellationToken);

    Task<SubscriptionView> UpdateSubscriptionAsync(Guid id, SubscriptionRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a rule (the change log keeps what it was).</summary>
    Task DeleteSubscriptionAsync(Guid id, long rowVersion, CancellationToken cancellationToken);
}

/// <summary>One message of the delivery log (KRN-NTF-05, ADM-OPS-04; slice S1.11b2). <see cref="Resendable"/> is false for identity e-mails.</summary>
public sealed record DeliveryView(Guid Id, DateTimeOffset CreatedAt, string Channel, string Status, string Recipient, Guid? UserId, string? EventCode, Guid? CompanyId,
    string Subject, int Attempts, DateTimeOffset? NextAttemptAt, string? Error, string? ProviderRef, DateTimeOffset UpdatedAt, bool Resendable);

/// <summary>A message with its text (the stored text; a sign-in link or code is never shown).</summary>
public sealed record DeliveryDetail(DeliveryView Message, string Body, string? Link, string Language, Guid? ResendOf);

public sealed record DeliveryQuery(string? Status = null, Guid? UserId = null, string? EventCode = null, Guid? CompanyId = null, string? Recipient = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int? Limit = null, int? Offset = null);

/// <summary>The delivery numbers of the operations screen (ADM-OPS-05): waiting now, and results of the last 24 hours.</summary>
public sealed record DeliverySummary(int Queued, int Retrying, int Sent24h, int Failed24h, int Dead24h, DateTimeOffset? OldestQueuedAt);

/// <summary>A test e-mail of a text to the signed-in person (KRN-NTF-02 "test send"): the event, language, the text and sample values.</summary>
public sealed record TestSendRequest(string EventCode, string Language, string? Subject, string Body, Dictionary<string, JsonElement>? Sample);

/// <summary>The e-mail delivery log and its actions (slice S1.11b2 Build items 12 and 14; screens in S1.16).</summary>
public interface IDeliveryLog
{
    Task<IReadOnlyList<DeliveryView>> ListAsync(DeliveryQuery query, CancellationToken cancellationToken);

    Task<DeliveryDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Queues a copy of a message as a new message; the original stays as it was. Refused for identity e-mails.</summary>
    Task<DeliveryView> ResendAsync(Guid id, CancellationToken cancellationToken);

    Task<DeliverySummary> SummaryAsync(CancellationToken cancellationToken);

    /// <summary>Queues the text as an e-mail to the signed-in person, marked as a test.</summary>
    Task<DeliveryView> TestSendAsync(TestSendRequest request, CancellationToken cancellationToken);
}
