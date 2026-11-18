using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Domain.Jobs;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>The access modes a job may run in (slice S1.11 ruling Q6).</summary>
public static class JobModes
{
    public const string Full = "full";
    public const string ReadOnly = "read_only";
    public const string Locked = "locked";
    public const string Suspended = "suspended";

    public static IReadOnlyList<string> All { get; } = [Full, ReadOnly, Locked, Suspended];
}

/// <summary>Who a run acts as (slice S1.11 ruling Q4, R-009).</summary>
public static class JobActors
{
    /// <summary>The reserved system user, holding exactly the permissions the manifest declares.</summary>
    public const string Job = "job";

    /// <summary>The person who asked for the run, with their own rights checked again when it starts.</summary>
    public const string Requester = "requester";
}

/// <summary>One job of a module's job manifest (slice S1.11 Build item 1, FR-KRN-JOB-01).</summary>
public sealed record JobSpec(
    string Code,
    string Module,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    JsonElement? Schedule,
    bool EnabledByDefault,
    IReadOnlyList<JobParameterSpec> Parameters,
    IReadOnlyList<string> Permissions,
    string RunsAs,
    string? RequestPermission,
    int MaxAttempts,
    IReadOnlyList<int> BackoffSeconds,
    int TimeoutSeconds,
    int Priority,
    int Concurrency,
    IReadOnlySet<string> RunsIn,
    string Entitlement,
    bool Manual,
    bool ScheduleEditable)
{
    /// <summary>The wait before the next attempt after <paramref name="failedAttempt"/> failed (the last wait repeats).</summary>
    public TimeSpan Backoff(int failedAttempt) =>
        BackoffSeconds.Count == 0 ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(BackoffSeconds[Math.Clamp(failedAttempt - 1, 0, BackoffSeconds.Count - 1)]);

    /// <summary>The retry policy as stored in <c>job.definitions.retry_policy</c>.</summary>
    public string RetryPolicyJson => JsonSerializer.Serialize(new { max_attempts = MaxAttempts, backoff_seconds = BackoffSeconds });

    /// <summary>The parameter list as stored in <c>job.definitions.parameters</c>.</summary>
    public string ParametersJson => JsonSerializer.Serialize(Parameters.Select(p => new { name = p.Name, type = p.Type, required = p.Required }));
}

/// <summary>A parameter a person may give when asking for a run: name, type (text, int, bool, date, uuid) and whether it is required.</summary>
public sealed record JobParameterSpec(string Name, string Type, bool Required);

/// <summary>
/// A module's job manifest <c>&lt;module&gt;.Infrastructure/Access/jobs.json</c> (slice S1.11 Build item 1): hand-written data
/// shipped with the other access manifests (seeded into <c>job.definitions</c> by <c>migrate</c> and provisioning) and
/// read by the worker host to run the jobs. The kernel's manifest is also embedded in this assembly.
/// </summary>
public static partial class JobManifest
{
    public const string FileName = "jobs.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip };

