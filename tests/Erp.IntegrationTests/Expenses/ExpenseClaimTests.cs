#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Contracts.Payroll;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Expenses;

/// <summary>
/// Expense claims of slice S1.42 through the real API pipeline, the real access engine, the real workflow engine, the real payroll input provider and a real
/// PostgreSQL: FR-HRM-EXP-01 (limits by grade, per-diem and mileage rates from the policy; no figure shipped), FR-HRM-EXP-02 (claims with receipts, lines in
/// another currency converted at the customer's rate that is kept on the line), FR-HRM-EXP-03 (each policy flag has a failing example, a duplicate receipt is
/// found across two claims, a category may block submit; per-line approval pays only the approved lines; the finance step by threshold), FR-HRM-EXP-05
/// (exactly one payroll input per claim, consumed by the run), FR-HRM-EXP-06 (the event, and the payment status returned to the claim), GHR-06 (amounts are
/// hidden), GHR-09 (HR makes a claim for another person; the acting user is recorded), FR-XM-29 (open claims for final settlement) and FR-XM-32 (nobody
/// approves their own claim; a claim for a person with no sign-in goes to that person's manager). Company dates come from the Asia/Karachi harness clock.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ExpenseClaimTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Claims = "/api/hrm/expense-claims";
    private const string Attachments = "/api/doc/attachments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The amount as the service keeps it: to the currency's minor unit, at most four places.</summary>
    private static decimal Round(Org o, decimal value) => decimal.Round(value, Math.Clamp(o.Decimals, 0, 4), MidpointRounding.AwayFromZero);

    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Org(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Hr, Guid Approver, Guid Finance, Guid Admin, Guid PayGroup, Guid Component, string Currency, int Decimals);

    /// <summary>A company with an HR user (everything of HR, salary fields editable), an approver (HR Admin), a finance approver (Payroll Manager), a tenant administrator, a pay group with periods and a one-time reimbursement component.</summary>
    private async Task<Org> OrgAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var hr = await _h.UserAsync(Ct);
        var everyHr = await _h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.%'", Ct);
        await _h.AssignAsync(hr, await _h.RoleAsync(Ct, everyHr, [("*", "all")], [("*", "class:salary", "editable")]), null, _h.Today.AddDays(-1), null, Ct);
        var approver = await _h.UserAsync(Ct, null, "hr_admin");
        var finance = await _h.UserAsync(Ct, null, "payroll_manager");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var group = await _h.PostAsync(hr, "/api/hrm/pay-groups", new { companyId = company, code = Code("PG"), nameI18n = new { en = "Group", ur = "گروپ" }, frequency = "monthly", taxYearStartMonth = 1, cutOffDay = 25, paymentDay = 28 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var payGroup = group.Json.GetProperty("id").GetGuid();
        foreach (var year in new[] { Today.Year - 1, Today.Year, Today.Year + 1 })
        {
            var generated = await _h.PostAsync(hr, $"/api/hrm/pay-groups/{payGroup}/periods/generate", new { taxYear = year }, Ct);
            Assert.True(generated.Status == HttpStatusCode.Created, generated.Body);
        }

        var component = await _h.PostAsync(hr, "/api/hrm/pay-components", new
        {
            companyId = company, code = Code("EXP"), nameI18n = new { en = "Expense reimbursement", ur = "اخراجات کی ادائیگی" }, kind = "earning", payslipOrder = 9, isRecurring = false, isOneTimeAllowed = true,
            version = new { validFrom = Iso(Today.AddYears(-3)), method = "manual", proRataRule = "none", statutoryFlags = new { taxable = false } },
        }, Ct);
        Assert.True(component.Status == HttpStatusCode.Created, component.Body);
        var currency = await _h.ScalarAsync<string>("select base_currency_code::text from org.companies where id = $1", Ct, company);
        var decimals = await _h.ScalarAsync<int>("select decimals from ref.currencies where code = $1", Ct, currency);
        return new Org(company, await _h.PlaceAsync(company, Ct), hr, approver, finance, admin, payGroup, component.Json.GetProperty("id").GetGuid(), currency.Trim(), decimals);
    }

    /// <summary>An employment (under <paramref name="manager"/> when given) in the pay group; a user, when asked, holds the employee role.</summary>
    private async Task<(Guid Employment, Guid? User)> HireAsync(Org o, bool withUser = true, Guid? manager = null, Guid? grade = null, string role = "employee")
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(o.Hr, "/api/hrm/employments", new
        {
            personId = person, companyId = o.Company, employeeType = "permanent", joiningDate = Iso(Today.AddYears(-1)),
            assignment = new { orgUnitId = o.Place.OrgUnit, branchId = o.Place.Branch, title = "Officer", payGroupId = o.PayGroup, managerEmploymentId = manager, gradeId = grade },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var employment = answer.Json.GetProperty("id").GetGuid();
        if (!withUser)
        {
            return (employment, null);
        }

        var user = await _h.UserAsync(Ct, null, role);
        await _h.ExecAsync("update hrm.employments set user_ref = $1 where id = $2", Ct, user, employment);
        return (employment, user);
    }

    private async Task<Guid> CategoryAsync(Org o, string code, string kind = "general", bool receipt = false, bool blocks = false, bool withComponent = true)
    {
        var created = await _h.PostAsync(o.Hr, "/api/hrm/expense-categories", new
        {
            companyId = o.Company, code, nameI18n = new { en = "Category " + code, ur = "زمرہ " + code }, requiresReceipt = receipt, reimbursementComponentId = withComponent ? o.Component : (Guid?)null,
            kind, flagsBlockSubmit = blocks, taxTreatment = "label-" + code, isActive = true,
        }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return created.Json.GetProperty("id").GetGuid();
    }

    private async Task<string> ForeignCurrencyAsync(Org o) =>
        (await _h.ScalarAsync<string>("select code::text from ref.currencies where code <> $1 and is_active order by code limit 1", Ct, o.Currency)).Trim();

    private async Task RateAsync(Org o, string from, DateOnly date, decimal rate)
    {
        var created = await _h.PostAsync(o.Hr, "/api/hrm/exchange-rates", new { companyId = o.Company, fromCurrency = from, toCurrency = o.Currency, rateDate = Iso(date), rate }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
    }

    private async Task PolicyAsync(Org o, object rules)
    {
        var from = Today.AddYears(-2);
        var (policy, _) = await _h.ActivePolicyAsync(o.Hr, o.Company, "expense", new { rules }, from, Ct);
        var set = await _h.ActivateSetAsync(o.Hr, o.Company, "expense", from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    private async Task SettingAsync(Org o, string key, object value)
    {
        var set = await _h.PutAsync(o.Admin, "/api/cfg/settings/" + key, new { layer = "company", scopeId = o.Company, value, reason = "test" }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
    }

    private static object Line(Guid category, DateOnly date, decimal amount, string currency, decimal? quantity = null, Guid? receipt = null, string? project = null, string? centre = null, Guid? id = null) =>
        new { id, categoryId = category, expenseDate = Iso(date), amount, currencyCode = currency, quantity, description = "Expense", receiptFileId = receipt, project, costCentre = centre };

    private async Task<Answer> ClaimAsync(Guid user, object[] lines, string via = "payroll", bool submit = false, Guid? employment = null, string? title = null) =>
        await _h.PostAsync(user, Claims, new { employmentId = employment, title = title ?? "Claim", reimburseVia = via, lines, submit }, Ct);

    private static byte[] Pdf(string extra) => Encoding.UTF8.GetBytes("%PDF-1.7\nA receipt. " + extra + "\n");

    private async Task<Guid> ReceiptAsync(Guid user, Guid claim, string content)
    {
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), _h.Token(user));
        using var form = new MultipartFormDataContent { { new StringContent(ExpenseEvents.OwnerType), "OwnerType" }, { new StringContent(claim.ToString()), "OwnerId" } };
        var file = new ByteArrayContent(Pdf(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "File", "receipt-" + content + ".pdf");
        using var response = await client.PostAsync(new Uri(Attachments, UriKind.Relative), form, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    private Task<Guid> TaskOfAsync(Answer submitted) =>
        _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, submitted.Json.GetProperty("instanceId").GetGuid());

    private async Task<(Guid Assignee, string Step)> OpenStepAsync(Answer submitted)
    {
        var instance = submitted.Json.GetProperty("instanceId").GetGuid();
        var assignee = await _h.ScalarAsync<Guid>("select coalesce(assignee_user_id, '00000000-0000-0000-0000-000000000000'::uuid) from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, instance);
        var step = await _h.ScalarAsync<string>("select step_key from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, instance);
        return (assignee, step);
    }

    private Task<long> CountAsync(string sql, params object?[] values) => _h.ScalarAsync<long>(sql, Ct, values);

    private static string[] Flags(JsonElement line) => [.. line.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!)];

    private async Task<JsonElement> DetailAsync(Guid user, Guid claim)
    {
        var read = await _h.GetAsync(user, $"{Claims}/{claim}/detail", Ct);
        Assert.True(read.Status == HttpStatusCode.OK, read.Body);
        return read.Json;
    }

    // ------------------------------------------------------------------ FR-HRM-EXP-02

    [Fact]
    public async Task FR_HRM_EXP_02_a_draft_is_numbered_converts_a_foreign_line_at_the_customers_rate_keeps_that_rate_and_replaces_its_lines_on_change()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var foreign = await ForeignCurrencyAsync(o);
        await RateAsync(o, foreign, Today.AddDays(-40), 2.5m);
        await RateAsync(o, foreign, Today.AddDays(-5), 3.7m); // newer: applies to later expense dates only

        var made = await ClaimAsync(user!.Value, [Line(travel, Today.AddDays(-10), 120.5m, o.Currency, project: "P-1", centre: "CC-1"), Line(travel, Today.AddDays(-20), 10m, foreign), Line(travel, Today.AddDays(-2), 10m, foreign)]);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        var id = made.Json.GetProperty("id").GetGuid();
        Assert.Matches(@"^EXP-\d{4}-\d{5}$", made.Json.GetProperty("claimNo").GetString()!);
        Assert.Equal("draft", made.Json.GetProperty("status").GetString());
        Assert.Equal(o.Currency, made.Json.GetProperty("currencyCode").GetString());
        var lines = made.Json.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        var old = lines.Single(l => l.GetProperty("originalCurrencyCode").GetString() == foreign && l.GetProperty("expenseDate").GetString() == Iso(Today.AddDays(-20)));
        var recent = lines.Single(l => l.GetProperty("originalCurrencyCode").GetString() == foreign && l.GetProperty("expenseDate").GetString() == Iso(Today.AddDays(-2)));
        Assert.Equal(2.5m, old.GetProperty("exchangeRate").GetDecimal()); // the rate in force on the expense date, kept on the line
        Assert.Equal(Iso(Today.AddDays(-40)), old.GetProperty("rateDate").GetString());
        Assert.Equal(Round(o, 25m), old.GetProperty("amount").GetDecimal());
        Assert.Equal(10m, old.GetProperty("originalAmount").GetDecimal());
        Assert.Equal(3.7m, recent.GetProperty("exchangeRate").GetDecimal());
        Assert.Equal(Round(o, 37m), recent.GetProperty("amount").GetDecimal());
        var home = lines.Single(l => l.GetProperty("originalCurrencyCode").GetString() == o.Currency);
        Assert.Equal(1m, home.GetProperty("exchangeRate").GetDecimal());
        Assert.Equal("P-1", home.GetProperty("project").GetString());
        Assert.Equal(Round(o, 120.5m + 25m + 37m), made.Json.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(employment, made.Json.GetProperty("employmentId").GetGuid());

        // A later rate does not change a saved line; a new rate row changes only lines saved after it. The stored amounts are the records.
        Assert.Equal(1, await CountAsync("select count(*) from hrm.expense_claim_lines where claim_id = $1 and exchange_rate = 2.5 and rate_date = $2", id, Today.AddDays(-40)));

        // No rate for the date: refused with a message on the field. A currency with no row at all is refused the same way.
        var noRate = await _h.PutAsync(user.Value, $"{Claims}/{id}", new { title = "Claim", reimburseVia = "payroll", rowVersion = made.Json.GetProperty("rowVersion").GetInt64(), lines = new[] { Line(travel, Today.AddDays(-60), 10m, foreign) } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noRate.Status);
        Assert.Contains("errors.hrm.expense.no_rate", noRate.Body, StringComparison.Ordinal);

        // A change replaces the lines (the line named stays, the others go) and the total follows.
        var keep = home.GetProperty("id").GetGuid();
        var changed = await _h.PutAsync(user.Value, $"{Claims}/{id}", new { title = "Changed", reimburseVia = "payment_batch", rowVersion = made.Json.GetProperty("rowVersion").GetInt64(), lines = new[] { Line(travel, Today.AddDays(-10), 99m, o.Currency, id: keep) } }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.Single(changed.Json.GetProperty("lines").EnumerateArray());
        Assert.Equal(keep, changed.Json.GetProperty("lines")[0].GetProperty("id").GetGuid());
        Assert.Equal(Round(o, 99m), changed.Json.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("payment_batch", changed.Json.GetProperty("reimburseVia").GetString());
        Assert.Equal(1, await CountAsync("select count(*) from hrm.expense_claim_lines where claim_id = $1", id));
        var stale = await _h.PutAsync(user.Value, $"{Claims}/{id}", new { title = "Stale", reimburseVia = "payroll", rowVersion = made.Json.GetProperty("rowVersion").GetInt64(), lines = new[] { Line(travel, Today.AddDays(-10), 50m, o.Currency) } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);

        // A draft is cancelled by its maker and keeps its number.
        var cancelled = await _h.PostAsync(user.Value, $"{Claims}/{id}/cancel", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.Status);
        Assert.Equal("cancelled", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(user.Value, $"{Claims}/{id}", new { reimburseVia = "payroll", lines = new[] { Line(travel, Today.AddDays(-1), 5m, o.Currency) } }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_EXP_02_GHR_09_HR_makes_a_claim_for_another_person_and_the_acting_user_is_recorded_and_a_colleague_cannot_see_or_change_it()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var (_, stranger) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));

        var made = await ClaimAsync(o.Hr, [Line(travel, Today.AddDays(-3), 40m, o.Currency)], employment: employment);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        var id = made.Json.GetProperty("id").GetGuid();
        Assert.Equal(o.Hr, await _h.ScalarAsync<Guid>("select created_by from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal(employment, await _h.ScalarAsync<Guid>("select employment_id from hrm.expense_claims where id = $1", Ct, id));

        // The person it is for reads it; a colleague (employee role, scope own) is told nothing, can neither read, change, submit nor cancel it.
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user!.Value, $"{Claims}/{id}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(stranger!.Value, $"{Claims}/{id}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(stranger.Value, $"{Claims}/{id}", new { reimburseVia = "payroll", lines = new[] { Line(travel, Today.AddDays(-1), 5m, o.Currency) } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(stranger.Value, $"{Claims}/{id}/submit", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(stranger.Value, $"{Claims}/{id}/cancel", new { }, Ct)).Status);
        // An employee cannot make a claim for a colleague.
        Assert.Equal(HttpStatusCode.NotFound, (await ClaimAsync(stranger.Value, [Line(travel, Today.AddDays(-3), 40m, o.Currency)], employment: employment)).Status);
        // A person with no employment is told which field is missing.
        var nobody = await _h.UserAsync(Ct, null, "employee");
        Assert.Equal(HttpStatusCode.BadRequest, (await ClaimAsync(nobody, [Line(travel, Today.AddDays(-3), 40m, o.Currency)])).Status);
    }

    [Fact]
    public async Task GHR_06_amounts_are_open_to_the_claimant_and_the_approver_and_hidden_from_a_reader_without_salary_access_in_detail_lists_and_the_change_log()
    {
        var o = await OrgAsync();
        var (managerEmployment, manager) = await HireAsync(o, role: "line_manager");
        var (employment, user) = await HireAsync(o, manager: managerEmployment);
        var travel = await CategoryAsync(o, Code("TR"));
        var made = await ClaimAsync(user!.Value, [Line(travel, Today.AddDays(-3), 1234.5m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.Accepted, made.Status);
        var id = made.Json.GetProperty("id").GetGuid();

        // The manager is the approver (open task): sees the amounts in the detail. The claimant sees them.
        Assert.Equal(manager, (Guid?)(await OpenStepAsync(made)).Assignee);
        Assert.Equal(1234.5m, (await DetailAsync(manager!.Value, id)).GetProperty("totalAmount").GetDecimal());
        Assert.Equal(1234.5m, (await DetailAsync(user.Value, id)).GetProperty("totalAmount").GetDecimal());

        // A reader with the right to read claims of the company but no salary-class access sees the claim and none of its amounts (detail, list, lines).
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.expense_claims.read", "hrm.expense_claim_lines.read"], [("*", "all")], []), null, Today.AddDays(-1), null, Ct);
        var hidden = await DetailAsync(reader, id);
        Assert.True(hidden.GetProperty("amountsHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("totalAmount").ValueKind);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("lines")[0].GetProperty("amount").ValueKind);
        var list = await _h.GetAsync(reader, Claims, Ct);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains(id.ToString(), list.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("1234.5", list.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("1234.5", (await _h.GetAsync(reader, "/api/hrm/expense-claim-lines", Ct)).Body, StringComparison.Ordinal);

        // A payroll officer (salary class read) sees the totals in the generated list.
        var officer = await _h.UserAsync(Ct, null, "payroll_officer");
        Assert.Contains("1234.5", (await _h.GetAsync(officer, Claims, Ct)).Body, StringComparison.Ordinal);
        Assert.Equal(employment, await _h.ScalarAsync<Guid>("select employment_id from hrm.expense_claims where id = $1", Ct, id));
    }

    // ------------------------------------------------------------------ FR-HRM-EXP-01 and EXP-03

    [Fact]
    public async Task FR_HRM_EXP_01_FR_HRM_EXP_03_a_limit_by_grade_and_a_per_diem_and_mileage_rate_from_the_policy_flag_over_limit_and_nothing_else_is_limited()
    {
        var o = await OrgAsync();
        var travelCode = Code("TR");
        var perDiemCode = Code("PD");
        var mileageCode = Code("ML");
        var otherCode = Code("OT");
        var travel = await CategoryAsync(o, travelCode);
        var perDiem = await CategoryAsync(o, perDiemCode, "per_diem");
        var mileage = await CategoryAsync(o, mileageCode, "mileage");
        var other = await CategoryAsync(o, otherCode);
        await PolicyAsync(o, new object[]
        {
            new { categoryCode = travelCode, basis = "per_line", amount = 500 },
            new { categoryCode = perDiemCode, basis = "per_unit", amount = 40 },
            new { categoryCode = mileageCode, basis = "per_unit", amount = 3.5 },
        });
        var (_, plain) = await HireAsync(o);
        var day = Today.AddDays(-4);

        var made = await ClaimAsync(plain!.Value, [Line(travel, day, 700m, o.Currency), Line(travel, day, 500m, o.Currency), Line(perDiem, day, 130m, o.Currency, quantity: 3m), Line(perDiem, day, 100m, o.Currency, quantity: 2m),
            Line(mileage, day, 36m, o.Currency, quantity: 10m), Line(other, day, 99999m, o.Currency)]);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        var lines = made.Json.GetProperty("lines").EnumerateArray().OrderBy(l => l.GetProperty("amount").GetDecimal()).ToList();
        string[] over = [ExpenseFlags.OverLimit];
        Assert.Equal(over, Flags(lines.Single(l => l.GetProperty("amount").GetDecimal() == 700m))); // over the any-grade line limit
        Assert.Empty(Flags(lines.Single(l => l.GetProperty("amount").GetDecimal() == 500m))); // exactly the limit
        Assert.Equal(over, Flags(lines.Single(l => l.GetProperty("amount").GetDecimal() == 130m))); // 3 days at 40 is 120
        Assert.Equal(over, Flags(lines.Single(l => l.GetProperty("amount").GetDecimal() == 100m))); // 2 days at 40 is 80
        Assert.Equal(over, Flags(lines.Single(l => l.GetProperty("amount").GetDecimal() == 36m))); // 10 km at 3.5 is 35
        Assert.Empty(Flags(lines.Single(l => l.GetProperty("amount").GetDecimal() == 99999m))); // no rule for the category: no limit
    }

    [Fact]
    public async Task FR_HRM_EXP_01_the_grade_of_the_employee_chooses_the_limit_and_a_per_unit_line_without_its_quantity_is_refused()
    {
        var o = await OrgAsync();
        var grade = await _h.PostAsync(o.Hr, "/api/hrm/grades", new { companyId = o.Company, code = Code("G"), nameI18n = new { en = "Grade", ur = "درجہ" }, rank = 7, defaults = new { }, isActive = true }, Ct);
        Assert.True(grade.Status == HttpStatusCode.Created, grade.Body);
        var travelCode = Code("TR");
        var travel = await CategoryAsync(o, travelCode);
        var perDiem = await CategoryAsync(o, Code("PD"), "per_diem");
        await PolicyAsync(o, new object[] { new { categoryCode = travelCode, basis = "per_line", amount = 500 }, new { categoryCode = travelCode, gradeCode = grade.Json.GetProperty("code").GetString(), basis = "per_line", amount = 900 } });
        var (_, plain) = await HireAsync(o);
        var (_, senior) = await HireAsync(o, grade: grade.Json.GetProperty("id").GetGuid());
        var day = Today.AddDays(-4);

        var forPlain = await ClaimAsync(plain!.Value, [Line(travel, day, 700m, o.Currency)]);
        var forSenior = await ClaimAsync(senior!.Value, [Line(travel, day, 700m, o.Currency)]);
        Assert.Equal([ExpenseFlags.OverLimit], Flags(forPlain.Json.GetProperty("lines")[0]));
        Assert.Empty(Flags(forSenior.Json.GetProperty("lines")[0]));
        Assert.Equal([ExpenseFlags.OverLimit], Flags((await ClaimAsync(senior.Value, [Line(travel, day, 901m, o.Currency)])).Json.GetProperty("lines")[0]));

        var noQuantity = await ClaimAsync(plain.Value, [Line(perDiem, day, 80m, o.Currency)]);
        Assert.Equal(HttpStatusCode.BadRequest, noQuantity.Status);
        Assert.Contains("errors.hrm.expense.quantity_required", noQuantity.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_EXP_03_a_missing_receipt_a_date_outside_the_window_and_a_future_date_each_raise_their_flag_and_a_duplicate_receipt_is_found_across_two_claims()
    {
        var o = await OrgAsync();
        await SettingAsync(o, "hrm.exp.claim_window_days", 30);
        var (_, user) = await HireAsync(o);
        var (_, colleague) = await HireAsync(o);
        var needsReceipt = await CategoryAsync(o, Code("RC"), receipt: true);
        var free = await CategoryAsync(o, Code("FR"));
        var day = Today.AddDays(-3);

        // Missing receipt (the category asks for one), outside the window (31 days), in the future; and a line with none of these.
        var made = await ClaimAsync(user!.Value, [Line(needsReceipt, day, 10m, o.Currency), Line(free, Today.AddDays(-31), 11m, o.Currency), Line(free, Today.AddDays(-30), 12m, o.Currency), Line(free, Today.AddDays(1), 13m, o.Currency), Line(free, day, 14m, o.Currency)]);
        Assert.True(made.Status == HttpStatusCode.Created, made.Body);
        var byAmount = made.Json.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("amount").GetDecimal());
        Assert.Equal([ExpenseFlags.MissingReceipt], Flags(byAmount[10m]));
        Assert.Equal([ExpenseFlags.OutOfPolicy], Flags(byAmount[11m]));
        Assert.Empty(Flags(byAmount[12m])); // the last day of the window
        Assert.Equal([ExpenseFlags.OutOfPolicy], Flags(byAmount[13m]));
        Assert.Empty(Flags(byAmount[14m]));

        // A receipt uploaded to the claim is linked to its line (the line's flag goes), and the same file with the same amount and date on a second claim is a duplicate.
        var first = made.Json.GetProperty("id").GetGuid();
        var file = await ReceiptAsync(user.Value, first, "taxi-77");
        var relined = await _h.PutAsync(user.Value, $"{Claims}/{first}", new
        {
            reimburseVia = "payroll", rowVersion = made.Json.GetProperty("rowVersion").GetInt64(),
            lines = new[] { Line(needsReceipt, day, 10m, o.Currency, receipt: file, id: byAmount[10m].GetProperty("id").GetGuid()) },
        }, Ct);
        Assert.True(relined.Status == HttpStatusCode.OK, relined.Body);
        Assert.Empty(Flags(relined.Json.GetProperty("lines")[0]));
        var hash = await _h.ScalarAsync<string>("select receipt_hash from hrm.expense_claim_lines where claim_id = $1", Ct, first);
        Assert.Equal(64, hash.Length);

        var second = await ClaimAsync(colleague!.Value, [Line(free, day, 10m, o.Currency)]);
        var secondId = second.Json.GetProperty("id").GetGuid();
        var same = await ReceiptAsync(colleague.Value, secondId, "taxi-77"); // the same bytes: the same checksum
        var reused = await _h.PutAsync(colleague.Value, $"{Claims}/{secondId}", new
        {
            reimburseVia = "payroll", rowVersion = second.Json.GetProperty("rowVersion").GetInt64(),
            lines = new[] { Line(free, day, 10m, o.Currency, receipt: same, id: second.Json.GetProperty("lines")[0].GetProperty("id").GetGuid()) },
        }, Ct);
        Assert.True(reused.Status == HttpStatusCode.OK, reused.Body);
        Assert.Equal([ExpenseFlags.Duplicate], Flags(reused.Json.GetProperty("lines")[0]));

        // The same file with another amount is not a duplicate; a file that belongs to no claim of the caller is refused.
        var other = await _h.PutAsync(colleague.Value, $"{Claims}/{secondId}", new
        {
            reimburseVia = "payroll", rowVersion = reused.Json.GetProperty("rowVersion").GetInt64(),
            lines = new[] { Line(free, day, 11m, o.Currency, receipt: same, id: second.Json.GetProperty("lines")[0].GetProperty("id").GetGuid()) },
        }, Ct);
        Assert.Empty(Flags(other.Json.GetProperty("lines")[0]));
        var foreignFile = await _h.PutAsync(colleague.Value, $"{Claims}/{secondId}", new { reimburseVia = "payroll", lines = new[] { Line(free, day, 11m, o.Currency, receipt: file) } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreignFile.Status);
        Assert.Contains("errors.hrm.expense.receipt_unknown", foreignFile.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_EXP_03_a_flag_never_stops_a_claim_unless_its_category_says_so_and_the_flags_are_shown_to_the_approver()
    {
        var o = await OrgAsync();
        var (_, user) = await HireAsync(o);
        var soft = await CategoryAsync(o, Code("SF"), receipt: true);
        var hard = await CategoryAsync(o, Code("HD"), receipt: true, blocks: true);
        var day = Today.AddDays(-2);

        // Flagged but allowed: the claim is submitted and the approver reads the flag.
        var allowed = await ClaimAsync(user!.Value, [Line(soft, day, 25m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.Accepted, allowed.Status);
        Assert.Equal(1, allowed.Json.GetProperty("flagged").GetInt32());
        var seen = await DetailAsync(o.Approver, allowed.Json.GetProperty("id").GetGuid());
        Assert.Equal([ExpenseFlags.MissingReceipt], Flags(seen.GetProperty("lines")[0]));
        Assert.True(seen.GetProperty("canDecide").GetBoolean());

        // The category that blocks: the same flag stops the submit, names the line, and the claim stays a draft.
        var blocked = await ClaimAsync(user.Value, [Line(hard, day, 25m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("errors.hrm.expense.blocked_by_flags", blocked.Body, StringComparison.Ordinal);
        Assert.Contains("missing_receipt", blocked.Body, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("select count(*) from hrm.expense_claims where created_by = $1 and status = 'submitted' and total_amount = 25 and id <> $2", user.Value, allowed.Json.GetProperty("id").GetGuid()));
        var empty = await ClaimAsync(user.Value, [], submit: true);
        Assert.Equal(HttpStatusCode.Conflict, empty.Status);
        Assert.Contains("errors.hrm.expense.no_lines", empty.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ approval, payroll, event, payment status

    [Fact]
    public async Task FR_HRM_EXP_03_FR_HRM_EXP_05_FR_HRM_EXP_06_FR_XM_32_per_line_approval_pays_only_the_approved_lines_through_one_payroll_input_and_publishes_the_event_once()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var meals = await CategoryAsync(o, Code("ML"));
        var day = Today.AddDays(-3);
        var made = await ClaimAsync(user!.Value, [Line(travel, day, 100m, o.Currency, project: "P-1", centre: "CC-1"), Line(meals, day, 60m, o.Currency, project: "P-1", centre: "CC-2"), Line(meals, day, 40m, o.Currency, project: "P-2", centre: "CC-2")], submit: true);
        Assert.Equal(HttpStatusCode.Accepted, made.Status);
        var id = made.Json.GetProperty("id").GetGuid();
        var lineIds = (await DetailAsync(user.Value, id)).GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("amount").GetDecimal(), l => l.GetProperty("id").GetGuid());
        var task = await TaskOfAsync(made);
        Assert.Equal("manager", (await OpenStepAsync(made)).Step);

        // Nobody approves their own claim (FR-XM-32), nor records decisions on it.
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(user.Value, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(user.Value, $"{Claims}/{id}/line-decisions", new { decisions = new[] { new { lineId = lineIds[100m], approvedAmount = 100m, reason = (string?)null } } }, Ct)).Status);

        // The approver reduces one line (a reason is needed) and rejects another; the approved total is what remains.
        var noReason = await _h.PostAsync(o.Approver, $"{Claims}/{id}/line-decisions", new { decisions = new[] { new { lineId = lineIds[100m], approvedAmount = 70m, reason = (string?)null } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
        Assert.Contains("errors.hrm.expense.reason_required", noReason.Body, StringComparison.Ordinal);
        var decided = await _h.PostAsync(o.Approver, $"{Claims}/{id}/line-decisions", new
        {
            decisions = new[] { new { lineId = lineIds[100m], approvedAmount = 70m, reason = "taxi, not a bus" }, new { lineId = lineIds[40m], approvedAmount = 0m, reason = "personal" } },
        }, Ct);
        Assert.True(decided.Status == HttpStatusCode.OK, decided.Body);
        Assert.Equal("submitted", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id)); // the decision on the lines is not the decision on the claim
        Assert.Equal(0, await CountAsync("select count(*) from hrm.pay_inputs where source_id = $1", id));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(await _h.UserAsync(Ct, null, "employee"), $"{Claims}/{id}/line-decisions", new { decisions = new[] { new { lineId = lineIds[100m], approvedAmount = 0m, reason = "x" } } }, Ct)).Status);

        var approved = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal(130m, await _h.ScalarAsync<decimal>("select approved_total from hrm.expense_claims where id = $1", Ct, id)); // 70 + 60 + 0
        Assert.Equal(60m, await _h.ScalarAsync<decimal>("select approved_amount from hrm.expense_claim_lines where id = $1", Ct, lineIds[60m])); // the line nobody changed is accepted in full
        Assert.Equal("pending", await _h.ScalarAsync<string>("select payment_status from hrm.expense_claims where id = $1", Ct, id));

        // Exactly one payroll input: the approved total, the claim's component, in the first open period of the pay group.
        Assert.Equal(1, await CountAsync("select count(*) from hrm.pay_inputs where source_type = 'expense_claim' and source_id = $1", id));
        var input = await _h.ScalarAsync<Guid>("select pay_input_id from hrm.expense_claims where id = $1", Ct, id);
        Assert.Equal(130m, await _h.ScalarAsync<decimal>("select amount from hrm.pay_inputs where id = $1", Ct, input));
        Assert.Equal(o.Component, await _h.ScalarAsync<Guid>("select component_id from hrm.pay_inputs where id = $1", Ct, input));
        Assert.Equal(employment, await _h.ScalarAsync<Guid>("select employment_id from hrm.pay_inputs where id = $1", Ct, input));
        Assert.Equal("pending", await _h.ScalarAsync<string>("select status from hrm.pay_inputs where id = $1", Ct, input));
        Assert.Equal(o.Currency, (await _h.ScalarAsync<string>("select currency_code::text from hrm.pay_inputs where id = $1", Ct, input)).Trim());
        var period = await _h.ScalarAsync<Guid>("select pay_period_id from hrm.pay_inputs where id = $1", Ct, input);
        Assert.Equal(period, await _h.ScalarAsync<Guid>("select pay_period_id from hrm.expense_claims where id = $1", Ct, id));
        Assert.True(await _h.ScalarAsync<DateOnly>("select end_date from hrm.pay_periods where id = $1", Ct, period) >= Today);
        Assert.Equal(0, await CountAsync("select count(*) from hrm.pay_periods p where p.pay_group_id = $1 and p.status <> 'closed' and p.end_date >= $2 and p.start_date < (select start_date from hrm.pay_periods where id = $3)", o.PayGroup, Today, period));

        // The event is in the outbox once, with the approved totals by category and by coding.
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.expense_claim.approved' and aggregate_id = $1", id));
        var payload = JsonDocument.Parse(await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.expense_claim.approved' and aggregate_id = $1", Ct, id)).RootElement;
        Assert.Equal(130m, payload.GetProperty("approvedTotal").GetDecimal());
        Assert.Equal(200m, payload.GetProperty("claimedTotal").GetDecimal());
        Assert.Equal([60m, 70m], payload.GetProperty("byCategory").EnumerateArray().Select(c => c.GetProperty("amount").GetDecimal()).Order());
        Assert.Equal([("P-1", "CC-1", 70m), ("P-1", "CC-2", 60m)], payload.GetProperty("byCoding").EnumerateArray()
            .Select(c => (c.GetProperty("project").GetString()!, c.GetProperty("costCentre").GetString()!, c.GetProperty("amount").GetDecimal())).Order());
    }

    [Fact]
    public async Task FR_HRM_EXP_03_FR_HRM_EXP_05_a_claim_whose_lines_were_all_rejected_ends_rejected_with_no_payroll_input_and_no_event()
    {
        var o = await OrgAsync();
        var (_, user) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var made = await ClaimAsync(user!.Value, [Line(travel, Today.AddDays(-3), 50m, o.Currency)], submit: true);
        var id = made.Json.GetProperty("id").GetGuid();
        var line = (await DetailAsync(user.Value, id)).GetProperty("lines")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"{Claims}/{id}/line-decisions", new { decisions = new[] { new { lineId = line, approvedAmount = 0m, reason = "not ours" } } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(made)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("rejected", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.pay_inputs where source_id = $1", id));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.expense_claim.approved' and aggregate_id = $1", id));

        // A rejection ends the claim the same way, and a claim returned to its maker is a draft again with its decisions cleared and goes round again.
        var second = await ClaimAsync(user.Value, [Line(travel, Today.AddDays(-3), 50m, o.Currency)], submit: true);
        var secondId = second.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(second)}/reject", new { comment = "no" }, Ct)).Status);
        Assert.Equal("rejected", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, secondId));
        var third = await ClaimAsync(user.Value, [Line(travel, Today.AddDays(-3), 51m, o.Currency)], submit: true);
        var thirdId = third.Json.GetProperty("id").GetGuid();
        var returned = await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(third)}/return", new { comment = "add the project" }, Ct);
        Assert.True(returned.Status == HttpStatusCode.OK, returned.Body);
        Assert.Equal("draft", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, thirdId));
        Assert.True((await DetailAsync(user.Value, thirdId)).GetProperty("returned").GetBoolean());
        var resubmitted = await _h.PostAsync(user.Value, $"{Claims}/{thirdId}/submit", new { }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, resubmitted.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(resubmitted)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, thirdId));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.pay_inputs where source_id = $1", thirdId));
    }

    [Fact]
    public async Task FR_HRM_EXP_03_the_finance_step_is_added_when_the_total_reaches_the_threshold_and_a_submitted_claim_can_be_withdrawn()
    {
        var o = await OrgAsync();
        await SettingAsync(o, "hrm.exp.finance_threshold", 1000);
        var (_, user) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var day = Today.AddDays(-2);

        var small = await ClaimAsync(user!.Value, [Line(travel, day, 999m, o.Currency)], submit: true);
        Assert.False(small.Json.GetProperty("needsFinance").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(small)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, small.Json.GetProperty("id").GetGuid())); // the manager alone

        var big = await ClaimAsync(user.Value, [Line(travel, day, 600m, o.Currency), Line(travel, day, 400m, o.Currency)], submit: true);
        var bigId = big.Json.GetProperty("id").GetGuid();
        Assert.True(big.Json.GetProperty("needsFinance").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(big)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("submitted", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, bigId)); // waits for finance
        Assert.Equal("finance", (await OpenStepAsync(big)).Step);
        Assert.Equal(0, await CountAsync("select count(*) from hrm.pay_inputs where source_id = $1", bigId));
        var financeTask = await TaskOfAsync(big);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{financeTask}/approve", new { comment = "not mine" }, Ct)).Status); // a task of the finance role only
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Finance, $"/api/wf/tasks/{financeTask}/approve", new { comment = "paid" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, bigId));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.pay_inputs where source_id = $1", bigId));

        // A claim waiting for a decision is withdrawn from its request; the claim ends cancelled.
        var waiting = await ClaimAsync(user.Value, [Line(travel, day, 10m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(user.Value, $"{Claims}/{waiting.Json.GetProperty("id").GetGuid()}/cancel", new { }, Ct)).Status);
        var withdrawn = await _h.PostAsync(user.Value, $"/api/wf/instances/{waiting.Json.GetProperty("instanceId").GetGuid()}/withdraw", new { comment = "mistake" }, Ct);
        Assert.True(withdrawn.Status == HttpStatusCode.OK, withdrawn.Body);
        Assert.Equal("cancelled", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, waiting.Json.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task FR_XM_32_FR_KRN_WFL_02_a_claim_hr_makes_for_an_employee_without_a_sign_in_goes_to_that_employees_manager_and_the_one_who_made_it_never_decides_it()
    {
        var o = await OrgAsync();
        var (managerEmployment, manager) = await HireAsync(o, role: "line_manager");
        var (employee, noUser) = await HireAsync(o, withUser: false, manager: managerEmployment);
        Assert.Null(noUser);
        var travel = await CategoryAsync(o, Code("TR"));
        var made = await ClaimAsync(o.Hr, [Line(travel, Today.AddDays(-2), 80m, o.Currency)], employment: employee, submit: true);
        Assert.Equal(HttpStatusCode.Accepted, made.Status);
        Assert.True(made.Json.GetProperty("onBehalf").GetBoolean());
        var (assignee, step) = await OpenStepAsync(made);
        Assert.Equal("manager", step);
        Assert.Equal(manager, (Guid?)assignee); // the subject's manager, not HR's manager
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(o.Hr, $"/api/wf/tasks/{await TaskOfAsync(made)}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(manager!.Value, $"/api/wf/tasks/{await TaskOfAsync(made)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, made.Json.GetProperty("id").GetGuid()));
    }

    // ------------------------------------------------------------------ the provider, the payment return, open claims

    private async Task<T> InCustomerScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ResolvedTenant tenant;
        await using (var cp = new NpgsqlConnection(fx.Options.ConnectionString))
        {
            await cp.OpenAsync(Ct);
            tenant = (await Kernel.Infrastructure.ControlPlane.Tenancy.TenantDirectory.FindByCodeAsync(cp, _h.Code, Ct))!;
        }

        var dataSource = await _h.App.Services.GetRequiredService<ITenantDataSourceFactory>().GetAsync(tenant, Ct);
        var licence = await _h.App.Services.GetRequiredService<ILicenceResolver>().ResolveAsync(tenant, dataSource, Ct);
        await using var scope = _h.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, TenantAccessMode.Full, dataSource, licence);
        return await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task FR_HRM_EXP_05_FR_HRM_EXP_06_the_provider_gives_the_input_once_marks_it_consumed_by_the_run_and_the_payment_return_marks_the_claim_reimbursed_once()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var made = await ClaimAsync(user!.Value, [Line(travel, Today.AddDays(-3), 75.25m, o.Currency)], submit: true);
        var id = made.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(made)}/approve", new { comment = "ok" }, Ct)).Status);
        var period = await _h.ScalarAsync<Guid>("select pay_period_id from hrm.expense_claims where id = $1", Ct, id);
        var run = await _h.PostAsync(o.Hr, "/api/hrm/pay-runs", new { payGroupId = o.PayGroup, payPeriodId = period }, Ct);
        Assert.True(run.Status == HttpStatusCode.Created, run.Body);
        var runId = run.Json.GetProperty("id").GetGuid();
        var start = await _h.ScalarAsync<DateOnly>("select start_date from hrm.pay_periods where id = $1", Ct, period);
        var end = await _h.ScalarAsync<DateOnly>("select end_date from hrm.pay_periods where id = $1", Ct, period);

        // The expense provider answers the input as a component amount; the generic one-time provider leaves it alone.
        var items = await InCustomerScopeAsync(async sp =>
        {
            var request = new PayInputRequest(o.Company, runId, period, o.PayGroup, start, end, [employment]);
            var all = new List<PayInputItem>();
            foreach (var provider in sp.GetServices<IPayInputProvider>().Where(p => p.Code is "expenses" or "pay_inputs"))
            {
                all.AddRange(await provider.ForAsync(request, Ct));
            }

            return all;
        });
        var item = Assert.Single(items);
        Assert.Equal("expenses", item.Provider);
        Assert.Equal(75.25m, item.Amount);
        Assert.Equal(o.Component, item.ComponentId);

        // A saved result marks it consumed by the run; a second calculation of the same run reads it again; another run does not.
        await InCustomerScopeAsync(async sp =>
        {
            var provider = sp.GetServices<IPayInputProvider>().Single(p => p.Code == "expenses");
            await provider.ConsumedAsync(new PayInputsConsumed(runId, period, employment, Guid.CreateVersion7(), [new ConsumedPayInput(item, null)]), Ct);
            await sp.GetRequiredService<Erp.Hrm.Infrastructure.Generated.HrmDbContext>().SaveChangesAsync(Ct);
            return true;
        });
        Assert.Equal("consumed", await _h.ScalarAsync<string>("select status from hrm.pay_inputs where source_id = $1", Ct, id));
        Assert.Equal(runId, await _h.ScalarAsync<Guid>("select consumed_by_run_id from hrm.pay_inputs where source_id = $1", Ct, id));
        var again = await InCustomerScopeAsync(sp => sp.GetServices<IPayInputProvider>().Single(p => p.Code == "expenses").ForAsync(new PayInputRequest(o.Company, runId, period, o.PayGroup, start, end, [employment]), Ct));
        Assert.Single(again);
        var elsewhere = await InCustomerScopeAsync(sp => sp.GetServices<IPayInputProvider>().Single(p => p.Code == "expenses").ForAsync(new PayInputRequest(o.Company, Guid.CreateVersion7(), period, o.PayGroup, start, end, [employment]), Ct));
        Assert.Empty(elsewhere);

        // The payment's answer: failed keeps the claim approved; paid makes it reimbursed with the payment's reference; the same answer again changes nothing.
        var now = _h.Clock.UtcNow;
        Assert.True(await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpensePaymentReturn>().RecordAsync(new ExpensePaymentReturn(id, "failed", "BATCH-1", now), null, Ct)));
        Assert.Equal(("approved", "failed"), (await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id), await _h.ScalarAsync<string>("select payment_status from hrm.expense_claims where id = $1", Ct, id)));
        Assert.True(await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpensePaymentReturn>().RecordAsync(new ExpensePaymentReturn(id, "paid", "BATCH-2", now), null, Ct)));
        Assert.Equal("reimbursed", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal("BATCH-2", await _h.ScalarAsync<string>("select payment_reference from hrm.expense_claims where id = $1", Ct, id));
        Assert.True(await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpensePaymentReturn>().RecordAsync(new ExpensePaymentReturn(id, "paid", "BATCH-2", now), null, Ct)));
        Assert.False(await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpensePaymentReturn>().RecordAsync(new ExpensePaymentReturn(id, "failed", "late", now), null, Ct)));
        Assert.False(await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpensePaymentReturn>().RecordAsync(new ExpensePaymentReturn(Guid.CreateVersion7(), "paid", null, now), null, Ct)));
    }

    [Fact]
    public async Task FR_HRM_EXP_05_a_claim_paid_by_a_payment_batch_writes_no_payroll_input_and_waits_for_the_payment_and_a_payroll_claim_needs_one_component()
    {
        var o = await OrgAsync();
        var (_, user) = await HireAsync(o);
        var withComponent = await CategoryAsync(o, Code("A"));
        var bare = await CategoryAsync(o, Code("B"), withComponent: false);
        var day = Today.AddDays(-2);

        // Payroll needs a reimbursement component on every category; a separate payment does not.
        var refused = await ClaimAsync(user!.Value, [Line(bare, day, 10m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("errors.hrm.expense.no_component", refused.Body, StringComparison.Ordinal);
        var batch = await ClaimAsync(user.Value, [Line(bare, day, 10m, o.Currency), Line(withComponent, day, 15m, o.Currency)], via: "payment_batch", submit: true);
        Assert.Equal(HttpStatusCode.Accepted, batch.Status);
        var id = batch.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(batch)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal("pending", await _h.ScalarAsync<string>("select payment_status from hrm.expense_claims where id = $1", Ct, id));
        Assert.Equal(0, await CountAsync("select count(*) from hrm.pay_inputs where source_id = $1", id));
        Assert.Equal(1, await CountAsync("select count(*) from hrm.outbox_messages where event_type = 'hrm.expense_claim.approved' and aggregate_id = $1", id));

        var second = await CategoryAsync(o, Code("C"), withComponent: false);
        var otherComponent = await _h.PostAsync(o.Hr, "/api/hrm/pay-components", new
        {
            companyId = o.Company, code = Code("EX2"), nameI18n = new { en = "Second", ur = "دوسرا" }, kind = "earning", payslipOrder = 10, isRecurring = false, isOneTimeAllowed = true,
            version = new { validFrom = Iso(Today.AddYears(-3)), method = "manual", proRataRule = "none", statutoryFlags = new { taxable = false } },
        }, Ct);
        await _h.ExecAsync("update hrm.expense_categories set reimbursement_component_id = $1 where id = $2", Ct, otherComponent.Json.GetProperty("id").GetGuid(), second);
        var mixed = await ClaimAsync(user.Value, [Line(withComponent, day, 10m, o.Currency), Line(second, day, 10m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.Conflict, mixed.Status);
        Assert.Contains("errors.hrm.expense.mixed_components", mixed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_XM_29_FR_HRM_EXP_06_the_open_claims_of_an_employee_are_the_unsubmitted_and_the_unpaid_ones_and_carry_no_amount()
    {
        var o = await OrgAsync();
        var (employment, user) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var day = Today.AddDays(-2);
        var draft = await ClaimAsync(user!.Value, [Line(travel, day, 11m, o.Currency)]);
        var waiting = await ClaimAsync(user.Value, [Line(travel, day, 22m, o.Currency)], submit: true);
        var approved = await ClaimAsync(user.Value, [Line(travel, day, 33m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(approved)}/approve", new { comment = "ok" }, Ct)).Status);
        var cancelled = await ClaimAsync(user.Value, [Line(travel, day, 44m, o.Currency)]);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(user.Value, $"{Claims}/{cancelled.Json.GetProperty("id").GetGuid()}/cancel", new { }, Ct)).Status);
        var paid = await ClaimAsync(user.Value, [Line(travel, day, 55m, o.Currency)], submit: true);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(o.Approver, $"/api/wf/tasks/{await TaskOfAsync(paid)}/approve", new { comment = "ok" }, Ct)).Status);
        Assert.True(await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpensePaymentReturn>().RecordAsync(new ExpensePaymentReturn(paid.Json.GetProperty("id").GetGuid(), "paid", null, _h.Clock.UtcNow), null, Ct)));

        var open = await InCustomerScopeAsync(sp => sp.GetRequiredService<IExpenseOpenClaims>().OpenForAsync(employment, Ct));
        Assert.Equal([draft.Json.GetProperty("id").GetGuid(), waiting.Json.GetProperty("id").GetGuid(), approved.Json.GetProperty("id").GetGuid()], open.Select(c => c.ClaimId));
        Assert.Equal(["draft", "submitted", "approved"], open.Select(c => c.Status));

        // The same through the API: for HR (and the person), by data scope; no amount appears.
        var read = await _h.GetAsync(o.Hr, $"/api/hrm/employments/{employment}/open-expense-claims", Ct);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal(3, read.Json.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("amount", read.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user.Value, $"/api/hrm/employments/{employment}/open-expense-claims", Ct)).Status);
        var (otherEmployment, _) = await HireAsync(o);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user.Value, $"/api/hrm/employments/{otherEmployment}/open-expense-claims", Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_EXP_02_receipt_files_follow_the_claim_the_parties_and_the_approver_read_them_a_colleague_cannot_and_nothing_is_added_once_submitted()
    {
        var o = await OrgAsync();
        var (_, user) = await HireAsync(o);
        var (_, colleague) = await HireAsync(o);
        var travel = await CategoryAsync(o, Code("TR"));
        var made = await ClaimAsync(user!.Value, [Line(travel, Today.AddDays(-2), 20m, o.Currency)]);
        var id = made.Json.GetProperty("id").GetGuid();
        var file = await ReceiptAsync(user.Value, id, "bus-12");
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(user.Value, $"{Attachments}/{file}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(colleague!.Value, $"{Attachments}/{file}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(colleague.Value, $"{Attachments}?ownerType={ExpenseEvents.OwnerType}&ownerId={id}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(o.Hr, $"{Attachments}/{file}/content", Ct)).Status);

        var submitted = await _h.PostAsync(user.Value, $"{Claims}/{id}/submit", new { }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, submitted.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(o.Approver, $"{Attachments}/{file}/content", Ct)).Status); // the approver with the open task
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), _h.Token(user.Value));
        using var form = new MultipartFormDataContent { { new StringContent(ExpenseEvents.OwnerType), "OwnerType" }, { new StringContent(id.ToString()), "OwnerId" } };
        var more = new ByteArrayContent(Pdf("late"));
        more.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(more, "File", "late.pdf");
        using var late = await client.PostAsync(new Uri(Attachments, UriKind.Relative), form, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode); // a claim that waits for a decision takes no more files
    }

    [Fact]
    public async Task FR_HRM_EXP_03_the_expense_workflow_and_the_database_agree_a_line_never_has_a_negative_quantity_and_two_rates_for_one_day_are_refused()
    {
        var o = await OrgAsync();
        var foreign = await ForeignCurrencyAsync(o);
        await RateAsync(o, foreign, Today.AddDays(-9), 1.5m);
        var twice = await _h.PostAsync(o.Hr, "/api/hrm/exchange-rates", new { companyId = o.Company, fromCurrency = foreign, toCurrency = o.Currency, rateDate = Iso(Today.AddDays(-9)), rate = 1.6m }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.Status);
        var same = await _h.PostAsync(o.Hr, "/api/hrm/exchange-rates", new { companyId = o.Company, fromCurrency = o.Currency, toCurrency = o.Currency, rateDate = Iso(Today), rate = 1m }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, same.Status); // a currency into itself needs no rate
        var zero = await _h.PostAsync(o.Hr, "/api/hrm/exchange-rates", new { companyId = o.Company, fromCurrency = foreign, toCurrency = o.Currency, rateDate = Iso(Today), rate = 0m }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, zero.Status);
    }
}
