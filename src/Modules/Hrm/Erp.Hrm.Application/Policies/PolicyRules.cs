using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Hrm.Contracts.Policies;

namespace Erp.Hrm.Application.Policies;

/// <summary>One condition of an assignment rule (ruling Q4): an attribute, an operator and the values it compares with.</summary>
public sealed record PolicyCondition(string Attribute, string? Field, string Operator, IReadOnlyList<JsonElement> Values, bool SubUnits);

/// <summary>An assignment rule: conditions joined by "and"; no conditions means everyone (a catch-all).</summary>
public sealed record PolicyRule(IReadOnlyList<PolicyCondition> Conditions)
{
    public JsonDocument ToJson()
    {
        var conditions = new JsonArray();
        foreach (var c in Conditions)
        {
            var o = new JsonObject { ["attribute"] = c.Attribute, ["operator"] = c.Operator, ["values"] = new JsonArray([.. c.Values.Select(v => JsonNode.Parse(v.GetRawText()))]) };
            if (c.Field is not null)
            {
                o["field"] = c.Field;
            }

            if (c.SubUnits)
            {
                o["subUnits"] = true;
            }

            conditions.Add(o);
        }

        return JsonDocument.Parse(new JsonObject { ["conditions"] = conditions }.ToJsonString());
    }
}

/// <summary>
/// Assignment rules of the policy framework (slice S1.23a Build item 3, ruling Q4): attributes location, branch, grade, employee type,
/// unit (optionally with its sub-units) and a custom field of the employment; operators equals, in and not in; every condition must hold
/// ("and"); "or" is another assignment. No expressions (the rules engine is Later). An employee without a value (no grade, no
/// location, an empty custom field) is in no list: equals and in are not met, not in is met. The company is not a condition: an
/// assignment belongs to one company and applies to its employments only (CR-079 S1.23a detail).
/// </summary>
public static partial class PolicyRules
{
    public const string Location = "location";
    public const string Branch = "branch";
    public const string Grade = "grade";
    public const string EmployeeType = "employee_type";
    public const string Unit = "unit";
    public const string Custom = "custom";

    public const string EqualsOp = "equals";
    public const string In = "in";
    public const string NotIn = "not_in";

    public const int MaxConditions = 10;
    public const int MaxValues = 100;

    public const string Shape = "errors.hrm.policy.rule.shape";
    public const string TooManyConditions = "errors.hrm.policy.rule.too_many_conditions";
    public const string UnknownAttribute = "errors.hrm.policy.rule.unknown_attribute";
    public const string AttributeNotAllowed = "errors.hrm.policy.rule.attribute_not_allowed";
    public const string UnknownOperator = "errors.hrm.policy.rule.unknown_operator";
    public const string ValueCount = "errors.hrm.policy.rule.value_count";
    public const string ValueShape = "errors.hrm.policy.rule.value_shape";
    public const string FieldRequired = "errors.hrm.policy.rule.field_required";
    public const string SubUnitsOnlyForUnit = "errors.hrm.policy.rule.sub_units_only_for_unit";
    public const string Repeated = "errors.hrm.policy.rule.repeated_condition";

    public static IReadOnlyList<string> Attributes { get; } = [Location, Branch, Grade, EmployeeType, Unit, Custom];

    public static IReadOnlyList<string> Operators { get; } = [EqualsOp, In, NotIn];

    private static readonly HashSet<string> IdAttributes = [Location, Branch, Grade, Unit];

    /// <summary>Reads a rule as stored or sent: <c>{"conditions": [{"attribute", "field"?, "operator", "values", "subUnits"?}]}</c>; the error key or the rule.</summary>
    public static (PolicyRule? Rule, string? Error) Parse(JsonElement json, IReadOnlyCollection<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Any(p => p.Name != "conditions"))
        {
            return (null, Shape);
        }

        if (!json.TryGetProperty("conditions", out var list))
        {
            return (new PolicyRule([]), null);
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            return (null, Shape);
        }

        if (list.GetArrayLength() > MaxConditions)
        {
            return (null, TooManyConditions);
        }

