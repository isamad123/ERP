using System.Net;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Contracts.Payroll;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Loans;

/// <summary>
/// Loans and advances of slice S1.41 through the real API pipeline, the real access engine, the real workflow and a real PostgreSQL: FR-HRM-LON-01 (the type's policy decides who may borrow
/// how much), LON-02 (an application is checked, shown with its schedule, and approved by the line manager, a guarantor and finance; nobody approves their own, FR-XM-32; an application
/// entered for an employee without a sign-in goes to that employee's manager, R-305), LON-03 (a schedule that sums to the principal exactly; each payroll run recovers the right
/// installment; an employee with no pay that month carries it forward), LON-05 (the outstanding loans of an employee), LON-07 (disbursement and recovery in the ledger and in the outbox;
/// the reconciliation), GHR-06 (amounts only with the salary field class) and GHR-09 (HR applying for an employee is recorded). The company's date is the harness clock's in Asia/Karachi.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class LoanTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Types = "/api/hrm/loan-types";
    private const string Loans = "/api/hrm/loans";
    private const string Runs = "/api/hrm/pay-runs";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly string[] SetUp =
    [
        "hrm.pay_components.read", "hrm.pay_components.create", "hrm.pay_components.update", "hrm.pay_component_versions.read",
        "hrm.salary_structures.read", "hrm.salary_structures.create", "hrm.salary_structures.update", "hrm.salary_structure_components.read", "hrm.salary_structure_components.update",
        "hrm.compensation_assignments.read", "hrm.compensation_assignments.create", "hrm.compensation_lines.read", "hrm.compensation_lines.create", "hrm.pay.compensation.read",
        "hrm.pay_groups.read", "hrm.pay_groups.create", "hrm.pay_periods.read", "hrm.pay_periods.generate", "hrm.employments.read", "hrm.employment_assignments.read",
        "hrm.pay.run", "hrm.pay.results.read", "hrm.pay_inputs.create", "hrm.pay_inputs.cancel", "hrm.pay_runs.read", "hrm.pay_run_employees.read", "hrm.pay_results.read",
        "hrm.pay_result_lines.read", "hrm.pay_inputs.read",
    ];

    private static readonly string[] LoanDesk =
    [
        "hrm.loan_types.read", "hrm.loans.read", "hrm.loan_installments.read", "hrm.loan_transactions.read", "hrm.loans.disburse", "hrm.loans.receipt", "hrm.loans.cancel",
        "hrm.loan_reports.read", "hrm.loans.create", "hrm.pay_readiness.read",
    ];

    /// <summary>A company with pay-change approval off; a payroll manager (set-up, engine, loan desk, salary editable), HR, a finance approver, a monthly pay group with periods around last month.</summary>
    private sealed record Rig(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Payroll, Guid Hr, Guid Finance, Guid Group, Period Period, Period Next, Guid Basic, Guid Deduction, Guid Structure);

    private static readonly (string Key, object Value)[] Switches = [("hrm.employment_events.approval_kinds", Array.Empty<string>()), ("hrm.pay.change_approval", false)];

    private static readonly string[] Grades = ["G1"];

    private sealed record Period(Guid Id, DateOnly Start, DateOnly End);

    private sealed record Staff(Guid Employment, Guid User, Guid Manager, Guid ManagerUser);

    private async Task<Rig> RigAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var a = await _h.UserAsync(Ct, null, "tenant_admin");
        var b = await _h.UserAsync(Ct, null, "tenant_admin");
        foreach (var (key, value) in Switches)
        {
            var held = await _h.PutAsync(a, "/api/cfg/settings/" + key, new { layer = "company", scopeId = company, value, reason = "test" }, Ct);
            Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(b, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        }

        var payroll = await _h.UserAsync(Ct);
        await _h.AssignAsync(payroll, await _h.RoleAsync(Ct, [.. SetUp, .. LoanDesk, "hrm.loan_types.create", "hrm.loan_types.update", "hrm.policies.manage"], [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var finance = await _h.UserAsync(Ct, null, "finance_approver");
        var group = await _h.PostAsync(payroll, "/api/hrm/pay-groups", new { companyId = company, code = Code("PG"), nameI18n = new { en = "Loan group", ur = "قرض گروپ" }, frequency = "monthly", taxYearStartMonth = 7 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var groupId = group.Json.GetProperty("id").GetGuid();
        // The harness gives its companies odd currencies: money has two decimals here, as for the rupee.
        await _h.ExecAsync("update ref.currencies set decimals = 2 where code = (select currency_code from hrm.pay_groups where id = $1)", Ct, groupId);
        var target = Today.AddMonths(-1);
        var taxYear = target.Month >= 7 ? target.Year : target.Year - 1;
        foreach (var year in new[] { taxYear, taxYear + 1 })
        {
            var generated = await _h.PostAsync(payroll, $"/api/hrm/pay-groups/{groupId}/periods/generate", new { taxYear = year }, Ct);
            Assert.True(generated.Status == HttpStatusCode.Created, generated.Body);
        }

        var period = await PeriodAsync(groupId, target);
        var next = await PeriodAsync(groupId, period.End.AddDays(1));
        var rig = new Rig(company, await _h.PlaceAsync(company, Ct), payroll, hr, finance, groupId, period, next, Guid.Empty, Guid.Empty, Guid.Empty);
        var from = period.Start.AddDays(-400);
        var basic = await ComponentAsync(rig, "earning", 1, from, "fixed", oneTime: false);
        var deduction = await ComponentAsync(rig, "deduction", 9, from, "manual", oneTime: true);
        var structure = await _h.PostAsync(payroll, "/api/hrm/salary-structures", new { companyId = company, code = Code("S"), nameI18n = new { en = "Loan structure", ur = "قرض ڈھانچہ" } }, Ct);
        Assert.True(structure.Status == HttpStatusCode.Created, structure.Body);
        var structureId = structure.Json.GetProperty("id").GetGuid();
        var set = await _h.PutAsync(payroll, $"/api/hrm/salary-structures/{structureId}", new
        {
            rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.salary_structures where id = $1", Ct, structureId),
            components = new { validFrom = Iso(period.Start.AddDays(-60)), items = new[] { new { componentId = basic, evaluationOrder = 1, defaultAmount = (decimal?)null, defaultPercent = (decimal?)null, isMandatory = false } } },
        }, Ct);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        return rig with { Basic = basic, Deduction = deduction, Structure = structureId };
    }

    private async Task<Period> PeriodAsync(Guid group, DateOnly day)
    {
        var id = await _h.ScalarAsync<Guid>("select id from hrm.pay_periods where pay_group_id = $1 and start_date <= $2 and end_date >= $2", Ct, group, day);
        return new Period(id, await _h.ScalarAsync<DateOnly>("select start_date from hrm.pay_periods where id = $1", Ct, id), await _h.ScalarAsync<DateOnly>("select end_date from hrm.pay_periods where id = $1", Ct, id));
    }

    private async Task<Guid> ComponentAsync(Rig rig, string kind, int order, DateOnly from, string method, bool oneTime)
    {
        var code = Code("C");
        var answer = await _h.PostAsync(rig.Payroll, "/api/hrm/pay-components", new
        {
            companyId = rig.Company, code, nameI18n = new { en = "Component " + code, ur = "جزو " + code }, kind, payslipOrder = order, isRecurring = !oneTime, isOneTimeAllowed = oneTime,
            version = new { validFrom = Iso(from), method, proRataRule = method == "fixed" ? "calendar_days" : "none", statutoryFlags = new { taxable = true } },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    /// <summary>An employee of the pay group with a sign-in (role employee), the given manager and, when asked, pay from <paramref name="payFrom"/>.</summary>
    private async Task<Staff> StaffAsync(Rig rig, DateOnly joining, decimal? pay = 60_000m, DateOnly? payFrom = null, bool signIn = true, Guid? managerEmployment = null, Guid? managerUser = null)
    {
        managerUser ??= await _h.UserAsync(Ct, null, "line_manager");
        managerEmployment ??= await HireAsync(rig, Today.AddYears(-3), managerUser, null, inGroup: false);
        var user = signIn ? await _h.UserAsync(Ct, null, "employee") : (Guid?)null;
        var employment = await HireAsync(rig, joining, user, managerEmployment);
        if (pay is { } amount)
        {
            var done = await _h.PostAsync(rig.Payroll, $"/api/hrm/employments/{employment}/compensation", new
            {
                structureId = rig.Structure, validFrom = Iso(payFrom ?? (joining > rig.Period.Start.AddDays(-40) ? joining : rig.Period.Start.AddDays(-40))), reasonCode = "initial_pay", lines = new object[] { new { componentId = rig.Basic, amount } },
            }, Ct);
            Assert.True(done.Status == HttpStatusCode.Created, done.Body);
        }

        return new Staff(employment, user ?? Guid.Empty, managerEmployment.Value, managerUser.Value);
    }

    private async Task<Guid> HireAsync(Rig rig, DateOnly joining, Guid? user, Guid? manager, bool inGroup = true)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(rig.Hr, "/api/hrm/employments", new
        {
            personId = person, companyId = rig.Company, employeeType = "permanent", joiningDate = Iso(joining),
            assignment = new { orgUnitId = rig.Place.OrgUnit, branchId = rig.Place.Branch, title = "Officer", payGroupId = inGroup ? rig.Group : (Guid?)null, managerEmploymentId = manager },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var id = answer.Json.GetProperty("id").GetGuid();
        if (user is { } u)
        {
            await _h.ExecAsync("update hrm.employments set user_ref = $2 where id = $1", Ct, id, u);
        }

        return id;
    }

    private static Dictionary<string, object?> LoanParameters(object? extra = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["maxAmountBasis"] = "salary_multiple", ["salaryMultiple"] = 3, ["interestMethod"] = "none", ["maxInstallments"] = 12,
        };
        foreach (var p in extra is null ? [] : JsonSerializer.SerializeToElement(extra, JsonSerializerOptions.Web).EnumerateObject())
        {
            parameters[p.Name] = p.Value;
        }

        return parameters;
    }

    /// <summary>A loan policy with these rules in force from long ago, and a loan type that uses it and deducts into the rig's component.</summary>
    private async Task<Guid> TypeAsync(Rig rig, object? parameters = null, string kind = "loan")
    {
        var (policy, _) = await _h.ActivePolicyAsync(rig.Hr, rig.Company, "loan", parameters ?? LoanParameters(), Today.AddYears(-5), Ct);
        var created = await _h.PostAsync(rig.Hr, Types, new { companyId = rig.Company, code = Code("LT"), nameI18n = new { en = "Loan type", ur = "قرض کی قسم" }, kind, policyId = policy, deductionComponentId = rig.Deduction }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return created.Json.GetProperty("id").GetGuid();
    }

    private static object Ask(Guid type, decimal principal, int installments, Guid? employment = null, Guid? start = null, Guid? guarantor = null, string? reason = "Family need") =>
        new { employmentId = employment, loanTypeId = type, principal, installments, startPeriodId = start, guarantorEmploymentId = guarantor, reason };

    private async Task ApproveTaskAsync(Guid approver, Guid loan)
    {
        var task = await _h.ScalarAsync<Guid>("""
            select t.id from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id
            where i.document_type = 'hrm.loan_application' and i.document_id = $1 and t.status = 'pending' order by t.created_at desc limit 1
            """, Ct, loan);
        var approved = await _h.PostAsync(approver, $"/api/wf/tasks/{task}/approve", new { comment = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private async Task<Guid> ApplyAndApproveAsync(Rig rig, Staff s, Guid type, decimal principal, int installments, Guid? start = null, Guid? guarantor = null, Guid? guarantorUser = null)
    {
        var asked = await _h.PostAsync(s.User, Loans, Ask(type, principal, installments, null, start ?? rig.Period.Id, guarantor), Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        await ApproveTaskAsync(s.ManagerUser, id);
        if (guarantorUser is { } g)
        {
            await ApproveTaskAsync(g, id);
        }

        await ApproveTaskAsync(rig.Finance, id);
        Assert.Equal("approved", await StatusAsync(id));
        return id;
    }

    private Task<string> StatusAsync(Guid loan) => _h.ScalarAsync<string>("select status from hrm.loans where id = $1", Ct, loan);

    private async Task DisburseAsync(Rig rig, Guid loan, string reference = "BANK-REF-1")
    {
        var done = await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/disburse", new { date = Iso(Today), reference }, Ct);
        Assert.True(done.Status == HttpStatusCode.OK, done.Body);
    }

    private async Task<Guid> RunAsync(Rig rig, Period period)
    {
        var created = await _h.PostAsync(rig.Payroll, Runs, new { payGroupId = rig.Group, payPeriodId = period.Id }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        return created.Json.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> CalculateAsync(Rig rig, Guid run, Period period)
    {
        await _h.ExecAsync("update hrm.pay_periods set cut_off_date = least(cut_off_date, $1) where id = $2", Ct, Today.AddDays(-1), period.Id);
        var answer = await _h.PostAsync(rig.Payroll, $"{Runs}/{run}/calculate", new { acceptOpenItems = true }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json;
    }

    private async Task<LoanPostingResult> LockAndPostAsync(Guid run)
    {
        await _h.ExecAsync("update hrm.pay_runs set status = 'locked' where id = $1", Ct, run);
        return await InCustomerScopeAsync(sp => sp.GetRequiredService<ILoanRecoveryPosting>().PostRunAsync(run, Ct));
    }

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

    // ------------------------------------------------------------------ types, policy and the application

    [Fact]
    public async Task FR_HRM_LON_01_02_the_type_policy_decides_who_may_borrow_how_much_and_the_applicant_is_told_why_not()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig, LoanParameters(new { minServiceMonths = 6, eligibleGrades = Grades, maxOpenLoans = 1, annualInterestPercent = 12, interestMethod = "reducing" }));
        var newcomer = await StaffAsync(rig, Today.AddMonths(-2));
        var senior = await StaffAsync(rig, Today.AddYears(-2));

        // Ineligible: too little service and a grade the policy does not name. Both reasons come back (FR-HRM-LON-01).
        var preview = await _h.PostAsync(newcomer.User, Loans + "/preview", Ask(type, 50_000m, 6, null, rig.Period.Id), Ct);
        Assert.True(preview.Status == HttpStatusCode.OK, preview.Body);
        Assert.False(preview.Json.GetProperty("eligible").GetBoolean());
        var keys = preview.Json.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        Assert.Contains("errors.hrm.loan.not_eligible.service", keys);
        Assert.Contains("errors.hrm.loan.not_eligible.grade", keys);
        var refused = await _h.PostAsync(newcomer.User, Loans, Ask(type, 50_000m, 6, null, rig.Period.Id), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.hrm.loan.not_eligible", refused.Json.GetProperty("title").GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.loans where employment_id = $1", Ct, newcomer.Employment));

        // The most that may be lent is three times pay, and the repayment at most twelve installments.
        var grade = await GradeAsync(rig, "G1");
        await _h.ExecAsync("update hrm.employment_assignments set grade_id = $2 where employment_id = $1", Ct, senior.Employment, grade);
        var tooMuch = await _h.PostAsync(senior.User, Loans + "/preview", Ask(type, 200_000m, 13, null, rig.Period.Id), Ct);
        var tooMuchKeys = tooMuch.Json.GetProperty("problems").EnumerateArray().ToList();
        Assert.Equal("errors.hrm.loan.amount_above_max", tooMuchKeys.Single(p => p.GetProperty("key").GetString() == "errors.hrm.loan.amount_above_max").GetProperty("key").GetString());
        Assert.Equal("180000", tooMuchKeys.Single(p => p.GetProperty("key").GetString() == "errors.hrm.loan.amount_above_max").GetProperty("value").GetString());
        Assert.Contains(tooMuchKeys, p => p.GetProperty("key").GetString() == "errors.hrm.loan.installments_above_max");

        // A 12-installment reducing-balance preview: the principals add up to the principal exactly, interest follows the balance, the last absorbs the remainder.
        var ok = await _h.PostAsync(senior.User, Loans + "/preview", Ask(type, 100_000m, 12, null, rig.Period.Id), Ct);
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body);
        Assert.True(ok.Json.GetProperty("eligible").GetBoolean(), ok.Body);
        var schedule = ok.Json.GetProperty("schedule").EnumerateArray().ToList();
        Assert.Equal(12, schedule.Count);
        Assert.Equal(100_000m, schedule.Sum(s => s.GetProperty("principalAmount").GetDecimal()));
        Assert.Equal(1_000m, schedule[0].GetProperty("interestAmount").GetDecimal()); // 1 percent a month of the whole balance
        Assert.All(schedule.Take(11), s => Assert.Equal(8_884.88m, s.GetProperty("total").GetDecimal()));
        Assert.Equal(rig.Period.Id, schedule[0].GetProperty("payPeriodId").GetGuid());
        Assert.Equal(12, schedule.Select(s => s.GetProperty("payPeriodId").GetGuid()).Distinct().Count());
    }

    private async Task<Guid> GradeAsync(Rig rig, string code)
    {
        var id = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into hrm.grades (id, company_id, code, name_i18n, rank, defaults, is_deleted, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, '{"en":"Grade"}'::jsonb, 1, '{}'::jsonb, false, now(), $4, now(), $4, 1)
            """, Ct, id, rig.Company, code, rig.Hr);
        return id;
    }

    [Fact]
    public async Task FR_HRM_LON_01_a_type_needs_a_loan_policy_and_a_deduction_component_and_is_retired_never_deleted()
    {
        var rig = await RigAsync();
        var (leave, _) = await _h.ActivePolicyAsync(rig.Hr, rig.Company, "probation", new { months = 3 }, Today.AddYears(-1), Ct);
        string Title(Answer a) => a.Json.TryGetProperty("title", out var t) ? t.GetString()! : a.Body;
        var wrongPolicy = await _h.PostAsync(rig.Hr, Types, new { companyId = rig.Company, code = Code("LT"), nameI18n = new { en = "x" }, kind = "loan", policyId = leave, deductionComponentId = rig.Deduction }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongPolicy.Status);
        Assert.Contains("errors.hrm.loan.type.policy_not_loan", wrongPolicy.Body, StringComparison.Ordinal);
        var (policy, _) = await _h.ActivePolicyAsync(rig.Hr, rig.Company, "loan", LoanParameters(), Today.AddYears(-1), Ct);
        var wrongComponent = await _h.PostAsync(rig.Hr, Types, new { companyId = rig.Company, code = Code("LT"), nameI18n = new { en = "x" }, kind = "loan", policyId = policy, deductionComponentId = rig.Basic }, Ct);
        Assert.Contains("errors.hrm.loan.type.component_not_deduction", wrongComponent.Body, StringComparison.Ordinal);
        var later = await _h.PostAsync(rig.Hr, Types, new { companyId = rig.Company, code = Code("LT"), nameI18n = new { en = "x" }, kind = "pf_loan", policyId = policy, deductionComponentId = rig.Deduction }, Ct);
        Assert.Contains("errors.hrm.loan.type.kind_later", later.Body, StringComparison.Ordinal);

        // The policy's own check: a fixed amount needs the amount, a rate needs an interest method, no rate without one.
        var created = await _h.PostAsync(rig.Hr, "/api/hrm/policies", new { companyId = rig.Company, policyType = "loan", code = Code("LP"), name = new { en = "Bad", ur = "برا" }, parameters = LoanParameters(new { maxAmountBasis = "fixed_amount", annualInterestPercent = 5 }) }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, created.Status);
        Assert.Contains("errors.hrm.loan.policy.amount_basis_missing", created.Body, StringComparison.Ordinal);
        Assert.Contains("errors.hrm.loan.policy.rate_not_allowed", created.Body, StringComparison.Ordinal);

        var typeId = await TypeAsync(rig);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.loan_types where id = $1", Ct, typeId);
        var off = await _h.PutAsync(rig.Hr, $"{Types}/{typeId}", new { rowVersion = version, isActive = false }, Ct);
        Assert.True(off.Status == HttpStatusCode.OK, off.Body);
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        var applied = await _h.PostAsync(staff.User, Loans + "/preview", Ask(typeId, 1_000m, 2, null, rig.Period.Id), Ct);
        Assert.Contains("errors.hrm.loan.type_off", applied.Body, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, Title(applied));
        Assert.NotEqual(HttpStatusCode.OK, (await _h.DeleteAsync(rig.Hr, $"{Types}/{typeId}", Ct)).Status); // no delete route: generated writes are closed
    }

    [Fact]
    public async Task FR_HRM_LON_02_FR_XM_32_the_manager_the_guarantor_and_finance_approve_in_turn_and_nobody_approves_their_own()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig, LoanParameters(new { requiresGuarantor = true }));
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        var friend = await StaffAsync(rig, Today.AddYears(-2));

        var withoutGuarantor = await _h.PostAsync(staff.User, Loans, Ask(type, 30_000m, 3, null, rig.Period.Id), Ct);
        Assert.Equal(HttpStatusCode.Conflict, withoutGuarantor.Status);
        Assert.Contains("errors.hrm.loan.guarantor_required", withoutGuarantor.Body, StringComparison.Ordinal);
        var self = await _h.PostAsync(staff.User, Loans, Ask(type, 30_000m, 3, null, rig.Period.Id, staff.Employment), Ct);
        Assert.Contains("errors.hrm.loan.guarantor_self", self.Body, StringComparison.Ordinal);

        var asked = await _h.PostAsync(staff.User, Loans, Ask(type, 30_000m, 3, null, rig.Period.Id, friend.Employment), Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        var id = asked.Json.GetProperty("id").GetGuid();
        Assert.Matches(@"^LN-\d{4}-\d{5}$", asked.Json.GetProperty("loanNo").GetString()!);
        Assert.Equal("applied", await StatusAsync(id));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_installments where loan_id = $1", Ct, id)); // the schedule is built on approval

        // The applicant cannot decide their own request; the first step is their line manager's.
        var task = await _h.ScalarAsync<Guid>("select t.id from wf.workflow_tasks t where t.instance_id = (select workflow_instance_id from hrm.loans where id = $1) and t.status = 'pending'", Ct, id);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(staff.User, $"/api/wf/tasks/{task}/approve", new { comment = "mine" }, Ct)).Status);
        Assert.Equal(staff.ManagerUser, await _h.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, task));
        await ApproveTaskAsync(staff.ManagerUser, id);
        Assert.Equal("applied", await StatusAsync(id));

        // The guarantor's consent is the second step: nobody else may give it, not even finance.
        var consent = await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = (select workflow_instance_id from hrm.loans where id = $1) and status = 'pending'", Ct, id);
        Assert.Equal(friend.User, await _h.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, consent));
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(rig.Finance, $"/api/wf/tasks/{consent}/approve", new { comment = "no" }, Ct)).Status);
        await ApproveTaskAsync(friend.User, id);
        Assert.Equal("applied", await StatusAsync(id));
        await ApproveTaskAsync(rig.Finance, id);

        Assert.Equal("approved", await StatusAsync(id));
        var rows = await _h.ScalarAsync<long>("select count(*) from hrm.loan_installments where loan_id = $1 and status = 'scheduled'", Ct, id);
        Assert.Equal(3L, rows);
        Assert.Equal(30_000m, await _h.ScalarAsync<decimal>("select sum(principal_amount) from hrm.loan_installments where loan_id = $1", Ct, id));
        Assert.Equal(0m, await _h.ScalarAsync<decimal>("select sum(interest_amount) from hrm.loan_installments where loan_id = $1", Ct, id));
    }

    [Fact]
    public async Task GHR_09_R_305_an_application_entered_for_an_employee_without_a_sign_in_goes_to_that_employees_manager_and_is_recorded()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        var staff = await StaffAsync(rig, Today.AddYears(-2), signIn: false);
        var other = await StaffAsync(rig, Today.AddYears(-2)); // another employee with another manager

        // HR enters it for the employee: recorded as on behalf, and the first task is the employee's own manager, never HR's or another manager.
        var asked = await _h.PostAsync(rig.Hr, Loans, Ask(type, 20_000m, 4, staff.Employment, rig.Period.Id), Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.True(asked.Json.GetProperty("onBehalf").GetBoolean());
        var id = asked.Json.GetProperty("id").GetGuid();
        var task = await _h.ScalarAsync<Guid>("select t.id from wf.workflow_tasks t where t.instance_id = (select workflow_instance_id from hrm.loans where id = $1) and t.status = 'pending'", Ct, id);
        Assert.Equal(staff.ManagerUser, await _h.ScalarAsync<Guid>("select assignee_user_id from wf.workflow_tasks where id = $1", Ct, task));
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(rig.Hr, $"/api/wf/tasks/{task}/approve", new { comment = "own" }, Ct)).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(other.ManagerUser, $"/api/wf/tasks/{task}/approve", new { comment = "wrong manager" }, Ct)).Status);
        Assert.Equal(rig.Hr, await _h.ScalarAsync<Guid>("select created_by from hrm.loans where id = $1", Ct, id));

        // An employee cannot apply for someone else (their data scope is their own record).
        Assert.NotEqual(HttpStatusCode.Accepted, (await _h.PostAsync(other.User, Loans, Ask(type, 5_000m, 2, staff.Employment, rig.Period.Id), Ct)).Status);
    }

    // ------------------------------------------------------------------ disbursement, recovery and the ledger

    [Fact]
    public async Task FR_HRM_LON_03_07_a_disbursement_opens_the_ledger_and_each_pay_run_recovers_the_right_installment_when_it_locks()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        var loan = await ApplyAndApproveAsync(rig, staff, type, 12_000m, 3);

        // Nothing is recovered, and nothing is owed, before the payout; the borrower cannot pay out their own loan.
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/disburse", new { date = Iso(Today) }, Ct)).Status);
        await DisburseAsync(rig, loan, "TT-2026-001");
        Assert.Equal("active", await StatusAsync(loan));
        Assert.Equal(12_000m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal("TT-2026-001", await _h.ScalarAsync<string>("select reference from hrm.loan_transactions where loan_id = $1 and kind = 'disbursement'", Ct, loan));
        var disbursed = await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.loan.disbursed' and aggregate_id = $1", Ct, loan);
        Assert.Contains("TT-2026-001", disbursed, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/disburse", new { date = Iso(Today), reference = "again" }, Ct)).Status);

        // The run of the first period: the deduction line is the installment, the installment holds the result line, the ledger is untouched until the lock.
        var run = await RunAsync(rig, rig.Period);
        var calculated = await CalculateAsync(rig, run, rig.Period);
        Assert.Equal(1, calculated.GetProperty("calculated").GetInt32());
        var results = await _h.GetAsync(rig.Payroll, $"{Runs}/{run}/results?employmentId={staff.Employment}", Ct);
        var line = results.Json.GetProperty("items")[0].GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("componentId").GetGuid() == rig.Deduction);
        Assert.Equal(4_000m, line.GetProperty("amount").GetDecimal());
        var first = await _h.ScalarAsync<Guid>("select pay_result_line_id from hrm.loan_installments where loan_id = $1 and seq = 1", Ct, loan);
        Assert.Equal(line.GetProperty("id").GetGuid(), first);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal("scheduled", await _h.ScalarAsync<string>("select status from hrm.loan_installments where loan_id = $1 and seq = 1", Ct, loan));

        // Posting needs a locked run, then writes the recovery once and publishes the event (FR-HRM-LON-07).
        await Assert.ThrowsAsync<InvalidOperationException>(() => InCustomerScopeAsync(sp => sp.GetRequiredService<ILoanRecoveryPosting>().PostRunAsync(run, Ct)));
        var posted = await LockAndPostAsync(run);
        Assert.Equal(new LoanPostingResult(1, 0, 0), posted);
        Assert.Equal(new LoanPostingResult(0, 0, 0), await InCustomerScopeAsync(sp => sp.GetRequiredService<ILoanRecoveryPosting>().PostRunAsync(run, Ct)));
        Assert.Equal(8_000m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal("deducted", await _h.ScalarAsync<string>("select status from hrm.loan_installments where loan_id = $1 and seq = 1", Ct, loan));
        var recovered = await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.loan.installment_recovered' and aggregate_id = $1", Ct, loan);
        Assert.Contains(run.ToString(), recovered, StringComparison.Ordinal);

        // The database refuses what the rules forbid: a recovered installment, the ledger and a recovery beyond the balance.
        async Task Refused(string sql, string constraint, params object?[] values)
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync(sql, Ct, values));
            Assert.Equal(constraint, e.ConstraintName ?? e.MessageText);
        }

        await Refused("update hrm.loan_installments set principal_amount = 1 where loan_id = $1 and seq = 1", "ck_loan_installments_frozen", loan);
        await Refused("delete from hrm.loan_installments where loan_id = $1 and seq = 1", "ck_loan_installments_frozen", loan);
        // The ledger is append-only: the kernel's refusal of any change or delete (invariant 8).
        var upd = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("update hrm.loan_transactions set amount = 1 where loan_id = $1", Ct, loan));
        Assert.Equal("ER001", upd.SqlState);
        var del = await Assert.ThrowsAsync<PostgresException>(() => _h.ExecAsync("delete from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal("ER001", del.SqlState);
        await Refused("""
            insert into hrm.loan_transactions (id, company_id, loan_id, kind, amount, txn_date, created_at, created_by)
            select $2, company_id, id, 'recovery', -8000.01, current_date, now(), created_by from hrm.loans where id = $1
            """, "ck_loan_transactions_balance", loan, Guid.CreateVersion7());
        await Refused("delete from hrm.loans where id = $1", "ck_loans_rules", loan);
        await Refused("update hrm.loans set principal = 1 where id = $1", "ck_loans_rules", loan);

        // The installment of a locked period that payroll holds cannot be changed either (FR-XM-26): the second installment has no line yet and may still be.
        await _h.ExecAsync("update hrm.loan_installments set principal_amount = principal_amount where loan_id = $1 and seq = 2", Ct, loan);
    }

    [Fact]
    public async Task FR_HRM_LON_03_an_employee_with_no_pay_that_month_carries_the_installment_forward_to_the_next_run()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        // Pay starts with the second period: the first run has no result for the employee (no compensation: incomplete, never zero).
        var staff = await StaffAsync(rig, Today.AddYears(-2), 60_000m, rig.Next.Start);
        var loan = await ApplyAndApproveAsync(rig, staff, type, 9_000m, 3);
        await DisburseAsync(rig, loan);

        var run1 = await RunAsync(rig, rig.Period);
        var first = await CalculateAsync(rig, run1, rig.Period);
        Assert.Single(first.GetProperty("incomplete").EnumerateArray());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_results where pay_run_id = $1", Ct, run1));
        Assert.Equal(new LoanPostingResult(0, 1, 0), await LockAndPostAsync(run1)) ;
        Assert.Equal("skipped", await _h.ScalarAsync<string>("select status from hrm.loan_installments where loan_id = $1 and seq = 1", Ct, loan));
        Assert.Equal(9_000m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan)); // nothing recovered

        // The next run takes the skipped installment and the one that falls due: 3,000 and 3,000.
        var run2 = await RunAsync(rig, rig.Next);
        var second = await CalculateAsync(rig, run2, rig.Next);
        Assert.Equal(1, second.GetProperty("calculated").GetInt32());
        var results = await _h.GetAsync(rig.Payroll, $"{Runs}/{run2}/results?employmentId={staff.Employment}", Ct);
        Assert.Equal(6_000m, results.Json.GetProperty("items")[0].GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("componentId").GetGuid() == rig.Deduction).GetProperty("amount").GetDecimal());
        Assert.Equal(new LoanPostingResult(2, 0, 0), await LockAndPostAsync(run2));
        Assert.Equal(3_000m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_installments where loan_id = $1 and status = 'deducted'", Ct, loan));
    }

    // ------------------------------------------------------------------ receipts, the contract, the reports and GHR-06

    [Fact]
    public async Task FR_HRM_LON_03_05_07_FR_XM_32_a_repayment_waits_for_a_second_person_settles_installments_from_the_end_and_the_ledger_reconciles()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        var loan = await ApplyAndApproveAsync(rig, staff, type, 12_000m, 4);
        await DisburseAsync(rig, loan);

        // The outstanding loan is in the contract the clearance and the final settlement use (FR-HRM-LON-05, FR-XM-29).
        var open = await InCustomerScopeAsync(sp => sp.GetRequiredService<IOutstandingLoans>().ForAsync([staff.Employment], Ct));
        var outstanding = Assert.Single(open);
        Assert.Equal(12_000m, outstanding.PrincipalOutstanding);
        Assert.Equal(12_000m, outstanding.RepayableScheduled);
        Assert.Equal(4, outstanding.InstallmentsLeft);
        Assert.Equal(rig.Period.Start, outstanding.NextDue);

        // Entered by payroll, approved by finance (a second person): 4,500 pays the last installment and half of the one before.
        var asked = await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/receipts", new { amount = 4_500m, date = Iso(Today), reference = "RCPT-1" }, Ct);
        Assert.True(asked.Status == HttpStatusCode.Accepted, asked.Body);
        Assert.Equal(12_000m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan)); // nothing booked while it waits
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/receipts", new { amount = 100m, date = Iso(Today), reference = "RCPT-2" }, Ct)).Status);
        var request = asked.Json.GetProperty("requestId").GetGuid();
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(rig.Payroll, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct)).Status);
        var approved = await _h.PostAsync(rig.Finance, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal(7_500m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Equal("RCPT-1", await _h.ScalarAsync<string>("select reference from hrm.loan_transactions where loan_id = $1 and kind = 'early_settlement'", Ct, loan));
        Assert.Equal("settled_early", await _h.ScalarAsync<string>("select status from hrm.loan_installments where loan_id = $1 and seq = 4", Ct, loan));
        Assert.Equal(1_500m, await _h.ScalarAsync<decimal>("select principal_amount from hrm.loan_installments where loan_id = $1 and seq = 3", Ct, loan));
        Assert.Equal(7_500m, await _h.ScalarAsync<decimal>("select sum(principal_amount) from hrm.loan_installments where loan_id = $1 and status = 'scheduled'", Ct, loan));

        // More than is owed is refused; a full settlement closes the loan.
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/receipts", new { amount = 7_500.01m, date = Iso(Today), reference = "RCPT-3" }, Ct)).Status);
        var full = await _h.PostAsync(rig.Payroll, $"{Loans}/{loan}/receipts", new { fullSettlement = true, date = Iso(Today), reference = "RCPT-4" }, Ct);
        Assert.True(full.Status == HttpStatusCode.Accepted, full.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Finance, $"/api/iam/maker-checker-requests/{full.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        Assert.Equal("settled", await StatusAsync(loan));
        Assert.Equal(0m, await _h.ScalarAsync<decimal>("select sum(amount) from hrm.loan_transactions where loan_id = $1", Ct, loan));
        Assert.Empty(await InCustomerScopeAsync(sp => sp.GetRequiredService<IOutstandingLoans>().ForAsync([staff.Employment], Ct)));

        // The reconciliation report: the ledger agrees with disbursed less recovered and with the schedule (D-43: a report, no Finance ledger).
        var report = await _h.GetAsync(rig.Payroll, $"/api/hrm/loan-reports/reconciliation?companyId={rig.Company}", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        var company = Assert.Single(report.Json.GetProperty("companies").EnumerateArray());
        Assert.True(company.GetProperty("reconciles").GetBoolean(), report.Body);
        Assert.Equal(12_000m, company.GetProperty("disbursed").GetDecimal());
        Assert.Equal(12_000m, company.GetProperty("recovered").GetDecimal());
        Assert.Equal(0m, company.GetProperty("outstanding").GetDecimal());
    }

    [Fact]
    public async Task GHR_06_amounts_are_hidden_without_the_salary_field_class_in_every_answer_and_the_loan_endpoints_check_permissions()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        var loan = await ApplyAndApproveAsync(rig, staff, type, 12_000m, 3);
        await DisburseAsync(rig, loan);

        // The finance approver reads salary-class data read-only: amounts show. HR (no salary class) sees the loan but no amount anywhere.
        var detail = await _h.GetAsync(rig.Finance, $"{Loans}/{loan}/detail", Ct);
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
        Assert.Equal(12_000m, detail.Json.GetProperty("principal").GetDecimal());
        Assert.Equal(12_000m, detail.Json.GetProperty("balance").GetDecimal());
        Assert.Equal(3, detail.Json.GetProperty("schedule").GetArrayLength());
        var noSalary = await _h.UserAsync(Ct, null, "hr_admin");
        var hidden = await _h.GetAsync(noSalary, $"{Loans}/{loan}/detail", Ct);
        Assert.True(hidden.Status == HttpStatusCode.OK, hidden.Body);
        Assert.False(hidden.Json.TryGetProperty("principal", out _), hidden.Body);
        Assert.False(hidden.Json.TryGetProperty("balance", out _), hidden.Body);
        Assert.All(hidden.Json.GetProperty("schedule").EnumerateArray(), s => Assert.False(s.TryGetProperty("principalAmount", out _)));
        Assert.All(hidden.Json.GetProperty("ledger").EnumerateArray(), s => Assert.False(s.TryGetProperty("amount", out _)));
        var list = await _h.GetAsync(noSalary, "/api/hrm/loans", Ct);
        Assert.DoesNotContain("12000", list.Body, StringComparison.Ordinal);
        var outstandingHidden = await _h.GetAsync(noSalary, "/api/hrm/loan-reports/outstanding", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, outstandingHidden.Status); // HR has no report permission

        // The employee sees their own loan and cannot see another's.
        var other = await StaffAsync(rig, Today.AddYears(-2));
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(staff.User, $"{Loans}/{loan}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(other.User, $"{Loans}/{loan}/detail", Ct)).Status);

        // Permissions: an employee cannot pay out, settle, cancel or read the reports.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(staff.User, $"{Loans}/{loan}/disburse", new { date = Iso(Today), reference = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(staff.User, $"{Loans}/{loan}/receipts", new { amount = 1m, date = Iso(Today), reference = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(staff.User, $"{Loans}/{loan}/cancel", new { }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(staff.User, "/api/hrm/loan-reports/reconciliation", Ct)).Status);

        // The outstanding report (payroll manager): the loan is there with its balance.
        var report = await _h.GetAsync(rig.Payroll, $"/api/hrm/loan-reports/outstanding?companyId={rig.Company}", Ct);
        Assert.True(report.Status == HttpStatusCode.OK, report.Body);
        var row = report.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("loanId").GetGuid() == loan);
        Assert.Equal(12_000m, row.GetProperty("balance").GetDecimal());
        Assert.Equal(3, row.GetProperty("installmentsLeft").GetInt32());
    }

    [Fact]
    public async Task FR_HRM_PAY_38_FR_HRM_LON_03_the_loans_item_of_the_readiness_list_fails_for_an_approved_loan_never_paid_out_and_passes_once_it_is()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        async Task<(string Status, int Count)> ItemAsync()
        {
            var answer = await _h.GetAsync(rig.Payroll, $"/api/hrm/pay-periods/{rig.Period.Id}/readiness", Ct);
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
            var item = answer.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "loans_updated");
            return (item.GetProperty("status").GetString()!, item.TryGetProperty("count", out var c) ? c.GetInt32() : 0);
        }

        Assert.Equal(("passed", 0), await ItemAsync());
        var loan = await ApplyAndApproveAsync(rig, staff, type, 6_000m, 2);
        Assert.Equal(("failed", 1), await ItemAsync()); // payroll would leave the installment out of the run
        await DisburseAsync(rig, loan);
        Assert.Equal(("passed", 0), await ItemAsync());
    }

    [Fact]
    public async Task FR_HRM_LON_02_an_approved_loan_that_was_not_paid_out_can_be_cancelled_and_a_waiting_application_is_rejected_by_finance()
    {
        var rig = await RigAsync();
        var type = await TypeAsync(rig);
        var staff = await StaffAsync(rig, Today.AddYears(-2));
        var approved = await ApplyAndApproveAsync(rig, staff, type, 6_000m, 2);
        var cancelled = await _h.PostAsync(rig.Payroll, $"{Loans}/{approved}/cancel", new { }, Ct);
        Assert.True(cancelled.Status == HttpStatusCode.OK, cancelled.Body);
        Assert.Equal("cancelled", await StatusAsync(approved));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_installments where loan_id = $1", Ct, approved));

        var asked = await _h.PostAsync(staff.User, Loans, Ask(type, 6_000m, 2, null, rig.Period.Id), Ct);
        var id = asked.Json.GetProperty("id").GetGuid();
        await ApproveTaskAsync(staff.ManagerUser, id);
        var task = await _h.ScalarAsync<Guid>("select t.id from wf.workflow_tasks t where t.instance_id = (select workflow_instance_id from hrm.loans where id = $1) and t.status = 'pending'", Ct, id);
        var rejected = await _h.PostAsync(rig.Finance, $"/api/wf/tasks/{task}/reject", new { comment = "not now" }, Ct);
        Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Body);
        Assert.Equal("rejected", await StatusAsync(id));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_installments where loan_id = $1", Ct, id));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.loan_transactions where loan_id = $1", Ct, id));
    }
}
