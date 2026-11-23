using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;

namespace Erp.IntegrationTests.Policies;

/// <summary>Helpers of the policy tests (slice S1.23a): a policy with an active version, and an assignment set activated through the API.</summary>
public static class PolicyTestSupport
{
    public const string Policies = "/api/hrm/policies";
    public const string Sets = "/api/hrm/policy-assignment-sets";

    /// <summary>
    /// The test module's policy types (slice S1.23a Build item 1: registered in tests only, D-10): <c>test_leave</c> (one policy applies,
    /// every employee should be covered) and <c>test_benefit</c> (several may apply).
    /// </summary>
    public const string TestTypes = """
        {
          "module": "test",
          "types": [
            {
              "code": "test_leave", "name": { "en": "Test leave", "ur": "آزمائشی رخصت" }, "cardinality": "one", "required": true,
              "attributes": ["location", "branch", "grade", "employee_type", "unit", "custom"],
              "schema": { "type": "object", "additionalProperties": false, "required": ["days"],
                "properties": { "days": { "type": "integer", "minimum": 0, "maximum": 365 }, "note": { "type": "string", "maxLength": 20 },
                  "kind": { "type": "string", "enum": ["paid", "unpaid"] } } }
            },
            {
              "code": "test_benefit", "name": { "en": "Test benefit", "ur": "آزمائشی فائدہ" }, "cardinality": "many", "required": false,
              "attributes": ["grade", "employee_type"],
              "schema": { "type": "object", "additionalProperties": false, "properties": { "amount": { "type": "number", "minimum": 0 } } }
            }
          ]
        }
        """;

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A new policy of the type with its first version activated from <paramref name="from"/>; returns the policy and version ids.</summary>
    public static async Task<(Guid Policy, Guid Version)> ActivePolicyAsync(this AccessHarness h, Guid user, Guid company, string type, object parameters, DateOnly from, CancellationToken ct, string? code = null)
    {
        ArgumentNullException.ThrowIfNull(h);
        code ??= "P" + Guid.NewGuid().ToString("N")[..10];
        var created = await h.PostAsync(user, Policies, new { companyId = company, policyType = type, code, name = new { en = "Policy " + code, ur = "پالیسی " + code }, parameters }, ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var policy = created.Json.GetProperty("id").GetGuid();
        var draft = created.Json.GetProperty("versions")[0];
        var activated = await h.PostAsync(user, $"/api/hrm/policy-versions/{draft.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = Iso(from), rowVersion = draft.GetProperty("rowVersion").GetInt64() }, ct);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
        return (policy, draft.GetProperty("id").GetGuid());
    }

    /// <summary>The start of the newest set of the type in the company (the <c>basedOn</c> of the next activation).</summary>
    public static async Task<string?> LatestSetAsync(this AccessHarness h, Guid user, Guid company, string type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(h);
        var read = await h.GetAsync(user, $"{Sets}?companyId={company}&policyType={type}", ct);
        Assert.True(read.Status == HttpStatusCode.OK, read.Body);
        return read.Json.GetProperty("latestFrom").ValueKind == JsonValueKind.Null ? null : read.Json.GetProperty("latestFrom").GetString();
    }

    /// <summary>Activates a set of assignments (policy, precedence, conditions) from the date, on top of the newest set.</summary>
    public static async Task<Answer> ActivateSetAsync(this AccessHarness h, Guid user, Guid company, string type, DateOnly from, CancellationToken ct, params (Guid Policy, int Precedence, object[] Conditions)[] assignments)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(assignments);
        var basedOn = await h.LatestSetAsync(user, company, type, ct);
        return await h.PostAsync(user, $"{Sets}/activate", new
        {
            companyId = company, policyType = type, effectiveFrom = Iso(from), basedOn,
            assignments = assignments.Select(a => new { policyId = a.Policy, precedence = a.Precedence, rule = new { conditions = a.Conditions } }).ToArray(),
        }, ct);
    }

    /// <summary>
    /// A probation policy of <paramref name="months"/> months assigned to the employee types (the S1.20 defaults: probation and permanent)
    /// from long ago, as the replaced settings did (S1.23 ruling Q3). The user needs <c>hrm.policies.manage</c> (HR administrators).
    /// </summary>
    public static async Task<Guid> ProbationAsync(this AccessHarness h, Guid hr, Guid company, CancellationToken ct, int months = 3, string[]? types = null, object? extra = null)
    {
        ArgumentNullException.ThrowIfNull(h);
        var from = h.Today.AddYears(-5);
        var parameters = extra ?? new { months };
        var (policy, _) = await h.ActivePolicyAsync(hr, company, "probation", parameters, from, ct);
        var set = await h.ActivateSetAsync(hr, company, "probation", from, ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = types ?? ["probation", "permanent"] }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return policy;
    }
}
