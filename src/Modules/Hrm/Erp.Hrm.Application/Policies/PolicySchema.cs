using System.Globalization;
using System.Text.Json;

namespace Erp.Hrm.Application.Policies;

/// <summary>
/// The JSON Schema subset a policy type describes its parameters with (slice S1.23a Build item 1, ruling Q2; no package, D-09):
/// <c>type</c> (object, array, string, integer, number, boolean), <c>properties</c>, <c>required</c>, <c>additionalProperties</c>
/// (false: unknown keys refused), <c>enum</c>, <c>minimum</c>, <c>maximum</c>, <c>minLength</c>, <c>maxLength</c>, <c>minItems</c>,
/// <c>maxItems</c>, <c>items</c>, and the annotations <c>title</c> (a text key) and <c>description</c>. A manifest using anything else
/// is refused when it is loaded, so a later slice cannot rely on a keyword that is not checked.
/// </summary>
public static class PolicySchema
{
    public const string Required = "errors.hrm.policy.param.required";
    public const string WrongType = "errors.hrm.policy.param.type";
    public const string Unknown = "errors.hrm.policy.param.unknown";
    public const string NotAllowed = "errors.hrm.policy.param.enum";
    public const string BelowMinimum = "errors.hrm.policy.param.minimum";
    public const string AboveMaximum = "errors.hrm.policy.param.maximum";
    public const string TooShort = "errors.hrm.policy.param.min_length";
    public const string TooLong = "errors.hrm.policy.param.max_length";
    public const string TooFew = "errors.hrm.policy.param.min_items";
    public const string TooMany = "errors.hrm.policy.param.max_items";

    private static readonly HashSet<string> Keywords =
        ["type", "properties", "required", "additionalProperties", "enum", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "items", "title", "description"];

    private static readonly HashSet<string> Types = ["object", "array", "string", "integer", "number", "boolean"];

    /// <summary>Throws <see cref="InvalidOperationException"/> when the schema uses a keyword or shape this checker does not support.</summary>
    public static void EnsureSupported(JsonElement schema, string where)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{where}: a schema must be an object.");
        }

        foreach (var p in schema.EnumerateObject())
        {
            if (!Keywords.Contains(p.Name))
            {
                throw new InvalidOperationException($"{where}: schema keyword '{p.Name}' is not supported.");
            }
        }

        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !Types.Contains(type.GetString()!))
        {
            throw new InvalidOperationException($"{where}: every schema needs a type of {string.Join(", ", Types)}.");
        }

        if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException($"{where}: additionalProperties must be true or false.");
        }

        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
            {
                EnsureSupported(p.Value, where + "." + p.Name);
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var r in required.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String || !props.ValueKindIs(JsonValueKind.Object) || !props.TryGetProperty(r.GetString()!, out _))
                {
                    throw new InvalidOperationException($"{where}: required names a property the schema does not declare.");
                }
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            EnsureSupported(items, where + "[]");
        }
    }

    /// <summary>The errors of a value, path to error keys (empty when the value is valid). Paths read <c>bands[0].days</c>; the root is <c>parameters</c>.</summary>
    public static Dictionary<string, string[]> Check(JsonElement schema, JsonElement value)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Walk(schema, value, "parameters", errors);
        return errors.ToDictionary(e => e.Key, e => e.Value.Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private static void Walk(JsonElement schema, JsonElement value, string path, Dictionary<string, List<string>> errors)
    {
        var type = schema.GetProperty("type").GetString();
        if (!Fits(type!, value))
        {
            Add(errors, path, WrongType);
            return;
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(a => Same(a, value)))
        {
            Add(errors, path, NotAllowed);
        }

        switch (type)
        {
            case "object":
                var props = schema.TryGetProperty("properties", out var p) ? p : default;
                var closed = schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False;
                if (schema.TryGetProperty("required", out var required))
                {
                    foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    {
                        if (!value.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                        {
                            Add(errors, Join(path, name), Required);
                        }
                    }
                }

                foreach (var member in value.EnumerateObject())
                {
                    if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(member.Name, out var sub))
                    {
                        if (member.Value.ValueKind != JsonValueKind.Null)
                        {
                            Walk(sub, member.Value, Join(path, member.Name), errors);
                        }
                    }
                    else if (closed)
                    {
                        Add(errors, Join(path, member.Name), Unknown);
                    }
                }

                break;
            case "array":
                var count = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out var minItems) && count < minItems.GetInt32())
                {
                    Add(errors, path, TooFew);
                }

                if (schema.TryGetProperty("maxItems", out var maxItems) && count > maxItems.GetInt32())
                {
                    Add(errors, path, TooMany);
                }

                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Walk(items, item, string.Create(CultureInfo.InvariantCulture, $"{path}[{i++}]"), errors);
                    }
                }

                break;
            case "string":
                var length = value.GetString()!.Length;
                if (schema.TryGetProperty("minLength", out var minLength) && length < minLength.GetInt32())
                {
                    Add(errors, path, TooShort);
                }

                if (schema.TryGetProperty("maxLength", out var maxLength) && length > maxLength.GetInt32())
                {
                    Add(errors, path, TooLong);
                }

                break;
            case "integer" or "number":
                var number = value.GetDecimal();
                if (schema.TryGetProperty("minimum", out var min) && number < min.GetDecimal())
                {
                    Add(errors, path, BelowMinimum);
                }

                if (schema.TryGetProperty("maximum", out var max) && number > max.GetDecimal())
                {
                    Add(errors, path, AboveMaximum);
                }

                break;
        }
    }

    private static bool Fits(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        // Exact decimals only (no floating point): an integer has no fraction; a number fits decimal.
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && decimal.Truncate(d) == d && d is >= int.MinValue and <= int.MaxValue,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
        _ => false,
    };

    /// <summary>JSON values compared by meaning: numbers by value, strings exactly, booleans and null by kind.</summary>
    public static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) && x == y;
        }

        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => JsonElement.DeepEquals(a, b),
        };
    }

    private static string Join(string path, string name) => path + "." + name;

    private static void Add(Dictionary<string, List<string>> errors, string path, string key)
    {
        if (!errors.TryGetValue(path, out var list))
        {
            errors[path] = list = [];
        }

        list.Add(key);
    }

    private static bool ValueKindIs(this JsonElement element, JsonValueKind kind) => element.ValueKind == kind;
}
