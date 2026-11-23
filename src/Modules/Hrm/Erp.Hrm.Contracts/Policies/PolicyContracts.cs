using System.Text.Json;

namespace Erp.Hrm.Contracts.Policies;

/// <summary>
/// Permissions of the policy framework (slice S1.23a, ruling Q1). The generated writes of <c>hrm.policies</c>, <c>hrm.policy_versions</c>
/// and <c>hrm.policy_assignments</c> are closed; reads stay the generated <c>&lt;table&gt;.read</c>. Declared in
/// <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>.
/// </summary>
public static class PolicyPermissions
{
    public const string Read = "hrm.policies.read";

    public const string VersionsRead = "hrm.policy_versions.read";

    public const string AssignmentsRead = "hrm.policy_assignments.read";

    /// <summary>Create policies, edit drafts, activate versions, retire policies and activate assignment sets (HR administrators, Q1).</summary>
    public const string Manage = "hrm.policies.manage";

    /// <summary>Approve or reject a held activation made by someone else while <c>hrm.policies.requires_approval</c> is on (FR-XM-32). Sensitive.</summary>
    public const string Approve = "hrm.policies.approve";

    /// <summary>The hand-written codes (declared in permissions.json).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [Manage, Approve];
}

/// <summary>
/// One module's policy-type manifest (<c>&lt;module&gt;.Infrastructure/Access/policy-types.json</c>, ruling Q2, D-10). A module registers
/// it as a singleton; the HR policy framework reads every registered source. Tests register a test type the same way.
/// </summary>
public sealed record PolicyTypeSource(string Json);

/// <summary>
/// How a decision was reached for one assignment: each condition with the employee's value on the date and whether it was met.
/// <paramref name="Outcome"/> is <c>matched</c>, <c>not_matched</c>, <c>not_reached</c> (an earlier assignment won) or
/// <c>no_version</c> (matched, but the policy has no version in force on the date).
/// </summary>
public sealed record AssignmentEvaluation(
    Guid AssignmentId, Guid PolicyId, string PolicyCode, int Precedence, string Outcome, IReadOnlyList<ConditionEvaluation> Conditions);

/// <summary>One condition of a rule as evaluated: what it asks and what the employee has (ids, codes or custom values as JSON).</summary>
public sealed record ConditionEvaluation(
    string Attribute, string? Field, string Operator, IReadOnlyList<JsonElement> Values, bool SubUnits, IReadOnlyList<JsonElement> Actual, bool Met);

/// <summary>
/// The policy that applies (FR-HRM-POL-03): the version in force on the date, its parameters, and why: the assignment that matched and
/// the layer the version comes from (<c>pack</c> when it is the pack's own version, <c>tenant</c> when the customer made or changed it).
/// </summary>
public sealed record PolicyMatch(
    Guid PolicyId, string PolicyCode, IReadOnlyDictionary<string, string> PolicyName, Guid VersionId, int Version, DateOnly ValidFrom, DateOnly? ValidTo,
    JsonElement Parameters, string Layer, Guid AssignmentId, int Precedence);

/// <summary>
/// The employee's attributes the rules looked at, as of the date (the employment, the assignment row valid on the date, the
/// employment's custom values) and the grade's named defaults (FR-HRM-POS-06), which engines may read beside the parameters.
/// </summary>
public sealed record PolicyAttributes(
    Guid EmploymentId, Guid CompanyId, string EmployeeType, string Status, bool HasAssignment, Guid? OrgUnitId, Guid? BranchId, Guid? LocationId,
    Guid? GradeId, JsonElement Custom, JsonElement? GradeDefaults);

/// <summary>
/// The answer of <see cref="IPolicyResolver"/>. <paramref name="Outcome"/> is <c>matched</c> or <c>no_policy</c>; for no policy,
/// <paramref name="Reason"/> is <c>no_assignment_row</c> (no job details on the date), <c>no_rule_matched</c> or
/// <c>no_version_in_force</c> (the first matching policy has no version on the date; never a silent fall-through, ruling Q5).
/// <paramref name="Matches"/> holds one policy for a type of cardinality one, every matching policy with a version for "many".
/// </summary>
public sealed record PolicyResolution(
    string PolicyType, DateOnly AsOf, string Outcome, string? Reason, PolicyAttributes Attributes, IReadOnlyList<PolicyMatch> Matches,
    IReadOnlyList<AssignmentEvaluation> Evaluations)
{
    public const string Matched = "matched";
    public const string NoPolicy = "no_policy";

    /// <summary>The single policy of a type of cardinality one, or null.</summary>
    public PolicyMatch? Policy => Matches.Count > 0 ? Matches[0] : null;
}

/// <summary>
/// Which policy of a type applies to an employment on a date, and why (slice S1.23a Build item 4, ruling Q5; contract
/// <c>IPolicyResolver</c>). Every HR engine (leave, attendance, pay, probation) asks this; it never applies a default of its own:
/// "no policy" is answered and the caller decides what that means. Deterministic: the same data gives the same answer. Policy data is
/// cached for the life of the instance (one request or one job chunk); the employee's attributes are read on every call.
/// </summary>
public interface IPolicyResolver
{
    /// <summary>Null when the employment does not exist. Throws <see cref="ArgumentException"/> for a type no module registered.</summary>
    Task<PolicyResolution?> ResolveAsync(Guid employmentId, string policyType, DateOnly asOf, CancellationToken cancellationToken);
}
