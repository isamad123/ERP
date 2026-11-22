using Erp.IntegrationTests.Access;

namespace Erp.IntegrationTests.Employments;

/// <summary>Helpers of the employment tests (slice S1.20b): a place to hire into and the body of a hire.</summary>
public static class EmploymentTestSupport
{
    /// <summary>An org unit of the company and the company's default branch (rule SC-2).</summary>
    public static async Task<(Guid OrgUnit, Guid Branch)> PlaceAsync(this AccessHarness h, Guid company, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(h);
        var unit = (Guid)(await h.RowAsync("org.org_units", company, null, ct))["id"]!;
        var branch = await h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 order by created_at, id limit 1", ct, company);
        return (unit, branch);
    }

    /// <summary>The body of <c>POST /api/hrm/employments</c> for an existing person or a new one.</summary>
    public static object HireBody(Guid company, (Guid OrgUnit, Guid Branch) place, Guid? person = null, object? newPerson = null, string joining = "2026-01-05",
        string? employeeNo = null, string title = "Officer", Guid? manager = null, object? costAllocation = null) => new
        {
            personId = person,
            person = newPerson,
            companyId = company,
            employeeType = "permanent",
            joiningDate = joining,
            employeeNo,
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title, managerEmploymentId = manager, costAllocation },
        };
}
