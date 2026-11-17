using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Erp.Kernel.Domain.Settings;

/// <summary>
/// Layers of a setting, least specific first (FR-KRN-SET-01, FR-KRN-SET-02; slice S1.09 ruling Q11). The product layer is
/// the definition's default; the others are rows of cfg.setting_values. The tenant layer is "the customer".
/// </summary>
#if ERP_CODEGEN
internal static class SettingLayers
#else
public static class SettingLayers
#endif
{
    public const string Product = "product";
    public const string Pack = "pack";
    public const string Tenant = "tenant";
    public const string Company = "company";
    public const string Role = "role";
    public const string User = "user";

    /// <summary>All layers, least specific first.</summary>
    public static IReadOnlyList<string> Order { get; } = [Product, Pack, Tenant, Company, Role, User];

    /// <summary>Layers a value can be stored in.</summary>
    public static IReadOnlyList<string> Stored { get; } = [Pack, Tenant, Company, Role, User];
}

/// <summary>Data types of a setting (spec 03 section 5.4, <c>cfg.settings.data_type</c>).</summary>
#if ERP_CODEGEN
internal static class SettingTypes
#else
public static class SettingTypes
#endif
{
    public const string Bool = "bool";
    public const string WholeNumber = "int";
    public const string Number = "decimal";
    public const string Text = "text";
    public const string Choice = "choice";
    public const string List = "list";
    public const string Json = "json";

    public static IReadOnlyList<string> All { get; } = [Bool, WholeNumber, Number, Text, Choice, List, Json];
}

/// <summary>How the values of several roles combine (ruling Q11): <c>any</c>/<c>all</c> for yes/no, <c>lowest</c>/<c>highest</c> for numbers.</summary>
#if ERP_CODEGEN
internal static class SettingRoleRules
#else
public static class SettingRoleRules
#endif
{
    public const string Any = "any";
    public const string All = "all";
    public const string Lowest = "lowest";
    public const string Highest = "highest";

    /// <summary>True when the rule fits the data type.</summary>
    public static bool Fits(string rule, string dataType) => rule switch
    {
        Any or All => dataType == SettingTypes.Bool,
        Lowest or Highest => dataType is SettingTypes.WholeNumber or SettingTypes.Number,
        _ => false,
    };
}

