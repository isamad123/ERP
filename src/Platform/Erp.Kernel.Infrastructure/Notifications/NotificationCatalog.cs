using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Domain.Workflow;

namespace Erp.Kernel.Infrastructure.Notifications;

/// <summary>The title and body of a product text in one language.</summary>
public sealed record NotificationTextSpec(string Title, string Body);

/// <summary>Who an event names by itself: users held in payload properties, fixed role codes and role codes held in a payload property.</summary>
public sealed record NotificationAudienceSpec(IReadOnlyList<string> UserProperties, IReadOnlyList<string> Roles, string? RoleProperty);

/// <summary>
/// One notifiable event of a module's catalogue (slice S1.11 Build item 8, HRM-NTF-02). <see cref="Code"/> is the code
/// customers see in templates, rules and items; <see cref="Source"/> is the outbox event type it comes from, and
/// <see cref="When"/> narrows a source event to one payload value (for example an alert of kind <c>job_failed</c>).
/// </summary>
public sealed record NotificationEventSpec(
    string Code,
    string Module,
    string Source,
    IReadOnlyDictionary<string, string> When,
    string Group,
    bool Mandatory,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MandatoryWhen,
    bool Audited,
    string? LinkProperty,
    string? LinkTemplate,
    string? CompanyProperty,
    string? FieldsFrom,
    IReadOnlyList<NotificationField> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Choices,
    NotificationAudienceSpec Audience,
    IReadOnlyDictionary<string, NotificationTextSpec> Texts)
{
    /// <summary>The fields as the workflow condition format sees them (rules of ruling Q14).</summary>
    public IReadOnlyDictionary<string, RouteField> RouteFields =>
        Fields.ToDictionary(f => f.Name, f => new RouteField(f.Name, f.Type, Choices.TryGetValue(f.Name, out var c) ? c : []), StringComparer.Ordinal);

    public IReadOnlyDictionary<string, NotificationField> FieldMap => Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
}

/// <summary>A module's catalogue file as text (<c>Access/notification-events.json</c>), registered by each module in both hosts.</summary>
public sealed record NotificationManifestSource(string Json);

/// <summary>
/// The catalogue of notifiable events of the installed modules (the kernel's is embedded). A host fails to start when a
/// manifest is wrong, like the job catalogue. Preference groups follow ruling Q11: approvals, my requests, documents and
/// system.
/// </summary>
public sealed partial class NotificationCatalog
{
    public const string FileName = "notification-events.json";

    public static IReadOnlyList<string> Groups { get; } = ["approvals", "my_requests", "documents", "system"];

    private readonly Dictionary<string, NotificationEventSpec> _byCode;

    public NotificationCatalog(IEnumerable<NotificationManifestSource> sources)
        : this(Parse(KernelJson).Concat((sources ?? throw new ArgumentNullException(nameof(sources))).SelectMany(s => Parse(s.Json))))
    {
    }

