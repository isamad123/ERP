using System.Text.Json;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Org;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Policies;

/// <summary>One assignment of a set, with its policy and parsed rule.</summary>
internal sealed record LoadedAssignment(PolicyAssignment Row, Policy Policy, PolicyRule Rule);

/// <summary>The assignments of one type and company valid on a date (precedence order), the versions in force, and the unit subtrees the rules name.</summary>
internal sealed record LoadedSet(IReadOnlyList<LoadedAssignment> Assignments, IReadOnlyDictionary<Guid, PolicyVersion> Versions, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> Subtrees);

/// <summary>
/// The policy resolver (contract <c>IPolicyResolver</c>, slice S1.23a Build item 4, ruling Q5). Reads through the given HR context, so it
/// sees the caller's own transaction (a hire resolves its probation from the assignment row it just saved). Collects the employee's
/// attributes on the date, evaluates the active assignments of the type in precedence order (ties are impossible: the database refuses
/// them), first match wins (all matches for a type of cardinality "many"), and answers the version in force with the reason. Policy data
/// is cached per instance and (type, company, date); attributes are read on every call. No permission check: engines call it; the
/// resolution view checks its own.
/// </summary>
public sealed class PolicyResolver(HrmDbContext db, PolicyTypeCatalog types, IOrgStructure org) : IPolicyResolver
{
    private readonly Dictionary<(string Type, Guid Company, DateOnly Date), LoadedSet> sets = [];

    private static readonly IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> NoSubtrees = new Dictionary<Guid, IReadOnlySet<Guid>>();

    /// <summary>A resolver reading through the given context (a job chunk's or an approval's transaction).</summary>
    public static PolicyResolver For(HrmDbContext db, IServiceProvider services) =>
        new(db, Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<PolicyTypeCatalog>(services),
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IOrgStructure>(services));

    public async Task<PolicyResolution?> ResolveAsync(Guid employmentId, string policyType, DateOnly asOf, CancellationToken cancellationToken)
    {
        var type = types.Find(policyType) ?? throw new ArgumentException("Unknown policy type: " + policyType, nameof(policyType));
        var who = await PolicyAttributeReader.OneAsync(db, employmentId, asOf, cancellationToken).ConfigureAwait(false);
        if (who is null)
        {
            return null;
        }

        var set = await SetAsync(type, who.CompanyId, asOf, cancellationToken).ConfigureAwait(false);
        return Decide(type, who, asOf, set);
    }

    /// <summary>The loaded set of a type in a company on a date (cached for this instance).</summary>
    internal async Task<LoadedSet> SetAsync(PolicyType type, Guid companyId, DateOnly asOf, CancellationToken ct)
    {
        if (!sets.TryGetValue((type.Code, companyId, asOf), out var set))
        {
            var rows = await (from a in db.Set<PolicyAssignment>().AsNoTracking()
                              join p in db.Set<Policy>().AsNoTracking() on a.PolicyId equals p.Id
                              where a.CompanyId == companyId && p.PolicyType == type.Code && a.ValidFrom <= asOf && (a.ValidTo == null || a.ValidTo > asOf)
                              select new { a, p }).ToListAsync(ct).ConfigureAwait(false);
            var loaded = rows.OrderBy(r => r.a.Precedence).ThenBy(r => r.a.Id)
                .Select(r => new LoadedAssignment(r.a, r.p, PolicyRules.Parse(r.a.Rule.RootElement, PolicyRules.Attributes).Rule ?? Never)).ToList();
            sets[(type.Code, companyId, asOf)] = set = await CompleteAsync(loaded, asOf, ct).ConfigureAwait(false);
        }

        return set;
    }

    /// <summary>Adds the versions in force on the date and the subtrees the rules name to a list of assignments.</summary>
    internal async Task<LoadedSet> CompleteAsync(IReadOnlyList<LoadedAssignment> assignments, DateOnly asOf, CancellationToken ct)
    {
        var policyIds = assignments.Select(a => a.Policy.Id).Distinct().ToList();
        var versions = policyIds.Count == 0 ? [] : await db.Set<PolicyVersion>().AsNoTracking()
            .Where(v => policyIds.Contains(v.PolicyId) && v.Status != "draft" && v.ValidFrom <= asOf && (v.ValidTo == null || v.ValidTo > asOf))
            .ToListAsync(ct).ConfigureAwait(false);
        var roots = assignments.SelectMany(a => PolicyRules.SubtreeRoots(a.Rule)).Distinct().ToList();
        var subtrees = roots.Count == 0 ? NoSubtrees : await org.SubtreesAsync(roots, asOf, ct).ConfigureAwait(false);
        return new LoadedSet(assignments, versions.ToDictionary(v => v.PolicyId), subtrees);
    }

