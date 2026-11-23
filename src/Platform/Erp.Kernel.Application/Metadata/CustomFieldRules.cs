using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Kernel.Contracts.Metadata;

namespace Erp.Kernel.Application.Metadata;

/// <summary>The outcome of checking one custom value: an error key, or the value to store (null with <see cref="IsEmpty"/> when nothing was given).</summary>
public readonly record struct CustomValueResult(string? ErrorKey, JsonNode? Value, bool IsEmpty)
{
    public static CustomValueResult Empty { get; } = new(null, null, true);

    public static CustomValueResult Fail(string key) => new(key, null, false);

    public static CustomValueResult Ok(JsonNode value) => new(null, value, false);
}

/// <summary>
/// The pure rules of custom fields (slice S1.15b, FR-KRN-MET-01, -02, -05; S1.15 Q5 and Q6): which validation settings a type
/// takes, and whether a value fits a definition. No database here; the validator adds existence, uniqueness and options.
/// Stored forms: text, long text, choice, date, user and employee as strings, number as an integer, decimal as an exact decimal
/// string, yes/no as a boolean, multi-choice as an array of codes.
/// </summary>
public static partial class CustomFieldRules
{
    private const string TooLong = "errors.custom.too_long";
    private const string TooShort = "errors.custom.too_short";
    private const string TooSmall = "errors.custom.too_small";
    private const string TooLarge = "errors.custom.too_large";
    private const string InvalidType = "errors.custom.invalid_type";
    private const string NotInList = "errors.custom.not_in_list";
    private const string InvalidDate = "errors.custom.invalid_date";
    private const string PatternKey = "errors.custom.pattern";

    private static readonly Dictionary<string, string[]> ValidationKeys = new(StringComparer.Ordinal)
    {
        [CustomFieldTypes.Text] = ["minLength", "maxLength", "pattern"],
        [CustomFieldTypes.LongText] = ["minLength", "maxLength"],
        [CustomFieldTypes.Number] = ["min", "max"],
        [CustomFieldTypes.DecimalNumber] = ["min", "max"],
        [CustomFieldTypes.Date] = ["minDate", "maxDate"],
        [CustomFieldTypes.MultiChoice] = ["maxItems"],
    };

    /// <summary>
    /// Checks the validation settings of a definition and returns them in their stored form, or the error key. Unknown settings, settings
    /// the type does not take and impossible ranges are refused (<c>errors.custom.def.validation_*</c>).
    /// </summary>
    public static (JsonObject? Validation, string? ErrorKey) NormalizeValidation(string dataType, JsonElement? validation)
    {
        var result = new JsonObject();
        if (validation is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (result, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, "errors.custom.def.validation_invalid");
        }

        var allowed = ValidationKeys.GetValueOrDefault(dataType) ?? [];
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                return (null, "errors.custom.def.validation_not_for_type");
            }

            var (node, error) = NormalizeSetting(dataType, property.Name, property.Value);
            if (error is not null)
            {
                return (null, error);
            }