    public static IReadOnlyList<JobSpec> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ManifestFile>(json, Options) ?? throw new InvalidDataException("empty job manifest");
        return [.. file.Jobs.Select(j => new JobSpec(
            j.Code ?? string.Empty,
            file.Module ?? string.Empty,
            j.Name ?? new Dictionary<string, string>(),
            j.Description ?? new Dictionary<string, string>(),
            j.Schedule.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : j.Schedule.Clone(),
            j.Enabled ?? true,
            [.. (j.Parameters ?? []).Select(p => new JobParameterSpec(p.Name ?? string.Empty, p.Type ?? string.Empty, p.Required))],
            j.Permissions ?? [],
            j.RunsAs ?? JobActors.Job,
            j.RequestPermission,
            j.Retry?.MaxAttempts ?? 1,
            j.Retry?.BackoffSeconds ?? [60],
            j.TimeoutSeconds ?? 0,
            j.Priority ?? 0,
            j.Concurrency ?? 1,
            (j.RunsIn ?? [JobModes.Full]).ToHashSet(StringComparer.Ordinal),
            j.Entitlement ?? string.Empty,
            j.Manual,
            j.ScheduleEditable))];
    }

    /// <summary>The jobs of the manifest folders (one <c>jobs.json</c> per module that has any).</summary>
    public static IReadOnlyList<JobSpec> FromFolders(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var specs = new List<JobSpec>();
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

    /// <summary>The kernel's manifest, embedded in this assembly (the kernel is always installed).</summary>
    public static string KernelJson { get; } = LoadKernel();

    private static string LoadKernel()
    {
        using var stream = typeof(JobManifest).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Access.jobs.json")
            ?? throw new InvalidOperationException("jobs.json is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The problems of one job (empty when it is valid).</summary>
    public static List<string> Check(JobSpec job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var problems = new List<string>();
        if (!CodePattern().IsMatch(job.Code) || !job.Code.StartsWith(job.Module + ".", StringComparison.Ordinal))
        {
            problems.Add("code must be lower-case words joined by dots or underscores, starting with the module id");
        }

        foreach (var (what, texts) in new[] { ("name", job.Name), ("description", job.Description) })
        {
            if (!texts.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en) || !texts.TryGetValue("ur", out var ur) || string.IsNullOrWhiteSpace(ur))
            {
                problems.Add($"{what} needs English and Urdu");
            }
        }

        if (job.Schedule is { } schedule)
        {
            try
            {
                JobSchedule.Parse(schedule);
            }
            catch (FormatException ex)
            {
                problems.Add("schedule: " + ex.Message);
            }
        }
        else if (!job.Manual)
        {
            problems.Add("a job without a schedule must be manual (someone must be able to start it)");
        }

        if (job.RunsAs is not (JobActors.Job or JobActors.Requester))
        {
            problems.Add("runs_as is job or requester");
        }

        if (job.RunsAs == JobActors.Requester && job.Schedule is not null)
        {
            problems.Add("a job that runs as the requester cannot have a schedule (nobody asked for scheduled runs)");
        }

        if (job.Manual && string.IsNullOrWhiteSpace(job.RequestPermission))
        {
            problems.Add("a manual job names the permission needed to start it (request_permission)");
        }

        if (job.MaxAttempts is < 1 or > 20 || job.BackoffSeconds.Any(s => s is < 1 or > 86400))
        {
            problems.Add("retry: max_attempts 1 to 20, back-off 1 to 86400 seconds");
        }

        if (job.TimeoutSeconds is < 1 or > 86400)
        {
            problems.Add("timeout_seconds is 1 to 86400");
        }

        if (job.Concurrency is < 1 or > 16 || job.Priority is < -100 or > 100)
        {
            problems.Add("concurrency 1 to 16, priority -100 to 100");
        }

        if (job.RunsIn.Count == 0 || job.RunsIn.Except(JobModes.All).Any())
        {
            problems.Add("runs_in lists full, read_only, locked or suspended");
        }

        if (string.IsNullOrWhiteSpace(job.Entitlement))
        {
            problems.Add("entitlement names the feature key that must be licensed");
        }

        foreach (var p in job.Parameters)
        {
            if (!ParameterPattern().IsMatch(p.Name) || p.Type is not ("text" or "int" or "bool" or "date" or "uuid"))
            {
                problems.Add($"parameter {p.Name}: snake_case name and type text, int, bool, date or uuid");
            }
        }

        return problems;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ParameterPattern();

    private sealed class ManifestFile
    {
        public string? Module { get; init; }

        public List<JobEntry> Jobs { get; init; } = [];
    }

    private sealed class JobEntry
    {
        public string? Code { get; init; }

        public Dictionary<string, string>? Name { get; init; }

        public Dictionary<string, string>? Description { get; init; }

        public JsonElement Schedule { get; init; }

        public bool? Enabled { get; init; }

        public List<ParameterEntry>? Parameters { get; init; }

        public List<string>? Permissions { get; init; }

        public string? RunsAs { get; init; }

        public string? RequestPermission { get; init; }

        public RetryEntry? Retry { get; init; }

        public int? TimeoutSeconds { get; init; }

        public int? Priority { get; init; }

        public int? Concurrency { get; init; }

        public List<string>? RunsIn { get; init; }

        public string? Entitlement { get; init; }

        public bool Manual { get; init; }

        public bool ScheduleEditable { get; init; }
    }

    private sealed class ParameterEntry
    {
        public string? Name { get; init; }

        public string? Type { get; init; }

        public bool Required { get; init; }
    }

    private sealed class RetryEntry
    {
        public int? MaxAttempts { get; init; }

        public List<int>? BackoffSeconds { get; init; }
    }
}

/// <summary>
/// Every job the host knows (slice S1.11 Build item 1): the kernel's manifest plus the manifests modules (and tests)
/// register, checked when the host starts, each with its handler. A wrong manifest stops the host instead of failing a
/// run later.
/// </summary>
public sealed class JobCatalog
{
    private readonly Dictionary<string, JobSpec> _specs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IJobHandler> _handlers = new(StringComparer.Ordinal);

    public JobCatalog(IEnumerable<JobManifestSource> sources, IEnumerable<IJobHandler> handlers)
        : this(JobManifest.Parse(JobManifest.KernelJson).Concat((sources ?? throw new ArgumentNullException(nameof(sources))).SelectMany(s => JobManifest.Parse(s.Json))), handlers)
    {
    }

    public JobCatalog(IEnumerable<JobSpec> specs, IEnumerable<IJobHandler> handlers)
        : this(specs, handlers, requireHandlers: true)
    {
    }

    private JobCatalog(IEnumerable<JobSpec> specs, IEnumerable<IJobHandler> handlers, bool requireHandlers)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(handlers);
        var problems = new List<string>();
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Code, handler))
            {
                problems.Add($"{handler.Code}: two handlers");
            }
        }

        foreach (var spec in specs)
        {
            var found = JobManifest.Check(spec);
            if (!_specs.TryAdd(spec.Code, spec))
            {
                found.Add("duplicate job code");
            }

            if (requireHandlers && !_handlers.ContainsKey(spec.Code))
            {
                found.Add("no handler is registered");
            }

            problems.AddRange(found.Select(p => $"{spec.Code}: {p}"));
        }

        problems.AddRange(_handlers.Keys.Where(k => !_specs.ContainsKey(k)).Select(k => $"{k}: a handler without a manifest entry"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Job manifest problems: " + string.Join("; ", problems));
        }
    }

    /// <summary>
    /// The catalogue of the API host: the job centre reads and queues runs but runs nothing, so no handlers are needed.
    /// </summary>
    public static JobCatalog WithoutHandlers(IEnumerable<JobManifestSource> sources) =>
        new(JobManifest.Parse(JobManifest.KernelJson).Concat((sources ?? throw new ArgumentNullException(nameof(sources))).SelectMany(s => JobManifest.Parse(s.Json))), [], requireHandlers: false);

    public IReadOnlyCollection<JobSpec> All => _specs.Values;

    public JobSpec? Find(string code) => _specs.GetValueOrDefault(code);

    public IJobHandler Handler(string code) => _handlers[code];
}
