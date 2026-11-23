using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Setup;

/// <summary>
/// Permissions of the first-run wizard and readiness checklist (slice S1.18a, CR-074). Reading the checklist and the generated
/// reads of <c>cfg.setup_tasks</c> share <see cref="Read"/>; ticking, skipping and re-opening an item need <see cref="Update"/>;
/// a module's go-live is its own right so a company administrator can be given the checklist without it.
/// </summary>
public static class SetupPermissions
{
    public const string Read = "cfg.setup_tasks.read";
    public const string Update = "cfg.setup_tasks.update";
    public const string GoLive = "cfg.setup_tasks.go_live";

    /// <summary>Hand-written codes (the generated writes of <c>cfg.setup_tasks</c> are closed); descriptions live in <c>setup.*.json</c>.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Update, "cfg", "permissions.cfg.setup_tasks.update", false),
        new(GoLive, "cfg", "permissions.cfg.setup_tasks.go_live", false),
    ];
}

/// <summary>The kinds of set-up task a module's manifest declares.</summary>
public static class SetupTaskKinds
{
    /// <summary>A wizard step: skippable, never blocks go-live.</summary>
    public const string Step = "step";

    /// <summary>A readiness item of a module's checklist: required items block go-live.</summary>
    public const string Item = "item";

    /// <summary>The recorded go-live milestone of a module and company (task key <see cref="GoLiveKey"/>).</summary>
    public const string GoLive = "go_live";

    public const string GoLiveKey = "go_live";
}

/// <summary>The statuses of a set-up task row (<c>cfg.setup_tasks.status</c>).</summary>
public static class SetupStatuses
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Skipped = "skipped";
}

/// <summary>The outcome of one readiness check: done or not, with a message key shown beside an open item.</summary>
public sealed record ReadinessResult(bool Done, string? MessageKey = null, IReadOnlyDictionary<string, string>? Arguments = null)
{
    public static ReadinessResult Ok { get; } = new(true);

    public static ReadinessResult Open(string messageKey, IReadOnlyDictionary<string, string>? arguments = null) => new(false, messageKey, arguments);
}

/// <summary>
/// A readiness check (slice S1.18a Build item 2, ruling Q2, D-10). The module that owns the data implements it and the kernel
/// only lists and runs it, by <see cref="Name"/> (the manifest's <c>check</c>). A check reads and never writes; a failure
/// to run it shows as open with the message <c>setup.check_failed</c>, never as done.
/// </summary>
public interface IReadinessCheck
{
    /// <summary>The manifest name, the owning module's id and a dot-free name (<c>kernel.company_profile</c>).</summary>
    string Name { get; }

    /// <param name="companyId">The company the item is for; null for a customer-level item.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken);
}

/// <summary>A module's set-up manifest (JSON <c>setup-tasks.json</c>), registered by the module in both hosts like its job manifest.</summary>
public sealed record SetupManifestSource(string Json);

/// <summary>One line of the checklist or wizard as the API shows it.</summary>
public sealed record SetupTaskView(
    string ModuleId,
    string TaskKey,
    string Kind,
    string TitleKey,
    bool Required,
    string Level,
    string? Link,
    string Status,
    bool Manual,
    string? MessageKey,
    IReadOnlyDictionary<string, string>? MessageArguments,
    string? Note,
    Guid? CompletedBy,
    DateTimeOffset? CompletedAt,
    int Order);

/// <summary>The progress of one module's items: required and done counts count items only (steps never count).</summary>
public sealed record ModuleReadiness(
    string ModuleId,
    string NameKey,
    int RequiredTotal,
    int RequiredDone,
    int OptionalTotal,
    int OptionalDone,
    bool IsLive,
    DateTimeOffset? LiveAt,
    string? LiveNote,
    IReadOnlyList<string> GoLiveActions,
    IReadOnlyList<SetupTaskView> Items);

/// <summary>The checklist of one company (or the customer level when <c>CompanyId</c> is null), grouped by module, and the wizard steps.</summary>
public sealed record ReadinessOverview(Guid? CompanyId, IReadOnlyList<SetupTaskView> Steps, IReadOnlyList<ModuleReadiness> Modules);

/// <summary>Ticking or skipping one task. A skip may carry a note; only steps can be skipped.</summary>
public sealed record SetupTaskChange(Guid? CompanyId, string? Note);

/// <summary>Go-live of one module for one company. <c>OverrideReason</c> is required when required items are open.</summary>
public sealed record GoLiveRequest(Guid CompanyId, string? OverrideReason);

/// <summary>What go-live recorded.</summary>
public sealed record GoLiveResult(string ModuleId, Guid CompanyId, DateTimeOffset LiveAt, bool Overridden, int OpenRequired);

/// <summary>A refusal with a localization key (and the field it is about); the endpoints turn it into a problem response.</summary>
public sealed class SetupRefusedException(int status, string errorKey, string? field = null) : Exception(errorKey)
{
    public int Status { get; } = status;

    public string ErrorKey { get; } = errorKey;

    public string? Field { get; } = field;
}

/// <summary>
/// The checklist and go-live service (slice S1.18a, contract "Readiness API"). Every change goes through the generated
/// repository, so the caller's company scope is applied and the change log records each tick, skip, re-open and go-live.
/// </summary>
public interface IReadinessService
{
    /// <summary>Runs the checks of the company's items (and the customer-level ones), stores what they found and lists everything.</summary>
    Task<ReadinessOverview> OverviewAsync(Guid? companyId, CancellationToken cancellationToken);

    /// <summary>Ticks a manual item or step as done (an item with a check cannot be ticked: the check decides).</summary>
    Task<SetupTaskView> CompleteAsync(string moduleId, string taskKey, SetupTaskChange change, CancellationToken cancellationToken);

    /// <summary>Skips a wizard step (a reason is optional).</summary>
    Task<SetupTaskView> SkipAsync(string moduleId, string taskKey, SetupTaskChange change, CancellationToken cancellationToken);

    /// <summary>Sets a manual task or step back to "to do".</summary>
    Task<SetupTaskView> ReopenAsync(string moduleId, string taskKey, SetupTaskChange change, CancellationToken cancellationToken);

    Task<GoLiveResult> GoLiveAsync(string moduleId, GoLiveRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Answers "may this action run?" for the actions a module names in its manifest (<c>requires_go_live</c>), for example payroll
/// finalization (S1.38). Nothing else is blocked before go-live (ruling Q4).
/// </summary>
public interface IGoLiveGate
{
    Task<bool> IsLiveAsync(string moduleId, Guid companyId, CancellationToken cancellationToken);
}