        var conditions = new List<PolicyCondition>();
        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || c.EnumerateObject().Any(p => p.Name is not ("attribute" or "field" or "operator" or "values" or "subUnits")))
            {
                return (null, Shape);
            }

            var attribute = Text(c, "attribute");
            if (attribute is null || !Attributes.Contains(attribute))
            {
                return (null, UnknownAttribute);
            }

            if (!allowed.Contains(attribute))
            {
                return (null, AttributeNotAllowed);
            }

            var op = Text(c, "operator");
            if (op is null || !Operators.Contains(op))
            {
                return (null, UnknownOperator);
            }

            var field = Text(c, "field");
            if ((attribute == Custom) != (field is not null) || (field is not null && !FieldKey().IsMatch(field)))
            {
                return (null, FieldRequired);
            }

            var subUnits = c.TryGetProperty("subUnits", out var s) && s.ValueKind == JsonValueKind.True;
            if ((c.TryGetProperty("subUnits", out var s2) && s2.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) || (subUnits && attribute != Unit))
            {
                return (null, SubUnitsOnlyForUnit);
            }

            if (!c.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return (null, ValueCount);
            }

            // Ids are kept in one spelling (lower-case, hyphens) so they compare exactly.
            var items = values.EnumerateArray().Select(v => IdAttributes.Contains(attribute) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
                ? JsonSerializer.SerializeToElement(g) : v.Clone()).ToList();
            if (items.Count == 0 || items.Count > MaxValues || (op == EqualsOp && items.Count != 1))
            {
                return (null, ValueCount);
            }

            if (!items.All(v => Fits(attribute, v)) || items.Count != items.Distinct(JsonComparer.Instance).Count())
            {
                return (null, ValueShape);
            }

            if (conditions.Any(x => x.Attribute == attribute && x.Field == field))
            {
                return (null, Repeated);
            }

            conditions.Add(new PolicyCondition(attribute, field, op, items, subUnits));
        }

        return (new PolicyRule(conditions), null);
    }

    /// <summary>
    /// Evaluates a rule for an employee's attributes on a date. <paramref name="subtrees"/> gives, for each unit named by a condition with
    /// sub-units, that unit and every unit below it on the date. Pure and deterministic.
    /// </summary>
    public static (bool Met, IReadOnlyList<ConditionEvaluation> Conditions) Evaluate(PolicyRule rule, PolicyAttributes who, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> subtrees)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(subtrees);
        var results = new List<ConditionEvaluation>();
        foreach (var c in rule.Conditions)
        {
            var actual = Actual(c, who);
            bool inList;
            if (c.Attribute == Unit && c.SubUnits)
            {
                inList = who.OrgUnitId is { } unit && c.Values.Any(v => subtrees.TryGetValue(v.GetGuid(), out var tree) && tree.Contains(unit));
            }
            else
            {
                inList = actual.Count > 0 && c.Values.Any(v => PolicySchema.Same(v, actual[0]));
            }

            var met = c.Operator == NotIn ? !inList : inList;
            results.Add(new ConditionEvaluation(c.Attribute, c.Field, c.Operator, c.Values, c.SubUnits, actual, met));
        }

        return (results.All(r => r.Met), results);
    }

    /// <summary>The unit ids named by conditions with sub-units (the caller loads their subtrees).</summary>
    public static IEnumerable<Guid> SubtreeRoots(PolicyRule rule) =>
        (rule ?? throw new ArgumentNullException(nameof(rule))).Conditions.Where(c => c.Attribute == Unit && c.SubUnits).SelectMany(c => c.Values.Select(v => v.GetGuid()));

    /// <summary>The ids a rule names for one attribute (to check they exist and to show their names).</summary>
    public static IEnumerable<Guid> Ids(PolicyRule rule, string attribute) =>
        (rule ?? throw new ArgumentNullException(nameof(rule))).Conditions.Where(c => c.Attribute == attribute).SelectMany(c => c.Values.Select(v => v.GetGuid()));

    /// <summary>The employee's value of the condition's attribute: none or one JSON value (an id as a string, a code, a custom value).</summary>
    private static List<JsonElement> Actual(PolicyCondition c, PolicyAttributes who)
    {
        JsonElement? value = c.Attribute switch
        {
            Location => Id(who.LocationId),
            Branch => Id(who.BranchId),
            Grade => Id(who.GradeId),
            Unit => Id(who.OrgUnitId),
            EmployeeType => JsonSerializer.SerializeToElement(who.EmployeeType),
            Custom => who.Custom.ValueKind == JsonValueKind.Object && who.Custom.TryGetProperty(c.Field!, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null,
            _ => null,
        };
        return value is { } some ? [some] : [];
    }

    private static JsonElement? Id(Guid? id) => id is { } g ? JsonSerializer.SerializeToElement(g) : null;

    private static bool Fits(string attribute, JsonElement value) => attribute switch
    {
        _ when IdAttributes.Contains(attribute) => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
        EmployeeType => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 40 },
        // A custom value is a scalar; the field's data type is checked against its definition by the service.
        _ => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
    };

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FieldKey();

    private sealed class JsonComparer : IEqualityComparer<JsonElement>
    {
        public static JsonComparer Instance { get; } = new();

        public bool Equals(JsonElement x, JsonElement y) => PolicySchema.Same(x, y);

        // Numbers hash by value so 1 and 1.0 collide (they are equal).
        public int GetHashCode(JsonElement obj) => obj.ValueKind == JsonValueKind.Number && obj.TryGetDecimal(out var d) ? d.GetHashCode() : obj.ValueKind == JsonValueKind.String ? obj.GetString()!.GetHashCode(StringComparison.Ordinal) : (int)obj.ValueKind;
    }
}
