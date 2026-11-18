using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Notifications;

/// <summary>
/// Permissions of the notification centre and its administration (slice S1.11 Build items 9 to 11 and 13). The generated
/// notification writes are closed (CR-048): items, templates and rules change only through the notification services, so
/// the create and update permissions of those tables are kept here as hand-written permissions. A person's own items and
/// preferences are held by the baseline roles with the data scope "own".
/// </summary>
public static class NotificationPermissions
{
    public const string ItemsRead = "ntf.in_app_items.read";
    public const string ItemsUpdate = "ntf.in_app_items.update";
    public const string PreferencesRead = "ntf.preferences.read";
    public const string PreferencesUpdate = "ntf.preferences.update";
    public const string TemplatesRead = "ntf.templates.read";
    public const string TemplatesCreate = "ntf.templates.create";
    public const string SubscriptionsRead = "ntf.subscriptions.read";
    public const string SubscriptionsCreate = "ntf.subscriptions.create";
    public const string SubscriptionsUpdate = "ntf.subscriptions.update";
    public const string SubscriptionsDelete = "ntf.subscriptions.delete";
    public const string CatalogueRead = "ntf.catalogue.read";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(ItemsUpdate, "ntf", "permissions.ntf.in_app_items.update", false),
        new(PreferencesRead, "ntf", "permissions.ntf.preferences.read", false),
        new(PreferencesUpdate, "ntf", "permissions.ntf.preferences.update", false),
        new(TemplatesCreate, "ntf", "permissions.ntf.templates.create", false),
        new(SubscriptionsCreate, "ntf", "permissions.ntf.subscriptions.create", false),
        new(SubscriptionsUpdate, "ntf", "permissions.ntf.subscriptions.update", false),
        new(SubscriptionsDelete, "ntf", "permissions.ntf.subscriptions.delete", false),
        new(CatalogueRead, "ntf", "permissions.ntf.catalogue.read", false),
    ];
}

/// <summary>Field classes a notification template may use (ruling Q13): salary-class values never, personal-class values never in e-mail.</summary>
public static class NotificationFieldClasses
{
    public const string General = "general";
    public const string Personal = "personal";
    public const string Salary = "salary";

    public static IReadOnlyList<string> All { get; } = [General, Personal, Salary];
}

/// <summary>Channels of release 1.0 (ruling Q1).</summary>
public static class NotificationChannels
{
    public const string InApp = "in_app";
    public const string Email = "email";

    public static IReadOnlyList<string> All { get; } = [InApp, Email];
}

/// <summary>Error keys of the notification services (texts in <c>notifications.*.json</c>).</summary>
public static class NotificationErrors
{
    public const string Invalid = "errors.notification.invalid";
    public const string NotFound = "errors.notification.not_found";
    public const string EventUnknown = "errors.notification.event_unknown";
    public const string FieldSalary = "errors.notification.field_salary_class";
    public const string FieldPersonalEmail = "errors.notification.field_personal_in_email";
    public const string FieldUnknown = "errors.notification.field_unknown";
    public const string LanguageUnknown = "errors.notification.language_unknown";
    public const string ChannelUnknown = "errors.notification.channel_unknown";
    public const string TemplateEmpty = "errors.notification.template_empty";
    public const string ConditionInvalid = "errors.notification.condition_invalid";
    public const string AudienceEmpty = "errors.notification.audience_empty";
    public const string AudienceUnknown = "errors.notification.audience_unknown";
    public const string MandatoryPreference = "errors.notification.mandatory_preference";
    public const string QuietHoursInvalid = "errors.notification.quiet_hours_invalid";
    public const string Changed = "errors.notification.changed";
    public const string NoProductText = "errors.notification.no_product_text";
}
