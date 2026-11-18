using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Erp.Kernel.Domain.Notifications;

/// <summary>A field a notification template may use: its name, type and field class (general, personal or salary).</summary>
public sealed record NotificationField(string Name, string Type, string FieldClass);

/// <summary>
/// Template text of notifications (slice S1.11 Build item 9; KRN-NTF-02, KRN-LOC-07, GHR-06). A text holds placeholders in
/// braces, <c>{name}</c>, which come from the event's fields. Rendering formats dates and numbers in the recipient's
/// language, and a missing value renders as nothing. The check refuses a placeholder the event does not declare, a
/// salary-class field in any channel and a personal-class field in e-mail (ruling Q13: the link shows them after sign-in).
/// </summary>
public static partial class NotificationText
{
    private static readonly string[] UrduMonths = ["جنوری", "فروری", "مارچ", "اپریل", "مئی", "جون", "جولائی", "اگست", "ستمبر", "اکتوبر", "نومبر", "دسمبر"];

    [GeneratedRegex(@"\{([a-z][a-z0-9_]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>Names of the placeholders a text uses, in order of first use.</summary>
    public static IReadOnlyList<string> Placeholders(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The error keys that refuse a text for a channel; empty when the text is allowed. <paramref name="channel"/> is
    /// <c>in_app</c> or <c>email</c>.
    /// </summary>
    public static IReadOnlyList<string> Check(string? text, IReadOnlyDictionary<string, NotificationField> fields, string channel)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var problems = new List<string>();
        foreach (var name in Placeholders(text))
        {
            if (!fields.TryGetValue(name, out var field))
            {
                problems.Add("errors.notification.field_unknown");
            }
            else if (field.FieldClass == "salary")
            {
                problems.Add("errors.notification.field_salary_class");
            }
            else if (field.FieldClass == "personal" && channel == "email")
            {
                problems.Add("errors.notification.field_personal_in_email");
            }
        }

        return [.. problems.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Replaces each placeholder with its value formatted for <paramref name="language"/> (<c>en</c> or <c>ur</c>).</summary>
    public static string Render(string? text, IReadOnlyDictionary<string, JsonElement> values, string language)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.IsNullOrEmpty(text) ? string.Empty : Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Format(v, language) : string.Empty);
    }

    /// <summary>A value as text: dates as day, month name, year; numbers with grouping; booleans as yes or no; text as it is.</summary>
    public static string Format(JsonElement value, string language)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var s = value.GetString() ?? string.Empty;
                return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && s.Length >= 10 && s[4] == '-' && s[7] == '-'
                    ? FormatDate(DateOnly.FromDateTime(d.UtcDateTime), language)
                    : s;
            case JsonValueKind.Number:
                return value.TryGetInt64(out var l)
                    ? l.ToString("N0", CultureInfo.InvariantCulture)
                    : value.GetDecimal().ToString("#,##0.####", CultureInfo.InvariantCulture);
            case JsonValueKind.True:
                return language == "ur" ? "ہاں" : "yes";
            case JsonValueKind.False:
                return language == "ur" ? "نہیں" : "no";
            default:
                return string.Empty;
        }
    }

    public static string FormatDate(DateOnly date, string language) =>
        language == "ur"
            ? string.Create(CultureInfo.InvariantCulture, $"{date.Day} {UrduMonths[date.Month - 1]} {date.Year}")
            : date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether <paramref name="utcNow"/> falls in the person's quiet hours (local times <paramref name="from"/> to
    /// <paramref name="to"/>; a window past midnight, such as 22:00 to 07:00, is allowed) and, when it does, when the quiet
    /// period ends (ruling Q11: e-mails are held until it ends).
    /// </summary>
    public static (bool Quiet, DateTimeOffset? EndsAt) QuietPeriod(DateTimeOffset utcNow, TimeZoneInfo zone, TimeOnly from, TimeOnly to)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (from == to)
        {
            return (false, null);
        }

        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var inside = from < to ? time >= from && time < to : time >= from || time < to;
        if (!inside)
        {
            return (false, null);
        }

        var endDate = from < to || time < to ? DateOnly.FromDateTime(local.DateTime) : DateOnly.FromDateTime(local.DateTime).AddDays(1);
        var endLocal = endDate.ToDateTime(to, DateTimeKind.Unspecified);
        var offset = zone.GetUtcOffset(endLocal);
        return (true, new DateTimeOffset(endLocal, offset).ToUniversalTime());
    }

    /// <summary>Parses "HH:mm"; false for anything else.</summary>
    public static bool TryTime(string? text, out TimeOnly time)
    {
        time = default;
        return text is { Length: 5 } && text[2] == ':' && TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>Case-insensitive text search used by the item list.</summary>
    public static string LikePattern(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var sb = new StringBuilder("%");
        foreach (var ch in search.Trim())
        {
            if (ch is '%' or '_' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.Append('%').ToString();
    }
}
