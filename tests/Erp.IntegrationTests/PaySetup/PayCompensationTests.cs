using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.PaySetup;

/// <summary>
/// Pay components with their versions, calculation methods, pro-rata rules, salary structures and compensation assignments and
/// revisions (slice S1.34b, Build items 4 to 8) through the real API pipeline, the real access engine and a real PostgreSQL:
/// FR-HRM-PAY-02 (kinds, flags, effective-dated versions), FR-HRM-PAY-03 (methods stored and name-checked, never evaluated),
/// FR-HRM-PAY-04 (structures with order of evaluation, effective-dated), FR-HRM-PAY-05 (pro-rata rule and parameters),
/// FR-HRM-PAY-06 (revisions: old and new structure, date, reason, backdated periods), FR-HRM-CMP-03, GHR-06 (amounts hidden
/// without the salary field class) and FR-XM-32 (maker-checker on structure and compensation changes).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PayCompensationTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Components = "/api/hrm/pay-components";
    private const string Structures = "/api/hrm/salary-structures";
    private const string Employments = "/api/hrm/employments";
    private static readonly string[] Astronaut = ["astronaut"];
    private static readonly string[] TwoTypes = ["permanent", "contract"];
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string FieldError(Answer a, string field) => a.Json.GetProperty("errors").GetProperty(field)[0].GetString()!;

    private static readonly string[] PayPermissions =
    [
        "hrm.pay_components.read", "hrm.pay_components.create", "hrm.pay_components.update", "hrm.pay_component_versions.read",
        "hrm.salary_structures.read", "hrm.salary_structures.create", "hrm.salary_structures.update",
        "hrm.salary_structure_components.read", "hrm.salary_structure_components.update",
        "hrm.compensation_assignments.read", "hrm.compensation_assignments.create", "hrm.compensation_lines.read", "hrm.compensation_lines.create",
        "hrm.pay.compensation.read", "hrm.pay_groups.read", "hrm.pay_groups.create", "hrm.pay_groups.update", "hrm.pay_periods.read", "hrm.pay_periods.generate",
        "hrm.employments.read", "hrm.employment_assignments.read", "aud.history.view",
    ];

    /// <summary>A company whose pay change approval is as asked, a payroll officer, a second person who may approve and an HR administrator.</summary>
    private async Task<Rig> RigAsync(bool approval)
    {
        var company = await _h.CompanyAsync(Ct);
        var tenantA = await _h.UserAsync(Ct, null, "tenant_admin");
        var tenantB = await _h.UserAsync(Ct, null, "tenant_admin");
        await SettingAsync(tenantA, tenantB, company, "hrm.employment_events.approval_kinds", Array.Empty<string>());
        if (!approval)
        {
            await SettingAsync(tenantA, tenantB, company, "hrm.pay.change_approval", false);
        }

        var officer = await _h.UserAsync(Ct);
        await _h.AssignAsync(officer, await _h.RoleAsync(Ct, PayPermissions, [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        var approver = await _h.UserAsync(Ct);
        await _h.AssignAsync(approver, await _h.RoleAsync(Ct, [.. PayPermissions, "hrm.pay.change.approve"], [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        return new Rig(company, await _h.PlaceAsync(company, Ct), officer, approver, hr);
    }

    private async Task SettingAsync(Guid admin, Guid second, Guid company, string key, object value)
    {
        var held = await _h.PutAsync(admin, "/api/cfg/settings/" + key, new { layer = "company", scopeId = company, value, reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
    }

    private sealed record Rig(Guid Company, (Guid OrgUnit, Guid Branch) Place, Guid Admin, Guid Second, Guid Hr);

    private async Task<(Guid Id, string Code, long Version)> ComponentAsync(Rig rig, string kind = "earning", int order = 10, object? version = null, string? code = null)
    {
        code ??= Code("C");
        var answer = await _h.PostAsync(rig.Admin, Components, new { companyId = rig.Company, code, nameI18n = new { en = "Component " + code, ur = "جزو " + code }, kind, payslipOrder = order, isRecurring = true, version }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), code, answer.Json.GetProperty("rowVersion").GetInt64());
    }

    private static object Fixed(DateOnly from, string flag = "taxable") => new { validFrom = Iso(from), method = "fixed", proRataRule = "calendar_days", statutoryFlags = new Dictionary<string, bool> { [flag] = true }, taxTreatment = "regular" };

    private async Task<Answer> VersionAsync(Guid user, Guid component, object body) => await _h.PostAsync(user, $"{Components}/{component}/versions", body, Ct);

    private async Task<Guid> StructureAsync(Rig rig, string? code = null)
    {
        code ??= Code("S");
        var answer = await _h.PostAsync(rig.Admin, Structures, new { companyId = rig.Company, code, nameI18n = new { en = "Structure " + code, ur = "ڈھانچہ " + code } }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private Task<long> StructureVersionAsync(Guid structure) => _h.ScalarAsync<long>("select row_version from hrm.salary_structures where id = $1", Ct, structure);

    private async Task<Answer> SetComponentsAsync(Guid user, Guid structure, DateOnly from, params (Guid Component, int Order, decimal? Amount, decimal? Percent, bool Mandatory)[] items) =>
        await _h.PutAsync(user, $"{Structures}/{structure}", new
        {
            rowVersion = await StructureVersionAsync(structure),
            components = new { validFrom = Iso(from), items = items.Select(i => new { componentId = i.Component, evaluationOrder = i.Order, defaultAmount = i.Amount, defaultPercent = i.Percent, isMandatory = i.Mandatory }).ToArray() },
        }, Ct);

    private async Task<Guid> HireAsync(Rig rig, int daysAgo = 400)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(rig.Hr, Employments, new
        {
            personId = person,
            companyId = rig.Company,
            employeeType = "permanent",
            joiningDate = Iso(Today.AddDays(-daysAgo)),
            assignment = new { orgUnitId = rig.Place.OrgUnit, branchId = rig.Place.Branch, title = "Officer" },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private async Task<(Guid Structure, Guid Basic, Guid House, Guid Tax)> BasicStructureAsync(Rig rig, DateOnly from)
    {
        var basic = await ComponentAsync(rig, order: 1, version: Fixed(from.AddYears(-1)));
        var house = await ComponentAsync(rig, order: 2, version: new { validFrom = Iso(from.AddYears(-1)), method = "percent_of", baseComponentIds = new[] { basic.Id }, proRataRule = "none", statutoryFlags = new { } });
        var tax = await ComponentAsync(rig, kind: "deduction", order: 90, version: new { validFrom = Iso(from.AddYears(-1)), method = "manual", proRataRule = "none", statutoryFlags = new { } });
        var structure = await StructureAsync(rig);
        var set = await SetComponentsAsync(rig.Admin, structure, from, (basic.Id, 1, null, null, true), (house.Id, 2, null, 45m, false), (tax.Id, 3, null, null, false));
        Assert.True(set.Status is HttpStatusCode.Accepted or HttpStatusCode.OK, set.Body);
        if (set.Status == HttpStatusCode.Accepted)
        {
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Second, $"/api/iam/maker-checker-requests/{set.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct)).Status);
        }

        return (structure, basic.Id, house.Id, tax.Id);
    }

    // ------------------------------------------------------------------ components and versions (FR-HRM-PAY-02, -03, -05)

    [Fact]
    public async Task FR_HRM_PAY_02_a_component_with_its_first_version_is_stored_audited_and_refuses_bad_codes_kinds_and_repeats()
    {
        var rig = await RigAsync(approval: false);
        var (id, code, _) = await ComponentAsync(rig, version: Fixed(Today.AddDays(-30)));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_component_versions where component_id = $1", Ct, id));
        Assert.Equal("tenant", await _h.ScalarAsync<string>("select source from hrm.pay_components where id = $1", Ct, id));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'hrm.pay_components' and entity_id = $1", Ct, id) >= 1);

        var detail = await _h.GetAsync(rig.Admin, $"{Components}/{id}/detail", Ct);
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Body);
        Assert.Equal(0, detail.Json.GetProperty("incomplete").GetArrayLength());
        Assert.True(detail.Json.GetProperty("versions")[0].GetProperty("statutoryFlags").GetProperty("taxable").GetBoolean());

        var again = await _h.PostAsync(rig.Admin, Components, new { companyId = rig.Company, code = code.ToLowerInvariant(), nameI18n = new { en = "x" }, kind = "earning", payslipOrder = 5 }, Ct);
        Assert.Equal("errors.hrm.pay.component_code_taken", FieldError(again, "code"));
        var bad = await _h.PostAsync(rig.Admin, Components, new { companyId = rig.Company, code = "1 bad-code", nameI18n = new { en = "x" }, kind = "bonus", payslipOrder = 5 }, Ct);
        Assert.Equal("errors.hrm.pay.component_code", FieldError(bad, "code"));
        Assert.Equal("errors.hrm.pay.component_kind", FieldError(bad, "kind"));
        var badFlags = await VersionAsync(rig.Admin, id, new { validFrom = Iso(Today), method = "fixed", proRataRule = "none", statutoryFlags = new Dictionary<string, bool> { ["Not A Code"] = true } });
        Assert.Equal("errors.hrm.pay.flag_code", FieldError(badFlags, "statutoryFlags"));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_component_versions where component_id = $1", Ct, id));

        // The code never changes (formulas name it) and neither does the kind.
        var rename = await _h.PutAsync(rig.Admin, $"{Components}/{id}", new { code = "OTHER", rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.pay_components where id = $1", Ct, id) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, rename.Status);
    }

    [Fact]
    public async Task FR_HRM_PAY_02_a_second_version_applies_from_its_date_and_the_old_version_stays()
    {
        var rig = await RigAsync(approval: false);
        var start = Today.AddDays(-90);
        var (id, _, _) = await ComponentAsync(rig, version: Fixed(start));
        var from = Today.AddDays(30);
        var second = await VersionAsync(rig.Admin, id, new { validFrom = Iso(from), method = "per_day", proRataRule = "working_days", statutoryFlags = new { taxable = false, pensionable = true } });
        Assert.Equal(HttpStatusCode.Created, second.Status);
        var rows = await _h.ScalarAsync<long>("select count(*) from hrm.pay_component_versions where component_id = $1", Ct, id);
        Assert.Equal(2L, rows);
        Assert.Equal(from, await _h.ScalarAsync<DateOnly>("select valid_to from hrm.pay_component_versions where component_id = $1 and valid_from = $2", Ct, id, start)); // the old row ends where the new one starts
        Assert.Equal("fixed", await _h.ScalarAsync<string>("select method from hrm.pay_component_versions where component_id = $1 and valid_from = $2", Ct, id, start)); // and is unchanged

        var then = await _h.GetAsync(rig.Admin, $"{Components}/{id}/detail?asOf={Iso(from)}", Ct);
        Assert.Equal("per_day", then.Json.GetProperty("versions").EnumerateArray().First(v => v.GetProperty("validTo").ValueKind == JsonValueKind.Null).GetProperty("method").GetString());
        var before = await _h.GetAsync(rig.Admin, $"{Components}/{id}/detail?asOf={Iso(from.AddDays(-1))}", Ct);
        Assert.Equal(1, before.Json.GetProperty("versions").EnumerateArray().Count(v => v.GetProperty("id").GetGuid() == before.Json.GetProperty("currentVersionId").GetGuid() && v.GetProperty("method").GetString() == "fixed"));

        // A version must start after the latest one.
        var late = await VersionAsync(rig.Admin, id, Fixed(start.AddDays(5)));
        Assert.Equal("errors.hrm.pay.version_not_later", FieldError(late, "validFrom"));
    }

    [Fact]
    public async Task FR_HRM_PAY_03_each_method_needs_what_it_needs_and_a_formula_naming_a_missing_component_or_a_cycle_is_refused()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-30);
        var (basic, basicCode, _) = await ComponentAsync(rig, version: Fixed(from));
        var (allowance, allowanceCode, _) = await ComponentAsync(rig);

        string Err(Answer a, string field) => FieldError(a, field);
        Assert.Equal("errors.hrm.pay.method_needs_formula", Err(await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "formula", proRataRule = "none", statutoryFlags = new { } }), "formula"));
        Assert.Equal("errors.hrm.pay.method_needs_bases", Err(await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "percent_of", proRataRule = "none", statutoryFlags = new { } }), "baseComponentIds"));
        Assert.Equal("errors.hrm.pay.method_needs_table", Err(await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "table_lookup", proRataRule = "none", statutoryFlags = new { } }), "statutoryTableCode"));
        Assert.Equal("errors.hrm.pay.method_no_extras", Err(await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "fixed", formula = "1", proRataRule = "none", statutoryFlags = new { } }), "formula"));
        Assert.Equal("errors.hrm.pay.method", Err(await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "magic", proRataRule = "none", statutoryFlags = new { } }), "method"));

        // A formula naming a component that does not exist is refused; the unknown name is in the answer, nothing is saved.
        var missing = await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "formula", formula = $"{basicCode} * 0.5 + NoSuchComponent", proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("errors.hrm.pay.formula_name_unknown", missing.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_component_versions where component_id = $1", Ct, allowance));
        var token = await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "formula", formula = $"{basicCode} ^ 2", proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.formula_token", Err(token, "formula"));
        var brackets = await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "formula", formula = $"min({basicCode}, 5", proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.formula_brackets", Err(brackets, "formula"));

        // A formula over existing components, named inputs and the three functions is stored as text and never evaluated.
        var formula = $"round(min({basicCode} * 0.5, $cap), 0)";
        var ok = await VersionAsync(rig.Admin, allowance, new { validFrom = Iso(from), method = "formula", formula, proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        Assert.Equal(formula, await _h.ScalarAsync<string>("select formula from hrm.pay_component_versions where component_id = $1", Ct, allowance));
        var check = await _h.PostAsync(rig.Admin, "/api/hrm/pay-formulas/check", new { companyId = rig.Company, formula = $"{allowanceCode} + $days" }, Ct);
        Assert.True(check.Json.GetProperty("valid").GetBoolean(), check.Body);

        // The basic component may not now be based on the allowance (a cycle) nor on itself; the database refuses the same on any other path.
        var cycle = await VersionAsync(rig.Admin, basic, new { validFrom = Iso(from.AddDays(10)), method = "percent_of", baseComponentIds = new[] { allowance }, proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.formula_cycle", FieldError(cycle, "baseComponentIds"));
        var self = await VersionAsync(rig.Admin, basic, new { validFrom = Iso(from.AddDays(10)), method = "percent_of", baseComponentIds = new[] { basic }, proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.base_self", FieldError(self, "baseComponentIds"));
        var viaFormula = await VersionAsync(rig.Admin, basic, new { validFrom = Iso(from.AddDays(10)), method = "formula", formula = allowanceCode + " + 1", proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.formula_cycle", FieldError(viaFormula, "formula"));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync(
            "insert into hrm.pay_component_versions (id, component_id, method, base_component_ids, pro_rata_rule, statutory_flags, valid_from, created_at, created_by, updated_at, updated_by) " +
            "values (gen_random_uuid(), $1, 'percent_of', array[$2::uuid], 'none', '{}', $3, now(), $4, now(), $4)", Ct, basic, basic, from.AddDays(10), Guid.Empty));
        Assert.Contains("based on itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_PAY_02_a_component_whose_statutory_table_is_missing_is_incomplete_and_no_statutory_value_is_stored()
    {
        var rig = await RigAsync(approval: false);
        var (id, _, _) = await ComponentAsync(rig, kind: "deduction", version: new { validFrom = Iso(Today.AddDays(-5)), method = "table_lookup", statutoryTableCode = "pk.test.table", proRataRule = "none", statutoryFlags = new { } });
        var detail = await _h.GetAsync(rig.Admin, $"{Components}/{id}/detail", Ct);
        Assert.Equal("statutory_table_missing:pk.test.table", detail.Json.GetProperty("incomplete")[0].GetString());
        var none = await ComponentAsync(rig);
        Assert.Equal("no_version", (await _h.GetAsync(rig.Admin, $"{Components}/{none.Id}/detail", Ct)).Json.GetProperty("incomplete")[0].GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.statutory_tables", Ct)); // no table, rate or slab is created here (AS-4)
    }

    [Fact]
    public async Task FR_HRM_PAY_05_the_pro_rata_rule_and_the_parameters_of_a_custom_rule_are_checked_and_stored()
    {
        var rig = await RigAsync(approval: false);
        var (id, _, _) = await ComponentAsync(rig);
        string Rule(Answer a) => FieldError(a, a.Json.GetProperty("errors").EnumerateObject().First().Name);
        var from = Today.AddDays(-20);
        Assert.Equal("errors.hrm.pay.pro_rata", Rule(await VersionAsync(rig.Admin, id, new { validFrom = Iso(from), method = "fixed", proRataRule = "weekly", statutoryFlags = new { } })));
        Assert.Equal("errors.hrm.pay.pro_rata_params", Rule(await VersionAsync(rig.Admin, id, new { validFrom = Iso(from), method = "fixed", proRataRule = "custom", statutoryFlags = new { } })));
        Assert.Equal("errors.hrm.pay.pro_rata_params", Rule(await VersionAsync(rig.Admin, id, new { validFrom = Iso(from), method = "fixed", proRataRule = "custom", proRataParams = new { divisor = 0 }, statutoryFlags = new { } })));
        Assert.Equal("errors.hrm.pay.pro_rata_params", Rule(await VersionAsync(rig.Admin, id, new { validFrom = Iso(from), method = "fixed", proRataRule = "fixed_30", proRataParams = new { divisor = 30 }, statutoryFlags = new { } })));
        Assert.Equal(HttpStatusCode.Created, (await VersionAsync(rig.Admin, id, new { validFrom = Iso(from), method = "fixed", proRataRule = "custom", proRataParams = new { divisor = 26, count = "working_days" }, statutoryFlags = new { } })).Status);
        Assert.Equal(26, await _h.ScalarAsync<int>("select (pro_rata_params->>'divisor')::int from hrm.pay_component_versions where component_id = $1", Ct, id));
        Assert.Equal("custom", await _h.ScalarAsync<string>("select pro_rata_rule from hrm.pay_component_versions where component_id = $1", Ct, id));
    }

    // ------------------------------------------------------------------ structures (FR-HRM-PAY-04)

    [Fact]
    public async Task FR_HRM_PAY_04_a_structure_of_ten_components_keeps_its_order_and_a_second_version_applies_from_its_date()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-60);
        var parts = new List<Guid>();
        for (var i = 1; i <= 10; i++)
        {
            parts.Add((await ComponentAsync(rig, order: i * 10, version: Fixed(from.AddYears(-1)))).Id);
        }

        var structure = await StructureAsync(rig);
        var first = await SetComponentsAsync(rig.Admin, structure, from, [.. parts.Select((p, i) => (p, 10 - i, (decimal?)(i + 1) * 1000m, (decimal?)null, i == 0))]);
        Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        var detail = await _h.GetAsync(rig.Admin, $"{Structures}/{structure}/detail", Ct);
        var order = detail.Json.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("evaluationOrder").GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(1, 10), order); // listed in the order of evaluation

        // A second version from a later date: unchanged components keep their row, a changed one is closed and re-opened, a left-out one ends.
        var later = Today.AddDays(30); // a change to an existing list cannot start in the past (review S1.34b M5)
        var items = parts.Take(9).Select((p, i) => (p, 10 - i, (decimal?)(i + 1) * 1000m, (decimal?)null, i == 0)).ToList();
        items[2] = (items[2].p, items[2].Item2, 9999m, null, false);
        var second = await SetComponentsAsync(rig.Admin, structure, later, [.. items]);
        Assert.True(second.Status == HttpStatusCode.OK, second.Body);
        Assert.Equal(parts.Count + 1L, await _h.ScalarAsync<long>("select count(*) from hrm.salary_structure_components where structure_id = $1", Ct, structure)); // 10 + the changed one
        Assert.Equal(later, await _h.ScalarAsync<DateOnly>("select valid_to from hrm.salary_structure_components where structure_id = $1 and component_id = $2", Ct, structure, parts[9])); // left out: ended
        var now = await _h.GetAsync(rig.Admin, $"{Structures}/{structure}/detail?asOf={Iso(later)}", Ct);
        Assert.Equal(9, now.Json.GetProperty("components").GetArrayLength());
        Assert.Equal(10, (await _h.GetAsync(rig.Admin, $"{Structures}/{structure}/detail?asOf={Iso(later.AddDays(-1))}", Ct)).Json.GetProperty("components").GetArrayLength());

        // Refusals: a repeated order, an unknown component, a date that does not follow the last, a base evaluated after its dependant.
        var dupOrder = await SetComponentsAsync(rig.Admin, structure, later.AddDays(5), (parts[0], 1, 1m, null, false), (parts[1], 1, 2m, null, false));
        Assert.Equal("errors.hrm.pay.order_duplicate", FieldError(dupOrder, "components.items"));
        var unknown = await SetComponentsAsync(rig.Admin, structure, later.AddDays(5), (Guid.NewGuid(), 1, 1m, null, false));
        Assert.Equal("errors.hrm.pay.structure_component_unknown", FieldError(unknown, "components.items"));
        var notLater = await SetComponentsAsync(rig.Admin, structure, later, (parts[0], 1, 1m, null, false));
        Assert.Equal("errors.hrm.pay.version_not_later", FieldError(notLater, "components.validFrom"));
        var basic = await ComponentAsync(rig, version: Fixed(from.AddYears(-1)));
        var pct = await ComponentAsync(rig, version: new { validFrom = Iso(from.AddYears(-1)), method = "percent_of", baseComponentIds = new[] { basic.Id }, proRataRule = "none", statutoryFlags = new { } });
        var wrongOrder = await SetComponentsAsync(rig.Admin, structure, later.AddDays(5), (pct.Id, 1, null, 10m, false), (basic.Id, 2, 1m, null, false));
        Assert.Equal("errors.hrm.pay.order_dependency", FieldError(wrongOrder, "components.items"));
        var both = await SetComponentsAsync(rig.Admin, structure, later.AddDays(5), (basic.Id, 1, 1m, 5m, false));
        Assert.Equal("errors.hrm.pay.default_both", FieldError(both, "components.items"));
    }

    [Fact]
    public async Task FR_HRM_PAY_04_the_assign_rule_is_by_grade_employee_type_or_individual_and_is_checked()
    {
        var rig = await RigAsync(approval: false);
        var structure = await StructureAsync(rig);
        string Put(object rule) => FieldError(_h.PutAsync(rig.Admin, $"{Structures}/{structure}", new { assignRule = rule, rowVersion = StructureVersionAsync(structure).Result }, Ct).Result, "assignRule");
        Assert.Equal("errors.hrm.pay.assign_rule", Put(new { by = "department" }));
        Assert.Equal("errors.hrm.pay.assign_rule_type_unknown", Put(new { by = "employee_type", employeeTypes = Astronaut }));
        Assert.Equal("errors.hrm.pay.assign_rule_grade_unknown", Put(new { by = "grade", gradeIds = new[] { Guid.NewGuid() } }));
        var ok = await _h.PutAsync(rig.Admin, $"{Structures}/{structure}", new { assignRule = new { by = "employee_type", employeeTypes = TwoTypes }, rowVersion = await StructureVersionAsync(structure) }, Ct);
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body);
        Assert.Contains("contract", await _h.ScalarAsync<string>("select assign_rule::text from hrm.salary_structures where id = $1", Ct, structure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_XM_32_a_structure_change_waits_for_a_second_person_while_the_setting_is_on_and_the_requester_cannot_approve_it()
    {
        var rig = await RigAsync(approval: true);
        var basic = await ComponentAsync(rig, version: Fixed(Today.AddDays(-100)));
        var structure = await StructureAsync(rig);
        var from = Today.AddDays(-10);
        var held = await SetComponentsAsync(rig.Admin, structure, from, (basic.Id, 1, 50_000m, null, true));
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.salary_structure_components where structure_id = $1", Ct, structure)); // nothing changed yet
        var requestId = held.Json.GetProperty("requestId").GetGuid();
        var own = await _h.PostAsync(rig.Admin, $"/api/iam/maker-checker-requests/{requestId}/approve", new { reason = "myself" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, own.Status); // nobody approves their own request
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.salary_structure_components where structure_id = $1", Ct, structure));
        var approved = await _h.PostAsync(rig.Second, $"/api/iam/maker-checker-requests/{requestId}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.salary_structure_components where structure_id = $1", Ct, structure));
        Assert.Equal(50_000m, await _h.ScalarAsync<decimal>("select default_amount from hrm.salary_structure_components where structure_id = $1", Ct, structure));

        // A rename alone is not held.
        var rename = await _h.PutAsync(rig.Admin, $"{Structures}/{structure}", new { nameI18n = new { en = "Renamed" }, rowVersion = await StructureVersionAsync(structure) }, Ct);
        Assert.Equal(HttpStatusCode.OK, rename.Status);
    }

    // ------------------------------------------------------------------ compensation and revisions (FR-HRM-PAY-04, -06)

    [Fact]
    public async Task FR_HRM_PAY_06_a_revision_records_old_and_new_structure_date_and_reason_and_never_edits_the_old_row()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-200);
        var s1 = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);

        var first = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new
        {
            structureId = s1.Structure, validFrom = Iso(from), reasonCode = "initial_pay",
            lines = new object[] { new { componentId = s1.Basic, amount = 100_000.5m }, new { componentId = s1.House, percent = 45m }, new { componentId = s1.Tax, amount = 1_000m } },
        }, Ct);
        Assert.True(first.Status == HttpStatusCode.Created, first.Body);
        var firstId = first.Json.GetProperty("id").GetGuid();
        Assert.Equal(100_000.5m, await _h.ScalarAsync<decimal>("select amount from hrm.compensation_lines where assignment_id = $1 and component_id = $2", Ct, firstId, s1.Basic));
        Assert.Equal(100_000.5m, await _h.ScalarAsync<decimal>("select gross_amount from hrm.compensation_assignments where id = $1", Ct, firstId)); // earnings with an amount
        Assert.Equal("salary_revision", await _h.ScalarAsync<string>("select e.kind from hrm.employment_events e join hrm.compensation_assignments a on a.event_id = e.id where a.id = $1", Ct, firstId));

        // A revision to a second structure from a later date: the first row ends where the new one starts and is otherwise unchanged.
        var s2 = await BasicStructureAsync(rig, from.AddDays(100));
        var revisedFrom = from.AddDays(120);
        var revision = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new
        {
            structureId = s2.Structure, validFrom = Iso(revisedFrom), reasonCode = "increment", notes = "annual review",
            lines = new object[] { new { componentId = s2.Basic, amount = 120_000m }, new { componentId = s2.House, percent = 45m }, new { componentId = s2.Tax, amount = 1_500m } },
        }, Ct);
        Assert.True(revision.Status == HttpStatusCode.Created, revision.Body);
        Assert.Equal(revisedFrom, await _h.ScalarAsync<DateOnly>("select valid_to from hrm.compensation_assignments where id = $1", Ct, firstId));
        Assert.Equal(100_000.5m, await _h.ScalarAsync<decimal>("select amount from hrm.compensation_lines where assignment_id = $1 and component_id = $2", Ct, firstId, s1.Basic));
        var changes = await _h.ScalarAsync<string>("select e.changes::text from hrm.employment_events e join hrm.compensation_assignments a on a.event_id = e.id where a.id = $1", Ct, revision.Json.GetProperty("id").GetGuid());
        Assert.Contains(s1.Structure.ToString(), changes, StringComparison.Ordinal); // the old structure
        Assert.Contains(s2.Structure.ToString(), changes, StringComparison.Ordinal); // and the new one
        Assert.Equal("increment", await _h.ScalarAsync<string>("select e.reason_code from hrm.employment_events e join hrm.compensation_assignments a on a.event_id = e.id where a.id = $1", Ct, revision.Json.GetProperty("id").GetGuid()));

        // Never an edit: the database refuses to change a line or a fixed field of an assignment, or to delete either.
        var line = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync("update hrm.compensation_lines set amount = 1 where assignment_id = $1", Ct, firstId));
        Assert.Contains("an ended compensation_lines row never changes", line.Message, StringComparison.Ordinal);
        var edit = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync("update hrm.compensation_assignments set gross_amount = 1 where id = $1", Ct, firstId));
        Assert.Contains("an ended compensation_assignments row never changes", edit.Message, StringComparison.Ordinal);
        var gone = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync("delete from hrm.compensation_assignments where id = $1", Ct, firstId));
        Assert.Contains("an ended compensation_assignments row never changes", gone.Message, StringComparison.Ordinal);

        // The history and the current arrangement.
        var read = await _h.GetAsync(rig.Admin, $"{Employments}/{employment}/compensation", Ct);
        Assert.True(read.Status == HttpStatusCode.OK, read.Body);
        Assert.Equal(2, read.Json.GetProperty("history").GetArrayLength());
        Assert.Equal(s2.Structure, read.Json.GetProperty("current").GetProperty("structureId").GetGuid());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.outbox_messages where event_type = 'hrm.compensation.changed' and aggregate_id = $1 and payload->>'validFrom' = $2", Ct, employment, Iso(revisedFrom)));

        // Refusals: not later than the last, a line outside the structure, a mandatory component without a line, a wrong reason.
        var same = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s2.Structure, validFrom = Iso(revisedFrom), reasonCode = "increment", lines = new object[] { new { componentId = s2.Basic, amount = 1m } } }, Ct);
        Assert.Equal("errors.hrm.pay.compensation_not_later", FieldError(same, "validFrom"));
        var outside = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s2.Structure, validFrom = Iso(revisedFrom.AddDays(10)), reasonCode = "increment", lines = new object[] { new { componentId = s2.Basic, amount = 1m }, new { componentId = s1.Basic, amount = 1m } } }, Ct);
        Assert.Equal("errors.hrm.pay.compensation_line_component", FieldError(outside, "lines"));
        var noBasic = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s2.Structure, validFrom = Iso(revisedFrom.AddDays(10)), reasonCode = "increment", lines = new object[] { new { componentId = s2.Tax, amount = 1m } } }, Ct);
        Assert.Equal("errors.hrm.pay.compensation_mandatory_missing", FieldError(noBasic, "lines"));
        var reason = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s2.Structure, validFrom = Iso(revisedFrom.AddDays(10)), reasonCode = "whim", lines = new object[] { new { componentId = s2.Basic, amount = 1m } } }, Ct);
        Assert.Equal("errors.hrm.pay.compensation_reason", FieldError(reason, "reasonCode"));
        var empty = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s2.Structure, validFrom = Iso(revisedFrom.AddDays(10)), reasonCode = "increment", lines = new object[] { new { componentId = s2.Basic } } }, Ct);
        Assert.Equal("errors.hrm.pay.line_needs_value", FieldError(empty, "lines"));
    }

    [Fact]
    public async Task FR_HRM_PAY_06_a_backdated_revision_lists_the_periods_already_opened_and_computes_no_arrears()
    {
        var rig = await RigAsync(approval: false);
        var year = Today.Year;
        var groupAnswer = await _h.PostAsync(rig.Admin, "/api/hrm/pay-groups", new { companyId = rig.Company, code = Code("PG"), nameI18n = new { en = "Monthly" }, frequency = "monthly", taxYearStartMonth = 1 }, Ct);
        Assert.Equal(HttpStatusCode.Created, groupAnswer.Status);
        var group = groupAnswer.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(rig.Admin, $"/api/hrm/pay-groups/{group}/periods/generate", new { taxYear = year }, Ct)).Status);
        var from = Today.AddDays(-300);
        var s = await BasicStructureAsync(rig, from);
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var hire = await _h.PostAsync(rig.Hr, Employments, new
        {
            personId = person, companyId = rig.Company, employeeType = "permanent", joiningDate = Iso(Today.AddDays(-400)),
            assignment = new { orgUnitId = rig.Place.OrgUnit, branchId = rig.Place.Branch, title = "Officer", payGroupId = group },
        }, Ct);
        var employment = hire.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 10m } } }, Ct)).Status);

        // The periods of this year that ended or started before today are already open or closed: a revision dated in them is backdated.
        await _h.ExecAsync("update hrm.pay_periods set status = 'open' where pay_group_id = $1 and start_date <= $2", Ct, group, Today);
        var opened = await _h.ScalarAsync<long>("select count(*) from hrm.pay_periods where pay_group_id = $1 and status = 'open'", Ct, group);
        var backdatedFrom = new DateOnly(year, 1, 1) > from ? new DateOnly(year, 1, 1) : from.AddDays(1);
        var revision = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(backdatedFrom), reasonCode = "market_adjustment", lines = new object[] { new { componentId = s.Basic, amount = 20m } } }, Ct);
        Assert.True(revision.Status == HttpStatusCode.Created, revision.Body);
        Assert.True(revision.Json.GetProperty("backdated").GetBoolean());
        Assert.Equal(opened, revision.Json.GetProperty("affectedPeriods").GetArrayLength());
        var payload = await _h.ScalarAsync<string>("select payload::text from hrm.outbox_messages where event_type = 'hrm.compensation.changed' and aggregate_id = $1 order by occurred_at desc limit 1", Ct, employment);
        Assert.Contains("earliestDate", payload, StringComparison.Ordinal);
        Assert.Contains("affectedPeriodIds", payload, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.retro_adjustments where employment_id = $1", Ct, employment)); // arrears are S1.39
    }

    [Fact]
    public async Task FR_XM_32_a_compensation_change_waits_for_a_second_person_and_is_applied_when_approved()
    {
        var rig = await RigAsync(approval: true);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        var held = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new
        {
            structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", reason = "new hire",
            lines = new object[] { new { componentId = s.Basic, amount = 70_000m } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.compensation_assignments where employment_id = $1", Ct, employment));
        var read = await _h.GetAsync(rig.Admin, $"{Employments}/{employment}/compensation", Ct);
        Assert.Equal(1, read.Json.GetProperty("pending").GetInt32());
        var requestId = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(rig.Admin, $"/api/iam/maker-checker-requests/{requestId}/approve", new { reason = "me" }, Ct)).Status);
        var approved = await _h.PostAsync(rig.Second, $"/api/iam/maker-checker-requests/{requestId}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal(70_000m, await _h.ScalarAsync<decimal>("select l.amount from hrm.compensation_lines l join hrm.compensation_assignments a on a.id = l.assignment_id where a.employment_id = $1", Ct, employment));
    }

    // ------------------------------------------------------------------ security (GHR-06, permissions, scope)

    [Fact]
    public async Task GHR_06_a_user_without_the_salary_class_sees_no_amounts_in_the_api_the_list_or_the_audit_search_and_cannot_set_them()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        var done = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 123_456.78m } } }, Ct);
        Assert.Equal(HttpStatusCode.Created, done.Status);
        var assignment = done.Json.GetProperty("id").GetGuid();
        var shown = await _h.GetAsync(rig.Admin, $"{Employments}/{employment}/compensation", Ct);
        Assert.Contains("123456.78", shown.Body, StringComparison.Ordinal);

        string[] read =
        [
            "hrm.pay.compensation.read", "hrm.employments.read", "hrm.employment_assignments.read", "hrm.compensation_assignments.read", "hrm.compensation_lines.read",
            "hrm.salary_structures.read", "hrm.salary_structure_components.read", "aud.history.view",
        ];
        (string, object)[] scopes = [("*", "all")];
        var plain = await _h.UserAsync(Ct);
        await _h.AssignAsync(plain, await _h.RoleAsync(Ct, read, scopes, [("*", "class:salary", "hidden")]), null, Today.AddDays(-1), null, Ct);
        var api = await _h.GetAsync(plain, $"{Employments}/{employment}/compensation", Ct);
        Assert.True(api.Status == HttpStatusCode.OK, api.Body);
        Assert.DoesNotContain("123456", api.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"amount\"", api.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("grossAmount", api.Body, StringComparison.Ordinal);
        Assert.Contains(s.Basic.ToString(), api.Body, StringComparison.Ordinal); // the shape is there, the amounts are not
        var list = await _h.GetAsync(plain, "/api/hrm/compensation-lines", Ct);
        Assert.DoesNotContain("123456", list.Body, StringComparison.Ordinal);
        var assignments = await _h.GetAsync(plain, "/api/hrm/compensation-assignments", Ct);
        Assert.DoesNotContain("123456", assignments.Body, StringComparison.Ordinal);
        var export = await _h.GetAsync(plain, "/api/hrm/compensation-lines?format=csv", Ct);
        Assert.DoesNotContain("123456", export.Body, StringComparison.Ordinal);
        var structure = await _h.GetAsync(plain, $"{Structures}/{s.Structure}/detail", Ct);
        Assert.Equal(HttpStatusCode.OK, structure.Status);
        Assert.DoesNotContain("defaultPercent", structure.Body, StringComparison.Ordinal);
        var history = await _h.GetAsync(plain, $"/api/aud/history/hrm.compensation_assignments/{assignment}", Ct);
        Assert.DoesNotContain("123456", history.Body, StringComparison.Ordinal);

        // With the field class the same user sees them.
        var rich = await _h.UserAsync(Ct);
        await _h.AssignAsync(rich, await _h.RoleAsync(Ct, read, scopes, [("*", "class:salary", "read_only")]), null, Today.AddDays(-1), null, Ct);
        Assert.Contains("123456.78", (await _h.GetAsync(rich, $"{Employments}/{employment}/compensation", Ct)).Body, StringComparison.Ordinal);

        // A writer without edit access to the class cannot set amounts: the field answers "not editable" and nothing changes.
        var writer = await _h.UserAsync(Ct);
        string[] write = ["hrm.compensation_assignments.create", "hrm.compensation_lines.create", "hrm.employments.read", "hrm.compensation_assignments.read", "hrm.pay.compensation.read"];
        await _h.AssignAsync(writer, await _h.RoleAsync(Ct, write, scopes, [("*", "class:salary", "hidden")]), null, Today.AddDays(-1), null, Ct);
        var denied = await _h.PostAsync(writer, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from.AddDays(30)), reasonCode = "increment", lines = new object[] { new { componentId = s.Basic, amount = 999m } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, denied.Status);
        Assert.Equal("errors.access.field_not_editable", denied.Json.GetProperty("errors").GetProperty("amount")[0].GetString());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.compensation_assignments where employment_id = $1", Ct, employment));
    }

    [Fact]
    public async Task FR_HRM_PAY_04_every_endpoint_needs_its_permission_and_the_salary_read_permission_and_stays_in_the_company_scope()
    {
        var rig = await RigAsync(approval: false);
        var component = await ComponentAsync(rig, version: Fixed(Today.AddDays(-10)));
        var structure = await StructureAsync(rig);
        var employment = await HireAsync(rig);
        var nobody = await _h.UserAsync(Ct);
        await _h.AssignAsync(nobody, await _h.RoleAsync(Ct, ["hrm.employments.read"], [("*", "all")]), null, Today.AddDays(-1), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Components, new { companyId = rig.Company, code = Code("C"), nameI18n = new { en = "x" }, kind = "earning", payslipOrder = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(nobody, $"{Components}/{component.Id}", new { isActive = false, rowVersion = component.Version }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await VersionAsync(nobody, component.Id, Fixed(Today))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Components}/{component.Id}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, "/api/hrm/pay-formulas/check", new { companyId = rig.Company, formula = "1" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Structures, new { companyId = rig.Company, code = Code("S"), nameI18n = new { en = "x" } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(nobody, $"{Structures}/{structure}", new { isActive = false, rowVersion = await StructureVersionAsync(structure) }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Structures}/{structure}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"{Employments}/{employment}/compensation", new { structureId = structure, validFrom = Iso(Today), reasonCode = "initial_pay", lines = new object[] { new { componentId = component.Id, amount = 1m } } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Employments}/{employment}/compensation", Ct)).Status);

        // Reading a salary arrangement needs hrm.pay.compensation.read on top of the table permission.
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.compensation_assignments.read", "hrm.employments.read"], [("*", "all")], [("*", "class:salary", "editable")]), null, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(reader, $"{Employments}/{employment}/compensation", Ct)).Status);

        // A pay administrator of another company cannot reach this company's components, structures or employees.
        var other = await _h.CompanyAsync(Ct);
        var scoped = await _h.UserAsync(Ct);
        string[] all = ["hrm.pay_components.read", "hrm.pay_components.update", "hrm.salary_structures.read", "hrm.salary_structures.update", "hrm.pay.compensation.read", "hrm.employments.read", "hrm.compensation_assignments.read"];
        await _h.AssignAsync(scoped, await _h.RoleAsync(Ct, all, [("*", await _h.ScopeAsync("companies", new { company_ids = new[] { other } }, Ct))]), other, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(scoped, $"{Components}/{component.Id}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(scoped, $"{Structures}/{structure}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(scoped, $"{Employments}/{employment}/compensation", Ct)).Status);
    }

    // ------------------------------------------------------------------ review S1.34b (docs/reviews/S1.34b-review.md)

    private async Task<Guid> AssignedAsync(Rig rig, DateOnly from, decimal amount = 50_000m)
    {
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        var done = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount } } }, Ct);
        Assert.True(done.Status is HttpStatusCode.Created or HttpStatusCode.Accepted, done.Body);
        if (done.Status == HttpStatusCode.Accepted)
        {
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Second, $"/api/iam/maker-checker-requests/{done.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct)).Status);
        }

        return employment;
    }

    [Fact]
    public async Task FR_XM_32_a_data_correction_cannot_target_a_salary_revision_so_job_details_never_skip_the_approval_of_promotions()
    {
        var rig = await RigAsync(approval: false);
        var employment = await AssignedAsync(rig, Today.AddDays(-100));
        var eventId = await _h.ScalarAsync<Guid>("select event_id from hrm.compensation_assignments where employment_id = $1", Ct, employment);
        var correction = await _h.PostAsync(rig.Hr, $"{Employments}/{employment}/events", new
        {
            kind = "data_correction",
            reasonCode = "entry_error",
            effectiveDate = Iso(Today),
            correctsEventId = eventId,
            assignment = new { title = "Director (via correction)" },
            rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment),
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, correction.Status);
        Assert.Contains("correctsEventId", correction.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employment_assignments where employment_id = $1 and title = 'Director (via correction)'", Ct, employment));
    }

    [Fact]
    public async Task GHR_06_a_held_salary_is_sealed_in_the_kernels_request_table_and_no_amount_is_readable_there()
    {
        var rig = await RigAsync(approval: true);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        var held = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 123456.78m } } }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var requestId = held.Json.GetProperty("requestId").GetGuid();
        Assert.DoesNotContain("123456", await _h.ScalarAsync<string>("select payload::text from iam.maker_checker_requests where id = $1", Ct, requestId), StringComparison.Ordinal);

        var tenantAdmin = await _h.UserAsync(Ct, null, "tenant_admin"); // a baseline role with no salary class
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(tenantAdmin, $"{Employments}/{employment}/compensation", Ct)).Status);
        var read = await _h.GetAsync(tenantAdmin, $"/api/iam/maker-checker-requests/{requestId}", Ct);
        Assert.DoesNotContain("123456", read.Body, StringComparison.Ordinal);

        // The sealed plan is still applied when the second person approves.
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(rig.Second, $"/api/iam/maker-checker-requests/{requestId}/approve", new { reason = "agreed" }, Ct)).Status);
        Assert.Equal(123456.78m, await _h.ScalarAsync<decimal>("select l.amount from hrm.compensation_lines l join hrm.compensation_assignments a on a.id = l.assignment_id where a.employment_id = $1", Ct, employment));
        Assert.DoesNotContain("123456", await _h.ScalarAsync<string>("select payload::text from iam.maker_checker_requests where id = $1", Ct, requestId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_HRM_PAY_03_a_loop_that_starts_at_a_later_date_is_refused()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-60);
        var a = await ComponentAsync(rig, version: Fixed(from));
        var b = await ComponentAsync(rig, version: Fixed(from));
        Assert.Equal(HttpStatusCode.Created, (await VersionAsync(rig.Admin, b.Id, new { validFrom = Iso(Today.AddDays(30)), method = "percent_of", baseComponentIds = new[] { a.Id }, proRataRule = "none", statutoryFlags = new { } })).Status);
        var loop = await VersionAsync(rig.Admin, a.Id, new { validFrom = Iso(Today.AddDays(10)), method = "percent_of", baseComponentIds = new[] { b.Id }, proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.formula_cycle", FieldError(loop, "baseComponentIds"));
        var viaFormula = await VersionAsync(rig.Admin, a.Id, new { validFrom = Iso(Today.AddDays(10)), method = "formula", formula = b.Code + " * 2", proRataRule = "none", statutoryFlags = new { } });
        Assert.Equal("errors.hrm.pay.formula_cycle", FieldError(viaFormula, "formula"));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.pay_component_versions where component_id = $1", Ct, a.Id));

        // The function names of the formula grammar cannot be component codes.
        var reserved = await _h.PostAsync(rig.Admin, Components, new { companyId = rig.Company, code = "Max", nameI18n = new { en = "x" }, kind = "earning", payslipOrder = 1 }, Ct);
        Assert.Equal("errors.hrm.pay.component_code", FieldError(reserved, "code"));
    }

    [Fact]
    public async Task GHR_06_a_user_with_read_only_salary_access_cannot_wipe_amounts_or_defaults_by_leaving_them_out()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 500m } } }, Ct)).Status);

        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, PayPermissions, [("*", "all")], [("*", "class:salary", "read_only")]), null, Today.AddDays(-1), null, Ct);
        var wipe = await _h.PostAsync(reader, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from.AddDays(30)), reasonCode = "increment", lines = new object[] { new { componentId = s.Basic, units = 1m } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wipe.Status);
        Assert.Equal("errors.access.field_not_editable", wipe.Json.GetProperty("errors").GetProperty("amount")[0].GetString());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.compensation_assignments where employment_id = $1", Ct, employment));

        // A structure list sent without defaults by someone who cannot edit them keeps the defaults in force.
        var structure = await StructureAsync(rig);
        var comp = await ComponentAsync(rig, version: Fixed(from));
        Assert.Equal(HttpStatusCode.OK, (await SetComponentsAsync(rig.Admin, structure, from, (comp.Id, 1, 500m, null, false))).Status);
        var again = await SetComponentsAsync(reader, structure, Today.AddDays(5), (comp.Id, 1, null, null, true));
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(500m, await _h.ScalarAsync<decimal>("select default_amount from hrm.salary_structure_components where structure_id = $1 and valid_to is null", Ct, structure));
        var set = await SetComponentsAsync(reader, structure, Today.AddDays(9), (comp.Id, 1, 999m, null, true));
        Assert.Equal(HttpStatusCode.BadRequest, set.Status);
    }

    [Fact]
    public async Task FR_XM_32_an_approver_who_may_not_see_the_amounts_cannot_approve_them()
    {
        var rig = await RigAsync(approval: true);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        var held = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 70_000m } } }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var blind = await _h.UserAsync(Ct);
        await _h.AssignAsync(blind, await _h.RoleAsync(Ct, [.. PayPermissions, "hrm.pay.change.approve"], [("*", "all")], [("*", "class:salary", "hidden")]), null, Today.AddDays(-1), null, Ct);
        var refused = await _h.PostAsync(blind, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "blind" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.compensation_assignments where employment_id = $1", Ct, employment));
    }

    [Fact]
    public async Task FR_HRM_PAY_04_a_change_to_an_existing_list_cannot_start_in_the_past_nor_drop_a_component_salaries_use_and_a_list_has_one_currency()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 500m }, new { componentId = s.Tax, amount = 5m } } }, Ct)).Status);

        var past = await SetComponentsAsync(rig.Admin, s.Structure, Today.AddDays(-30), (s.Basic, 1, null, null, true));
        Assert.Equal("errors.hrm.pay.structure_date_past", FieldError(past, "components.validFrom"));
        var dropped = await SetComponentsAsync(rig.Admin, s.Structure, Today.AddDays(5), (s.Basic, 1, null, null, true));
        Assert.Equal("errors.hrm.pay.structure_component_in_use", FieldError(dropped, "components.items"));

        var other = await StructureAsync(rig);
        var c1 = await ComponentAsync(rig, version: Fixed(from));
        var c2 = await ComponentAsync(rig, version: Fixed(from));
        var mixed = await _h.PutAsync(rig.Admin, $"{Structures}/{other}", new
        {
            rowVersion = await StructureVersionAsync(other),
            components = new { validFrom = Iso(from), items = new object[] { new { componentId = c1.Id, evaluationOrder = 1, currencyCode = "PKR" }, new { componentId = c2.Id, evaluationOrder = 2, currencyCode = "USD" } } },
        }, Ct);
        Assert.Equal("errors.hrm.pay.structure_currency_mixed", FieldError(mixed, "components.items"));
    }

    [Fact]
    public async Task FR_XM_32_nobody_sets_their_own_pay_and_no_salary_starts_after_a_recorded_end()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        var company = rig.Company;
        await _h.ExecAsync("""
            insert into hrm.employment_events (id, company_id, employment_id, kind, reason_code, effective_date, status, created_at, created_by, updated_at, updated_by, row_version)
            values (gen_random_uuid(), $1, $2, 'separation', 'transfer', $3, 'effective', now(), $4, now(), $4, 1)
            """, Ct, company, employment, Today.AddDays(10), rig.Admin);
        var after = await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(Today.AddDays(20)), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 1m } } }, Ct);
        Assert.Equal("errors.hrm.pay.compensation_after_end", FieldError(after, "validFrom"));

        var mine = await HireAsync(rig);
        await _h.ExecAsync("update hrm.employments set user_ref = $1 where id = $2", Ct, rig.Admin, mine);
        var own = await _h.PostAsync(rig.Admin, $"{Employments}/{mine}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 1m } } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, own.Status);
    }

    [Fact]
    public async Task FR_HRM_PAY_06_the_database_refuses_lines_for_an_ended_row_and_a_change_of_the_row_in_forces_employee_or_structure()
    {
        var rig = await RigAsync(approval: false);
        var from = Today.AddDays(-100);
        var s = await BasicStructureAsync(rig, from);
        var employment = await HireAsync(rig);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s.Structure, validFrom = Iso(from), reasonCode = "initial_pay", lines = new object[] { new { componentId = s.Basic, amount = 1m } } }, Ct)).Status);
        var first = await _h.ScalarAsync<Guid>("select id from hrm.compensation_assignments where employment_id = $1", Ct, employment);
        var s2 = await BasicStructureAsync(rig, from.AddDays(50));
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(rig.Admin, $"{Employments}/{employment}/compensation", new { structureId = s2.Structure, validFrom = Iso(from.AddDays(60)), reasonCode = "increment", lines = new object[] { new { componentId = s2.Basic, amount = 2m } } }, Ct)).Status);
        var late = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync(
            "insert into hrm.compensation_lines (id, company_id, assignment_id, component_id, amount, created_at, created_by, updated_at, updated_by) values (gen_random_uuid(), $1, $2, $3, 9, now(), $4, now(), $4)",
            Ct, rig.Company, first, s.House, rig.Admin));
        Assert.Contains("an ended compensation_assignments row never changes", late.Message, StringComparison.Ordinal);
        var current = await _h.ScalarAsync<Guid>("select id from hrm.compensation_assignments where employment_id = $1 and valid_to is null", Ct, employment);
        var move = await Assert.ThrowsAnyAsync<Exception>(() => _h.ExecAsync("update hrm.compensation_assignments set structure_id = $1 where id = $2", Ct, s.Structure, current));
        Assert.Contains("keeps its employee, structure, event and start", move.Message, StringComparison.Ordinal);
    }
}
