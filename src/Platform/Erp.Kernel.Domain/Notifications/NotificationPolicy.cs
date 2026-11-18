namespace Erp.Kernel.Domain.Notifications;

/// <summary>What happens to an e-mail of a notification (ruling Q11): sent now, held until the quiet hours end, or not sent.</summary>
public enum EmailTiming
{
    /// <summary>The person switched e-mail off for this event group and the event is not mandatory.</summary>
    Skip,

    /// <summary>Send at once.</summary>
    Now,

    /// <summary>Quiet hours: send when they end.</summary>
    Held,
}

/// <summary>
/// The delivery rules of ruling Q11 and KRN-NTF-04 as one decision, used by the e-mail sender of S1.11b2: in-app items are
/// always delivered; a mandatory notification (security alerts, sign-in codes and links, licence read-only and locked,
/// operational alerts) ignores preferences and quiet hours; any other e-mail is skipped when the person switched the event
/// group off, and held to the end of the person's quiet hours otherwise.
/// </summary>
public static class NotificationPolicy
{
    public static (EmailTiming Timing, DateTimeOffset? SendAt) Email(bool mandatory, bool groupEnabled, DateTimeOffset utcNow, TimeZoneInfo zone, TimeOnly? quietFrom, TimeOnly? quietTo)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (mandatory)
        {
            return (EmailTiming.Now, null);
        }

        if (!groupEnabled)
        {
            return (EmailTiming.Skip, null);
        }

        if (quietFrom is { } from && quietTo is { } to)
        {
            var (quiet, ends) = NotificationText.QuietPeriod(utcNow, zone, from, to);
            if (quiet)
            {
                return (EmailTiming.Held, ends);
            }
        }

        return (EmailTiming.Now, null);
    }
}
