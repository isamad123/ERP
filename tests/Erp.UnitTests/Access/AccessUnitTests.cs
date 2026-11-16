#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.CodeGen;
using Erp.CodeGen.Design;
using Erp.CodeGen.Emit;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Access;

namespace Erp.UnitTests.Access;

/// <summary>Slice S1.06: the rules of the access engine, field security and the generator's access keys, without a database.</summary>
public sealed partial class AccessUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);
    private static readonly Lazy<DesignModel> Model = new(() => Generator.LoadModel(Root));

    private static readonly FieldCatalog Pay = new("hrm.pay_results",
    [
        new("id", "Id", null),
        new("net", "Net", "salary"),
        new("status", "Status", null),
        new("custom", "Custom", null, IsCustom: true),
    ]);

    // ------------------------------------------------------------------ field security (FR-KRN-IAM-13, GHR-06, Q5)

    [Fact]
    public void FR_KRN_IAM_13_Q5_masking_shows_only_the_last_four_characters()
    {
        Assert.Equal("****7890", FieldShaping.Mask(JsonValue.Create("PK1234567890"))!.GetValue<string>());
        Assert.Equal("****", FieldShaping.Mask(JsonValue.Create("abc"))!.GetValue<string>());
        Assert.Equal("****5000", FieldShaping.Mask(JsonValue.Create(125000.0000m))!.GetValue<string>());
        Assert.Null(FieldShaping.Mask(null));
    }

    [Fact]
    public void FR_KRN_IAM_13_FR_KRN_MET_02_shaping_leaves_hidden_fields_out_and_masks_masked_ones_including_custom_keys()
    {
        var map = new FieldAccessMap(new Dictionary<string, FieldAccessLevel>
        {
            ["id"] = FieldAccessLevel.ReadOnly,
            ["net"] = FieldAccessLevel.Hidden,
            ["status"] = FieldAccessLevel.Masked,
            ["custom"] = FieldAccessLevel.Editable,
            ["custom.secret"] = FieldAccessLevel.Hidden,
            ["custom.code"] = FieldAccessLevel.Masked,
        });
        var answer = JsonNode.Parse("""{"id":"x","net":1000.5,"status":"approved","custom":{"secret":"s","code":"ABC12345","free":"f"}}""")!.AsObject();
        var shaped = FieldShaping.Shape(answer, Pay, map);
        Assert.False(shaped.ContainsKey("net"));
        Assert.Equal("****oved", (string?)shaped["status"]);
        var custom = shaped["custom"]!.AsObject();
        Assert.False(custom.ContainsKey("secret"));
        Assert.Equal("****2345", (string?)custom["code"]);
        Assert.Equal("f", (string?)custom["free"]);
        Assert.Equal(FieldAccessLevel.Hidden, map.Of("unknown_field")); // a key the catalogue does not know is hidden
    }

    [Fact]
    public void FR_KRN_MET_02_a_write_keeps_custom_values_the_caller_may_not_edit_and_reports_changes_to_them()
    {
        var map = new FieldAccessMap(new Dictionary<string, FieldAccessLevel> { ["custom"] = FieldAccessLevel.Editable, ["custom.locked"] = FieldAccessLevel.ReadOnly, ["custom.secret"] = FieldAccessLevel.Hidden });
        using var stored = JsonDocument.Parse("""{"locked":"L","secret":"S","free":"F"}""");
        var denied = new List<string>();
        using var kept = FieldShaping.MergeCustom(JsonDocument.Parse("""{"locked":"L","free":"G"}""").RootElement, stored, map, denied);
        Assert.Empty(denied);
        Assert.Equal("""{"locked":"L","secret":"S","free":"G"}""", kept.RootElement.GetRawText());

        using var changed = FieldShaping.MergeCustom(JsonDocument.Parse("""{"locked":"X","secret":"Y"}""").RootElement, stored, map, denied);
        Assert.Equal(["custom.locked", "custom.secret"], denied);
    }

    [Fact]
    public void FR_KRN_IAM_13_a_field_the_caller_may_not_edit_is_unchanged_only_when_not_sent_or_sent_as_stored()
    {
        Assert.True(FieldShaping.Unchanged(null, 5m, update: true));
        Assert.True(FieldShaping.Unchanged(5m, 5m, update: true));
        Assert.False(FieldShaping.Unchanged(6m, 5m, update: true));
        Assert.False(FieldShaping.Unchanged(5m, 5m, update: false)); // on create any value is a change
        using var doc = JsonDocument.Parse("""{"a":1}""");
        Assert.True(FieldShaping.Same(JsonDocument.Parse("""{ "a" : 1 }""").RootElement, doc));
        Assert.True(FieldShaping.Same(new[] { "x", "y" }, new[] { "x", "y" }));
        Assert.True(FieldShaping.Same("10.0.0.1", System.Net.IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void GHR_06_Q4_salary_is_hidden_unless_a_rule_opens_it_and_the_most_open_role_wins()
    {
        var plain = new RoleRules();
        var auditor = new RoleRules();
        auditor.Fields[("*", "class:salary")] = FieldAccessLevel.ReadOnly;
        var officer = new RoleRules();
        officer.Fields[("hrm.pay_results", "net")] = FieldAccessLevel.Editable;
        var hider = new RoleRules();
        hider.Fields[("hrm.pay_results", "status")] = FieldAccessLevel.Hidden;
        hider.Fields[("hrm.pay_results", "custom.secret")] = FieldAccessLevel.Hidden;

        Assert.Equal(FieldAccessLevel.Hidden, plain.FieldLevel("hrm.pay_results", "net", "salary"));
        Assert.Equal(FieldAccessLevel.Editable, plain.FieldLevel("hrm.pay_results", "status", null));
        Assert.Equal(FieldAccessLevel.ReadOnly, auditor.FieldLevel("hrm.pay_results", "net", "salary"));

        var merged = AccessEngine.Merge(Pay, [plain, auditor, hider]);
        Assert.Equal(FieldAccessLevel.ReadOnly, merged.Of("net"));
        Assert.Equal(FieldAccessLevel.Editable, merged.Of("status")); // plain and auditor leave it editable: the most open wins
        Assert.Equal(FieldAccessLevel.Editable, merged.Of("custom.secret"));
        Assert.Equal(FieldAccessLevel.Hidden, AccessEngine.Merge(Pay, [hider]).Of("custom.secret"));
        Assert.Equal(FieldAccessLevel.Editable, AccessEngine.Merge(Pay, [officer, plain]).Of("net"));
        Assert.Equal(FieldAccessLevel.Hidden, AccessEngine.Merge(Pay, []).Of("status")); // no role: nothing visible
    }

    // ------------------------------------------------------------------ data scopes (FR-KRN-IAM-12, Q5)

    [Fact]
    public void FR_KRN_IAM_12_scope_definitions_are_read_strictly_and_never_widened_by_mistake()
    {
        var company = Guid.CreateVersion7();
        var companies = ScopeRule.Parse("companies", $$"""{"company_ids":["{{company}}"]}""");
        Assert.Equal([company], companies.Ids);
        var units = ScopeRule.Parse("org_units", $$"""{"org_unit_ids":["{{company}}"],"include_children":true}""");
        Assert.True(units.IncludeChildren);
        var conditions = ScopeRule.Parse("all", """{"conditions":[{"attribute":"status","in":["active","on_notice"]},{"attribute":"employee_type","equals":"permanent"}]}""");
        Assert.Equal(["active", "on_notice"], conditions.Conditions[0].Values);
        Assert.Equal(["permanent"], conditions.Conditions[1].Values);

        // An unknown kind or an unreadable definition matches nothing; it never becomes "all".
        Assert.Same(ScopeRule.Nothing, ScopeRule.Parse("planet", "{}"));
        Assert.Same(ScopeRule.Nothing, ScopeRule.Parse("all", "{not json"));
        Assert.Same(ScopeRule.Nothing, ScopeRule.Parse("companies", """{"company_ids":["not-a-guid"]}"""));
        Assert.Empty(ScopeRule.Nothing.Ids);
    }

    [Fact]
    public void FR_KRN_IAM_10_manifest_patterns_match_codes_exactly_or_by_star()
    {
        string[] codes = ["org.companies.read", "org.companies.update", "org.branches.read", "orgx.a.read", "hrm.persons.read"];
        Assert.Equal(["org.companies.read", "org.branches.read"], AccessSetup.Matching("org.*.read", codes));
        Assert.Equal(["org.companies.read", "org.companies.update", "org.branches.read"], AccessSetup.Matching("org.*", codes));
        Assert.Equal(["hrm.persons.read"], AccessSetup.Matching("hrm.persons.read", codes));
        Assert.Empty(AccessSetup.Matching("org.companies", codes));
    }

    [Fact]
    public void FR_KRN_IAM_14_a_held_change_survives_the_round_trip_through_the_request_payload()
    {
        var change = new AccessChange
        {
            Kind = AccessChange.SetFieldPolicies,
            RoleId = Guid.CreateVersion7(),
            Fields = [new Kernel.Application.Access.RoleFieldChange("hrm.pay_results", "net", "read_only")],
            Reason = "payroll review",
        };
        var back = AccessChange.FromJson(change.ToJson());
        Assert.Equal(change.RoleId, back.RoleId);
        Assert.Equal("net", back.Fields![0].Field);
        Assert.Equal(("iam.roles", change.RoleId!.Value), back.Subject());
    }

    [Fact]
    public void CR_024_R_008_the_Tenant_Admin_role_must_keep_Manage_Roles_and_Assign_Roles()
    {
        Assert.Equal(["iam.roles.manage", "iam.roles.assign"], ProtectedRoles.Mandatory);
        Assert.True(ProtectedRoles.IsProtected("TENANT_ADMIN"));
        Assert.True(ProtectedRoles.Keeps("tenant_admin", ["iam.roles.manage", "iam.roles.assign", "org.companies.read"]));
        Assert.False(ProtectedRoles.Keeps("tenant_admin", ["iam.roles.manage"]));
        Assert.False(ProtectedRoles.Keeps("tenant_admin", []));
        Assert.True(ProtectedRoles.Keeps("company_admin", [])); // only the protected role is bound
    }

    // ------------------------------------------------------------------ generator keys (CR-018, CR-020, CR-021)

    [Fact]
    public void CR_018_scope_and_field_class_keys_reach_the_model()
    {
        var assignments = Model.Value.Table("hrm.employment_assignments");
        Assert.Equal("employment_id", assignments.ScopeLocators["employment"]);
        Assert.Equal("org_unit_id", assignments.ScopeLocators["org_unit"]);
        Assert.Equal("branch_id", assignments.ScopeLocators["branch"]);
        Assert.Equal("company_id", assignments.ScopeLocators["company"]);
        Assert.Equal("id", Model.Value.Table("hrm.employments").ScopeLocators["employment"]);
        Assert.Equal(["status", "employee_type"], Model.Value.Table("hrm.employments").ScopeAttributes);
        Assert.Empty(Model.Value.Table("ref.currencies").ScopeLocators); // no locating column: the permission alone decides
        Assert.Equal("salary", Model.Value.Table("hrm.pay_results").Column("net").FieldClass);
        Assert.Equal("salary", Model.Value.Table("hrm.compensation_assignments").Column("gross_amount").FieldClass);
        Assert.Null(Model.Value.Table("hrm.pay_runs").Column("total_gross").FieldClass); // ruling B: totals are not salary
        Assert.Null(Model.Value.Table("hrm.salary_bands").Column("midpoint").FieldClass);
    }

    [Fact]
    public void CR_020_CR_021_closed_tables_get_read_endpoints_only_and_their_permissions_are_not_registered()
    {
        foreach (var name in new[] { "iam.permissions", "iam.user_roles", "iam.maker_checker_requests", "iam.field_policies", "iam.roles", "iam.user_groups", "iam.user_group_roles" })
        {
            Assert.Equal(["read"], CSharpEmitter.Actions(Model.Value.Table(name)));
        }

        Assert.Equal(["read", "create", "update"], CSharpEmitter.Actions(Model.Value.Table("iam.users")));
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Platform/Erp.Kernel.Infrastructure/Access/permissions.g.json")));
        var codes = manifest.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains("iam.user_roles.read", codes);
        Assert.DoesNotContain("iam.user_roles.create", codes);
        Assert.DoesNotContain("iam.users.delete", codes);
    }

    [Fact]
    public void CR_018_the_generator_refuses_an_unknown_scope_dimension_or_field_class()
    {
        foreach (var (key, problem) in new[] { ("scope: {planet: id}", "unknown scope dimension"), ("field_class: {code: secret}", "unknown field class"), ("api_actions: [create]", "api_actions must list read") })
        {
            var folder = Directory.CreateTempSubdirectory("erp-s106-design-").FullName;
            try
            {
                foreach (var file in Directory.GetFiles(Path.Combine(Root, "docs", "data-design"), "*.yaml"))
                {
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                }

                File.AppendAllText(Path.Combine(folder, "kernel.yaml"), $"\n- table: iam.s106_probe\n  traits: [std, name]\n  {key}\n  reqs: [KRN-IAM-12]\n");
                var error = Assert.Throws<DesignException>(() => DesignLoader.Load(folder, Path.Combine(Root, "tools", "Erp.CodeGen")));
                Assert.Contains(problem, error.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------ messages (English and Urdu)

    [Fact]
    public void S106_every_access_message_key_has_english_and_urdu_text()
    {
        var folder = Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Access/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "access.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "access.ur.json")))!;
        var identity = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Identity/Localization/identity.en.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));

        var sources = Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Access{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || f.EndsWith("FieldSecurity.cs", StringComparison.Ordinal) || f.EndsWith("AccessAbstractions.cs", StringComparison.Ordinal));
        var used = sources.SelectMany(f => MessageKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Concat(AccessControlPermissions.All.Select(p => p.DescriptionKey))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.DoesNotContain(used, k => !en.ContainsKey(k) && !identity.ContainsKey(k));
    }

    [GeneratedRegex("\"(errors\\.[a-z_.]+)\"")]
    private static partial Regex MessageKey();
}
