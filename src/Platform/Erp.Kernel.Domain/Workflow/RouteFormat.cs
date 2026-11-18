using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Erp.Kernel.Domain.Workflow;

/// <summary>Field types a document type may declare (slice S1.10 ruling Q5); the same codes as the module contract.</summary>
public static class FieldTypes
{
    public const string Number = "number";
    public const string Money = "money";
    public const string Date = "date";
    public const string Text = "text";
    public const string Choice = "choice";
    public const string Bool = "bool";
    public const string Company = "company";
    public const string OrgUnit = "org_unit";
    public const string User = "user";

    public static IReadOnlyList<string> All { get; } = [Number, Money, Date, Text, Choice, Bool, Company, OrgUnit, User];

    /// <summary>Types whose values are ordered (greater, less).</summary>
    public static bool IsOrdered(string type) => type is Number or Money or Date;

    /// <summary>Types compared as identifiers.</summary>
    public static bool IsId(string type) => type is Company or OrgUnit or User;
}

/// <summary>A field the route may use: its name, type and, for a choice, the allowed codes.</summary>
public sealed record RouteField(string Name, string Type, IReadOnlyList<string> Choices);

/// <summary>Approver kinds of a step (slice S1.10 ruling Q4). Expression and partner-field kinds are not in 1.0.</summary>
public static class ApproverKinds
{
    public const string User = "user";
    public const string Role = "role";
    public const string DocumentUser = "document_user";
    public const string Manager = "manager";
    public const string DepartmentHead = "department_head";
    public const string Position = "position";

    public static IReadOnlyList<string> All { get; } = [User, Role, DocumentUser, Manager, DepartmentHead, Position];

    /// <summary>Kinds answered by the organisation directory (implemented by HR).</summary>
    public static IReadOnlyList<string> Directory { get; } = [Manager, DepartmentHead, Position];

    /// <summary>Kinds a fallback or escalation target may use.</summary>
    public static IReadOnlyList<string> Fixed { get; } = [User, Role];
}

/// <summary>Who decides a step: a named user, a role (queue), a user field of the document, or a directory question.</summary>
public sealed record ApproverSpec(string Kind, Guid? UserId = null, string? Role = null, string? Field = null, int Levels = 1, string? Position = null);

/// <summary>A step condition (slice S1.10 ruling Q5): a test on one field, or all-of / any-of a list.</summary>
public abstract record Condition;

/// <summary>A test on one declared field. <see cref="Value"/> is absent for <c>empty</c> / <c>not_empty</c>.</summary>
public sealed record FieldCondition(string Field, string Op, JsonElement? Value) : Condition;

/// <summary>All of (<see cref="All"/> true) or any of the items.</summary>
public sealed record ConditionGroup(bool All, IReadOnlyList<Condition> Items) : Condition;

/// <summary>One step of a route, run top to bottom (no branches or parallel steps in 1.0, ruling Q3).</summary>
public sealed record RouteStep(
    string Key,
    IReadOnlyDictionary<string, string> Name,
    Condition? Condition,
    ApproverSpec Approver,
    ApproverSpec? Fallback,
    int? TimeLimitWorkingDays,
    ApproverSpec? Escalation,
    bool BulkApproval);

/// <summary>A route: the content of <c>wf.workflow_versions.definition</c>.</summary>
public sealed record Route(IReadOnlyList<RouteStep> Steps);

/// <summary>One problem of a route: where (for example <c>steps[1].condition</c>) and what (a localization key).</summary>
public sealed record RouteProblem(string Path, string ErrorKey)
{
    public override string ToString() => Path + ": " + ErrorKey;
}

/// <summary>
/// The route format of slice S1.10 (rulings Q2 to Q5, Q10; product-owner rulings R1 and R2 of S1.10a): reading and checking a
/// route against the document type's fields, and writing it back in one canonical shape. Only static checks live here; the
/// customer's users, roles and directory are checked by the administration service.
/// </summary>
public static partial class RouteFormat
{
    public const int MaxSteps = 20;
    public const int MaxTimeLimitDays = 365;
    public const int MaxManagerLevels = 10;
    public const int MaxConditionDepth = 3;

