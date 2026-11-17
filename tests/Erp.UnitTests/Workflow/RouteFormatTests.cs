using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Workflow;

namespace Erp.UnitTests.Workflow;

/// <summary>
/// The route format, conditions and document-type manifest of slice S1.10a (build items 1 to 4; rulings Q2 to Q5, Q10,
/// R2): FR-KRN-WFL-01, FR-KRN-WFL-02, FR-KRN-WFL-03, FR-XM-32.
/// </summary>
public sealed partial class RouteFormatTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static readonly Dictionary<string, RouteField> Fields = new(StringComparer.Ordinal)
    {
        ["amount"] = new("amount", FieldTypes.Money, []),
        ["days"] = new("days", FieldTypes.Number, []),
        ["start"] = new("start", FieldTypes.Date, []),
        ["note"] = new("note", FieldTypes.Text, []),
        ["category"] = new("category", FieldTypes.Choice, ["travel", "meals", "other"]),
        ["urgent"] = new("urgent", FieldTypes.Bool, []),
        ["company"] = new("company", FieldTypes.Company, []),
        ["unit"] = new("unit", FieldTypes.OrgUnit, []),
        ["partner"] = new("partner", FieldTypes.User, []),
    };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (Route? Route, IReadOnlyList<RouteProblem> Problems) Parse(string json, bool bulk = true) => RouteFormat.Parse(J(json), Fields, bulk);

    private const string Valid = """
        {"steps": [
          {"key": "head", "name": {"en": "Department head", "ur": "شعبے کا سربراہ"}, "condition": {"field": "amount", "op": "gt", "value": "1000.00"},
           "approver": {"kind": "department_head"}, "fallback": {"kind": "role", "role": "hr_admin"}, "time_limit_working_days": 2},
          {"key": "finance", "name": {"en": "Finance", "ur": "مالیات"}, "approver": {"kind": "role", "role": "finance"},
           "escalation": {"kind": "user", "user_id": "019a8a00-0000-7000-8000-0000000000f1"}, "bulk_approval": true},
          {"key": "partner", "name": {"en": "Partner", "ur": "پارٹنر"}, "approver": {"kind": "document_user", "field": "partner"},
           "condition": {"any": [{"field": "category", "op": "in", "value": ["travel", "meals"]}, {"all": [{"field": "urgent", "op": "eq", "value": true}, {"field": "note", "op": "not_empty"}]}]}},
          {"key": "boss", "name": {"en": "Manager's manager", "ur": "مینیجر کا مینیجر"}, "approver": {"kind": "manager", "levels": 2}}
        ]}
        """;

    // ------------------------------------------------------------------ route format (FR-KRN-WFL-01, 02)

    [Fact]
    public void FR_KRN_WFL_01_FR_KRN_WFL_02_a_valid_route_is_read_with_every_approver_kind_and_written_back_in_one_canonical_shape()
    {
        var (route, problems) = Parse(Valid);
        Assert.Empty(problems);
        Assert.NotNull(route);
        Assert.Equal(["head", "finance", "partner", "boss"], route.Steps.Select(s => s.Key));
        Assert.Equal(ApproverKinds.DepartmentHead, route.Steps[0].Approver.Kind);
        Assert.Equal("hr_admin", route.Steps[0].Fallback!.Role);
        Assert.Equal(2, route.Steps[0].TimeLimitWorkingDays);
        Assert.True(route.Steps[1].BulkApproval);
        Assert.Equal("partner", route.Steps[2].Approver.Field);
        Assert.Equal(2, route.Steps[3].Approver.Levels);

        var stored = RouteFormat.Serialize(route);
        var (again, none) = RouteFormat.Parse(J(stored), Fields, true);
        Assert.Empty(none);
        Assert.Equal(stored, RouteFormat.Serialize(again!));
    }

    [Theory]
    [InlineData("""{"steps": []}""", "steps", "errors.workflow.route_step_count")]
    [InlineData("""{"stages": []}""", "route", "errors.workflow.route_format")]
    [InlineData("""{"steps": [{"key": "A b", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}}]}""", "steps[0].key", "errors.workflow.step_key_invalid")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x"}, "approver": {"kind": "role", "role": "r"}}]}""", "steps[0].name", "errors.workflow.step_name_required")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "expression"}}]}""", "steps[0].approver.kind", "errors.workflow.approver_kind")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "document_user", "field": "note"}}]}""", "steps[0].approver.field", "errors.workflow.approver_field_not_user")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "fallback": {"kind": "manager"}}]}""", "steps[0].fallback.kind", "errors.workflow.approver_kind")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "manager", "levels": 0}}]}""", "steps[0].approver.levels", "errors.workflow.approver_levels")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "time_limit_working_days": 0}]}""", "steps[0].time_limit_working_days", "errors.workflow.time_limit_invalid")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "parallel": true}]}""", "steps[0].parallel", "errors.workflow.route_unknown_property")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}}, {"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}}]}""", "steps[1].key", "errors.workflow.step_key_duplicate")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "salary", "op": "gt", "value": 1}}]}""", "steps[0].condition.field", "errors.workflow.condition_unknown_field")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "note", "op": "gt", "value": "a"}}]}""", "steps[0].condition.op", "errors.workflow.condition_operator_type")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "category", "op": "eq", "value": "cars"}}]}""", "steps[0].condition.value", "errors.workflow.condition_value")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "amount", "op": "gt", "value": "1,500"}}]}""", "steps[0].condition.value", "errors.workflow.condition_value")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "start", "op": "ge", "value": "17/11/2026"}}]}""", "steps[0].condition.value", "errors.workflow.condition_value")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "note", "op": "empty", "value": "x"}}]}""", "steps[0].condition.value", "errors.workflow.condition_value")]
    [InlineData("""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"all": [{"any": [{"all": [{"field": "urgent", "op": "eq", "value": true}]}]}]}}]}""", "steps[0].condition.all[0].any[0]", "errors.workflow.condition_too_deep")]
    public void FR_KRN_WFL_01_FR_KRN_WFL_03_a_wrong_route_is_refused_with_where_and_why(string json, string path, string error)
    {
        var (route, problems) = Parse(json);
        Assert.Null(route);
        Assert.Contains(problems, p => p.Path == path && p.ErrorKey == error);
    }

    [Fact]
    public void FR_XM_32_S110a_R2_a_route_whose_every_step_has_a_condition_is_refused_so_every_request_reaches_someone()
    {
        var (route, problems) = Parse("""
            {"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "days", "op": "gt", "value": 3}}]}
            """);
        Assert.Null(route);
        Assert.Contains(problems, p => p.ErrorKey == "errors.workflow.route_needs_unconditional_step");
    }

    [Fact]
    public void FR_KRN_WFL_08_bulk_approval_on_a_step_is_refused_when_the_document_type_does_not_allow_it()
    {
        var (_, problems) = Parse(Valid, bulk: false);
        Assert.Contains(problems, p => p.Path == "steps[1].bulk_approval" && p.ErrorKey == "errors.workflow.bulk_not_allowed");
    }

    // ------------------------------------------------------------------ conditions (FR-KRN-WFL-03, ruling Q5)

    private static bool Eval(string condition, string values)
    {
        var (route, problems) = Parse($$$"""{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {{{condition}}}}, {"key": "b", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}}]}""");
        Assert.Empty(problems);
        var dict = J(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return ConditionEvaluator.Evaluate(route!.Steps[0].Condition!, dict, Fields);
    }

    [Theory]
    [InlineData("""{"field": "amount", "op": "gt", "value": "1000.00"}""", """{"amount": "1000.0001"}""", true)]
    [InlineData("""{"field": "amount", "op": "gt", "value": "1000.00"}""", """{"amount": 1000}""", false)]
    [InlineData("""{"field": "amount", "op": "ge", "value": 1000}""", """{"amount": "1000.0000"}""", true)]
    [InlineData("""{"field": "amount", "op": "lt", "value": "0.1"}""", """{"amount": "0.0999999999"}""", true)]
    [InlineData("""{"field": "days", "op": "le", "value": 3}""", """{"days": 3.5}""", false)]
    [InlineData("""{"field": "days", "op": "in", "value": [1, 2, 3]}""", """{"days": "2"}""", true)]
    [InlineData("""{"field": "start", "op": "lt", "value": "2026-12-01"}""", """{"start": "2026-11-30"}""", true)]
    [InlineData("""{"field": "start", "op": "eq", "value": "2026-12-01"}""", """{"start": "2026-12-02"}""", false)]
    [InlineData("""{"field": "category", "op": "not_in", "value": ["travel"]}""", """{"category": "meals"}""", true)]
    [InlineData("""{"field": "category", "op": "eq", "value": "travel"}""", """{"category": "Travel"}""", false)]
    [InlineData("""{"field": "urgent", "op": "eq", "value": true}""", """{"urgent": true}""", true)]
    [InlineData("""{"field": "company", "op": "eq", "value": "019a8a00-0000-7000-8000-0000000000aa"}""", """{"company": "019A8A00-0000-7000-8000-0000000000AA"}""", true)]
    [InlineData("""{"field": "note", "op": "empty"}""", """{"note": ""}""", true)]
    [InlineData("""{"field": "note", "op": "not_empty"}""", """{}""", false)]
    [InlineData("""{"field": "amount", "op": "gt", "value": 1}""", """{}""", false)]
    [InlineData("""{"field": "amount", "op": "ne", "value": 1}""", """{}""", true)]
    [InlineData("""{"all": [{"field": "urgent", "op": "eq", "value": true}, {"field": "days", "op": "gt", "value": 3}]}""", """{"urgent": true, "days": 2}""", false)]
    [InlineData("""{"any": [{"field": "urgent", "op": "eq", "value": true}, {"field": "days", "op": "gt", "value": 3}]}""", """{"urgent": false, "days": 4}""", true)]
    public void FR_KRN_WFL_03_conditions_compare_exact_decimals_dates_choices_ids_and_empty_values(string condition, string values, bool expected) =>
        Assert.Equal(expected, Eval(condition, values));

    [Fact]
    public void FR_KRN_WFL_03_values_sent_by_a_module_must_be_declared_fields_of_the_right_type()
    {
        var values = J("""{"amount": "12.5", "start": "2026-13-01", "salary": 1, "partner": "not-a-guid", "note": null}""")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var problems = RouteFormat.CheckValues(values, Fields);
        Assert.Equal(["values.partner: errors.workflow.value_type", "values.salary: errors.workflow.value_unknown_field", "values.start: errors.workflow.value_type"],
            problems.Select(p => p.ToString()).Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ manifest (build item 1, ruling Q11)

    private static string Manifest(string typeOverrides = "", string route = """{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}}]}""") => $$$"""
        {"module": "test", "document_types": [{
          "code": "test.expense", "entitlement_key": "platform.base", "name": {"en": "Expense", "ur": "اخراجات"},
          "fields": [
            {"name": "company", "type": "company", "label": {"en": "Company", "ur": "کمپنی"}},
            {"name": "employee", "type": "user", "label": {"en": "Employee", "ur": "ملازم"}},
            {"name": "amount", "type": "money", "label": {"en": "Amount", "ur": "رقم"}}
          ],
          "company_field": "company", "subject_fields": ["employee"],
          "title": {"en": "Expense of {amount}", "ur": "اخراجات {amount}"}, "link": "/test/expenses/{document_id}",
          "bulk_approval": false, "requires_approval": false, "default_route": {{{route}}}{{{typeOverrides}}}
        }]}
        """;

    [Fact]
    public void FR_KRN_WFL_01_a_valid_manifest_registers_its_document_type_and_fields()
    {
        var catalog = new WorkflowCatalog(WorkflowManifest.Parse(Manifest()));
        var type = Assert.Single(catalog.All);
        Assert.Equal("test", type.Module);
        Assert.Equal(["company", "employee", "amount"], type.Fields.Select(f => f.Name));
        Assert.Equal(FieldTypes.Money, catalog.Fields("test.expense")["amount"].Type);
        Assert.Equal("Expense of 12.50", WorkflowCatalog.Render(type.Title["en"], Guid.Empty, new Dictionary<string, JsonElement> { ["amount"] = J("\"12.50\"") }));
    }

    [Theory]
    [InlineData(""", "company_field": "employee" """, "company_field must name a field of type company")]
    [InlineData(""", "subject_fields": ["amount"] """, "subject field 'amount' must name a field of type user")]
    [InlineData(""", "title": {"en": "x {nothing}", "ur": "ی"} """, "placeholder {nothing} names no field")]
    [InlineData(""", "name": {"en": "Expense"} """, "name needs English and Urdu text")]
    [InlineData(""", "code": "other.expense" """, "code must be <module>.<name>")]
    public void FR_KRN_WFL_01_a_wrong_manifest_stops_the_host(string overrides, string problem)
    {
        // Later keys of a JSON object replace earlier ones, so the overrides win.
        var ex = Assert.Throws<InvalidDataException>(() => new WorkflowCatalog(WorkflowManifest.Parse(Manifest(overrides))));
        Assert.Contains(problem, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR_XM_32_a_default_route_without_an_unconditional_step_stops_the_host()
    {
        var route = """{"steps": [{"key": "a", "name": {"en": "x", "ur": "ی"}, "approver": {"kind": "role", "role": "r"}, "condition": {"field": "amount", "op": "gt", "value": 1}}]}""";
        var ex = Assert.Throws<InvalidDataException>(() => new WorkflowCatalog(WorkflowManifest.Parse(Manifest(route: route))));
        Assert.Contains("errors.workflow.route_needs_unconditional_step", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR_KRN_WFL_02_Q4_without_the_HR_module_the_kernel_directory_answers_no_manager_department_head_or_position()
    {
        var directory = new NoApproverDirectory();
        Assert.All(ApproverKinds.Directory, k => Assert.False(directory.Supports(k)));
    }

    // ------------------------------------------------------------------ texts (CLAUDE.md localization)

    [GeneratedRegex("\"(errors\\.workflow\\.[a-z_]+)\"")]
    private static partial Regex WorkflowKey();

    [Fact]
    public void S110a_every_workflow_message_key_has_english_and_urdu_text()
    {
        var folder = Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Workflow/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "workflow.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "workflow.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));

        var used = Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => WorkflowKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Concat(WorkflowPermissions.All.Select(p => p.DescriptionKey))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(used.Count > 40, $"only {used.Count} keys found");
        Assert.DoesNotContain(used, k => !en.ContainsKey(k));
    }
}
