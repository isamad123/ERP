using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Policies;

namespace Erp.UnitTests.Policies;

/// <summary>
/// The pure parts of the policy framework (slice S1.23a): the JSON Schema subset of parameters (Build item 1, ruling Q2), the type
/// catalogue, assignment rules and their evaluation (Build item 3, ruling Q4) and the decision of the resolver (Build item 4, ruling Q5).
/// </summary>
public sealed partial class PolicyUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement Schema = J("""
        { "type": "object", "additionalProperties": false, "required": ["days"],
          "properties": { "days": { "type": "integer", "minimum": 0, "maximum": 365 }, "note": { "type": "string", "minLength": 2, "maxLength": 5 },
            "kind": { "type": "string", "enum": ["paid", "unpaid"] }, "on": { "type": "boolean" }, "rate": { "type": "number", "minimum": 0 },
            "bands": { "type": "array", "minItems": 1, "maxItems": 2, "items": { "type": "object", "required": ["d"], "properties": { "d": { "type": "integer" } } } } } }
        """);

    [Theory]
    [InlineData("""{"days":10}""", null, null)]
    [InlineData("""{"days":10,"note":"ab","kind":"paid","on":true,"rate":1.25,"bands":[{"d":1}]}""", null, null)]
    [InlineData("""{}""", "parameters.days", PolicySchema.Required)]
    [InlineData("""{"days":null}""", "parameters.days", PolicySchema.Required)]
    [InlineData("""{"days":"10"}""", "parameters.days", PolicySchema.WrongType)]
    [InlineData("""{"days":1.5}""", "parameters.days", PolicySchema.WrongType)]
    [InlineData("""{"days":-1}""", "parameters.days", PolicySchema.BelowMinimum)]
    [InlineData("""{"days":366}""", "parameters.days", PolicySchema.AboveMaximum)]
    [InlineData("""{"days":1,"x":1}""", "parameters.x", PolicySchema.Unknown)]
    [InlineData("""{"days":1,"kind":"half"}""", "parameters.kind", PolicySchema.NotAllowed)]
    [InlineData("""{"days":1,"note":"a"}""", "parameters.note", PolicySchema.TooShort)]
    [InlineData("""{"days":1,"note":"abcdef"}""", "parameters.note", PolicySchema.TooLong)]
    [InlineData("""{"days":1,"on":"yes"}""", "parameters.on", PolicySchema.WrongType)]
    [InlineData("""{"days":1,"bands":[]}""", "parameters.bands", PolicySchema.TooFew)]
    [InlineData("""{"days":1,"bands":[{"d":1},{"d":2},{"d":3}]}""", "parameters.bands", PolicySchema.TooMany)]
    [InlineData("""{"days":1,"bands":[{"d":1},{}]}""", "parameters.bands[1].d", PolicySchema.Required)]
    [InlineData("""{"days":1,"kind":null}""", "parameters.kind", PolicySchema.WrongType)]
    public void FR_HRM_POL_04_parameters_are_checked_against_the_schema_subset(string value, string? path, string? key)
    {
        var errors = PolicySchema.Check(Schema, J(value));
        if (path is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Contains(key!, errors[path]);
        }
    }

    [Theory]
    [InlineData("""{"type":"object","pattern":"x"}""")]
    [InlineData("""{"properties":{}}""")]
    [InlineData("""{"type":"object","additionalProperties":{"type":"string"}}""")]
    [InlineData("""{"type":"object","required":["a"]}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"date"}}}""")]
    [InlineData("""{"type":"integer","minimum":"3"}""")]
    [InlineData("""{"type":"string","maxLength":-1}""")]
    [InlineData("""{"type":"string","enum":"paid"}""")]
    [InlineData("""{"type":"string","enum":[]}""")]
    [InlineData("""{"type":"string","title":1}""")]
    public void FR_HRM_POL_04_a_schema_using_anything_the_checker_does_not_check_is_refused_at_load(string schema) =>
        Assert.Throws<InvalidOperationException>(() => PolicySchema.EnsureSupported(J(schema), "test"));

    private static string Type(string code = "t", string cardinality = "one", string attributes = "\"grade\"", string schema = """{"type":"object"}""") =>
        $$"""{"module":"x","types":[{"code":"{{code}}","name":{"en":"T","ur":"ٹی"},"cardinality":"{{cardinality}}","attributes":[{{attributes}}],"schema":{{schema}}}]}""";

    [Fact]
    public void FR_HRM_POL_01_the_HR_manifest_loads_and_a_bad_manifest_stops_the_host()
    {
        var hr = new PolicyTypeCatalog([new PolicyTypeSource(PolicyServices.TypeManifest)]);
        Assert.Equal(["attendance", "bonus", "leave", "notice_period", "probation", "shift"], hr.All.Select(t => t.Code)); // attendance: S1.29a, bonus: S1.34b, leave: S1.32a
        Assert.True(hr.Find("notice_period")!.Required);
        Assert.False(hr.Find("probation")!.IsMany);
        Assert.Throws<InvalidOperationException>(() => new PolicyTypeCatalog([new PolicyTypeSource(Type()), new PolicyTypeSource(Type())])); // twice
        Assert.Throws<InvalidOperationException>(() => new PolicyTypeCatalog([new PolicyTypeSource(Type(code: "Bad-Code"))]));
        Assert.Throws<InvalidOperationException>(() => new PolicyTypeCatalog([new PolicyTypeSource(Type(cardinality: "some"))]));
        Assert.Throws<InvalidOperationException>(() => new PolicyTypeCatalog([new PolicyTypeSource(Type(attributes: "\"salary\""))]));
        Assert.Throws<InvalidOperationException>(() => new PolicyTypeCatalog([new PolicyTypeSource(Type(schema: """{"type":"array"}"""))]));
    }

    [Fact]
    public void AS_4_R_002_the_HR_policy_types_carry_no_statutory_values_and_every_text_exists_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(PolicyServices.TypeManifest);
        // No defaults at all: every value is the customer's choice (AS-4, R-002).
        Assert.DoesNotContain("\"default\"", PolicyServices.TypeManifest, StringComparison.Ordinal);
        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Policies/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "policies.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "policies.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(Titles(manifest.RootElement), t => Assert.True(en.ContainsKey(t), t));
        Assert.All(manifest.RootElement.GetProperty("types").EnumerateArray(), t => Assert.True(en.ContainsKey("hrm.policy.type." + t.GetProperty("code").GetString()), t.ToString()));
        Assert.All(PolicyPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        // Every key the policy code answers with has a text.
        var sources = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => f.Contains("Policies", StringComparison.Ordinal));
        var used = sources.SelectMany(f => PolicyKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
    }

    private static IEnumerable<string> Titles(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => p.Name == "title" && p.Value.ValueKind == JsonValueKind.String ? [p.Value.GetString()!] : Titles(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Titles),
        _ => [],
    };

    [GeneratedRegex("\"(errors\\.hrm\\.policy\\.[a-z_.]+|hrm\\.policy\\.detail\\.[a-z_]+)\"")]
    private static partial Regex PolicyKey();

    // ------------------------------------------------------------------ rules (ruling Q4)

    [Theory]
    [InlineData("""{"x":1}""", PolicyRules.Shape)]
    [InlineData("""{"conditions":{}}""", PolicyRules.Shape)]
    [InlineData("""{"conditions":[{"attribute":"salary","operator":"in","values":[1]}]}""", PolicyRules.UnknownAttribute)]
    [InlineData("""{"conditions":[{"attribute":"location","operator":"in","values":["4f0c2c0e-0000-7000-8000-000000000001"]}]}""", PolicyRules.AttributeNotAllowed)]
    [InlineData("""{"conditions":[{"attribute":"grade","operator":"above","values":["4f0c2c0e-0000-7000-8000-000000000001"]}]}""", PolicyRules.UnknownOperator)]
    [InlineData("""{"conditions":[{"attribute":"grade","operator":"equals","values":["4f0c2c0e-0000-7000-8000-000000000001","4f0c2c0e-0000-7000-8000-000000000002"]}]}""", PolicyRules.ValueCount)]
    [InlineData("""{"conditions":[{"attribute":"grade","operator":"in","values":[]}]}""", PolicyRules.ValueCount)]
    [InlineData("""{"conditions":[{"attribute":"grade","operator":"in","values":["G5"]}]}""", PolicyRules.ValueShape)]
    [InlineData("""{"conditions":[{"attribute":"employee_type","operator":"in","values":["permanent","permanent"]}]}""", PolicyRules.ValueShape)]
    [InlineData("""{"conditions":[{"attribute":"custom","operator":"equals","values":[true]}]}""", PolicyRules.FieldRequired)]
    [InlineData("""{"conditions":[{"attribute":"grade","field":"x","operator":"equals","values":["4f0c2c0e-0000-7000-8000-000000000001"]}]}""", PolicyRules.FieldRequired)]
    [InlineData("""{"conditions":[{"attribute":"grade","operator":"equals","values":["4f0c2c0e-0000-7000-8000-000000000001"],"subUnits":true}]}""", PolicyRules.SubUnitsOnlyForUnit)]
    [InlineData("""{"conditions":[{"attribute":"employee_type","operator":"in","values":["intern"]},{"attribute":"employee_type","operator":"not_in","values":["contract"]}]}""", PolicyRules.Repeated)]
    public void FR_HRM_POL_02_rules_that_do_not_read_are_refused(string rule, string error)
    {
        string[] allowed = ["grade", "employee_type", "custom", "unit"];
        Assert.Equal(error, PolicyRules.Parse(J(rule), allowed).Error);
    }

    private static readonly Guid UnitA = Guid.Parse("4f0c2c0e-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitB = Guid.Parse("4f0c2c0e-0000-7000-8000-0000000000b1");
    private static readonly Guid Grade5 = Guid.Parse("4f0c2c0e-0000-7000-8000-000000000005");

    private static PolicyAttributes Who(Guid? grade = null, Guid? unit = null, string type = "permanent", string custom = "{}", bool row = true) =>
        new(Guid.Empty, Guid.Empty, type, "active", row, unit, null, null, grade, J(custom), null);

    private static PolicyRule Rule(string json) => PolicyRules.Parse(J(json), PolicyRules.Attributes).Rule!;

    [Fact]
    public void FR_HRM_POL_02_conditions_join_by_and_with_equals_in_not_in_sub_units_custom_values_and_missing_values()
    {
        var subtrees = new Dictionary<Guid, IReadOnlySet<Guid>> { [UnitA] = new HashSet<Guid> { UnitA, UnitB } };
        var both = Rule($$"""{"conditions":[{"attribute":"grade","operator":"in","values":["{{Grade5.ToString().ToUpperInvariant()}}"]},{"attribute":"employee_type","operator":"not_in","values":["intern"]}]}""");
        Assert.True(PolicyRules.Evaluate(both, Who(Grade5), subtrees).Met); // ids compare in one spelling
        Assert.False(PolicyRules.Evaluate(both, Who(Grade5, type: "intern"), subtrees).Met);
        var (met, conditions) = PolicyRules.Evaluate(both, Who(), subtrees); // no grade: not in the list
        Assert.False(met);
        Assert.Empty(conditions[0].Actual);
        Assert.True(conditions[1].Met);

        var tree = Rule($$"""{"conditions":[{"attribute":"unit","operator":"equals","values":["{{UnitA}}"],"subUnits":true}]}""");
        Assert.True(PolicyRules.Evaluate(tree, Who(unit: UnitB), subtrees).Met);
        Assert.False(PolicyRules.Evaluate(Rule($$"""{"conditions":[{"attribute":"unit","operator":"equals","values":["{{UnitA}}"]}]}"""), Who(unit: UnitB), subtrees).Met);
        var notInTree = Rule($$"""{"conditions":[{"attribute":"unit","operator":"not_in","values":["{{UnitA}}"],"subUnits":true}]}""");
        Assert.False(PolicyRules.Evaluate(notInTree, Who(unit: UnitB), subtrees).Met);

        var shift = Rule("""{"conditions":[{"attribute":"custom","field":"shift","operator":"equals","values":[true]}]}""");
        Assert.True(PolicyRules.Evaluate(shift, Who(custom: """{"shift":true}"""), subtrees).Met);
        Assert.False(PolicyRules.Evaluate(shift, Who(custom: """{"shift":false}"""), subtrees).Met);
        Assert.False(PolicyRules.Evaluate(shift, Who(), subtrees).Met);
        var level = Rule("""{"conditions":[{"attribute":"custom","field":"level","operator":"in","values":[2,3]}]}""");
        Assert.True(PolicyRules.Evaluate(level, Who(custom: """{"level":3.0}"""), subtrees).Met); // numbers by value
        Assert.True(PolicyRules.Evaluate(Rule("""{"conditions":[{"attribute":"custom","field":"level","operator":"not_in","values":[2]}]}"""), Who(), subtrees).Met);
        Assert.True(PolicyRules.Evaluate(Rule("""{"conditions":[]}"""), Who(), subtrees).Met); // a catch-all
    }

    // ------------------------------------------------------------------ the decision (ruling Q5)

    private static LoadedAssignment Assignment(int precedence, string rule, out Policy policy)
    {
        policy = new Policy { Id = Guid.CreateVersion7(), Code = "P" + precedence, NameI18n = JsonDocument.Parse("""{"en":"P"}"""), PolicyType = "t", Source = "tenant" };
        return new LoadedAssignment(new PolicyAssignment { Id = Guid.CreateVersion7(), PolicyId = policy.Id, Precedence = precedence, Rule = JsonDocument.Parse(rule) }, policy, Rule(rule));
    }

    private static PolicyVersion Version(Policy p, int days) =>
        new() { Id = Guid.CreateVersion7(), PolicyId = p.Id, Version = 1, Parameters = JsonDocument.Parse($$"""{"days":{{days}}}"""), Status = "active", ValidFrom = new DateOnly(2020, 1, 1) };

    private static PolicyType T(string cardinality) => new("t", "x", new Dictionary<string, string>(), new Dictionary<string, string>(), cardinality, false, PolicyRules.Attributes, J("""{"type":"object"}"""));

    [Fact]
    public void FR_HRM_POL_03_first_match_wins_a_match_without_a_version_is_no_policy_and_many_lists_every_match()
    {
        var day = new DateOnly(2026, 3, 1);
        var senior = Assignment(1, $$"""{"conditions":[{"attribute":"grade","operator":"in","values":["{{Grade5}}"]}]}""", out var p1);
        var all = Assignment(2, """{"conditions":[]}""", out var p2);
        var none = new Dictionary<Guid, IReadOnlySet<Guid>>();
        var set = new LoadedSet([senior, all], new Dictionary<Guid, PolicyVersion> { [p1.Id] = Version(p1, 30), [p2.Id] = Version(p2, 20) }, none);
        var top = PolicyResolver.Decide(T("one"), Who(Grade5), day, set);
        Assert.Equal(p1.Id, top.Policy!.PolicyId);
        Assert.Equal(["matched", "not_reached"], top.Evaluations.Select(e => e.Outcome));
        var rest = PolicyResolver.Decide(T("one"), Who(), day, set);
        Assert.Equal(p2.Id, rest.Policy!.PolicyId);
        Assert.Equal(["not_matched", "matched"], rest.Evaluations.Select(e => e.Outcome));

        // The first matching policy has nothing in force: no policy, never a fall-through to the catch-all.
        var gap = new LoadedSet([senior, all], new Dictionary<Guid, PolicyVersion> { [p2.Id] = Version(p2, 20) }, none);
        var answer = PolicyResolver.Decide(T("one"), Who(Grade5), day, gap);
        Assert.Equal(PolicyResolution.NoPolicy, answer.Outcome);
        Assert.Equal("no_version_in_force", answer.Reason);
        Assert.Equal("no_assignment_row", PolicyResolver.Decide(T("one"), Who(row: false), day, set).Reason);
        Assert.Equal("no_rule_matched", PolicyResolver.Decide(T("one"), Who(), day, new LoadedSet([senior], set.Versions, none)).Reason);

        var many = PolicyResolver.Decide(T("many"), Who(Grade5), day, set);
        Assert.Equal([p1.Id, p2.Id], many.Matches.Select(m => m.PolicyId));
        Assert.Equal("tenant", many.Matches[0].Layer);

        // Deterministic: the same inputs give the same answer every time.
        var first = JsonSerializer.Serialize(top);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(first, JsonSerializer.Serialize(PolicyResolver.Decide(T("one"), Who(Grade5), day, set)));
        }
    }

    [Fact]
    public void FR_HRM_POL_02_a_set_date_follows_the_newest_set_or_replaces_it_while_it_is_still_in_the_future()
    {
        var today = new DateOnly(2026, 3, 1);
        Assert.Null(PolicySetDates.Problem(today.AddDays(-400), null, today));
        Assert.Null(PolicySetDates.Problem(today, today.AddDays(-1), today));
        Assert.NotNull(PolicySetDates.Problem(today.AddDays(-1), today.AddDays(-1), today)); // the same start once in force
        Assert.Null(PolicySetDates.Problem(today.AddDays(5), today.AddDays(5), today)); // a future set is replaced
        Assert.NotNull(PolicySetDates.Problem(today.AddDays(-2), today.AddDays(-1), today));
    }
}
