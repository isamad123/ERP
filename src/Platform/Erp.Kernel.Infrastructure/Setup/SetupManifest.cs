using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Kernel.Contracts.Setup;

namespace Erp.Kernel.Infrastructure.Setup;

/// <summary>One task of a module's set-up manifest (slice S1.18a Build item 1).</summary>
public sealed record SetupTaskSpec(
    string ModuleId,
    string Key,
    string TitleKey,
    string Kind,
    bool Required,
    string Level,
    string? Link,
    string? Check,
    int Order)
{
    public bool CompanyLevel => Level == SetupLevels.Company;
}

/// <summary>A module's set-up manifest: its tasks and the actions that need the module to be live.</summary>
public sealed record SetupModuleSpec(string ModuleId, string NameKey, IReadOnlyList<string> GoLiveActions, IReadOnlyList<SetupTaskSpec> Tasks);

/// <summary>Where a task applies: once for the customer or once per company.</summary>
public static class SetupLevels
{
    public const string Customer = "customer";
    public const string Company = "company";
}

/// <summary>
/// A module's set-up manifest <c>&lt;module&gt;.Infrastructure/Access/setup-tasks.json</c> (slice S1.18a, ruling Q2, D-10): wizard steps
/// and readiness items with their level, whether they are required, the screen that fixes them and the name of the check that
/// decides them (none means a person ticks it). Shipped like the job manifest, seeded by <c>migrate</c> and provisioning.
/// </summary>
public static partial class SetupManifest
{
    public const string FileName = "setup-tasks.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip };

    public static SetupModuleSpec Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ManifestFile>(json, Options) ?? throw new InvalidDataException("empty set-up manifest");
        var module = file.Module ?? string.Empty;
        return new SetupModuleSpec(
            module,
            file.NameKey ?? string.Empty,
            file.GoLiveActions ?? [],
            [.. (file.Tasks ?? []).Select(t => new SetupTaskSpec(module, t.Key ?? string.Empty, t.TitleKey ?? string.Empty, t.Kind ?? string.Empty, t.Required, t.Level ?? SetupLevels.Company, t.Link, t.Check, t.Order))]);
    }

    /// <summary>The manifests of the folders (one file per module that has any).</summary>
    public static IReadOnlyList<SetupModuleSpec> FromFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var specs = new List<SetupModuleSpec>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
            {
                specs.Add(Parse(File.ReadAllText(path)));
            }
        }

        return specs;
    }

    /// <summary>The kernel's manifest, embedded in this assembly (the kernel is always installed).</summary>
    public static string KernelJson { get; } = LoadKernel();

    private static string LoadKernel()
    {
        using var stream = typeof(SetupManifest).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Access.setup-tasks.json")
            ?? throw new InvalidOperationException("setup-tasks.json is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The problems of a module's manifest (empty when valid).</summary>
    public static List<string> Check(SetupModuleSpec module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var problems = new List<string>();
        if (!ModulePattern().IsMatch(module.ModuleId))
        {
            problems.Add("module must be a lower-case id of at most 40 characters");
        }

        if (string.IsNullOrWhiteSpace(module.NameKey))
        {
            problems.Add("name_key is required");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in module.Tasks)
        {
            var at = $"{module.ModuleId}.{task.Key}";
            if (!KeyPattern().IsMatch(task.Key) || task.Key == SetupTaskKinds.GoLiveKey)
            {
                problems.Add($"{at}: key is lower-case words joined by underscores, at most 40 characters, and not '{SetupTaskKinds.GoLiveKey}'");
            }

            if (!seen.Add(task.Key))
            {
                problems.Add($"{at}: duplicate key");
            }

            if (string.IsNullOrWhiteSpace(task.TitleKey))
            {
                problems.Add($"{at}: title_key is required");
            }

            if (task.Kind is not (SetupTaskKinds.Step or SetupTaskKinds.Item))
            {
                problems.Add($"{at}: kind is step or item");
            }

            if (task.Level is not (SetupLevels.Customer or SetupLevels.Company))
            {
                problems.Add($"{at}: level is customer or company");
            }

            if (task.Kind == SetupTaskKinds.Step && task.Required)
            {
                problems.Add($"{at}: a wizard step is never required (steps are skippable)");
            }

            if (task.Link is { } link && (!link.StartsWith('/') || link.StartsWith("//", StringComparison.Ordinal) || link.Contains("://", StringComparison.Ordinal)))
            {
                problems.Add($"{at}: link is a path of this application");
            }

            if (task.Check is { } check && (!check.StartsWith(module.ModuleId + ".", StringComparison.Ordinal) || check.Length > 100))
            {
                problems.Add($"{at}: check names a check of the module (starts with '{module.ModuleId}.')");
            }
        }

        return problems;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ModulePattern();

    private sealed class ManifestFile
    {
        public string? Module { get; init; }

        public string? NameKey { get; init; }

        public List<string>? GoLiveActions { get; init; }

        public List<TaskEntry>? Tasks { get; init; }
    }

    private sealed class TaskEntry
    {
        public string? Key { get; init; }

        public string? TitleKey { get; init; }

        public string? Kind { get; init; }

        public bool Required { get; init; }

        public string? Level { get; init; }

        public string? Link { get; init; }

        public string? Check { get; init; }

        public int Order { get; init; }
    }
}

/// <summary>
/// The set-up tasks of every installed module (kernel always, others by the manifests their hosts register), checked once when the
/// host starts: a wrong manifest stops the host instead of failing a request later.
/// </summary>
public sealed class SetupCatalog
{
    private readonly Dictionary<string, SetupModuleSpec> _modules = new(StringComparer.Ordinal);

    public SetupCatalog(IEnumerable<SetupManifestSource> sources)
        : this(Build(sources))
    {
    }

    public SetupCatalog(IEnumerable<SetupModuleSpec> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var problems = new List<string>();
        foreach (var module in modules)
        {
            problems.AddRange(SetupManifest.Check(module).Select(p => $"{module.ModuleId}: {p}"));
            if (!_modules.TryAdd(module.ModuleId, module))
            {
                problems.Add($"{module.ModuleId}: duplicate module manifest");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Set-up manifest problems: " + string.Join("; ", problems));
        }
    }

    public IReadOnlyCollection<SetupModuleSpec> Modules => _modules.Values;

    public SetupModuleSpec? Find(string moduleId) => _modules.GetValueOrDefault(moduleId);

    public static IReadOnlyList<SetupModuleSpec> Build(IEnumerable<SetupManifestSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.Select(s => SetupManifest.Parse(s.Json)).ToList();
        if (!list.Exists(m => m.ModuleId == "kernel"))
        {
            list.Insert(0, SetupManifest.Parse(SetupManifest.KernelJson));
        }

        return list;
    }
}