    public static IReadOnlyList<string> Operators { get; } = ["eq", "ne", "gt", "ge", "lt", "le", "in", "not_in", "empty", "not_empty"];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    /// <summary>Reads a route. The route is returned only when there is no problem.</summary>
    public static (Route? Route, IReadOnlyList<RouteProblem> Problems) Parse(JsonElement json, IReadOnlyDictionary<string, RouteField> fields, bool bulkAllowed)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var problems = new List<RouteProblem>();
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("steps", out var stepsJson) || stepsJson.ValueKind != JsonValueKind.Array)
        {
            return (null, [new("route", "errors.workflow.route_format")]);
        }

        Unknown(json, "route", problems, "steps");
        var steps = new List<RouteStep>();
        var count = stepsJson.GetArrayLength();
        if (count == 0 || count > MaxSteps)
        {
            problems.Add(new("steps", "errors.workflow.route_step_count"));
        }

        var index = 0;
        foreach (var s in stepsJson.EnumerateArray())
        {
            var path = $"steps[{index++}]";
            if (s.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new(path, "errors.workflow.route_format"));
                continue;
            }

            Unknown(s, path, problems, "key", "name", "condition", "approver", "fallback", "time_limit_working_days", "escalation", "bulk_approval");
            var key = Text(s, "key");
            if (key is null || !KeyPattern().IsMatch(key))
            {
                problems.Add(new(path + ".key", "errors.workflow.step_key_invalid"));
            }
            else if (steps.Exists(x => x.Key == key))
            {
                problems.Add(new(path + ".key", "errors.workflow.step_key_duplicate"));
            }

            var name = Texts(s, "name");
            if (name is null || !name.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en) || !name.TryGetValue("ur", out var ur) || string.IsNullOrWhiteSpace(ur))
            {
                problems.Add(new(path + ".name", "errors.workflow.step_name_required"));
            }

            Condition? condition = null;
            if (s.TryGetProperty("condition", out var c) && c.ValueKind != JsonValueKind.Null)
            {
                condition = ReadCondition(c, path + ".condition", fields, 1, problems);
            }

            var approver = s.TryGetProperty("approver", out var a) ? ReadApprover(a, path + ".approver", fields, ApproverKinds.All, problems) : null;
            if (approver is null && !problems.Exists(p => p.Path.StartsWith(path + ".approver", StringComparison.Ordinal)))
            {
                problems.Add(new(path + ".approver", "errors.workflow.approver_required"));
            }

            var fallback = s.TryGetProperty("fallback", out var f) && f.ValueKind != JsonValueKind.Null ? ReadApprover(f, path + ".fallback", fields, ApproverKinds.Fixed, problems) : null;
            var escalation = s.TryGetProperty("escalation", out var e) && e.ValueKind != JsonValueKind.Null ? ReadApprover(e, path + ".escalation", fields, ApproverKinds.Fixed, problems) : null;

            int? limit = null;
            if (s.TryGetProperty("time_limit_working_days", out var l) && l.ValueKind != JsonValueKind.Null)
            {
                if (l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var days) && days is >= 1 and <= MaxTimeLimitDays)
                {
                    limit = days;
                }
                else
                {
                    problems.Add(new(path + ".time_limit_working_days", "errors.workflow.time_limit_invalid"));
                }
            }

            var bulk = false;
            if (s.TryGetProperty("bulk_approval", out var b) && b.ValueKind != JsonValueKind.Null)
            {
                if (b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    bulk = b.GetBoolean();
                    if (bulk && !bulkAllowed)
                    {
                        problems.Add(new(path + ".bulk_approval", "errors.workflow.bulk_not_allowed"));
                    }
                }
                else
                {
                    problems.Add(new(path + ".bulk_approval", "errors.workflow.route_format"));
                }
            }

            if (approver is not null)
            {
                steps.Add(new RouteStep(key ?? string.Empty, name ?? new Dictionary<string, string>(), condition, approver, fallback, limit, escalation, bulk));
            }
        }

        // Ruling R2 (S1.10a): at least one step without a condition, so every request reaches at least one person (FR-XM-32).
        if (count > 0 && steps.Count == count && steps.TrueForAll(x => x.Condition is not null))
        {
            problems.Add(new("steps", "errors.workflow.route_needs_unconditional_step"));
        }

        return problems.Count == 0 ? (new Route(steps), problems) : (null, problems);
    }

    /// <summary>Checks the values a module sends against the declared fields (unknown names and wrong types are refused).</summary>
    public static IReadOnlyList<RouteProblem> CheckValues(IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<string, RouteField> fields)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(fields);
        var problems = new List<RouteProblem>();
        foreach (var (name, value) in values)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                problems.Add(new("values." + name, "errors.workflow.value_unknown_field"));
            }
            else if (value.ValueKind != JsonValueKind.Null && !Scalar(value, field))
            {
                problems.Add(new("values." + name, "errors.workflow.value_type"));
            }
        }

        return problems;
    }

    /// <summary>Writes a route in its canonical shape (the stored form).</summary>
    public static string Serialize(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("steps");
            foreach (var s in route.Steps)
            {
                w.WriteStartObject();
                w.WriteString("key", s.Key);
                w.WriteStartObject("name");
                foreach (var (lang, text) in s.Name.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WriteString(lang, text);
                }

                w.WriteEndObject();
                if (s.Condition is not null)
                {
                    w.WritePropertyName("condition");
                    WriteCondition(w, s.Condition);
                }

                WriteApprover(w, "approver", s.Approver);
                if (s.Fallback is not null)
                {
                    WriteApprover(w, "fallback", s.Fallback);
                }

                if (s.TimeLimitWorkingDays is { } days)
                {
                    w.WriteNumber("time_limit_working_days", days);
                }

                if (s.Escalation is not null)
                {
                    WriteApprover(w, "escalation", s.Escalation);
                }

                w.WriteBoolean("bulk_approval", s.BulkApproval);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Whether a JSON value fits the field's type (null is handled by the caller).</summary>
    public static bool Scalar(JsonElement value, RouteField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Type switch
        {
            FieldTypes.Number or FieldTypes.Money => TryDecimal(value, out _),
            FieldTypes.Date => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldTypes.Text => value.ValueKind == JsonValueKind.String,
            FieldTypes.Choice => value.ValueKind == JsonValueKind.String && field.Choices.Contains(value.GetString()!, StringComparer.Ordinal),
            FieldTypes.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FieldTypes.Company or FieldTypes.OrgUnit or FieldTypes.User => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
            _ => false,
        };
    }

    /// <summary>
    /// An exact decimal from a JSON number or a numeric string (money and numbers are never floating point, spec 03).
    /// </summary>
    public static bool TryDecimal(JsonElement value, out decimal result)
    {
        result = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out result),
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }

    /// <summary>
    /// Reads one condition in the route format over <paramref name="fields"/> (slice S1.11b1: audience rules of notifications
    /// use the same structured conditions as workflow, ruling Q14). The condition is null when there are problems.
    /// </summary>
    public static (Condition? Condition, IReadOnlyList<RouteProblem> Problems) ParseCondition(JsonElement json, IReadOnlyDictionary<string, RouteField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var problems = new List<RouteProblem>();
        var condition = ReadCondition(json, "condition", fields, 1, problems);
        return (problems.Count == 0 ? condition : null, problems);
    }

    private static Condition? ReadCondition(JsonElement c, string path, IReadOnlyDictionary<string, RouteField> fields, int depth, List<RouteProblem> problems)
    {
        if (c.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(path, "errors.workflow.condition_format"));
            return null;
        }

        var isAll = c.TryGetProperty("all", out var all);
        var isAny = c.TryGetProperty("any", out var any);
        if (isAll || isAny)
        {
            Unknown(c, path, problems, "all", "any");
            var list = isAll ? all : any;
            if (isAll && isAny || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                problems.Add(new(path, "errors.workflow.condition_format"));
                return null;
            }

            if (depth >= MaxConditionDepth)
            {
                problems.Add(new(path, "errors.workflow.condition_too_deep"));
                return null;
            }

            var items = new List<Condition>();
            var i = 0;
            foreach (var item in list.EnumerateArray())
            {
                if (ReadCondition(item, $"{path}.{(isAll ? "all" : "any")}[{i++}]", fields, depth + 1, problems) is { } read)
                {
                    items.Add(read);
                }
            }

            return items.Count == list.GetArrayLength() ? new ConditionGroup(isAll, items) : null;
        }

        Unknown(c, path, problems, "field", "op", "value");
        var name = Text(c, "field");
        var op = Text(c, "op");
        if (name is null || !fields.TryGetValue(name, out var field))
        {
            problems.Add(new(path + ".field", "errors.workflow.condition_unknown_field"));
            return null;
        }

        if (op is null || !Operators.Contains(op, StringComparer.Ordinal))
        {
            problems.Add(new(path + ".op", "errors.workflow.condition_operator"));
            return null;
        }

        var hasValue = c.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Null;
        switch (op)
        {
            case "empty" or "not_empty":
                if (hasValue)
                {
                    problems.Add(new(path + ".value", "errors.workflow.condition_value"));
                    return null;
                }

                return new FieldCondition(name, op, null);
            case "gt" or "ge" or "lt" or "le" when !FieldTypes.IsOrdered(field.Type):
            case "in" or "not_in" when field.Type is FieldTypes.Bool or FieldTypes.Date or FieldTypes.Money:
                problems.Add(new(path + ".op", "errors.workflow.condition_operator_type"));
                return null;
            case "in" or "not_in":
                if (!hasValue || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 || value.EnumerateArray().Any(v => !Scalar(v, field)))
                {
                    problems.Add(new(path + ".value", "errors.workflow.condition_value"));
                    return null;
                }

                return new FieldCondition(name, op, value.Clone());
            default:
                if (!hasValue || !Scalar(value, field))
                {
                    problems.Add(new(path + ".value", "errors.workflow.condition_value"));
                    return null;
                }

                return new FieldCondition(name, op, value.Clone());
        }
    }

    private static ApproverSpec? ReadApprover(JsonElement a, string path, IReadOnlyDictionary<string, RouteField> fields, IReadOnlyList<string> kinds, List<RouteProblem> problems)
    {
        if (a.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(path, "errors.workflow.approver_format"));
            return null;
        }

        var kind = Text(a, "kind");
        if (kind is null || !kinds.Contains(kind, StringComparer.Ordinal))
        {
            problems.Add(new(path + ".kind", "errors.workflow.approver_kind"));
            return null;
        }

        switch (kind)
        {
            case ApproverKinds.User:
                Unknown(a, path, problems, "kind", "user_id");
                if (Text(a, "user_id") is not { } id || !Guid.TryParse(id, out var userId))
                {
                    problems.Add(new(path + ".user_id", "errors.workflow.approver_user_required"));
                    return null;
                }

                return new ApproverSpec(kind, UserId: userId);
            case ApproverKinds.Role:
                Unknown(a, path, problems, "kind", "role");
                if (Text(a, "role") is not { Length: > 0 } role)
                {
                    problems.Add(new(path + ".role", "errors.workflow.approver_role_required"));
                    return null;
                }

                return new ApproverSpec(kind, Role: role);
            case ApproverKinds.DocumentUser:
                Unknown(a, path, problems, "kind", "field");
                if (Text(a, "field") is not { } name || !fields.TryGetValue(name, out var field) || field.Type != FieldTypes.User)
                {
                    problems.Add(new(path + ".field", "errors.workflow.approver_field_not_user"));
                    return null;
                }

                return new ApproverSpec(kind, Field: name);
            case ApproverKinds.Manager:
                Unknown(a, path, problems, "kind", "levels");
                var levels = 1;
                if (a.TryGetProperty("levels", out var l) && l.ValueKind != JsonValueKind.Null
                    && !(l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out levels) && levels is >= 1 and <= MaxManagerLevels))
                {
                    problems.Add(new(path + ".levels", "errors.workflow.approver_levels"));
                    return null;
                }

                return new ApproverSpec(kind, Levels: levels);
            case ApproverKinds.Position:
                Unknown(a, path, problems, "kind", "position");
                if (Text(a, "position") is not { Length: > 0 } position)
                {
                    problems.Add(new(path + ".position", "errors.workflow.approver_position_required"));
                    return null;
                }

                return new ApproverSpec(kind, Position: position);
            default:
                Unknown(a, path, problems, "kind");
                return new ApproverSpec(kind);
        }
    }

    private static void WriteApprover(Utf8JsonWriter w, string property, ApproverSpec a)
    {
        w.WriteStartObject(property);
        w.WriteString("kind", a.Kind);
        switch (a.Kind)
        {
            case ApproverKinds.User:
                w.WriteString("user_id", a.UserId!.Value);
                break;
            case ApproverKinds.Role:
                w.WriteString("role", a.Role);
                break;
            case ApproverKinds.DocumentUser:
                w.WriteString("field", a.Field);
                break;
            case ApproverKinds.Manager:
                w.WriteNumber("levels", a.Levels);
                break;
            case ApproverKinds.Position:
                w.WriteString("position", a.Position);
                break;
        }

        w.WriteEndObject();
    }

    private static void WriteCondition(Utf8JsonWriter w, Condition c)
    {
        w.WriteStartObject();
        switch (c)
        {
            case ConditionGroup g:
                w.WriteStartArray(g.All ? "all" : "any");
                foreach (var item in g.Items)
                {
                    WriteCondition(w, item);
                }

                w.WriteEndArray();
                break;
            case FieldCondition f:
                w.WriteString("field", f.Field);
                w.WriteString("op", f.Op);
                if (f.Value is { } v)
                {
                    w.WritePropertyName("value");
                    v.WriteTo(w);
                }

                break;
        }

        w.WriteEndObject();
    }

    private static void Unknown(JsonElement element, string path, List<RouteProblem> problems, params string[] known)
    {
        foreach (var p in element.EnumerateObject())
        {
            if (!known.Contains(p.Name, StringComparer.Ordinal))
            {
                problems.Add(new(path + "." + p.Name, "errors.workflow.route_unknown_property"));
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, string>? Texts(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            result[p.Name] = p.Value.GetString()!;
        }

        return result;
    }
}

/// <summary>
/// Decides a step condition on the request's frozen values (slice S1.10 ruling Q5). Numbers and money compare as exact
/// decimals, dates as dates, ids as ids, text and choices exactly. A missing or empty value never equals, is never
/// greater or less and is never in a list; it is "empty", "not equal" and "not in".
/// </summary>
public static class ConditionEvaluator
{
    private const int Mismatch = int.MinValue;

    public static bool Evaluate(Condition condition, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<string, RouteField> fields)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(fields);
        return condition switch
        {
            ConditionGroup g => g.All ? g.Items.All(i => Evaluate(i, values, fields)) : g.Items.Any(i => Evaluate(i, values, fields)),
            FieldCondition f => Field(f, values, fields[f.Field]),
            _ => false,
        };
    }

    private static bool Field(FieldCondition f, IReadOnlyDictionary<string, JsonElement> values, RouteField field)
    {
        var present = values.TryGetValue(f.Field, out var value) && value.ValueKind != JsonValueKind.Null
            && !(value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0);
        switch (f.Op)
        {
            case "empty":
                return !present;
            case "not_empty":
                return present;
            case "ne":
                return !present || Compare(value, f.Value!.Value, field) != 0;
            case "not_in":
                return !present || f.Value!.Value.EnumerateArray().All(v => Compare(value, v, field) != 0);
        }

        if (!present)
        {
            return false;
        }

        if (f.Op == "in")
        {
            return f.Value!.Value.EnumerateArray().Any(v => Compare(value, v, field) == 0);
        }

        var cmp = Compare(value, f.Value!.Value, field);
        return cmp != Mismatch && f.Op switch
        {
            "eq" => cmp == 0,
            "gt" => cmp > 0,
            "ge" => cmp >= 0,
            "lt" => cmp < 0,
            "le" => cmp <= 0,
            _ => false,
        };
    }

    /// <summary>Compares a value with a condition value of the same field; a value of the wrong type never matches (int.MinValue).</summary>
    private static int Compare(JsonElement left, JsonElement right, RouteField field)
    {
        if (!RouteFormat.Scalar(left, field) || !RouteFormat.Scalar(right, field))
        {
            return Mismatch;
        }

        switch (field.Type)
        {
            case FieldTypes.Number or FieldTypes.Money:
                RouteFormat.TryDecimal(left, out var a);
                RouteFormat.TryDecimal(right, out var b);
                return a.CompareTo(b);
            case FieldTypes.Date:
                return DateOnly.ParseExact(left.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    .CompareTo(DateOnly.ParseExact(right.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            case FieldTypes.Bool:
                return left.GetBoolean() == right.GetBoolean() ? 0 : 1;
            case FieldTypes.Company or FieldTypes.OrgUnit or FieldTypes.User:
                return Guid.Parse(left.GetString()!) == Guid.Parse(right.GetString()!) ? 0 : 1;
            default:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal) ? 0 : 1;
        }
    }
}
