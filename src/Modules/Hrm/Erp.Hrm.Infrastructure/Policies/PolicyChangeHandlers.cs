using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Policies;

/// <summary>
/// What both policy approval handlers share (ruling Q1, FR-XM-32: the kernel refuses the requester as approver): the approver holds
/// <c>hrm.policies.approve</c> and the company inside their data scope (read and update); everything is checked again on the approval's
/// transaction, so a change that became invalid meanwhile is refused with the reason, never applied.
/// </summary>
internal abstract class PolicyChangeHandler(IServiceProvider services, IDataScopeFilter scope) : IHeldChangeHandler
{
    public abstract string Kind { get; }

    public string ApprovePermission => PolicyPermissions.Approve;

    protected PolicyTypeCatalog Types => services.GetRequiredService<PolicyTypeCatalog>();

    protected IClock Clock => services.GetRequiredService<IClock>();

    protected ICurrentActor Actor => services.GetRequiredService<ICurrentActor>();

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (CompanyOf(change) is not { } company)
        {
            return false;
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);
        var probe = new Policy { CompanyId = company, Code = string.Empty, NameI18n = JsonDocument.Parse("{}"), PolicyType = string.Empty, Source = "tenant" };
        return await scope.AllowsAsync(probe, PolicyAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(probe, PolicyAccess.Scope, "approve", source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        return Problem(await RunAsync(change, db, apply: false, cancellationToken).ConfigureAwait(false));
    }

    public async Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        return Problem(await RunAsync(change, db, apply: true, cancellationToken).ConfigureAwait(false));
    }

    public abstract Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken);

    protected abstract Guid? CompanyOf(HeldChange change);

    /// <summary>The refusal (as a person-service answer) or null; applies the change when <paramref name="apply"/>.</summary>
    protected abstract Task<PeopleResult?> RunAsync(HeldChange change, HrmDbContext db, bool apply, CancellationToken ct);

    protected PolicyWriter Writer(HrmDbContext db) => new(db, Types, services.GetRequiredService<IOrgStructure>(), services.GetRequiredService<ICustomFieldCatalog>());

    protected Task<DateOnly> TodayAsync(HrmDbContext db, Guid company, CancellationToken ct) =>
        new EmploymentApplier(db, services.GetRequiredService<ISettings>(), PolicyResolver.For(db, services)).TodayAsync(company, Clock.UtcNow, ct);

    protected async Task<HrmDbContext> OnAsync(DbTransaction transaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }

    protected static T? Read<T>(HeldChange change)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(change.Payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static Dictionary<string, string> Names(JsonDocument name) => PolicyAttributeReader.Names(name);

    private static HeldChangeProblem? Problem(PeopleResult? result) => result switch
    {
        null => null,
        { FieldErrors: { Count: > 0 } errors } => new HeldChangeProblem(errors.Values.First()[0]),
        _ => new HeldChangeProblem(result.Error ?? "errors.data.invalid_request", result.Outcome == PeopleOutcome.Forbidden),
    };
}

/// <summary>Approval of a held version activation (kind <c>hrm.policy.version_activation</c>): the draft is unchanged since the request and still activates on its date.</summary>
internal sealed class PolicyVersionChangeHandler(IServiceProvider services, IDataScopeFilter scope) : PolicyChangeHandler(services, scope)
{
    public override string Kind => PolicyService.VersionKind;

    protected override Guid? CompanyOf(HeldChange change) => Read<VersionPlan>(change)?.CompanyId;

    protected override async Task<PeopleResult?> RunAsync(HeldChange change, HrmDbContext db, bool apply, CancellationToken ct)
    {
        if (Read<VersionPlan>(change) is not { } plan)
        {
            return PeopleResult.Invalid("errors.data.invalid_request");
        }

        if (apply)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.policies where id = {plan.PolicyId} for update", ct).ConfigureAwait(false);
        }