    public NotificationCatalog(IEnumerable<NotificationEventSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var list = specs.ToList();
        var problems = list.SelectMany(s => Check(s).Select(p => $"{s.Code}: {p}")).ToList();
        problems.AddRange(list.GroupBy(s => s.Code, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{g.Key}: declared twice"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Notification catalogue problems: " + string.Join("; ", problems));
        }

        _byCode = list.ToDictionary(s => s.Code, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<NotificationEventSpec> All => _byCode.Values;

    public NotificationEventSpec? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>The events one outbox event type can give (a payload decides among the ones with <see cref="NotificationEventSpec.When"/>).</summary>
    public IEnumerable<NotificationEventSpec> ForSource(string eventType) => _byCode.Values.Where(s => s.Source == eventType);

    public bool Accepts(string eventType) => _byCode.Values.Any(s => s.Source == eventType);

    public static string KernelJson { get; } = LoadKernel();

    private static string LoadKernel()
    {
        using var stream = typeof(NotificationCatalog).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Access.notification-events.json")
            ?? throw new InvalidOperationException("notification-events.json is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Catalogue files of the module folders (<c>Access</c> folders), when they have one.</summary>
    public static IReadOnlyList<NotificationEventSpec> FromFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var specs = new List<NotificationEventSpec>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
            {
                specs.AddRange(Parse(File.ReadAllText(path)));
            }
        }

        return specs;
    }

    public static IReadOnlyList<NotificationEventSpec> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var module = doc.RootElement.GetProperty("module").GetString()!;
        var specs = new List<NotificationEventSpec>();
        foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var fields = new List<NotificationField>();
            var choices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var f in e.GetProperty("fields").EnumerateArray())
            {
                var name = f.GetProperty("name").GetString()!;
                fields.Add(new NotificationField(name, f.GetProperty("type").GetString()!, f.GetProperty("class").GetString()!));
                if (f.TryGetProperty("choices", out var c))
                {
                    choices[name] = [.. c.EnumerateArray().Select(x => x.GetString()!)];
                }
            }

            var audience = e.GetProperty("audience");
            var texts = new Dictionary<string, NotificationTextSpec>(StringComparer.Ordinal);
            foreach (var language in e.GetProperty("texts").EnumerateObject())
            {
                texts[language.Name] = new NotificationTextSpec(language.Value.GetProperty("title").GetString()!, language.Value.GetProperty("body").GetString()!);
            }

            specs.Add(new NotificationEventSpec(
                e.GetProperty("code").GetString()!,
                module,
                e.GetProperty("source").GetString()!,
                e.TryGetProperty("when", out var when) ? when.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal) : [],
                e.GetProperty("group").GetString()!,
                e.TryGetProperty("mandatory", out var m) && m.GetBoolean(),
                e.TryGetProperty("mandatory_when", out var mw)
                    ? mw.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)[.. p.Value.EnumerateArray().Select(x => x.GetString()!)], StringComparer.Ordinal)
                    : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                e.TryGetProperty("audited", out var a) && a.GetBoolean(),
                e.TryGetProperty("link", out var link) ? link.GetString() : null,
                e.TryGetProperty("link_template", out var lt) ? lt.GetString() : null,
                e.TryGetProperty("company", out var company) ? company.GetString() : null,
                e.TryGetProperty("fields_from", out var from) ? from.GetString() : null,
                fields,
                choices,
                new NotificationAudienceSpec(
                    audience.TryGetProperty("users", out var users) ? [.. users.EnumerateArray().Select(x => x.GetString()!)] : [],
                    audience.TryGetProperty("roles", out var roles) ? [.. roles.EnumerateArray().Select(x => x.GetString()!)] : [],
                    audience.TryGetProperty("role_property", out var rp) ? rp.GetString() : null),
                texts));
        }

        return specs;
    }

    /// <summary>The problems of one catalogue entry: code and group, texts in English and Urdu, and the template check on every text.</summary>
    public static IReadOnlyList<string> Check(NotificationEventSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new List<string>();
        if (!CodePattern().IsMatch(spec.Code) || spec.Code.Length > 40)
        {
            problems.Add("code must be lower case words with dots or underscores, at most 40 characters");
        }

        if (!Groups.Contains(spec.Group, StringComparer.Ordinal))
        {
            problems.Add("group must be one of " + string.Join(", ", Groups));
        }

        if (spec.Audience.UserProperties.Count == 0 && spec.Audience.Roles.Count == 0 && spec.Audience.RoleProperty is null)
        {
            problems.Add("audience names nobody");
        }

        foreach (var field in spec.Fields)
        {
            if (!FieldTypes.All.Contains(field.Type, StringComparer.Ordinal) || !new[] { "general", "personal", "salary" }.Contains(field.FieldClass, StringComparer.Ordinal))
            {
                problems.Add($"field {field.Name}: unknown type or class");
            }
        }

        var map = spec.FieldMap;
        foreach (var language in new[] { "en", "ur" })
        {
            if (!spec.Texts.TryGetValue(language, out var text) || string.IsNullOrWhiteSpace(text.Title) || string.IsNullOrWhiteSpace(text.Body))
            {
                problems.Add($"text needs a title and a body in {language}");
                continue;
            }

            foreach (var key in NotificationText.Check(text.Title, map, "in_app").Concat(NotificationText.Check(text.Body, map, "in_app")).Distinct(StringComparer.Ordinal))
            {
                problems.Add($"text {language}: {key}");
            }
        }

        if (spec.LinkProperty is null && spec.LinkTemplate is null)
        {
            problems.Add("an event needs a deep link (link or link_template)");
        }

        return problems;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex CodePattern();
}
