using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Generated;
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
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Hrm.Infrastructure.Policies;

/// <summary>
/// The policy framework's services (slice S1.23a Build items 1 to 6; FR-HRM-POL-02, FR-HRM-POL-03, FR-HRM-POL-06, ruling Q1). The generated
/// writes of the three policy tables are closed: policies are created here with a first draft; drafts are edited freely and checked
/// against the type's schema; a draft is activated from a date (the version before ends the day before, and a version in force is never
/// edited, so results keep the version they used); a policy is retired from a date once nothing assigns it. Assignments of one type in
/// one company are activated as a set from a date after checks (unique precedence, rules that read, records that exist, active custom
/// fields) with a warning listing current employees no rule covers. Activations wait for a second person while
/// <c>hrm.policies.requires_approval</c> is on (the kernel refuses the requester: FR-XM-32). Every write checks <c>hrm.policies.manage</c>
/// and the company's data scope; the change log names the acting user.
/// </summary>
internal sealed partial class PolicyService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISettings settings,
    IHeldChanges held,
    EmploymentApplier applier,
    PolicyTypeCatalog types,
    IOrgStructure org,
    ICustomFieldCatalog customFields,
    IPolicyResolver resolver) : IPolicyService
{
    public const string VersionKind = "hrm.policy.version_activation";
    public const string SetKind = "hrm.policy.assignment_activation";
    private const int NameLength = 200;
    private const int ReasonLength = 500;

    private DbContextScopeSource Source => new(db);

    private PolicyWriter Writer => new(db, types, org, customFields);

    // ================================================================== types and reads

    public async Task<PeopleResult> TypesAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Read, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var items = types.All.Select(t => new
        {
            code = t.Code, module = t.Module, name = t.Name, description = t.Description, cardinality = t.Cardinality, required = t.Required,
            attributes = t.Attributes, schema = t.Schema,
        });
        return new PeopleResult(PeopleOutcome.Done, Body: new { items, operators = PolicyRules.Operators, employeeTypes = EmploymentCatalogue.Default.EmployeeTypes });
    }

    public async Task<PeopleResult> DetailAsync(Guid policyId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Read, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var policy = await db.Set<Policy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId && !p.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (policy is null || !await scope.AllowsAsync(policy, PolicyAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(policy, cancellationToken).ConfigureAwait(false));
    }

    private async Task<object> ViewAsync(Policy policy, CancellationToken ct)
    {
        var versions = await db.Set<PolicyVersion>().AsNoTracking().Where(v => v.PolicyId == policy.Id).OrderByDescending(v => v.Version).ToListAsync(ct).ConfigureAwait(false);
        var today = await applier.TodayAsync(policy.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
        var assigned = await db.Set<PolicyAssignment>().AsNoTracking().CountAsync(a => a.PolicyId == policy.Id && (a.ValidTo == null || a.ValidTo > today), ct).ConfigureAwait(false);
        return new
        {
            id = policy.Id, companyId = policy.CompanyId, policyType = policy.PolicyType, code = policy.Code, name = PolicyAttributeReader.Names(policy.NameI18n),
            source = policy.Source, isActive = policy.IsActive, rowVersion = policy.RowVersion, assignments = assigned,
            versions = versions.Select(v => VersionView(policy, v, today)),
        };
    }

    private static object VersionView(Policy policy, PolicyVersion v, DateOnly today) => new
    {
        id = v.Id, policyId = v.PolicyId, version = v.Version, status = v.Status, parameters = v.Parameters.RootElement.Clone(), isOverridden = v.IsOverridden,
        layer = policy.Source == "pack" && !v.IsOverridden ? "pack" : "tenant",
        validFrom = v.Status == "draft" ? (DateOnly?)null : v.ValidFrom, validTo = v.Status == "draft" ? null : v.ValidTo,
        inForce = v.Status != "draft" && v.ValidFrom <= today && (v.ValidTo is null || v.ValidTo > today),
        rowVersion = v.RowVersion,
    };

    // ================================================================== policies and versions

    public async Task<PeopleResult> CreateAsync(PolicyCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Manage, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = types.Find(request.PolicyType);
        if (request.CompanyId is null)
        {
            errors["companyId"] = [PersonService.Required];
        }

        if (type is null)
        {
            errors["policyType"] = [PolicyErrors.UnknownType];
        }

        var code = request.Code?.Trim();
        if (code is null || !CodeShape().IsMatch(code))
        {
            errors["code"] = [PolicyErrors.CodeShape];
        }

        var name = NameJson(request.Name, errors);
        var parameters = request.Parameters is { ValueKind: JsonValueKind.Object } p ? p : JsonSerializer.SerializeToElement(new { });
        if (type is not null)
        {
            foreach (var (path, keys) in PolicySchema.Check(type.Schema, parameters))
            {
                errors[path] = keys;
            }
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        var policy = new Policy
        {
            Id = Guid.CreateVersion7(now), CompanyId = request.CompanyId!.Value, Code = code!, NameI18n = name!, PolicyType = type!.Code, Source = "tenant", IsActive = true,
            CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
        };
        if (!await scope.AllowsAsync(policy, PolicyAccess.Scope, "manage", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var today = await applier.TodayAsync(policy.CompanyId, now, cancellationToken).ConfigureAwait(false);
        return await SaveAsync(async () =>
        {
            db.Add(policy);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.Add(NewVersion(policy, 1, parameters, today, now));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: await ViewAsync(policy, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> EditAsync(Guid policyId, PolicyEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (policy, problem) = await ManagedPolicyAsync(policyId, request.RowVersion, tracked: true, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = NameJson(request.Name, errors);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        policy!.NameI18n = name!;
        PolicyWriter.Bump(policy, clock.UtcNow, actor.UserId);
        return await SaveAsync(async () =>
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(policy, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> NewDraftAsync(Guid policyId, PolicyDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (policy, problem) = await ManagedPolicyAsync(policyId, null, tracked: false, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!policy!.IsActive)
        {
            return PeopleResult.Conflict(PolicyErrors.Retired);
        }

        var versions = await db.Set<PolicyVersion>().AsNoTracking().Where(v => v.PolicyId == policyId).OrderByDescending(v => v.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versions.Any(v => v.Status == "draft"))
        {
            return PeopleResult.Conflict(PolicyErrors.DraftExists);
        }

        // A new draft starts from the latest version unless parameters are given (the form's "start from version n").
        var parameters = request.Parameters is { ValueKind: JsonValueKind.Object } given ? given
            : versions.Count > 0 ? versions[0].Parameters.RootElement.Clone() : JsonSerializer.SerializeToElement(new { });
        if (Check(policy, parameters) is { } invalid)
        {
            return invalid;
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(policy.CompanyId, now, cancellationToken).ConfigureAwait(false);
        var version = NewVersion(policy, versions.Count == 0 ? 1 : versions.Max(v => v.Version) + 1, parameters, today, now);
        return await SaveAsync(async () =>
        {
            db.Add(version);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: VersionView(policy, version, today));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> EditDraftAsync(Guid versionId, PolicyDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (policy, version, problem) = await DraftAsync(versionId, request.RowVersion, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Parameters is not { ValueKind: JsonValueKind.Object } parameters)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["parameters"] = [PersonService.ObjectRequired] });
        }

        if (Check(policy!, parameters) is { } invalid)
        {
            return invalid;
        }

        version!.Parameters = JsonDocument.Parse(parameters.GetRawText());
        PolicyWriter.Bump(version, clock.UtcNow, actor.UserId);
        var today = await applier.TodayAsync(policy!.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return await SaveAsync(async () =>
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: VersionView(policy, version, today));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> DeleteDraftAsync(Guid versionId, long? rowVersion, CancellationToken cancellationToken)
    {
        var (_, version, problem) = await DraftAsync(versionId, rowVersion, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        db.Remove(version!);
        return await SaveAsync(async () =>
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.NoContent);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> ActivateAsync(Guid versionId, PolicyDateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (policy, version, problem) = await DraftAsync(versionId, request.RowVersion, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (request.EffectiveFrom is not { } from)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["effectiveFrom"] = [PersonService.Required] });
        }

        if (Reason(request.Reason) is { } tooLong)
        {
            return tooLong;
        }

        var approval = await settings.GetAsync(HrmSettingKeys.HrmPoliciesRequiresApproval, (Guid?)policy!.CompanyId, cancellationToken).ConfigureAwait(false);
        return await SaveAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.policies where id = {policy.Id} for update", cancellationToken).ConfigureAwait(false);
            if (await Writer.CheckActivationAsync(policy, version!, from, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            if (approval.Value.ValueKind == JsonValueKind.True)
            {
                var payload = JsonSerializer.Serialize(new VersionPlan(policy.CompanyId, policy.Id, version!.Id, from, version.RowVersion), JsonSerializerOptions.Web);
                var requestId = await held.SubmitAsync(new HeldChangeRequest(VersionKind, PolicyAccess.Scope.Resource, policy.Id, payload, Blank(request.Reason)), tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId });
            }

            await Writer.ActivateAsync(policy, version!, from, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { status = "applied", policy = await ViewAsync(policy, cancellationToken).ConfigureAwait(false) });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RetireAsync(Guid policyId, PolicyDateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (policy, problem) = await ManagedPolicyAsync(policyId, request.RowVersion, tracked: true, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!policy!.IsActive)
        {
            return PeopleResult.Conflict(PolicyErrors.Retired);
        }

        if (request.EffectiveFrom is not { } from)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["effectiveFrom"] = [PersonService.Required] });
        }

        return await SaveAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.policies where id = {policy.Id} for update", cancellationToken).ConfigureAwait(false);
            var latest = await db.Set<PolicyVersion>().Where(v => v.PolicyId == policyId && v.Status != "draft").OrderByDescending(v => v.ValidFrom).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (latest is not null && (latest.ValidTo is not null || from <= latest.ValidFrom))
            {
                return new PeopleResult(PeopleOutcome.Invalid, "errors.data.invalid_request", FieldErrors: new Dictionary<string, string[]> { ["effectiveFrom"] = [PolicyErrors.RetireAfter] },
                    Extensions: new Dictionary<string, object?> { ["after"] = latest.ValidFrom });
            }

            // Nothing may still assign the policy on or after the date: the assignments change first, so nobody silently loses a policy.
            if (await db.Set<PolicyAssignment>().AnyAsync(a => a.PolicyId == policyId && (a.ValidTo == null || a.ValidTo > from), cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Conflict(PolicyErrors.StillAssigned);
            }

            var now = clock.UtcNow;
            if (latest is not null)
            {
                latest.ValidTo = from;
                latest.Status = "retired";
                PolicyWriter.Bump(latest, now, actor.UserId);
            }

            db.RemoveRange(await db.Set<PolicyVersion>().Where(v => v.PolicyId == policyId && v.Status == "draft").ToListAsync(cancellationToken).ConfigureAwait(false));
            policy.IsActive = false;
            PolicyWriter.Bump(policy, now, actor.UserId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(policy, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    // ================================================================== assignments

    public async Task<PeopleResult> AssignmentsAsync(Guid? companyId, string? policyType, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Read, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (type, company, problem) = await TypeAndCompanyAsync(companyId, policyType, "read", cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var today = await applier.TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var date = asOf ?? today;
        var rows = await (from a in db.Set<PolicyAssignment>().AsNoTracking()
                          join p in db.Set<Policy>().AsNoTracking() on a.PolicyId equals p.Id
                          where a.CompanyId == company && p.PolicyType == type!.Code
                          select new { a, p }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sets = rows.GroupBy(r => r.a.ValidFrom).OrderByDescending(g => g.Key)
            .Select(g => new { validFrom = g.Key, validTo = g.Max(r => r.a.ValidTo), count = g.Count(), inForce = g.Key <= today && g.All(r => r.a.ValidTo is null || r.a.ValidTo > today) }).ToList();
        var current = rows.Where(r => r.a.ValidFrom <= date && (r.a.ValidTo is null || r.a.ValidTo > date)).OrderBy(r => r.a.Precedence).ToList();
        var rules = current.Select(r => PolicyRules.Parse(r.a.Rule.RootElement, PolicyRules.Attributes).Rule).OfType<PolicyRule>().ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            companyId = company,
            policyType = type!.Code,
            asOf = date,
            latestFrom = rows.Count == 0 ? (DateOnly?)null : rows.Max(r => r.a.ValidFrom),
            validFrom = current.Count == 0 ? (DateOnly?)null : current[0].a.ValidFrom,
            assignments = current.Select(r => new
            {
                id = r.a.Id, policyId = r.p.Id, policyCode = r.p.Code, policyName = PolicyAttributeReader.Names(r.p.NameI18n), precedence = r.a.Precedence,
                rule = r.a.Rule.RootElement.Clone(), validFrom = r.a.ValidFrom, validTo = r.a.ValidTo,
            }),
            sets,
            names = await Writer.NamesAsync(company, rules, null, cancellationToken).ConfigureAwait(false),
        });
    }

    public async Task<PeopleResult> CheckAssignmentsAsync(PolicyAssignmentSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Manage, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (type, company, problem) = await TypeAndCompanyAsync(request.CompanyId, request.PolicyType, "manage", cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var today = await applier.TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var latest = await Writer.LatestSetAsync(company, type!.Code, cancellationToken).ConfigureAwait(false);
        if (request.EffectiveFrom is not { } from)
        {
            errors["effectiveFrom"] = [PersonService.Required];
            from = today;
        }
        else if (PolicySetDates.Problem(from, latest, today) is { } dateProblem)
        {
            errors["effectiveFrom"] = [dateProblem];
        }

        var (set, setErrors) = await Writer.PrepareAsync(type, company, request.Assignments ?? [], cancellationToken).ConfigureAwait(false);
        foreach (var (k, v) in setErrors ?? [])
        {
            errors[k] = v;
        }

        var unmatched = set is null ? null : await Writer.UnmatchedAsync(type, company, from, set, ReadableAsync, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { valid = errors.Count == 0, errors, latestFrom = latest, unmatched });
    }

    public async Task<PeopleResult> ActivateAssignmentsAsync(PolicyAssignmentSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Manage, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (type, company, problem) = await TypeAndCompanyAsync(request.CompanyId, request.PolicyType, "manage", cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (request.EffectiveFrom is not { } from)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["effectiveFrom"] = [PersonService.Required] });
        }

        if (Reason(request.Reason) is { } tooLong)
        {
            return tooLong;
        }

        var approval = await settings.GetAsync(HrmSettingKeys.HrmPoliciesRequiresApproval, (Guid?)company, cancellationToken).ConfigureAwait(false);
        var today = await applier.TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return await SaveAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var writer = Writer;
            await writer.LockSetAsync(company, type!.Code, cancellationToken).ConfigureAwait(false);
            var (set, unmatched, refused) = await SetCheckAsync(writer, type, company, from, request.BasedOn, request.Assignments ?? [], today, ReadableAsync, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
            {
                return refused;
            }

            if (approval.Value.ValueKind == JsonValueKind.True)
            {
                var payload = JsonSerializer.Serialize(new SetPlan(company, type.Code, from, request.BasedOn, request.Assignments ?? []), JsonSerializerOptions.Web);
                var requestId = await held.SubmitAsync(new HeldChangeRequest(SetKind, "org.companies", company, payload, Blank(request.Reason)), tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId, unmatched });
            }

            var ids = await writer.ApplySetAsync(type, company, from, set!, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { status = "applied", effectiveFrom = from, assignments = ids, unmatched });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every check of a set activation in the open transaction (also run again by the approval of a held one): the set the caller edited is
    /// still the newest, the date fits, the set is valid. Returns the prepared set and the warning, or the refusal.
    /// </summary>
    internal static async Task<(List<LoadedAssignment>? Set, Unmatched? Unmatched, PeopleResult? Refused)> SetCheckAsync(
        PolicyWriter writer, PolicyType type, Guid company, DateOnly from, DateOnly? basedOn, IReadOnlyList<PolicyAssignmentInput> inputs, DateOnly today,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlySet<Guid>>>? readable, CancellationToken ct)
    {
        var latest = await writer.LatestSetAsync(company, type.Code, ct).ConfigureAwait(false);
        if (latest != basedOn)
        {
            return (null, null, PeopleResult.Conflict(PersonService.Concurrency, new Dictionary<string, object?> { ["latestFrom"] = latest }));
        }

        if (PolicySetDates.Problem(from, latest, today) is { } dateProblem)
        {
            return (null, null, new PeopleResult(PeopleOutcome.Invalid, "errors.data.invalid_request", FieldErrors: new Dictionary<string, string[]> { ["effectiveFrom"] = [dateProblem] },
                Extensions: new Dictionary<string, object?> { ["latestFrom"] = latest }));
        }

        var (set, errors) = await writer.PrepareAsync(type, company, inputs, ct).ConfigureAwait(false);
        if (errors is not null)
        {
            return (null, null, PeopleResult.Fields(errors));
        }

        return (set, await writer.UnmatchedAsync(type, company, from, set!, readable, ct).ConfigureAwait(false), null);
    }

    // ================================================================== resolution view (FR-HRM-POL-03)

    public async Task<PeopleResult> ExplainAsync(Guid? employmentId, string? policyType, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Read, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = types.Find(policyType);
        if (employmentId is null)
        {
            errors["employmentId"] = [PersonService.Required];
        }

        if (type is null)
        {
            errors["policyType"] = [PolicyErrors.UnknownType];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var day = asOf ?? await applier.TodayAsync(employment.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var resolution = (await resolver.ResolveAsync(employment.Id, type!.Code, day, cancellationToken).ConfigureAwait(false))!;
        var person = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == employment.PersonId, cancellationToken).ConfigureAwait(false);
        var rules = resolution.Evaluations.Select(e => new PolicyRule([.. e.Conditions.Select(c => new PolicyCondition(c.Attribute, c.Field, c.Operator, c.Values, c.SubUnits))])).ToList();
        var who = resolution.Attributes;
        var names = await Writer.NamesAsync(employment.CompanyId, rules, new PolicyAttributesView(who.GradeId, who.OrgUnitId, who.BranchId, who.LocationId), cancellationToken).ConfigureAwait(false);
        var policyNames = await db.Set<Policy>().AsNoTracking().Where(p => resolution.Evaluations.Select(e => e.PolicyId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => PolicyAttributeReader.Names(p.NameI18n), cancellationToken).ConfigureAwait(false);

        // The employee's values are shown under the caller's field rules of the employment (review S1.23a finding 1): a hidden custom
        // field or employee type is left out (a condition keeps whether it was met, its value reads "hidden"), a masked one is masked.
        var plan = await fields.PlanAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([employment], Source, cancellationToken).ConfigureAwait(false))[0];
        var custom = System.Text.Json.Nodes.JsonNode.Parse(who.Custom.GetRawText()) as System.Text.Json.Nodes.JsonObject ?? [];
        FieldShaping.ShapeCustom(custom, access);
        var typeLevel = access.Of("employee_type");
        var evaluations = resolution.Evaluations.Select(e => new
        {
            e.AssignmentId, e.PolicyId, e.PolicyCode, policyName = policyNames.GetValueOrDefault(e.PolicyId), e.Precedence, e.Outcome,
            conditions = e.Conditions.Select(c => Shown(c, access)),
        });
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            employee = new { employmentId = employment.Id, employeeNo = employment.EmployeeNo, name = person is null ? string.Empty : PersonService.DisplayName(person), companyId = employment.CompanyId },
            resolution.PolicyType,
            cardinality = type.Cardinality,
            resolution.AsOf,
            resolution.Outcome,
            resolution.Reason,
            resolution.Matches,
            evaluations,
            attributes = new
            {
                employeeType = typeLevel == FieldAccessLevel.Hidden ? null : typeLevel == FieldAccessLevel.Masked ? FieldShaping.Mask(who.EmployeeType)?.ToString() : who.EmployeeType,
                who.Status, who.HasAssignment, who.OrgUnitId, who.BranchId, who.LocationId, who.GradeId, custom, who.GradeDefaults,
            },
            // The job details (unit, branch, location, grade) are read as valid on the date; the employee type and custom fields as they are
            // today (CR-079 S1.23a detail p, R-260).
            currentValues = new[] { PolicyRules.EmployeeType, PolicyRules.Custom },
            names,
        });
    }

    // ================================================================== helpers

    /// <summary>The employments of the list the caller may read: <c>hrm.employments.read</c> and the employment data scope (review S1.23a finding 2).</summary>
    private async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || !await permissions.HasPermissionAsync(EmploymentAccess.Scope.Resource + ".read", ct).ConfigureAwait(false))
        {
            return new HashSet<Guid>();
        }

        var query = await scope.ApplyAsync(db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)), EmploymentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        return (await query.Select(e => e.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
    }

    /// <summary>A condition as the caller may see it: the employee's value of a hidden field is left out (<c>hidden</c> true), a masked one masked.</summary>
    private static object Shown(ConditionEvaluation c, FieldAccessMap access)
    {
        var level = c.Attribute switch
        {
            PolicyRules.Custom => access.Of(FieldAccessMap.CustomPrefix + c.Field),
            PolicyRules.EmployeeType => access.Of("employee_type"),
            _ => FieldAccessLevel.ReadOnly,
        };
        IReadOnlyList<JsonElement> actual = level switch
        {
            FieldAccessLevel.Hidden => [],
            FieldAccessLevel.Masked => [.. c.Actual.Select(v => JsonSerializer.SerializeToElement(FieldShaping.Mask(System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText()))))],
            _ => c.Actual,
        };
        return new { c.Attribute, c.Field, c.Operator, c.Values, c.SubUnits, actual, c.Met, hidden = level == FieldAccessLevel.Hidden };
    }

    private PeopleResult? Check(Policy policy, JsonElement parameters)
    {
        var type = types.Find(policy.PolicyType);
        if (type is null)
        {
            return PeopleResult.Invalid(PolicyErrors.UnknownType);
        }

        var errors = PolicySchema.Check(type.Schema, parameters);
        return errors.Count > 0 ? PeopleResult.Fields(errors) : null;
    }

    private PolicyVersion NewVersion(Policy policy, int number, JsonElement parameters, DateOnly today, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        PolicyId = policy.Id,
        Version = number,
        Parameters = JsonDocument.Parse(parameters.GetRawText()),
        // A customer's draft of a pack policy is the customer's override of the pack's value (FR-HRM-POL-06).
        IsOverridden = policy.Source == "pack",
        Status = "draft",
        // A draft has no period: the date is a placeholder replaced on activation (drafts are outside the exclusion constraint).
        ValidFrom = today,
        ValidTo = null,
        CreatedAt = now,
        CreatedBy = actor.UserId,
        UpdatedAt = now,
        UpdatedBy = actor.UserId,
        RowVersion = 1,
    };

    /// <summary>The policy when the caller may manage it (permission, scope read and update, row version when given); else the answer.</summary>
    private async Task<(Policy? Policy, PeopleResult? Problem)> ManagedPolicyAsync(Guid policyId, long? rowVersion, bool tracked, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Manage, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.Forbidden());
        }

        var query = tracked ? db.Set<Policy>() : db.Set<Policy>().AsNoTracking();
        var policy = await query.FirstOrDefaultAsync(p => p.Id == policyId && !p.IsDeleted, ct).ConfigureAwait(false);
        if (policy is null || !await scope.AllowsAsync(policy, PolicyAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        if (!await scope.AllowsAsync(policy, PolicyAccess.Scope, "manage", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        if (tracked && rowVersion is null)
        {
            return (null, PeopleResult.Invalid(PersonService.RowVersionRequired));
        }

        return rowVersion is { } expected && policy.RowVersion != expected ? (null, PeopleResult.Conflict(PersonService.Concurrency)) : (policy, null);
    }

    /// <summary>A draft (tracked) and its policy when the caller may manage it and the row version matches.</summary>
    private async Task<(Policy? Policy, PolicyVersion? Version, PeopleResult? Problem)> DraftAsync(Guid versionId, long? rowVersion, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(PolicyPermissions.Manage, ct).ConfigureAwait(false))
        {
            return (null, null, PeopleResult.Forbidden());
        }

        if (rowVersion is not { } expected)
        {
            return (null, null, PeopleResult.Invalid(PersonService.RowVersionRequired));
        }

        var version = await db.Set<PolicyVersion>().FirstOrDefaultAsync(v => v.Id == versionId, ct).ConfigureAwait(false);
        var policy = version is null ? null : await db.Set<Policy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == version.PolicyId && !p.IsDeleted, ct).ConfigureAwait(false);
        if (policy is null || !await scope.AllowsAsync(policy, PolicyAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, null, PeopleResult.NotFound);
        }

        if (!await scope.AllowsAsync(policy, PolicyAccess.Scope, "manage", Source, ct).ConfigureAwait(false))
        {
            return (null, null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        if (version!.Status != "draft")
        {
            return (null, null, PeopleResult.Conflict(PolicyErrors.NotDraft));
        }

        return version.RowVersion != expected ? (null, null, PeopleResult.Conflict(PersonService.Concurrency)) : (policy, version, null);
    }

    /// <summary>The registered type and a company inside the caller's scope for the action (read, or update for writes).</summary>
    private async Task<(PolicyType? Type, Guid Company, PeopleResult? Problem)> TypeAndCompanyAsync(Guid? companyId, string? policyType, string action, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = types.Find(policyType);
        if (companyId is null)
        {
            errors["companyId"] = [PersonService.Required];
        }

        if (type is null)
        {
            errors["policyType"] = [PolicyErrors.UnknownType];
        }

        if (errors.Count > 0)
        {
            return (null, default, PeopleResult.Fields(errors));
        }

        var probe = new Policy { CompanyId = companyId!.Value, Code = string.Empty, NameI18n = JsonDocument.Parse("{}"), PolicyType = type!.Code, Source = "tenant" };
        if (!await scope.AllowsAsync(probe, PolicyAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, default, PeopleResult.NotFound);
        }

        if (action != "read" && !await scope.AllowsAsync(probe, PolicyAccess.Scope, action, Source, ct).ConfigureAwait(false))
        {
            return (null, default, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        // A company of another customer does not exist here (one database per customer): unknown, like any record that is not there.
        if ((await org.FindAsync(OrgRecordKind.Company, [companyId.Value], ct).ConfigureAwait(false)).Count == 0)
        {
            return (null, default, PeopleResult.NotFound);
        }

        return (type, companyId.Value, null);
    }

    private static JsonDocument? NameJson(IReadOnlyDictionary<string, string>? name, Dictionary<string, string[]> errors)
    {
        var en = name?.GetValueOrDefault("en")?.Trim();
        var ur = name?.GetValueOrDefault("ur")?.Trim();
        if (string.IsNullOrEmpty(en) || en.Length > NameLength || (ur?.Length ?? 0) > NameLength || name!.Keys.Any(k => k is not ("en" or "ur")))
        {
            errors["name"] = [PolicyErrors.NameRequired];
            return null;
        }

        return JsonSerializer.SerializeToDocument(string.IsNullOrEmpty(ur) ? new Dictionary<string, string> { ["en"] = en } : new Dictionary<string, string> { ["en"] = en, ["ur"] = ur });
    }

    private static PeopleResult? Reason(string? reason) =>
        reason is { Length: > ReasonLength } ? PeopleResult.Fields(new Dictionary<string, string[]> { ["reason"] = ["errors.hrm.field.too_long"] }) : null;

    private static string? Blank(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

    private async Task<PeopleResult> SaveAsync(Func<Task<PeopleResult>> work, CancellationToken ct)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Changed by someone else since it was read (row_version): "reload", as the other HR services answer (review S1.23a finding 4).
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PolicyWriter.Refusal(pg);
        }
        catch (Npgsql.PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PolicyWriter.Refusal(pg);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex CodeShape();
}

/// <summary>A held version activation (kind <c>hrm.policy.version_activation</c>).</summary>
internal sealed record VersionPlan(Guid CompanyId, Guid PolicyId, Guid VersionId, DateOnly EffectiveFrom, long RowVersion);

/// <summary>A held assignment set activation (kind <c>hrm.policy.assignment_activation</c>).</summary>
internal sealed record SetPlan(Guid CompanyId, string PolicyType, DateOnly EffectiveFrom, DateOnly? BasedOn, IReadOnlyList<PolicyAssignmentInput> Assignments);