        var policy = await db.Set<Policy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == plan.PolicyId && p.CompanyId == plan.CompanyId && !p.IsDeleted, ct).ConfigureAwait(false);
        var version = await db.Set<PolicyVersion>().FirstOrDefaultAsync(v => v.Id == plan.VersionId && v.PolicyId == plan.PolicyId, ct).ConfigureAwait(false);
        if (policy is null || version is null)
        {
            return PeopleResult.NotFound;
        }

        if (version.Status == "draft" && version.RowVersion != plan.RowVersion)
        {
            return PeopleResult.Conflict(PersonService.Concurrency); // the draft was edited after the request: the approver would approve unseen parameters
        }

        var writer = Writer(db);
        if (await writer.CheckActivationAsync(policy, version, plan.EffectiveFrom, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if (apply)
        {
            await writer.ActivateAsync(policy, version, plan.EffectiveFrom, Clock.UtcNow, Actor.UserId, ct).ConfigureAwait(false);
        }

        return null;
    }

    public override async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read<VersionPlan>(change) is not { } plan)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        var policy = await db.Set<Policy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == plan.PolicyId, cancellationToken).ConfigureAwait(false);
        var version = await db.Set<PolicyVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == plan.VersionId, cancellationToken).ConfigureAwait(false);
        var details = new List<HeldChangeDetail>
        {
            new("hrm.policy.detail.type", "key", Text: "hrm.policy.type." + (policy?.PolicyType ?? string.Empty)),
            new("hrm.policy.detail.version", "text", Text: version?.Version.ToString(CultureInfo.InvariantCulture)),
            new("hrm.policy.detail.effective_from", "date", Date: plan.EffectiveFrom),
            new("hrm.policy.detail.parameters", "text", Text: version?.Parameters.RootElement.GetRawText()),
        };
        return new HeldChangeDescription(policy is null ? null : Names(policy.NameI18n), details);
    }
}

/// <summary>Approval of a held assignment set activation (kind <c>hrm.policy.assignment_activation</c>): the same checks as at the request, on the newest data.</summary>
internal sealed class PolicyAssignmentChangeHandler(IServiceProvider services, IDataScopeFilter scope) : PolicyChangeHandler(services, scope)
{
    public override string Kind => PolicyService.SetKind;

    protected override Guid? CompanyOf(HeldChange change) => Read<SetPlan>(change)?.CompanyId;

    protected override async Task<PeopleResult?> RunAsync(HeldChange change, HrmDbContext db, bool apply, CancellationToken ct)
    {
        if (Read<SetPlan>(change) is not { } plan || Types.Find(plan.PolicyType) is not { } type)
        {
            return PeopleResult.Invalid("errors.data.invalid_request");
        }

        var writer = Writer(db);
        if (apply)
        {
            await writer.LockSetAsync(plan.CompanyId, type.Code, ct).ConfigureAwait(false);
        }

        var today = await TodayAsync(db, plan.CompanyId, ct).ConfigureAwait(false);
        var (set, _, refused) = await PolicyService.SetCheckAsync(writer, type, plan.CompanyId, plan.EffectiveFrom, plan.BasedOn, plan.Assignments, today, null, ct).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        if (apply)
        {
            await writer.ApplySetAsync(type, plan.CompanyId, plan.EffectiveFrom, set!, Clock.UtcNow, Actor.UserId, ct).ConfigureAwait(false);
        }

        return null;
    }

    public override async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read<SetPlan>(change) is not { } plan || Types.Find(plan.PolicyType) is not { } type)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        var ids = plan.Assignments.Where(a => a.PolicyId is not null).Select(a => a.PolicyId!.Value).ToList();
        var codes = await db.Set<Policy>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken).ConfigureAwait(false);
        var items = plan.Assignments.OrderBy(a => a.Precedence)
            .Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Precedence}: {(a.PolicyId is { } id ? codes.GetValueOrDefault(id) : null) ?? "?"} {(a.Rule is { } r ? r.GetRawText() : "{}")}")).ToList();
        var details = new List<HeldChangeDetail>
        {
            new("hrm.policy.detail.type", "key", Text: "hrm.policy.type." + type.Code),
            new("hrm.policy.detail.effective_from", "date", Date: plan.EffectiveFrom),
            new("hrm.policy.detail.assignments", "items", Items: items),
        };

        // The warning the maker saw: how many current employees the set would leave without a policy (review S1.23a finding 5).
        var today = await TodayAsync(db, plan.CompanyId, cancellationToken).ConfigureAwait(false);
        var (_, unmatched, _) = await PolicyService.SetCheckAsync(Writer(db), type, plan.CompanyId, plan.EffectiveFrom, plan.BasedOn, plan.Assignments, today, null, cancellationToken).ConfigureAwait(false);
        if (unmatched is { Count: > 0 } warning)
        {
            details.Add(new HeldChangeDetail("hrm.policy.detail.unmatched", "text", Text: warning.Count.ToString(CultureInfo.InvariantCulture)));
        }
        return new HeldChangeDescription(new Dictionary<string, string>(type.Name, StringComparer.Ordinal), details);
    }
}
