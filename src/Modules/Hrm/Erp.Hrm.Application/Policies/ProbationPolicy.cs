using System.Text.Json;
using Erp.Hrm.Contracts.Policies;

namespace Erp.Hrm.Application.Policies;

/// <summary>
/// The parameters of policy type <c>probation</c> (slice S1.23 ruling Q3; replaces the S1.20 settings <c>hrm.probation.default_months</c>
/// and <c>hrm.probation.employee_types</c>): months of the probation, the most months an extension may add (none given: no limit; 0: no
/// extension), and who is reminded to review it (<c>line_manager_and_hr</c>, the default, or <c>hr</c> only). Customer choices, never
/// statutory values (AS-4).
/// </summary>
public static class ProbationPolicy
{
    public const string Type = "probation";
    public const string ReviewerHr = "hr";
    public const string ReviewerBoth = "line_manager_and_hr";

    public static int? Months(PolicyMatch? policy) => Int(policy, "months") is { } m and >= 1 ? m : null;

    public static int? ExtensionMonthsMax(PolicyMatch? policy) => Int(policy, "extensionMonthsMax");

    public static string Reviewer(PolicyMatch? policy) =>
        policy is not null && policy.Parameters.TryGetProperty("reviewer", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : ReviewerBoth;

    private static int? Int(PolicyMatch? policy, string name) =>
        policy is not null && policy.Parameters.ValueKind == JsonValueKind.Object && policy.Parameters.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
