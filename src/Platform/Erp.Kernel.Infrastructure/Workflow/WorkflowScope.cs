using Erp.Kernel.Infrastructure.Access;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// Which requests a user sees as an administrator through one permission (slice S1.10 ruling Q13, S1.10b ruling P1),
/// by the access engine's rules for the resource <c>wf.workflow_instances</c>: a grant whose data scope is "all" covers
/// every company (or the company of its assignment); a "companies" scope covers the companies it names (within the
/// assignment's company); an "own" scope covers only the user's own requests. Other scopes (team, subtree, org units,
/// branches) and scopes with conditions cover nothing here: requests have no such locators, and the answer is never wider
/// than the access engine's.
/// </summary>
internal sealed record WorkflowCoverage(bool AllCompanies, IReadOnlySet<Guid> Companies, bool Own)
{
    public const string Resource = "wf.workflow_instances";

    public static WorkflowCoverage None { get; } = new(false, new HashSet<Guid>(), false);

    /// <summary>Whether the user administers requests of the company (own requests do not count).</summary>
    public bool CoversCompany(Guid companyId) => AllCompanies || Companies.Contains(companyId);

    public bool AdministersAny => AllCompanies || Companies.Count > 0;

    public bool Sees(Guid companyId, Guid requester, Guid userId) => CoversCompany(companyId) || (Own && requester == userId);

    public static async Task<WorkflowCoverage> ForAsync(WorkflowWork work, Guid userId, string permission, CancellationToken ct)
    {
        var snapshot = await AccessSnapshot.LoadAsync(work.Connection, work.Transaction, userId, ApproverResolution.Today(work), ct).ConfigureAwait(false);
        var all = false;
        var own = false;
        var companies = new HashSet<Guid>();
        foreach (var (assignment, role) in snapshot.Granting(permission))
        {
            if (role.ScopeFor(Resource) is not { Conditions.Count: 0 } scope)
            {
                continue; // deny by default, as the access engine does; conditions name attributes requests do not have
            }

            switch (scope.Kind)
            {
                case ScopeRule.All when assignment.CompanyId is { } company:
                    companies.Add(company);
                    break;
                case ScopeRule.All:
                    all = true;
                    break;
                case ScopeRule.Companies:
                    companies.UnionWith(scope.Ids.Where(id => assignment.CompanyId is null || assignment.CompanyId == id));
                    break;
                case ScopeRule.Own:
                    own = true;
                    break;
            }
        }

        return new WorkflowCoverage(all, companies, own);
    }
}
