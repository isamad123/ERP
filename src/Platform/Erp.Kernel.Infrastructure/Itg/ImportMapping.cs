using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Application.Itg;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>One mapped column of a template: the import type's column (<see cref="Field"/> names the column or its field), the header of the file it reads, the transformations and a default.</summary>
public sealed record TemplateColumn(string Field, string Source, IReadOnlyList<string> Transform, string? Default);

/// <summary>The mapping of a template (<c>itg.import_templates.mapping</c>): delimiter, sheet, header row and the columns (slice S1.16a, FR-KRN-INT-03).</summary>
public sealed record TemplateMapping(char? Delimiter, string? Sheet, int HeaderRow, IReadOnlyList<TemplateColumn> Columns)
{
    public static TemplateMapping Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The mapping must be an object.");
        }

        char? delimiter = null;
        if (json.TryGetProperty("delimiter", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: 1 } one && one[0] is ',' or ';' or '\t' or '|')
        {
            delimiter = d.GetString()![0];
        }

        var sheet = json.TryGetProperty("sheet", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()) ? s.GetString() : null;
        var header = json.TryGetProperty("headerRow", out var h) && h.TryGetInt32(out var hr) && hr >= 1 ? hr : 1;
        var columns = new List<TemplateColumn>();
        if (json.TryGetProperty("columns", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray())
            {
                var field = c.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                var source = c.TryGetProperty("source", out var so) && so.ValueKind == JsonValueKind.String ? so.GetString() : null;
                if (string.IsNullOrWhiteSpace(field))
                {
                    throw new InvalidDataException("Every mapped column needs a field.");
                }

                var transforms = new List<string>();
                if (c.TryGetProperty("transform", out var t) && t.ValueKind == JsonValueKind.Array)
                {
                    transforms.AddRange(t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }

                var def = c.TryGetProperty("default", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
                columns.Add(new TemplateColumn(field, source ?? string.Empty, transforms, def));
            }
        }

        return new TemplateMapping(delimiter, sheet, header, columns);
    }

    /// <summary>The default mapping of an import type: every column reads the header of the same name.</summary>
    public static JsonObject DefaultFor(ImportTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var columns = new JsonArray();
        foreach (var c in type.Columns)
        {
            columns.Add(new JsonObject { ["field"] = c.Name, ["source"] = c.Name, ["transform"] = new JsonArray("trim"), ["default"] = null });
        }

        return new JsonObject { ["delimiter"] = null, ["sheet"] = null, ["headerRow"] = 1, ["columns"] = columns };
    }

    /// <summary>The problems of a mapping against its import type (unknown field, unknown transformation, no source). Empty when it is usable.</summary>
    public IReadOnlyList<string> Check(ImportTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var problems = new List<string>();
        if (HeaderRow != 1)
        {
            problems.Add("headerRow: only the first row can hold the headers");
        }

        foreach (var c in Columns)
        {
            if (Column(type, c.Field) is null)
            {
                problems.Add($"{c.Field}: not a column of {type.Code}");
            }

            if (string.IsNullOrWhiteSpace(c.Source) && c.Default is null)
            {
                problems.Add($"{c.Field}: no source column and no default");
            }

            problems.AddRange(c.Transform.Where(t => !ImportTransforms.IsKnown(t)).Select(t => $"{c.Field}: unknown transformation {t}"));
        }

        foreach (var required in type.Columns.Where(c => c.Required))
        {
            if (!Columns.Any(c => ColumnMatches(required, c.Field) && (!string.IsNullOrWhiteSpace(c.Source) || c.Default is not null)))
            {
                problems.Add($"{required.Name}: required column is not mapped");
            }
        }

        return problems;
    }

    public static ImportColumnDefinition? Column(ImportTypeDefinition type, string field) =>
        type.Columns.FirstOrDefault(c => ColumnMatches(c, field));

    private static bool ColumnMatches(ImportColumnDefinition column, string field) =>
        string.Equals(column.Name, field, StringComparison.OrdinalIgnoreCase) || string.Equals(column.Field, field, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The cell transformations of a template (trim, upper, lower, <c>date:&lt;format&gt;</c>, <c>number:&lt;culture&gt;</c>); pure functions of the cell text.</summary>
public static class ImportTransforms
{
    public static bool IsKnown(string transform) =>
        transform is "trim" or "upper" or "lower" || transform.StartsWith("date:", StringComparison.Ordinal) && transform.Length > 5
        || transform.StartsWith("number:", StringComparison.Ordinal) && transform.Length > 7;

    /// <summary>Applies the transformations in order. Returns the new text, or an error key when a date or number does not fit its format.</summary>
    public static (string? Value, string? Error) Apply(string? cell, IEnumerable<string> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        var value = cell;
        foreach (var t in transforms)
        {
            if (value is null)
            {
                break;
            }

            switch (t)
            {
                case "trim":
                    value = value.Trim();
                    break;
                case "upper":
                    value = value.ToUpperInvariant();
                    break;
                case "lower":
                    value = value.ToLowerInvariant();
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        break;
                    }

                    if (t.StartsWith("date:", StringComparison.Ordinal))
                    {
                        if (!DateTime.TryParseExact(value.Trim(), t[5..], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        {
                            return (value, "errors.import.not_a_date");
                        }

                        value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    else if (t.StartsWith("number:", StringComparison.Ordinal))
                    {
                        CultureInfo culture;
                        try
                        {
                            culture = CultureInfo.GetCultureInfo(t[7..]);
                        }
                        catch (CultureNotFoundException)
                        {
                            return (value, "errors.import.template_invalid");
                        }

                        if (!decimal.TryParse(value.Trim(), NumberStyles.Number | NumberStyles.AllowLeadingSign, culture, out var number))
                        {
                            return (value, "errors.import.not_a_number");
                        }

                        value = number.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
            }
        }

        return (value, null);
    }

    private static readonly string[] Yes = ["yes", "y", "true", "1", "ہاں"];
    private static readonly string[] No = ["no", "n", "false", "0", "نہیں"];

    /// <summary>Turns cell text into the JSON value of a column type. Null means the cell is empty; an error key means it does not fit.</summary>
    public static (JsonNode? Value, string? Error) ToJson(ImportColumnDefinition column, string? text)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var t = text.Trim();
        switch (column.Type)
        {
            case "number":
                return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                    ? (JsonValue.Create(n), null) : (null, "errors.import.not_a_number");
            case "date":
                return DateOnly.TryParseExact(t, ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? (JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null) : (null, "errors.import.not_a_date");
            case "bool":
                return Yes.Contains(t, StringComparer.OrdinalIgnoreCase) ? (JsonValue.Create(true), null)
                    : No.Contains(t, StringComparer.OrdinalIgnoreCase) ? (JsonValue.Create(false), null) : (null, "errors.import.not_a_yes_no");
            case "option":
                var match = column.Options?.FirstOrDefault(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase));
                return match is null ? (null, "errors.import.not_an_option") : (JsonValue.Create(match), null);
            default:
                return (JsonValue.Create(t), null);
        }
    }

    /// <summary>Sets a value at a dotted field (<c>nameI18n.en</c> builds <c>{"nameI18n": {"en": ...}}</c>).</summary>
    public static void Set(JsonObject target, string field, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        var parts = field.Split('.');
        var node = target;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject next)
            {
                next = [];
                node[parts[i]] = next;
            }

            node = next;
        }

        node[parts[^1]] = value;
    }
}