/// <summary>
/// The validation rules of a setting (ruling Q10): <c>min</c> and <c>max</c> (numbers), <c>choices</c> (choice and list
/// items), <c>pattern</c> (text, whole value), <c>max_length</c> (text and list items), <c>item_type</c> (list: text or int,
/// default text), <c>min_items</c> and <c>max_items</c> (list). Stored as JSON in <c>cfg.settings.validation</c>.
/// Shared with the code generator, which checks every default against the same rules.
/// </summary>
#if ERP_CODEGEN
internal sealed record SettingValidation(
#else
public sealed record SettingValidation(
#endif
    decimal? Min,
    decimal? Max,
    IReadOnlyList<string>? Choices,
    string? Pattern,
    int? MaxLength,
    string? ItemType,
    int? MinItems,
    int? MaxItems)
{
    public static readonly IReadOnlyList<string> Keys = ["min", "max", "choices", "pattern", "max_length", "item_type", "min_items", "max_items"];

    public static SettingValidation None { get; } = new(null, null, null, null, null, null, null, null);

    /// <summary>Reads stored rules. Keys this version does not know are ignored, so an older host never fails on newer data.</summary>
    public static SettingValidation Parse(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        decimal? Number(string name) => rules.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
        int? Whole(string name) => rules.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        string? Text(string name) => rules.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        IReadOnlyList<string>? Texts(string name) => rules.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : null;
        return new SettingValidation(Number("min"), Number("max"), Texts("choices"), Text("pattern"), Whole("max_length"), Text("item_type"), Whole("min_items"), Whole("max_items"));
    }

    /// <summary>Null when <paramref name="value"/> is a valid value of the type under these rules, else what is wrong.</summary>
    public string? Problem(string dataType, JsonElement value) => dataType switch
    {
        SettingTypes.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "not_bool",
        SettingTypes.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? Range(i) : "not_int",
        SettingTypes.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) ? Range(d) : "not_decimal",
        SettingTypes.Text => value.ValueKind == JsonValueKind.String ? TextProblem(value.GetString()!) : "not_text",
        SettingTypes.Choice => value.ValueKind == JsonValueKind.String && Choices is not null && Choices.Contains(value.GetString()!, StringComparer.Ordinal) ? null : "not_a_choice",
        SettingTypes.List => ListProblem(value),
        SettingTypes.Json => value.ValueKind is JsonValueKind.Undefined ? "not_json" : null,
        _ => "unknown_type",
    };

    private string? Range(decimal number) =>
        Min is { } min && number < min ? "below_min"
        : Max is { } max && number > max ? "above_max"
        : null;

    private string? TextProblem(string text)
    {
        if (MaxLength is { } length && text.Length > length)
        {
            return "too_long";
        }

        if (Choices is not null && !Choices.Contains(text, StringComparer.Ordinal))
        {
            return "not_a_choice";
        }

        return Pattern is not null && !Regex.IsMatch(text, "^(?:" + Pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? "pattern" : null;
    }

    private string? ListProblem(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return "not_list";
        }

        var count = value.GetArrayLength();
        if ((MinItems is { } least && count < least) || (MaxItems is { } most && count > most))
        {
            return "item_count";
        }

        foreach (var item in value.EnumerateArray())
        {
            var problem = (ItemType ?? SettingTypes.Text) switch
            {
                SettingTypes.WholeNumber => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n) ? Range(n) : "item_type",
                _ => item.ValueKind == JsonValueKind.String ? TextProblem(item.GetString()!) : "item_type",
            };
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// Problems of the rules themselves for a data type (used by the code generator on every manifest): rules that do not
    /// apply to the type, a choice without choices, an unknown item type, minimum above maximum, a pattern that is not a
    /// regular expression.
    /// </summary>
    public IEnumerable<string> DefinitionProblems(string dataType)
    {
        var numeric = dataType is SettingTypes.WholeNumber or SettingTypes.Number || (dataType == SettingTypes.List && ItemType == SettingTypes.WholeNumber);
        if ((Min is not null || Max is not null) && !numeric)
        {
            yield return "min and max apply to numbers only";
        }

        if (Min is { } min && Max is { } max && min > max)
        {
            yield return "min is above max";
        }

        if (dataType == SettingTypes.Choice && (Choices is null || Choices.Count == 0))
        {
            yield return "a choice setting needs choices";
        }

        if (Choices is not null && dataType is not (SettingTypes.Choice or SettingTypes.Text or SettingTypes.List))
        {
            yield return "choices apply to choice, text and list settings only";
        }

        if ((Pattern is not null || MaxLength is not null) && dataType is not (SettingTypes.Text or SettingTypes.List))
        {
            yield return "pattern and max_length apply to text and list settings only";
        }

        if ((ItemType is not null || MinItems is not null || MaxItems is not null) && dataType != SettingTypes.List)
        {
            yield return "item_type, min_items and max_items apply to list settings only";
        }

        if (ItemType is not null and not (SettingTypes.Text or SettingTypes.WholeNumber))
        {
            yield return "item_type is text or int";
        }

        if (Pattern is not null && !IsRegex(Pattern))
        {
            yield return "pattern is not a valid regular expression";
        }
    }

    private static bool IsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The rules as stored JSON (only the rules that are set, in a fixed order).</summary>
    public string ToJson()
    {
        var parts = new List<string>();
        void Add(string name, string json) => parts.Add(JsonSerializer.Serialize(name) + ":" + json);
        if (Min is { } min)
        {
            Add("min", min.ToString(CultureInfo.InvariantCulture));
        }

        if (Max is { } max)
        {
            Add("max", max.ToString(CultureInfo.InvariantCulture));
        }

        if (Choices is not null)
        {
            Add("choices", JsonSerializer.Serialize(Choices));
        }

        if (Pattern is not null)
        {
            Add("pattern", JsonSerializer.Serialize(Pattern));
        }

        if (MaxLength is { } length)
        {
            Add("max_length", length.ToString(CultureInfo.InvariantCulture));
        }

        if (ItemType is not null)
        {
            Add("item_type", JsonSerializer.Serialize(ItemType));
        }

        if (MinItems is { } least)
        {
            Add("min_items", least.ToString(CultureInfo.InvariantCulture));
        }

        if (MaxItems is { } most)
        {
            Add("max_items", most.ToString(CultureInfo.InvariantCulture));
        }

        return "{" + string.Join(",", parts) + "}";
    }
}