            result[property.Name] = node;
        }

        return RangeError(dataType, result) is { } range ? (null, range) : (result, null);
    }

    private static (JsonNode? Node, string? Error) NormalizeSetting(string dataType, string name, JsonElement value)
    {
        const string invalid = "errors.custom.def.validation_invalid";
        switch (name)
        {
            case "minLength" or "maxLength" or "maxItems":
                var cap = name == "maxItems" ? 200 : 4000;
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= (name == "minLength" ? 0 : 1) && n <= cap
                    ? (JsonValue.Create(n), null)
                    : (null, invalid);
            case "pattern":
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } pattern || pattern.Length > MetadataLimits.PatternLength || BuildPattern(pattern) is null)
                {
                    return (null, "errors.custom.def.pattern_invalid");
                }

                return (JsonValue.Create(pattern), null);
            case "min" or "max":
                if (dataType == CustomFieldTypes.Number)
                {
                    return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l) ? (JsonValue.Create(l), null) : (null, invalid);
                }

                return TryDecimal(value, out var d) ? (JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)), null) : (null, invalid);
            case "minDate" or "maxDate":
                return value.ValueKind == JsonValueKind.String && TryDate(value.GetString(), out var date)
                    ? (JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null)
                    : (null, invalid);
            default:
                return (null, invalid);
        }
    }

    private static string? RangeError(string dataType, JsonObject v)
    {
        bool Reversed<T>(string low, string high, Func<JsonNode, T> read)
            where T : IComparable<T> =>
            v[low] is { } a && v[high] is { } b && read(a).CompareTo(read(b)) > 0;

        var reversed = dataType switch
        {
            CustomFieldTypes.Text or CustomFieldTypes.LongText => Reversed("minLength", "maxLength", x => x.GetValue<int>()),
            CustomFieldTypes.Number => Reversed("min", "max", x => x.GetValue<long>()),
            CustomFieldTypes.DecimalNumber => Reversed("min", "max", x => decimal.Parse(x.GetValue<string>(), CultureInfo.InvariantCulture)),
            CustomFieldTypes.Date => Reversed("minDate", "maxDate", x => x.GetValue<string>()),
            _ => false,
        };
        return reversed ? "errors.custom.def.validation_range" : null;
    }

    /// <summary>
    /// Checks one value against a definition's type and validation. <paramref name="options"/> holds the codes a choice may take
    /// (null for other types). Existence of users and employees is checked by the validator, not here.
    /// </summary>
    public static CustomValueResult CheckValue(string dataType, JsonElement validation, JsonElement value, IReadOnlySet<string>? options)
    {
        if (IsEmpty(value))
        {
            return CustomValueResult.Empty;
        }

        return dataType switch
        {
            CustomFieldTypes.Text => CheckText(validation, value, MetadataLimits.TextDefault, pattern: true),
            CustomFieldTypes.LongText => CheckText(validation, value, MetadataLimits.LongTextDefault, pattern: false),
            CustomFieldTypes.Number => CheckNumber(validation, value),
            CustomFieldTypes.DecimalNumber => CheckDecimal(validation, value),
            CustomFieldTypes.Date => CheckDate(validation, value),
            CustomFieldTypes.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? CustomValueResult.Ok(JsonValue.Create(value.GetBoolean())) : CustomValueResult.Fail(InvalidType),
            CustomFieldTypes.Choice => CheckChoice(value, options),
            CustomFieldTypes.MultiChoice => CheckMultiChoice(validation, value, options),
            CustomFieldTypes.User or CustomFieldTypes.Employee => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty
                ? CustomValueResult.Ok(JsonValue.Create(id.ToString()))
                : CustomValueResult.Fail(InvalidType),
            _ => CustomValueResult.Fail(InvalidType),
        };
    }

    /// <summary>A value that counts as "not filled in": null, blank text or an empty list.</summary>
    public static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false,
    };

    private static CustomValueResult CheckText(JsonElement v, JsonElement value, int defaultMax, bool pattern)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return CustomValueResult.Fail(InvalidType);
        }

        var text = value.GetString()!.Trim();
        var max = Int(v, "maxLength") ?? defaultMax;
        if (text.Length > max)
        {
            return CustomValueResult.Fail(TooLong);
        }

        if (Int(v, "minLength") is { } min && text.Length < min)
        {
            return CustomValueResult.Fail(TooShort);
        }

        if (pattern && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String
            && BuildPattern(p.GetString()!) is { } regex && !Matches(regex, text))
        {
            return CustomValueResult.Fail(PatternKey);
        }

        return CustomValueResult.Ok(JsonValue.Create(text));
    }

    private static CustomValueResult CheckNumber(JsonElement v, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var n))
        {
            return CustomValueResult.Fail(InvalidType);
        }

        if (Long(v, "min") is { } min && n < min)
        {
            return CustomValueResult.Fail(TooSmall);
        }

        return Long(v, "max") is { } max && n > max ? CustomValueResult.Fail(TooLarge) : CustomValueResult.Ok(JsonValue.Create(n));
    }

    private static CustomValueResult CheckDecimal(JsonElement v, JsonElement value)
    {
        if (!TryDecimal(value, out var d))
        {
            return CustomValueResult.Fail(InvalidType);
        }

        if (Dec(v, "min") is { } min && d < min)
        {
            return CustomValueResult.Fail(TooSmall);
        }

        return Dec(v, "max") is { } max && d > max ? CustomValueResult.Fail(TooLarge) : CustomValueResult.Ok(JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)));
    }

    private static CustomValueResult CheckDate(JsonElement v, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || !TryDate(value.GetString(), out var date))
        {
            return CustomValueResult.Fail(InvalidDate);
        }

        if (Str(v, "minDate") is { } min && TryDate(min, out var low) && date < low)
        {
            return CustomValueResult.Fail(TooSmall);
        }

        return Str(v, "maxDate") is { } max && TryDate(max, out var high) && date > high
            ? CustomValueResult.Fail(TooLarge)
            : CustomValueResult.Ok(JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    private static CustomValueResult CheckChoice(JsonElement value, IReadOnlySet<string>? options)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return CustomValueResult.Fail(InvalidType);
        }

        var code = value.GetString()!.Trim();
        return options is not null && options.Contains(code) ? CustomValueResult.Ok(JsonValue.Create(code)) : CustomValueResult.Fail(NotInList);
    }

    private static CustomValueResult CheckMultiChoice(JsonElement v, JsonElement value, IReadOnlySet<string>? options)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return CustomValueResult.Fail(InvalidType);
        }

        var codes = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return CustomValueResult.Fail(InvalidType);
            }

            var code = item.GetString()!.Trim();
            if (options is null || !options.Contains(code))
            {
                return CustomValueResult.Fail(NotInList);
            }

            if (!codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        if (codes.Count > (Int(v, "maxItems") ?? MetadataLimits.MultiChoiceDefault))
        {
            return CustomValueResult.Fail(TooLarge);
        }

        return CustomValueResult.Ok(new JsonArray([.. codes.Select(c => (JsonNode?)JsonValue.Create(c))]));
    }

    /// <summary>A decimal from an exact string or a JSON number: at most 15 digits before and 6 after the point.</summary>
    public static bool TryDecimal(JsonElement value, out decimal result)
    {
        result = 0;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return text is not null && DecimalShape().IsMatch(text) && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }

    public static bool TryDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>The pattern as a whole-value, linear-time expression, or null when it cannot be built (lookarounds and back-references are not supported).</summary>
    public static Regex? BuildPattern(string pattern)
    {
        try
        {
            return new Regex("^(?:" + pattern + ")$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Matches(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static int? Int(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;

    private static long? Long(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : null;

    private static decimal? Dec(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty(name, out var p) && TryDecimal(p, out var d) ? d : null;

    private static string? Str(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    [GeneratedRegex(@"^-?\d{1,15}(\.\d{1,6})?$")]
    private static partial Regex DecimalShape();
}
