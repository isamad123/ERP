using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Policies;

/// <summary>A new policy of a type for a company, with its first draft version (slice S1.23a Build item 2).</summary>
public sealed record PolicyCreateRequest(Guid? CompanyId, string? PolicyType, string? Code, IReadOnlyDictionary<string, string>? Name, JsonElement? Parameters);

/// <summary>A new name of a policy (its code and type are fixed).</summary>
public sealed record PolicyEditRequest(IReadOnlyDictionary<string, string>? Name, long? RowVersion);

/// <summary>A new draft (the parameters of the latest version when none are given) or an edit of a draft.</summary>
public sealed record PolicyDraftRequest(JsonElement? Parameters, long? RowVersion);

/// <summary>Activation of a draft from a date (the version in force before it ends the day before), or retirement of a policy from a date.</summary>
public sealed record PolicyDateRequest(DateOnly? EffectiveFrom, long? RowVersion, string? Reason);

/// <summary>One assignment of a set: the policy, its precedence (unique in the set; 1 is tried first) and its rule.</summary>
public sealed record PolicyAssignmentInput(Guid? PolicyId, int? Precedence, JsonElement? Rule);

/// <summary>
/// The assignments of one policy type in one company from a date (ruling Q4): they replace the set in force from that date.
/// <paramref name="BasedOn"/> is the start date of the set the caller edited (null when there was none), so a set activated meanwhile is
/// not overwritten unseen.
/// </summary>
public sealed record PolicyAssignmentSetRequest(
    Guid? CompanyId, string? PolicyType, DateOnly? EffectiveFrom, DateOnly? BasedOn, IReadOnlyList<PolicyAssignmentInput>? Assignments, string? Reason);

/// <summary>
/// The HR policy services (slice S1.23a Build items 1 to 6; FR-HRM-POL-02, FR-HRM-POL-03, FR-HRM-POL-06). Writes need
/// <c>hrm.policies.manage</c> and the company inside the caller's data scope; activations are held for a second person while
/// <c>hrm.policies.requires_approval</c> is on (ruling Q1, FR-XM-32). Answers are those of the person services.
/// </summary>
public interface IPolicyService
{
    Task<PeopleResult> TypesAsync(CancellationToken cancellationToken);

    Task<PeopleResult> CreateAsync(PolicyCreateRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> EditAsync(Guid policyId, PolicyEditRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> DetailAsync(Guid policyId, CancellationToken cancellationToken);

    Task<PeopleResult> NewDraftAsync(Guid policyId, PolicyDraftRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> EditDraftAsync(Guid versionId, PolicyDraftRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> DeleteDraftAsync(Guid versionId, long? rowVersion, CancellationToken cancellationToken);

    Task<PeopleResult> ActivateAsync(Guid versionId, PolicyDateRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> RetireAsync(Guid policyId, PolicyDateRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> AssignmentsAsync(Guid? companyId, string? policyType, DateOnly? asOf, CancellationToken cancellationToken);

    Task<PeopleResult> CheckAssignmentsAsync(PolicyAssignmentSetRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ActivateAssignmentsAsync(PolicyAssignmentSetRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ExplainAsync(Guid? employmentId, string? policyType, DateOnly? asOf, CancellationToken cancellationToken);
}
