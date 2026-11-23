using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Employments;

/// <summary>
/// The employee list of slice S1.21a (Build item 1; FR-HRM-EMP-01, GHR-06) through the real API pipeline, the real access engine and a real
/// PostgreSQL: filters on every column (names in both scripts, number, company, unit, title, manager, status, type, joining dates), the as-of
/// date, paging, the data scope of the employment, field security of the names and no pay data.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EmployeeListTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private const string List = "/api/hrm/employee-list";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        return (company, await _h.PlaceAsync(company, Ct));
    }

    private async Task<Guid> HrAsync(Guid? company, params string[] extra)
    {
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employments.create", "hrm.employment_assignments.read", "hrm.persons.read", "hrm.persons.create", .. extra],
            [("hrm.employments", "all"), ("hrm.employment_assignments", "all"), ("hrm.persons", "all")]);
        await _h.AssignAsync(user, role, company, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    private async Task<(Guid Id, string No)> HireAsync(Guid hr, Guid company, (Guid OrgUnit, Guid Branch) place, string first, string? local, string title, string joining, Guid? manager = null)
    {
        var body = EmploymentTestSupport.HireBody(company, place, newPerson: new { firstName = first, lastName = "Listed", nameLocal = local, gender = "female" }, joining: joining, title: title, manager: manager);
        var answer = await _h.PostAsync(hr, Employments, body, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Text("employeeNo")!);
    }

    private static string[] Ids(Answer a) => [.. a.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid().ToString())];

    [Fact]
    [Trait("Requirement", "FR-HRM-EMP-01")]
    public async Task S121a_FR_HRM_EMP_01_the_list_filters_by_name_in_either_script_number_title_manager_status_type_company_and_joining_date()
    {
        var (company, place) = await CompanyAsync();
        var hr = await HrAsync(company);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var boss = await HireAsync(hr, company, place, "Boss" + tag, null, "Head " + tag, "2024-01-10");
        var ayesha = await HireAsync(hr, company, place, "Ayesha" + tag, "عائشہ" + tag, "Accountant " + tag, "2025-03-01", boss.Id);
        var bilal = await HireAsync(hr, company, place, "Bilal" + tag, null, "Clerk " + tag, "2026-02-01", boss.Id);

        var all = await _h.GetAsync(hr, $"{List}?companyId={company}&sort=joiningDate", Ct);
        Assert.True(all.Status == HttpStatusCode.OK, all.Body);
        Assert.Equal(new[] { bilal.Id, ayesha.Id, boss.Id }.Select(g => g.ToString()), Ids(all)); // newest joiner first
        var row = all.Json.GetProperty("items")[1];
        Assert.Equal("Ayesha" + tag, row.GetProperty("person").GetProperty("firstName").GetString());
        Assert.Equal("Accountant " + tag, row.GetProperty("title").GetString());
        Assert.Equal("Boss" + tag + " Listed", row.GetProperty("managerName").GetString());

        Assert.Equal([ayesha.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&search=ayesha{tag}", Ct)));        // not case sensitive
        Assert.Equal([ayesha.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&search={Uri.EscapeDataString("عائشہ" + tag)}", Ct))); // the local-script name
        Assert.Equal([bilal.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&employeeNo={bilal.No}", Ct)));
        Assert.Equal([bilal.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&title=clerk%20{tag}", Ct)));
        Assert.Equal([bilal.Id.ToString(), ayesha.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&managerEmploymentId={boss.Id}&sort=joiningDate", Ct)));
        Assert.Equal([ayesha.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&joinedFrom=2025-01-01&joinedTo=2025-12-31", Ct)));
        Assert.Equal(3, Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&status=active&employeeType=permanent", Ct)).Length);
        Assert.Empty(Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&status=separated", Ct)));
        Assert.Equal([boss.Id.ToString()], Ids(await _h.GetAsync(hr, $"{List}?companyId={company}&orgUnitId={place.OrgUnit}&search=head%20{tag}", Ct)));
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(hr, $"{List}?sort=salary", Ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(hr, $"{List}?after=nonsense", Ct)).Status);
    }

    [Fact]
    [Trait("Requirement", "FR-HRM-EMP-01")]
    public async Task S121a_FR_HRM_EMP_01_the_list_pages_with_a_cursor_and_the_as_of_date_leaves_out_people_who_had_not_joined_yet()
    {
        var (company, place) = await CompanyAsync();
        var hr = await HrAsync(company);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var first = await HireAsync(hr, company, place, "Page1" + tag, null, "T", "2024-05-01");
        var second = await HireAsync(hr, company, place, "Page2" + tag, null, "T", "2025-05-01");
        var third = await HireAsync(hr, company, place, "Page3" + tag, null, "T", "2026-05-01");

        var seen = new List<string>();
        string? after = null;
        for (var round = 0; round < 5; round++)
        {
            var page = await _h.GetAsync(hr, $"{List}?companyId={company}&limit=2" + (after is null ? string.Empty : "&after=" + Uri.EscapeDataString(after)), Ct);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            seen.AddRange(Ids(page));
            after = page.Json.GetProperty("next").GetString();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(new[] { first.Id, second.Id, third.Id }.Select(g => g.ToString()).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal)); // every row once
        Assert.Equal(3, seen.Distinct().Count());

        var inMid2025 = await _h.GetAsync(hr, $"{List}?companyId={company}&asOf=2025-07-01", Ct);
        Assert.Equal(new[] { first.Id, second.Id }.Select(g => g.ToString()).Order(StringComparer.Ordinal), Ids(inMid2025).Order(StringComparer.Ordinal));
        Assert.Equal("2025-07-01", inMid2025.Json.GetProperty("asOf").GetString());
    }

    [Fact]
    [Trait("Requirement", "FR-HRM-EMP-01")]
    public async Task S121a_FR_HRM_EMP_01_a_role_limited_to_one_company_sees_only_its_employees_and_nobody_without_the_right_sees_any()
    {
        var (mine, minePlace) = await CompanyAsync();
        var (other, otherPlace) = await CompanyAsync();
        var hr = await HrAsync(null);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var inMine = await HireAsync(hr, mine, minePlace, "Mine" + tag, null, "T", "2025-01-01");
        var inOther = await HireAsync(hr, other, otherPlace, "Other" + tag, null, "T", "2025-01-01");

        var limited = await HrAsync(mine);
        var seen = Ids(await _h.GetAsync(limited, $"{List}?search={tag}", Ct));
        Assert.Equal([inMine.Id.ToString()], seen);
        Assert.DoesNotContain(inOther.Id.ToString(), seen);
        Assert.Equal(2, Ids(await _h.GetAsync(hr, $"{List}?search={tag}", Ct)).Length);

        var nobody = await _h.UserAsync(Ct);
        var noRole = await _h.RoleAsync(Ct, ["org.companies.read"]);
        await _h.AssignAsync(nobody, noRole, null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, List, Ct)).Status);
    }

    [Fact]
    [Trait("Requirement", "GHR-06")]
    public async Task S121a_GHR_06_a_caller_without_person_or_job_detail_rights_gets_no_names_titles_or_job_filters_and_the_list_has_no_pay_data()
    {
        var (company, place) = await CompanyAsync();
        var hr = await HrAsync(company);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var hired = await HireAsync(hr, company, place, "Secret" + tag, null, "Treasurer " + tag, "2025-01-01");

        var narrow = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.employments.read"], [("hrm.employments", "all")]);
        await _h.AssignAsync(narrow, role, company, _h.Today.AddDays(-1), null, Ct);
        var seen = await _h.GetAsync(narrow, $"{List}?companyId={company}", Ct);
        var item = Assert.Single(seen.Json.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == hired.Id);
        Assert.False(item.TryGetProperty("person", out _)); // no persons.read: no name
        Assert.False(item.TryGetProperty("title", out _)); // no assignments.read: no job details
        Assert.Empty(Ids(await _h.GetAsync(narrow, $"{List}?companyId={company}&search=Secret{tag}", Ct))); // the name cannot be probed by searching
        Assert.Empty(Ids(await _h.GetAsync(narrow, $"{List}?companyId={company}&title=Treasurer", Ct))); // nor the title

        var text = (await _h.GetAsync(hr, $"{List}?companyId={company}", Ct)).Body;
        foreach (var pay in new[] { "salary", "bank", "iban", "amount", "payGroup" })
        {
            Assert.DoesNotContain(pay, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
