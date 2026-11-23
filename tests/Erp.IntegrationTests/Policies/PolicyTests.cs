#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;
using static Erp.IntegrationTests.Policies.PolicyTestSupport;

namespace Erp.IntegrationTests.Policies;

/// <summary>
/// The policy framework (slice S1.23a, Build items 1 to 6) through the real API pipeline, the real access engine, maker-checker and a real
/// PostgreSQL: FR-HRM-POL-02 (assignment by attribute rules with precedence and effective dates), FR-HRM-POL-03 (which policy applies and
/// why, rule and layer), FR-HRM-POL-04/06 (versions checked against the type's schema, pack versions overridden by the customer),
/// FR-HRM-POS-06 (grade defaults beside the parameters), FR-HRM-EMP-10 (probation by policy, replacing the S1.20 settings; ruling Q3) and
/// FR-XM-32 (nobody approves their own activation). The test types <c>test_leave</c> and <c>test_benefit</c> are registered by the
/// harness (tests only).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PolicyTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private AccessHarness _h = null!;
    private Guid _hr;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => _h.Today;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        _hr = await _h.UserAsync(Ct, null, "hr_admin");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A company of its own where employment changes apply without approval.</summary>
    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync()
    {
        var company = await _h.CompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        return (company, await _h.PlaceAsync(company, Ct));
    }

    private async Task<Guid> GradeAsync(Guid company, string code, int rank, string? defaults = null) =>
        (Guid)(await _h.RowAsync("hrm.grades", company, new Dictionary<string, object?> { ["code"] = code + Guid.NewGuid().ToString("N")[..6], ["rank"] = rank, ["defaults"] = defaults ?? "{}" }, Ct))["id"]!;

    private async Task<Guid> HireAsync(Guid company, (Guid OrgUnit, Guid Branch) place, Guid? grade = null, DateOnly? joining = null, string type = "permanent", object? custom = null)
    {
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(_hr, Employments, new
        {
            personId = person, companyId = company, employeeType = type, joiningDate = Iso(joining ?? Today.AddDays(-300)), custom,
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer", gradeId = grade },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> ExplainAsync(Guid employment, string type, DateOnly date, Guid? user = null)
    {
        var answer = await _h.GetAsync(user ?? _hr, $"/api/hrm/policy-resolution?employmentId={employment}&policyType={type}&date={Iso(date)}", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json;
    }

    private static Guid? Matched(JsonElement view) =>
        view.GetProperty("outcome").GetString() == "matched" ? view.GetProperty("matches")[0].GetProperty("policyId").GetGuid() : null;

    private static object Cond(string attribute, string op, object[] values, bool subUnits = false, string? field = null) =>
        field is null ? (subUnits ? new { attribute, @operator = op, values, subUnits } : new { attribute, @operator = op, values }) : new { attribute, field, @operator = op, values };

    // ------------------------------------------------------------------ FR-HRM-POL-02 / -03: rules, precedence, dates, the view

    [Fact]
    public async Task FR_HRM_POL_02_FR_HRM_POL_03_grade_rules_resolve_by_precedence_follow_a_promotion_and_the_view_explains_both()
    {
        var (company, place) = await CompanyAsync();
        var g3 = await GradeAsync(company, "G3", 3);
        var g4 = await GradeAsync(company, "G4", 4);
        var g5 = await GradeAsync(company, "G5", 5);
        var g6 = await GradeAsync(company, "G6", 6, """{"leave_entitlement":"senior"}""");
        var from = Today.AddYears(-2);
        var (senior, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 30 }, from, Ct);
        var (everyone, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        var set = await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct,
            (senior, 1, [Cond("grade", "in", [g5, g6])]),
            (everyone, 2, []));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);

        var six = await HireAsync(company, place, g6);
        var three = await HireAsync(company, place, g3);
        var viewSix = await ExplainAsync(six, "test_leave", Today);
        Assert.Equal(senior, Matched(viewSix));
        Assert.Equal(30, viewSix.GetProperty("matches")[0].GetProperty("parameters").GetProperty("days").GetInt32());
        Assert.Equal("tenant", viewSix.GetProperty("matches")[0].GetProperty("layer").GetString());
        Assert.Equal("senior", viewSix.GetProperty("attributes").GetProperty("gradeDefaults").GetProperty("leave_entitlement").GetString()); // FR-HRM-POS-06
        Assert.Equal("not_reached", viewSix.GetProperty("evaluations")[1].GetProperty("outcome").GetString());

        // The G3 employee: the first rule did not match (grade is not one of G5, G6: the view says which grade they have), the second did.
        var viewThree = await ExplainAsync(three, "test_leave", Today);
        Assert.Equal(everyone, Matched(viewThree));
        var first = viewThree.GetProperty("evaluations")[0];
        Assert.Equal("not_matched", first.GetProperty("outcome").GetString());
        var condition = first.GetProperty("conditions")[0];
        Assert.False(condition.GetProperty("met").GetBoolean());
        Assert.Equal(g3.ToString(), condition.GetProperty("actual")[0].GetString());
        Assert.Equal("matched", viewThree.GetProperty("evaluations")[1].GetProperty("outcome").GetString());
        Assert.True(viewThree.GetProperty("names").GetProperty("records").TryGetProperty(g5.ToString(), out _)); // rules in words name the grades

        // A promotion from G4 to G6 on a date: the day before, the second policy; from the date, the first.
        var four = await HireAsync(company, place, g4, Today.AddDays(-300));
        var promoted = Today.AddDays(-30);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, four);
        var change = await _h.PostAsync(_hr, $"{Employments}/{four}/events", new { kind = "promotion", reasonCode = "merit", effectiveDate = Iso(promoted), assignment = new { gradeId = g6 }, rowVersion = version }, Ct);
        Assert.True(change.Status == HttpStatusCode.Created, change.Body);
        Assert.Equal(everyone, Matched(await ExplainAsync(four, "test_leave", promoted.AddDays(-1))));
        Assert.Equal(senior, Matched(await ExplainAsync(four, "test_leave", promoted)));
    }

    [Fact]
    public async Task FR_HRM_POL_02_a_custom_field_rule_resolves_a_unit_rule_reaches_sub_units_and_a_retired_field_is_refused()
    {
        var (company, place) = await CompanyAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = "shift_" + Guid.NewGuid().ToString("N")[..8];
        var field = await _h.PostAsync(admin, "/api/cfg/custom-fields", new { entityType = "hrm.employments", fieldKey = key, dataType = "bool", label = new { en = "Shift worker", ur = "شفٹ کارکن" } }, Ct);
        Assert.True(field.Status == HttpStatusCode.Created, field.Body);
        var hierarchy = (Guid)(await _h.RowAsync("org.org_hierarchies", company, new Dictionary<string, object?> { ["kind"] = "reporting" }, Ct))["id"]!;
        var child = (Guid)(await _h.RowAsync("org.org_units", company, null, Ct))["id"]!;
        await _h.RowAsync("org.org_unit_links", company, new Dictionary<string, object?> { ["hierarchy_id"] = hierarchy, ["unit_id"] = child, ["parent_unit_id"] = place.OrgUnit, ["valid_from"] = Today.AddDays(-1000), ["valid_to"] = null }, Ct);

        var from = Today.AddYears(-2);
        var (shift, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 15 }, from, Ct);
        var (below, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 18 }, from, Ct);
        var (rest, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 10 }, from, Ct);
        var set = await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct,
            (shift, 1, [Cond("custom", "equals", [true], field: key)]),
            (below, 2, [Cond("unit", "equals", [place.OrgUnit], subUnits: true), Cond("employee_type", "not_in", ["intern"])]),
            (rest, 3, []));
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);

        var worker = await HireAsync(company, place, custom: new Dictionary<string, object> { [key] = true });
        var inChild = await HireAsync(company, (child, place.Branch));
        var intern = await HireAsync(company, (child, place.Branch), type: "intern");
        Assert.Equal(shift, Matched(await ExplainAsync(worker, "test_leave", Today)));
        Assert.Equal(below, Matched(await ExplainAsync(inChild, "test_leave", Today)));
        Assert.Equal(rest, Matched(await ExplainAsync(intern, "test_leave", Today)));

        // Retire the field: a new set naming it is refused at activation.
        var id = field.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/cfg/custom-fields/{id}/retire", new { rowVersion = field.Json.GetProperty("rowVersion").GetInt64() }, Ct)).Status);
        var refused = await _h.ActivateSetAsync(_hr, company, "test_leave", Today.AddDays(1), Ct, (shift, 1, [Cond("custom", "equals", [true], field: key)]), (rest, 2, []));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.hrm.policy.rule.custom_field_retired", refused.Json.GetProperty("errors").GetProperty("assignments[0].rule")[0].GetString());
    }

    [Fact]
    public async Task FR_HRM_POL_02_the_activation_check_refuses_a_shared_precedence_and_reports_employees_matching_nothing()
    {
        var (company, place) = await CompanyAsync();
        var g5 = await GradeAsync(company, "G5", 5);
        var g3 = await GradeAsync(company, "G3", 3);
        var from = Today.AddYears(-1);
        var (a, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 30 }, from, Ct);
        var (b, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        var low = await HireAsync(company, place, g3);

        var tie = await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (a, 1, [Cond("grade", "in", [g5])]), (b, 1, []));
        Assert.Equal(HttpStatusCode.BadRequest, tie.Status);
        Assert.Equal("errors.hrm.policy.assignment.precedence_taken", tie.Json.GetProperty("errors").GetProperty("assignments[1].precedence")[0].GetString());

        var check = await _h.PostAsync(_hr, $"{Sets}/check", new
        {
            companyId = company, policyType = "test_leave", effectiveFrom = Iso(from),
            assignments = new[] { new { policyId = a, precedence = 1, rule = new { conditions = new[] { Cond("grade", "in", [g5]) } } } },
        }, Ct);
        Assert.True(check.Status == HttpStatusCode.OK, check.Body);
        Assert.True(check.Json.GetProperty("valid").GetBoolean());
        Assert.Equal(1, check.Json.GetProperty("unmatched").GetProperty("count").GetInt32());
        Assert.Equal(low, check.Json.GetProperty("unmatched").GetProperty("items")[0].GetProperty("employmentId").GetGuid());

        // Activated anyway, the warning comes with the answer; the employee then has no policy, with the reason.
        var applied = await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (a, 1, [Cond("grade", "in", [g5])]));
        Assert.True(applied.Status == HttpStatusCode.OK, applied.Body);
        Assert.Equal(1, applied.Json.GetProperty("unmatched").GetProperty("count").GetInt32());
        var none = await ExplainAsync(low, "test_leave", Today);
        Assert.Equal("no_policy", none.GetProperty("outcome").GetString());
        Assert.Equal("no_rule_matched", none.GetProperty("reason").GetString());

        // A set activated meanwhile is not overwritten unseen; a date before the newest set is refused.
        var stale = await _h.PostAsync(_hr, $"{Sets}/activate", new { companyId = company, policyType = "test_leave", effectiveFrom = Iso(Today), basedOn = (string?)null, assignments = Array.Empty<object>() }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var early = await _h.ActivateSetAsync(_hr, company, "test_leave", from.AddDays(-1), Ct, (b, 1, []));
        Assert.Equal("errors.hrm.policy.assignment.set_date", early.Json.GetProperty("errors").GetProperty("effectiveFrom")[0].GetString());

        // A new set from a later date ends the old one the day before: both periods stay explainable.
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", Today.AddDays(-10), Ct, (b, 1, []))).Status);
        Assert.Equal("no_policy", (await ExplainAsync(low, "test_leave", Today.AddDays(-11))).GetProperty("outcome").GetString());
        Assert.Equal(b, Matched(await ExplainAsync(low, "test_leave", Today.AddDays(-10))));

        // The database refuses a second assignment of the type with the same precedence on the same dates (written past the service).
        await using var owner = await _h.OwnerAsync(null, Ct);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, """
            insert into hrm.policy_assignments (id, company_id, policy_id, precedence, rule, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 1, '{}', $4, now(), $5, now(), $5)
            """, Ct, Guid.CreateVersion7(), company, a, Today, BaselineSeed.SystemUserId));
        Assert.Equal("ck_policy_assignments_precedence_unique", direct.ConstraintName);
    }

    // ------------------------------------------------------------------ FR-HRM-POL-04 / -06: versions, schema, pack layer

    [Fact]
    public async Task FR_HRM_POL_06_version_2_from_a_date_leaves_earlier_dates_on_version_1_and_a_version_in_force_never_changes()
    {
        var (company, place) = await CompanyAsync();
        var from = Today.AddYears(-1);
        var (policy, v1) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (policy, 1, []))).Status);
        var employee = await HireAsync(company, place);

        var draft = await _h.PostAsync(_hr, $"{PolicyTestSupport.Policies}/{policy}/versions", new { }, Ct);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        Assert.Equal(20, draft.Json.GetProperty("parameters").GetProperty("days").GetInt32()); // starts from the latest version
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(_hr, $"{PolicyTestSupport.Policies}/{policy}/versions", new { }, Ct)).Status); // one draft at a time
        var v2 = draft.Json.GetProperty("id").GetGuid();
        var edited = await _h.PutAsync(_hr, $"/api/hrm/policy-versions/{v2}/parameters", new { parameters = new { days = 25 }, rowVersion = draft.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(edited.Status == HttpStatusCode.OK, edited.Body);

        var start = Today.AddDays(10);
        var before = await _h.PostAsync(_hr, $"/api/hrm/policy-versions/{v2}/activate", new { effectiveFrom = Iso(from), rowVersion = edited.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal("errors.hrm.policy.activate_after", before.Json.GetProperty("errors").GetProperty("effectiveFrom")[0].GetString());
        var activated = await _h.PostAsync(_hr, $"/api/hrm/policy-versions/{v2}/activate", new { effectiveFrom = Iso(start), rowVersion = edited.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);

        var dayBefore = (await ExplainAsync(employee, "test_leave", start.AddDays(-1))).GetProperty("matches")[0];
        Assert.Equal(1, dayBefore.GetProperty("version").GetInt32());
        Assert.Equal(20, dayBefore.GetProperty("parameters").GetProperty("days").GetInt32());
        var onDay = (await ExplainAsync(employee, "test_leave", start)).GetProperty("matches")[0];
        Assert.Equal(2, onDay.GetProperty("version").GetInt32());
        Assert.Equal(25, onDay.GetProperty("parameters").GetProperty("days").GetInt32());
        Assert.Equal(Iso(start), await _h.ScalarAsync<string>("select valid_to::text from hrm.policy_versions where id = $1", Ct, v1));

        // A version that was in force is never edited, through the API or past it.
        var v1Version = await _h.ScalarAsync<long>("select row_version from hrm.policy_versions where id = $1", Ct, v1);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PutAsync(_hr, $"/api/hrm/policy-versions/{v1}/parameters", new { parameters = new { days = 99 }, rowVersion = v1Version }, Ct)).Status);
        await using var owner = await _h.OwnerAsync(null, Ct);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, "update hrm.policy_versions set parameters = '{\"days\":99}' where id = $1", Ct, v1));
        Assert.Equal("ck_policy_versions_fixed", direct.ConstraintName);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, "delete from hrm.policy_versions where id = $1", Ct, v1));
        Assert.Equal("ck_policy_versions_fixed", deleted.ConstraintName);
    }

    [Fact]
    public async Task FR_HRM_POL_04_parameters_are_checked_against_the_type_schema_and_unknown_types_are_refused()
    {
        var (company, _) = await CompanyAsync();
        async Task<JsonElement> Refused(object parameters, string type = "test_leave")
        {
            var answer = await _h.PostAsync(_hr, PolicyTestSupport.Policies, new { companyId = company, policyType = type, code = "X" + Guid.NewGuid().ToString("N")[..8], name = new { en = "X" }, parameters }, Ct);
            Assert.True(answer.Status == HttpStatusCode.BadRequest, answer.Body);
            return answer.Json.GetProperty("errors");
        }

        Assert.Equal("errors.hrm.policy.param.required", (await Refused(new { })).GetProperty("parameters.days")[0].GetString());
        Assert.Equal("errors.hrm.policy.param.type", (await Refused(new { days = "thirty" })).GetProperty("parameters.days")[0].GetString());
        Assert.Equal("errors.hrm.policy.param.type", (await Refused(new { days = 1.5 })).GetProperty("parameters.days")[0].GetString());
        Assert.Equal("errors.hrm.policy.param.maximum", (await Refused(new { days = 400 })).GetProperty("parameters.days")[0].GetString());
        Assert.Equal("errors.hrm.policy.param.unknown", (await Refused(new { days = 1, rate = 2 })).GetProperty("parameters.rate")[0].GetString());
        Assert.Equal("errors.hrm.policy.param.enum", (await Refused(new { days = 1, kind = "half" })).GetProperty("parameters.kind")[0].GetString());
        Assert.Equal("errors.hrm.policy.param.max_length", (await Refused(new { days = 1, note = new string('n', 21) })).GetProperty("parameters.note")[0].GetString());
        Assert.Equal("errors.hrm.policy.unknown_type", (await Refused(new { days = 1 }, "tax_slabs")).GetProperty("policyType")[0].GetString());

        // The type catalogue lists every registered type with its schema (the probation and notice types of the HR module among them).
        var types = await _h.GetAsync(_hr, "/api/hrm/policy-types", Ct);
        var codes = types.Json.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("code").GetString()).ToList();
        Assert.Contains("probation", codes);
        Assert.Contains("notice_period", codes);
        Assert.Contains("test_leave", codes);
    }

    [Fact]
    public async Task FR_HRM_POL_06_a_pack_version_resolves_in_the_pack_layer_and_the_customers_new_version_is_an_override()
    {
        var (company, place) = await CompanyAsync();
        var from = Today.AddYears(-1);
        // A policy as a pack installs it (the pack loader is S1.23b): source pack, version 1 not overridden.
        var policy = Guid.CreateVersion7();
        var v1 = Guid.CreateVersion7();
        await using (var owner = await _h.OwnerAsync(null, Ct))
        {
            await AccessHarness.ExecAsync(owner, """
                insert into hrm.policies (id, company_id, code, name_i18n, policy_type, source, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, '{"en":"Pack leave"}', 'test_leave', 'pack', now(), $4, now(), $4)
                """, Ct, policy, company, "PACK" + Guid.NewGuid().ToString("N")[..6], BaselineSeed.SystemUserId);
            await AccessHarness.ExecAsync(owner, """
                insert into hrm.policy_versions (id, policy_id, version, parameters, is_overridden, status, valid_from, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 1, '{"days":14}', false, 'active', $3, now(), $4, now(), $4)
                """, Ct, v1, policy, from, BaselineSeed.SystemUserId);
        }

        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (policy, 1, []))).Status);
        var employee = await HireAsync(company, place);
        Assert.Equal("pack", (await ExplainAsync(employee, "test_leave", Today)).GetProperty("matches")[0].GetProperty("layer").GetString());

        var draft = await _h.PostAsync(_hr, $"{PolicyTestSupport.Policies}/{policy}/versions", new { parameters = new { days = 16 } }, Ct);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        Assert.True(draft.Json.GetProperty("isOverridden").GetBoolean());
        var start = Today.AddDays(-5);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(_hr, $"/api/hrm/policy-versions/{draft.Json.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = Iso(start), rowVersion = draft.Json.GetProperty("rowVersion").GetInt64() }, Ct)).Status);
        var now = (await ExplainAsync(employee, "test_leave", Today)).GetProperty("matches")[0];
        Assert.Equal("tenant", now.GetProperty("layer").GetString());
        Assert.Equal(16, now.GetProperty("parameters").GetProperty("days").GetInt32());
        Assert.Equal("pack", (await ExplainAsync(employee, "test_leave", start.AddDays(-1))).GetProperty("matches")[0].GetProperty("layer").GetString());
    }

    // ------------------------------------------------------------------ no policy, several policies, retirement, determinism

    [Fact]
    public async Task FR_HRM_POL_03_no_policy_is_answered_with_its_reason_and_a_type_of_many_lists_every_match()
    {
        var (company, place) = await CompanyAsync();
        var g5 = await GradeAsync(company, "G5", 5);
        var from = Today.AddYears(-1);
        var (current, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        var (later, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 22 }, Today.AddDays(30), Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (later, 1, [Cond("grade", "in", [g5])]), (current, 2, []))).Status);

        // Not yet joined: no job details on the date.
        var joiner = await HireAsync(company, place, g5, Today.AddDays(10));
        Assert.Equal("no_assignment_row", (await ExplainAsync(joiner, "test_leave", Today)).GetProperty("reason").GetString());
        // The first matching policy has no version in force yet: no policy, never the next rule (ruling Q5).
        var senior = await HireAsync(company, place, g5);
        var view = await ExplainAsync(senior, "test_leave", Today);
        Assert.Equal("no_version_in_force", view.GetProperty("reason").GetString());
        Assert.Equal("no_version", view.GetProperty("evaluations")[0].GetProperty("outcome").GetString());
        Assert.Equal(later, Matched(await ExplainAsync(senior, "test_leave", Today.AddDays(30))));

        // Several benefits apply at once.
        var (lunch, _) = await _h.ActivePolicyAsync(_hr, company, "test_benefit", new { amount = 500 }, from, Ct);
        var (phone, _) = await _h.ActivePolicyAsync(_hr, company, "test_benefit", new { amount = 1200.5m }, from, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_benefit", from, Ct, (lunch, 1, []), (phone, 2, [Cond("grade", "equals", [g5])]))).Status);
        var both = await ExplainAsync(senior, "test_benefit", Today);
        Assert.Equal([lunch, phone], both.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("policyId").GetGuid()).ToArray());

        // A rule on an attribute the type does not allow is refused.
        var location = await _h.ActivateSetAsync(_hr, company, "test_benefit", Today.AddDays(1), Ct, (lunch, 1, [Cond("unit", "equals", [place.OrgUnit])]));
        Assert.Equal("errors.hrm.policy.rule.attribute_not_allowed", location.Json.GetProperty("errors").GetProperty("assignments[0].rule")[0].GetString());
    }

    [Fact]
    public async Task FR_HRM_POL_02_a_policy_is_retired_only_once_nothing_assigns_it()
    {
        var (company, place) = await CompanyAsync();
        var from = Today.AddYears(-1);
        var (old, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        var (next, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 21 }, from, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (old, 1, []))).Status);
        var employee = await HireAsync(company, place);
        long Version(JsonElement p) => p.GetProperty("rowVersion").GetInt64();
        var detail = await _h.GetAsync(_hr, $"{PolicyTestSupport.Policies}/{old}/detail", Ct);
        var end = Today.AddDays(5);
        var assigned = await _h.PostAsync(_hr, $"{PolicyTestSupport.Policies}/{old}/retire", new { effectiveFrom = Iso(end), rowVersion = Version(detail.Json) }, Ct);
        Assert.Equal("errors.hrm.policy.still_assigned", assigned.Json.GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", end, Ct, (next, 1, []))).Status);
        var retired = await _h.PostAsync(_hr, $"{PolicyTestSupport.Policies}/{old}/retire", new { effectiveFrom = Iso(end), rowVersion = Version(detail.Json) }, Ct);
        Assert.True(retired.Status == HttpStatusCode.OK, retired.Body);
        Assert.False(retired.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal("retired", retired.Json.GetProperty("versions")[0].GetProperty("status").GetString());
        Assert.Equal(old, Matched(await ExplainAsync(employee, "test_leave", end.AddDays(-1))));
        Assert.Equal(next, Matched(await ExplainAsync(employee, "test_leave", end)));
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(_hr, $"{PolicyTestSupport.Policies}/{old}/versions", new { }, Ct)).Status); // retired: no new draft
    }

    [Fact]
    public async Task FR_HRM_POL_03_resolution_is_deterministic_the_same_answer_1000_times()
    {
        var (company, place) = await CompanyAsync();
        var g5 = await GradeAsync(company, "G5", 5);
        var from = Today.AddYears(-1);
        var (a, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 30 }, from, Ct);
        var (b, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (b, 7, []), (a, 3, [Cond("grade", "in", [g5])]))).Status);
        var employee = await HireAsync(company, place, g5);
        var path = $"/api/hrm/policy-resolution?employmentId={employee}&policyType=test_leave&date={Iso(Today)}";
        var first = (await _h.GetAsync(_hr, path, Ct)).Body;
        Assert.Contains(a.ToString(), first, StringComparison.Ordinal);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(first, (await _h.GetAsync(_hr, path, Ct)).Body);
        }
    }

    // ------------------------------------------------------------------ FR-XM-32 maker-checker, permissions and scopes

    [Fact]
    public async Task FR_XM_32_with_approval_on_an_activation_waits_for_a_second_person_and_the_requester_cannot_approve_it()
    {
        var (company, place) = await CompanyAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var on = await _h.PutAsync(admin, "/api/cfg/settings/hrm.policies.requires_approval", new { layer = "company", scopeId = company, value = true, reason = "test" }, Ct);
        Assert.True(on.Status == HttpStatusCode.Accepted, on.Body); // switching it is held too
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{on.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);

        var created = await _h.PostAsync(_hr, PolicyTestSupport.Policies, new { companyId = company, policyType = "test_leave", code = "HELD" + Guid.NewGuid().ToString("N")[..6], name = new { en = "Held" }, parameters = new { days = 12 } }, Ct);
        var draft = created.Json.GetProperty("versions")[0];
        var held = await _h.PostAsync(_hr, $"/api/hrm/policy-versions/{draft.GetProperty("id").GetGuid()}/activate", new { effectiveFrom = Iso(Today.AddYears(-1)), rowVersion = draft.GetProperty("rowVersion").GetInt64(), reason = "new year" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal("draft", await _h.ScalarAsync<string>("select status from hrm.policy_versions where id = $1", Ct, draft.GetProperty("id").GetGuid()));
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(_hr, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct)).Status);
        var otherHr = await _h.UserAsync(Ct, null, "hr_admin");
        var approved = await _h.PostAsync(otherHr, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("active", await _h.ScalarAsync<string>("select status from hrm.policy_versions where id = $1", Ct, draft.GetProperty("id").GetGuid()));

        // An assignment set is held the same way and applied by the second person.
        var policy = created.Json.GetProperty("id").GetGuid();
        var set = await _h.ActivateSetAsync(_hr, company, "test_leave", Today.AddYears(-1), Ct, (policy, 1, []));
        Assert.True(set.Status == HttpStatusCode.Accepted, set.Body);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.policy_assignments where policy_id = $1", Ct, policy));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(otherHr, $"/api/iam/maker-checker-requests/{set.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "checked" }, Ct)).Status);
        Assert.Equal(policy, Matched(await ExplainAsync(await HireAsync(company, place), "test_leave", Today)));
    }

    [Fact]
    public async Task FR_HRM_POL_02_FR_KRN_IAM_12_writes_need_the_manage_permission_and_every_endpoint_keeps_to_the_data_scope()
    {
        var (company, place) = await CompanyAsync();
        var (otherCompany, _) = await CompanyAsync();
        var from = Today.AddYears(-1);
        var (policy, _) = await _h.ActivePolicyAsync(_hr, company, "test_leave", new { days = 20 }, from, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.ActivateSetAsync(_hr, company, "test_leave", from, Ct, (policy, 1, []))).Status);
        var employee = await HireAsync(company, place);

        // A reader sees policies and the explanation but changes nothing.
        var reader = await _h.UserAsync(Ct);
        var readRole = await _h.RoleAsync(Ct, ["hrm.policies.read", "hrm.employments.read"], [("hrm.policies", "all"), ("hrm.employments", "all")]);
        await _h.AssignAsync(reader, readRole, null, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(reader, $"{PolicyTestSupport.Policies}/{policy}/detail", Ct)).Status);
        Assert.Equal(policy, Matched(await ExplainAsync(employee, "test_leave", Today, reader)));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, PolicyTestSupport.Policies, new { companyId = company, policyType = "test_leave", code = "NOPE", name = new { en = "No" }, parameters = new { days = 1 } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, $"{Sets}/activate", new { companyId = company, policyType = "test_leave", effectiveFrom = Iso(Today), assignments = Array.Empty<object>() }, Ct)).Status);

        // A manager of the other company only: the first company's policy, set and employee are not found.
        var outsider = await _h.UserAsync(Ct);
        var scoped = await _h.RoleAsync(Ct, ["hrm.policies.read", "hrm.policies.manage", "hrm.employments.read"], [("hrm.policies", "all"), ("hrm.employments", "all")]);
        await _h.AssignAsync(outsider, scoped, otherCompany, Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"{PolicyTestSupport.Policies}/{policy}/detail", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"{Sets}?companyId={company}&policyType=test_leave", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"/api/hrm/policy-resolution?employmentId={employee}&policyType=test_leave", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(outsider, $"{PolicyTestSupport.Policies}/{policy}/versions", new { }, Ct)).Status);
        Assert.NotEqual(HttpStatusCode.Created, (await _h.PostAsync(outsider, PolicyTestSupport.Policies, new { companyId = company, policyType = "test_leave", code = "OUT", name = new { en = "Out" }, parameters = new { days = 1 } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(outsider, $"{Sets}?companyId={otherCompany}&policyType=test_leave", Ct)).Status);

        // The generated writes of the three tables are closed.
        Assert.Contains((await _h.PostAsync(_hr, "/api/hrm/policy-assignments", new { companyId = company, policyId = policy, precedence = 9, rule = new { }, validFrom = Iso(Today) }, Ct)).Status,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    // ------------------------------------------------------------------ FR-HRM-EMP-10: probation by policy (ruling Q3)

    [Fact]
    public async Task FR_HRM_EMP_10_Q3_probation_comes_from_the_probation_policy_its_months_types_extension_limit_and_reviewer()
    {
        var (company, place) = await CompanyAsync();
        var hr = _hr;
        // No probation policy: no probation (the resolver never answers a default; ruling Q5).
        var none = await HireAsync(company, place, joining: Today.AddDays(-20));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.probation_records where employment_id = $1", Ct, none));

        await _h.ProbationAsync(hr, company, Ct, types: ["permanent"], extra: new { months = 6, extensionMonthsMax = 2, reviewer = "hr" });
        var joining = Today.AddDays(-170);
        var employee = await HireAsync(company, place, joining: joining);
        var contractor = await HireAsync(company, place, joining: joining, type: "contract");
        Assert.Equal($"in_progress|{Iso(joining)}|{Iso(joining.AddMonths(6).AddDays(-1))}",
            await _h.ScalarAsync<string>("select status || '|' || start_date || '|' || end_date from hrm.probation_records where employment_id = $1", Ct, employee));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.probation_records where employment_id = $1", Ct, contractor));

        // An extension is limited to two months after the original last day.
        var end = joining.AddMonths(6).AddDays(-1);
        Task<long> Version() => _h.ScalarAsync<long>("select row_version from hrm.probation_records where employment_id = $1", Ct, employee);
        var tooLong = await _h.PostAsync(hr, $"{Employments}/{employee}/probation/extend", new { extendedTo = Iso(end.AddDays(1).AddMonths(2)), rowVersion = await Version() }, Ct);
        Assert.Equal("errors.hrm.probation.extension_limit", tooLong.Json.GetProperty("errors").GetProperty("extendedTo")[0].GetString());
        var extended = await _h.PostAsync(hr, $"{Employments}/{employee}/probation/extend", new { extendedTo = Iso(end.AddDays(1).AddMonths(2).AddDays(-1)), rowVersion = await Version() }, Ct);
        Assert.True(extended.Status == HttpStatusCode.OK, extended.Body);

        // The reviewer is HR alone: the reminder names no manager even when the employee has one with a user.
        var manager = await HireAsync(company, place, joining: Today.AddYears(-3), type: "contract");
        var managerUser = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(hr, $"{Employments}/{manager}/user", new { userId = managerUser, rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, manager) }, Ct)).Status);
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var reviewedJoining = Today.AddMonths(-6).AddDays(5);
        var reviewed = await _h.PostAsync(hr, Employments, new
        {
            personId = person, companyId = company, employeeType = "permanent", joiningDate = Iso(reviewedJoining),
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer", managerEmploymentId = manager },
        }, Ct);
        Assert.True(reviewed.Status == HttpStatusCode.Created, reviewed.Body);
        var record = await _h.ScalarAsync<Guid>("select id from hrm.probation_records where employment_id = $1", Ct, reviewed.Json.GetProperty("id").GetGuid());
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var worker = rig.Worker();
        await rig.QueueAsync("hrm.employment_terms_due", ct: Ct);
        await rig.RunUntilIdleAsync(worker, Ct);
        Assert.Equal("null", await _h.ScalarAsync<string>("select coalesce(payload->>'manager_user_id', 'null') from hrm.outbox_messages where event_type = 'hrm.probation_ending' and aggregate_id = $1", Ct, record));
    }
}
