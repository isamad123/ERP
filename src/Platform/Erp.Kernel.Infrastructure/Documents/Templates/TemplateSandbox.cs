using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>
/// Scriban in a sandbox (KRN-DOC-05, ruling Q17). A template sees only the values the owning module supplied: no file or include
/// access, no .NET objects, no clock of its own (<c>now</c> and <c>today</c> come from the injected clock), a loop, recursion,
/// output and time limit. Text values are HTML-encoded for HTML and PDF output; the template's own markup is trusted (it is
/// written by an administrator). <c>qr</c> and <c>barcode</c> give images as SVG.
/// </summary>
public static partial class TemplateSandbox
{
    private const int LoopLimit = 5000;
    private const int RecursionLimit = 50;
    private const int OutputLimit = 4 * 1024 * 1024;

    /// <summary>Checks the syntax of a template; returns the problems (empty when the text is valid).</summary>
    public static IReadOnlyList<string> SyntaxErrors(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var template = Template.Parse(body);
        return [.. template.Messages.Where(m => m.Type == ParserMessageType.Error).Select(m => m.Message)];
    }

    /// <summary>Renders a body with values. Unknown names are an error when <paramref name="strict"/> is set. Throws <see cref="DocumentException"/> on a template error.</summary>
    public static string Render(string body, IReadOnlyDictionary<string, object?> values, string format, string language, DateTimeOffset now, bool strict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(values);
        var template = Template.Parse(body);
        if (template.HasErrors)
        {
            throw Failure(TemplateErrors.Syntax, string.Join("; ", template.Messages.Where(m => m.Type == ParserMessageType.Error).Select(m => m.Message)));
        }

        var encode = format is TemplateFormats.Html or TemplateFormats.Pdf;
        var model = new ScriptObject();
        foreach (var (key, value) in values)
        {
            model.SetValue(key, Convert(value, encode), true);
        }

        model.SetValue("now", now.UtcDateTime, true);
        model.SetValue("today", now.UtcDateTime.Date, true);
        model.Import("qr", new Func<string?, string>(text => Qr.Svg(text ?? string.Empty)));
        model.Import("barcode", new Func<string?, string>(text => Code128.Svg(text ?? string.Empty)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var context = new TemplateContext
        {
            StrictVariables = strict,
            LoopLimit = LoopLimit,
            RecursiveLimit = RecursionLimit,
            OutputLimit = OutputLimit,
            LimitToString = OutputLimit,
            RegexTimeOut = TimeSpan.FromSeconds(1),
            TemplateLoader = null,
            EnableRelaxedMemberAccess = false,
            CancellationToken = timeout.Token,
        };
        context.BuiltinObject.Remove("date"); // date.now would read the machine clock; now and today come from the injected clock
        context.BuiltinObject.SetValue("date", SafeDate(), true);
        context.PushCulture(CultureOf(language));
        context.PushGlobal(model);
        try
        {
            return template.Render(context);
        }
        catch (Exception e) when (e is ScriptRuntimeException or ScriptParserRuntimeException or OperationCanceledException or InvalidOperationException)
        {
            throw Failure(e is ScriptRuntimeException && (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Cannot get member", StringComparison.OrdinalIgnoreCase)) ? TemplateErrors.UnknownField : TemplateErrors.RenderFailed, e.Message);
        }
    }

    /// <summary>
    /// The names a template mentions inside its <c>{{ }}</c> blocks. Used to find which declared fields a template uses
    /// (ruling Q26); a name that only looks like a field is counted, so the check errs on the side of caution.
    /// </summary>
    public static IReadOnlySet<string> Mentions(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Block().Matches(body))
        {
            foreach (Match token in Name().Matches(block.Groups[1].Value))
            {
                names.Add(token.Value);
            }
        }

        return names;
    }

    /// <summary>
    /// The declared fields a body uses: a mention equal to the field's path or below it; for a field inside a list, also any
    /// <c>name.leaf</c> mention (the loop variable stands for the list item, so its path cannot be matched exactly).
    /// </summary>
    public static IReadOnlyList<TemplateFieldDefinition> UsedFields(string body, IEnumerable<TemplateFieldDefinition> fields)
    {
        var mentions = Mentions(body);
        var leaves = mentions.Where(m => m.Contains('.', StringComparison.Ordinal)).Select(m => m[(m.LastIndexOf('.') + 1)..]).ToHashSet(StringComparer.Ordinal);
        return [.. fields.Where(f =>
        {
            var path = f.Path.Replace("[]", string.Empty, StringComparison.Ordinal);
            var leaf = path[(path.LastIndexOf('.') + 1)..];
            return mentions.Any(m => m == path || m.StartsWith(path + ".", StringComparison.Ordinal)) || f.Path.Contains("[]", StringComparison.Ordinal) && leaves.Contains(leaf);
        })];
    }

    /// <summary>Values to try a template with when no sample data is given: a text, number, date or one-item list for every declared field.</summary>
    public static Dictionary<string, object?> SampleValues(IEnumerable<TemplateFieldDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            Put(root, field.Path.Split('.'), 0, Sample(field));
        }

        return root;
    }

    private static void Put(Dictionary<string, object?> node, string[] path, int index, object? value)
    {
        var segment = path[index];
        var isList = segment.EndsWith("[]", StringComparison.Ordinal);
        var name = isList ? segment[..^2] : segment;
        if (index == path.Length - 1)
        {
            node[name] = value;
            return;
        }

        Dictionary<string, object?> child;
        if (isList)
        {
            if (!node.TryGetValue(name, out var list) || list is not List<object?> items)
            {
                items = [new Dictionary<string, object?>(StringComparer.Ordinal)];
                node[name] = items;
            }

            child = (Dictionary<string, object?>)((List<object?>)node[name]!)[0]!;
        }
        else
        {
            if (!node.TryGetValue(name, out var existing) || existing is not Dictionary<string, object?> dictionary)
            {
                dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                node[name] = dictionary;
            }

            child = dictionary;
        }

        Put(child, path, index + 1, value);
    }

    private static object? Sample(TemplateFieldDefinition field) => field.Type switch
    {
        "number" or "money" or "decimal" or "int" => 1234.5m,
        "date" => new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        "bool" => true,
        _ => "Sample " + field.Path[(field.Path.LastIndexOf('.') + 1)..].Replace("[]", string.Empty, StringComparison.Ordinal),
    };

    private static object? Convert(object? value, bool encode) => value switch
    {
        null => null,
        RawHtml raw => raw.Value,
        string text => encode ? WebUtility.HtmlEncode(text) : text,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        DateTimeOffset at => at.UtcDateTime,
        IReadOnlyDictionary<string, object?> map => ToObject(map.Select(p => (p.Key, p.Value)), encode),
        IDictionary<string, object?> map => ToObject(map.Select(p => (p.Key, p.Value)), encode),
        System.Collections.IEnumerable list => ToArray(list, encode),
        _ => value,
    };

    private static ScriptObject ToObject(IEnumerable<(string Key, object? Value)> entries, bool encode)
    {
        var result = new ScriptObject();
        foreach (var (key, value) in entries)
        {
            result.SetValue(key, Convert(value, encode), true);
        }

        return result;
    }

    private static ScriptArray ToArray(System.Collections.IEnumerable list, bool encode)
    {
        var result = new ScriptArray();
        foreach (var item in list)
        {
            result.Add(Convert(item, encode));
        }

        return result;
    }

    /// <summary>The date functions of Scriban without <c>now</c>.</summary>
    private static ScriptObject SafeDate()
    {
        var all = new Scriban.Functions.DateTimeFunctions();
        var safe = new ScriptObject();
        foreach (var key in all.Keys.Where(k => k != "now"))
        {
            safe.SetValue(key, all[key], true);
        }

        return safe;
    }

    private static CultureInfo CultureOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static DocumentException Failure(string key, string detail) => new(key, 400) { Data = { ["detail"] = detail } };

    [GeneratedRegex(@"\{\{-?(.*?)-?\}\}", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Block();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Name();
}

/// <summary>A value that is already safe markup (the content of a letter inside its letterhead); it is not encoded again.</summary>
public sealed record RawHtml(string Value);

/// <summary>QR codes as SVG (QRCoder, MIT).</summary>
public static class Qr
{
    public static string Svg(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.SvgQRCode(data).GetGraphic(4);
    }
}