    /// <summary>The decision for one employee (pure given the loaded set): precedence order, first match (or every match for "many").</summary>
    internal static PolicyResolution Decide(PolicyType type, PolicyAttributes who, DateOnly asOf, LoadedSet set)
    {
        var evaluations = new List<AssignmentEvaluation>();
        var matches = new List<PolicyMatch>();
        string? reason = null;
        var decided = false;
        foreach (var a in set.Assignments)
        {
            var (met, conditions) = PolicyRules.Evaluate(a.Rule, who, set.Subtrees);
            string outcome;
            if (decided || !who.HasAssignment)
            {
                outcome = "not_reached";
            }
            else if (!met)
            {
                outcome = "not_matched";
            }
            else if (set.Versions.TryGetValue(a.Policy.Id, out var version))
            {
                outcome = "matched";
                matches.Add(Match(a, version));
                decided = !type.IsMany;
            }
            else
            {
                // Matched, but nothing is in force: answered as "no policy", never a fall-through to the next rule (ruling Q5).
                outcome = "no_version";
                if (!type.IsMany)
                {
                    reason = "no_version_in_force";
                    decided = true;
                }
            }

            evaluations.Add(new AssignmentEvaluation(a.Row.Id, a.Policy.Id, a.Policy.Code, a.Row.Precedence, outcome, conditions));
        }

        if (matches.Count == 0)
        {
            reason ??= who.HasAssignment ? "no_rule_matched" : "no_assignment_row";
        }
        else
        {
            reason = null;
        }

        return new PolicyResolution(type.Code, asOf, matches.Count > 0 ? PolicyResolution.Matched : PolicyResolution.NoPolicy, reason, who, matches, evaluations);
    }

    private static PolicyMatch Match(LoadedAssignment a, PolicyVersion v) => new(
        a.Policy.Id, a.Policy.Code, PolicyAttributeReader.Names(a.Policy.NameI18n), v.Id, v.Version, v.ValidFrom, v.ValidTo, v.Parameters.RootElement.Clone(),
        a.Policy.Source == "pack" && !v.IsOverridden ? "pack" : "tenant", a.Row.Id, a.Row.Precedence);

    /// <summary>A stored rule that no longer reads (never written by the service) matches nobody.</summary>
    private static readonly PolicyRule Never = new([new PolicyCondition(PolicyRules.EmployeeType, null, PolicyRules.In, [JsonSerializer.SerializeToElement("\u0000")], false)]);
}

/// <summary>Reads employees' attributes on a date: the employment, its assignment row valid on the date, its custom values and the grade's defaults.</summary>
internal static class PolicyAttributeReader
{
    public static async Task<PolicyAttributes?> OneAsync(HrmDbContext db, Guid employmentId, DateOnly asOf, CancellationToken ct)
    {
        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, ct).ConfigureAwait(false);
        if (employment is null)
        {
            return null;
        }

        var row = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employmentId && a.ValidFrom <= asOf && (a.ValidTo == null || a.ValidTo > asOf))
            .OrderByDescending(a => a.ValidFrom).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var defaults = row?.GradeId is { } grade
            ? (await db.Set<Grade>().AsNoTracking().Where(g => g.Id == grade).Select(g => g.Defaults).FirstOrDefaultAsync(ct).ConfigureAwait(false))?.RootElement.Clone()
            : null;
        return Of(employment, row, defaults);
    }

    /// <summary>The employments of a company in the given statuses with their attributes on the date (the activation check's population).</summary>
    public static async Task<List<(Employment Employment, PolicyAttributes Attributes)>> CompanyAsync(HrmDbContext db, Guid companyId, IReadOnlyCollection<string> statuses, DateOnly asOf, CancellationToken ct)
    {
        var employments = await db.Set<Employment>().AsNoTracking().Where(e => e.CompanyId == companyId && statuses.Contains(e.Status)).OrderBy(e => e.EmployeeNo).ToListAsync(ct).ConfigureAwait(false);
        var ids = employments.Select(e => e.Id).ToList();
        var rows = ids.Count == 0 ? [] : await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.ValidFrom <= asOf && (a.ValidTo == null || a.ValidTo > asOf)).ToListAsync(ct).ConfigureAwait(false);
        var byEmployment = rows.GroupBy(r => r.EmploymentId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ValidFrom).First());
        return [.. employments.Select(e => (e, Of(e, byEmployment.GetValueOrDefault(e.Id), null)))];
    }

    private static PolicyAttributes Of(Employment e, EmploymentAssignment? row, JsonElement? gradeDefaults) => new(
        e.Id, e.CompanyId, e.EmployeeType, e.Status, row is not null, row?.OrgUnitId, row?.BranchId, row?.LocationId, row?.GradeId,
        e.Custom.RootElement.Clone(), gradeDefaults);

    public static Dictionary<string, string> Names(JsonDocument name) =>
        name.RootElement.ValueKind == JsonValueKind.Object
            ? name.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];
}
