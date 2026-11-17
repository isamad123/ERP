using System.Text.Json;
using Erp.Kernel.Domain.Settings;

namespace Erp.UnitTests.Settings;

/// <summary>
/// The one resolution rule of settings and flags (slice S1.09a build items 2 and 5; rulings Q11, Q14, CR-037):
/// FR-KRN-SET-01, FR-KRN-SET-02, FR-KRN-SET-05.
/// </summary>
public sealed class SettingResolutionTests
{
    private static readonly Guid CompanyA = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid RoleX = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid RoleY = Guid.Parse("00000000-0000-7000-8000-0000000000a2");
    private static readonly Guid UserU = Guid.Parse("00000000-0000-7000-8000-0000000000b1");
    private static readonly Guid GroupG = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
    private static readonly Guid GroupH = Guid.Parse("00000000-0000-7000-8000-0000000000c2");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SettingDefinition Int(int @default, string validation = """{"min":1,"max":100}""", string[]? layers = null, string? roleRule = null) =>
        new("t.int", SettingTypes.WholeNumber, J(@default.ToString(System.Globalization.CultureInfo.InvariantCulture)), SettingValidation.Parse(J(validation)),
            layers ?? [SettingLayers.Pack, SettingLayers.Tenant, SettingLayers.Company, SettingLayers.Role, SettingLayers.User], roleRule, TightenOnly: false);

    private static SettingDefinition Bool(bool @default, string? roleRule, bool tightenOnly) =>
        new("t.bool", SettingTypes.Bool, J(@default ? "true" : "false"), SettingValidation.None,
            [SettingLayers.Tenant, SettingLayers.Role, SettingLayers.User], roleRule, tightenOnly);

    private static StoredSettingValue V(string layer, string json, Guid? scope = null) => new(layer, scope, J(json), At, UserU);

    [Fact]
    public void FR_KRN_SET_02_the_product_default_applies_when_no_layer_has_a_value()
    {
        var r = SettingResolution.Resolve(Int(10), []);
        Assert.Equal(10, r.Value.GetInt32());
        Assert.Equal(SettingLayers.Product, r.Layer);
    }

    /// <summary>Every combination of the five stored layers: the most specific one present wins and is named.</summary>
    [Fact]
    public void FR_KRN_SET_02_for_every_layer_combination_the_most_specific_value_wins_and_names_its_layer()
    {
        var layers = new (string Layer, Guid? Scope, int Value)[]
        {
            (SettingLayers.Pack, null, 20), (SettingLayers.Tenant, null, 30), (SettingLayers.Company, CompanyA, 40), (SettingLayers.Role, RoleX, 50), (SettingLayers.User, UserU, 60),
        };
        for (var mask = 0; mask < 1 << layers.Length; mask++)
        {
            var present = layers.Where((_, i) => (mask & (1 << i)) != 0).ToList();
            var r = SettingResolution.Resolve(Int(10, roleRule: SettingRoleRules.Highest), present.Select(p => V(p.Layer, p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Scope)));
            var expected = present.Count == 0 ? (SettingLayers.Product, (Guid?)null, 10) : present[^1];
            Assert.True(expected.Item3 == r.Value.GetInt32(), $"mask {mask}: value {r.Value}");
            Assert.True(expected.Item1 == r.Layer, $"mask {mask}: layer {r.Layer}");
            Assert.Equal(expected.Item2, r.ScopeId);
            Assert.Single(r.Layers, l => l.Ignored is null);
        }
    }

    [Fact]
    public void FR_KRN_SET_02_a_stored_value_that_is_no_longer_valid_is_ignored_and_the_next_layer_used()
    {
        var r = SettingResolution.Resolve(Int(10), [V(SettingLayers.Tenant, "30"), V(SettingLayers.Company, "500", CompanyA), V(SettingLayers.User, "\"ten\"", UserU)]);
        Assert.Equal(30, r.Value.GetInt32());
        Assert.Equal(SettingLayers.Tenant, r.Layer);
        Assert.Equal(SettingResolution.IgnoredInvalid, r.Layers.Single(l => l.Layer == SettingLayers.Company).Ignored);
        Assert.Equal(SettingResolution.IgnoredInvalid, r.Layers.Single(l => l.Layer == SettingLayers.User).Ignored);
        Assert.Null(r.Layers.Single(l => l.Layer == SettingLayers.Tenant).Ignored);
        Assert.Equal(SettingResolution.IgnoredOverridden, r.Layers.Single(l => l.Layer == SettingLayers.Product).Ignored);
    }

    [Fact]
    public void FR_KRN_SET_01_a_value_at_a_layer_the_setting_does_not_allow_is_ignored()
    {
        var definition = Int(10, layers: [SettingLayers.Tenant]);
        var r = SettingResolution.Resolve(definition, [V(SettingLayers.Tenant, "30"), V(SettingLayers.User, "60", UserU), V(SettingLayers.Role, "70", RoleX)]);
        Assert.Equal(30, r.Value.GetInt32());
        Assert.All(r.Layers.Where(l => l.Layer is SettingLayers.User or SettingLayers.Role), l => Assert.Equal(SettingResolution.IgnoredLayerNotAllowed, l.Ignored));
    }

    [Fact]
    public void FR_KRN_SET_02_ruling_Q11_the_role_layer_needs_a_role_rule()
    {
        var r = SettingResolution.Resolve(Int(10, roleRule: null), [V(SettingLayers.Role, "70", RoleX)]);
        Assert.Equal(SettingLayers.Product, r.Layer);
        Assert.Equal(SettingResolution.IgnoredLayerNotAllowed, r.Layers.Single(l => l.Layer == SettingLayers.Role).Ignored);
    }

    [Theory]
    [InlineData(SettingRoleRules.Lowest, 20)]
    [InlineData(SettingRoleRules.Highest, 50)]
    public void FR_KRN_SET_02_ruling_Q11_lowest_and_highest_combine_several_roles(string rule, int expected)
    {
        var r = SettingResolution.Resolve(Int(10, roleRule: rule), [V(SettingLayers.Tenant, "30"), V(SettingLayers.Role, "50", RoleX), V(SettingLayers.Role, "20", RoleY)]);
        Assert.Equal(expected, r.Value.GetInt32());
        Assert.Equal(SettingLayers.Role, r.Layer);
        Assert.Equal(expected == 50 ? RoleX : RoleY, r.ScopeId);
    }

    [Theory]
    [InlineData(SettingRoleRules.Any, "true", "false", true)]
    [InlineData(SettingRoleRules.Any, "false", "false", false)]
    [InlineData(SettingRoleRules.All, "true", "false", false)]
    [InlineData(SettingRoleRules.All, "true", "true", true)]
    public void FR_KRN_SET_02_ruling_Q11_any_and_all_combine_several_roles(string rule, string x, string y, bool expected)
    {
        var r = SettingResolution.Resolve(Bool(false, rule, tightenOnly: false), [V(SettingLayers.Role, x, RoleX), V(SettingLayers.Role, y, RoleY)]);
        Assert.Equal(expected, r.Value.GetBoolean());
        Assert.Equal(SettingLayers.Role, r.Layer);
    }

    /// <summary>CR-037 (the CR-017 and S1.06 rule of iam.mfa.required): role and user values can only switch a yes/no setting on.</summary>
    [Theory]
    [InlineData("true", null, "false", true, SettingLayers.Tenant)]
    [InlineData("false", "true", null, true, SettingLayers.Role)]
    [InlineData("false", null, "true", true, SettingLayers.User)]
    [InlineData("false", "false", "false", false, SettingLayers.Tenant)]
    [InlineData(null, null, "false", false, SettingLayers.Product)]
    public void CR_037_FR_KRN_SET_02_a_tighten_only_setting_is_never_relaxed_by_a_role_or_user(string? tenant, string? role, string? user, bool expected, string source)
    {
        var values = new List<StoredSettingValue>();
        if (tenant is not null)
        {
            values.Add(V(SettingLayers.Tenant, tenant));
        }

        if (role is not null)
        {
            values.Add(V(SettingLayers.Role, role, RoleX));
        }

        if (user is not null)
        {
            values.Add(V(SettingLayers.User, user, UserU));
        }

        var r = SettingResolution.Resolve(Bool(false, SettingRoleRules.Any, tightenOnly: true), values);
        Assert.Equal(expected, r.Value.GetBoolean());
        Assert.Equal(source, r.Layer);
    }

    [Theory]
    [InlineData("bool", "{}", "true", true)]
    [InlineData("bool", "{}", "1", false)]
    [InlineData("int", """{"min":1,"max":12}""", "12", true)]
    [InlineData("int", """{"min":1,"max":12}""", "13", false)]
    [InlineData("int", "{}", "2.5", false)]
    [InlineData("decimal", """{"min":0.5}""", "0.75", true)]
    [InlineData("decimal", """{"min":0.5}""", "0.25", false)]
    [InlineData("text", """{"pattern":"[A-Z]{3}","max_length":3}""", "\"PKR\"", true)]
    [InlineData("text", """{"pattern":"[A-Z]{3}"}""", "\"PKRX\"", false)]
    [InlineData("choice", """{"choices":["a","b"]}""", "\"b\"", true)]
    [InlineData("choice", """{"choices":["a","b"]}""", "\"c\"", false)]
    [InlineData("list", "{}", """["totp"]""", true)]
    [InlineData("list", "{}", """[1]""", false)]
    [InlineData("list", """{"item_type":"int","min":1,"max_items":2}""", """[1,2]""", true)]
    [InlineData("list", """{"item_type":"int","min":1,"max_items":2}""", """[1,2,3]""", false)]
    [InlineData("list", """{"choices":["totp","sms"],"min_items":1}""", """[]""", false)]
    [InlineData("json", "{}", """{"any":["thing"]}""", true)]
    public void FR_KRN_SET_01_FR_ADM_SET_02_values_are_checked_against_the_type_and_rules(string type, string rules, string value, bool valid) =>
        Assert.Equal(valid, SettingValidation.Parse(J(rules)).Problem(type, J(value)) is null);

    [Fact]
    public void FR_KRN_SET_01_rules_round_trip_as_stored_json_and_refuse_rules_that_do_not_fit_the_type()
    {
        var rules = SettingValidation.Parse(J("""{"min":1,"max":12}"""));
        Assert.Equal("""{"min":1,"max":12}""", rules.ToJson());
        Assert.Empty(rules.DefinitionProblems(SettingTypes.WholeNumber));
        Assert.NotEmpty(rules.DefinitionProblems(SettingTypes.Text));
        Assert.NotEmpty(SettingValidation.Parse(J("""{"min":5,"max":1}""")).DefinitionProblems(SettingTypes.WholeNumber));
        Assert.NotEmpty(SettingValidation.None.DefinitionProblems(SettingTypes.Choice));
        Assert.NotEmpty(SettingValidation.Parse(J("""{"pattern":"("}""")).DefinitionProblems(SettingTypes.Text));
    }

    // ------------------------------------------------------------------ flags (ruling Q14)

    private static FlagOverride F(string layer, bool on, Guid? scope = null) => new(layer, scope, on, At, UserU);

    [Fact]
    public void FR_KRN_SET_05_a_flag_follows_its_default_then_customer_then_company_then_user_group()
    {
        Assert.Equal(new ResolvedFlag(false, FlagResolution.Default, null), FlagResolution.Resolve(false, true, []));
        Assert.Equal(new ResolvedFlag(true, FlagResolution.Tenant, null), FlagResolution.Resolve(false, true, [F(FlagResolution.Tenant, true)]));
        Assert.Equal(new ResolvedFlag(false, FlagResolution.Company, CompanyA), FlagResolution.Resolve(false, true, [F(FlagResolution.Tenant, true), F(FlagResolution.Company, false, CompanyA)]));
        Assert.Equal(new ResolvedFlag(true, FlagResolution.UserGroup, GroupG),
            FlagResolution.Resolve(false, true, [F(FlagResolution.Tenant, false), F(FlagResolution.Company, false, CompanyA), F(FlagResolution.UserGroup, true, GroupG)]));
    }

    [Fact]
    public void FR_KRN_SET_05_ruling_Q14_when_the_users_groups_disagree_on_wins()
    {
        var r = FlagResolution.Resolve(false, true, [F(FlagResolution.UserGroup, false, GroupG), F(FlagResolution.UserGroup, true, GroupH)]);
        Assert.True(r.Enabled);
        Assert.Equal(GroupH, r.ScopeId);
        Assert.False(FlagResolution.Resolve(true, true, [F(FlagResolution.UserGroup, false, GroupG)]).Enabled);
    }

    [Fact]
    public void FR_KRN_SET_05_ruling_Q14_a_flag_whose_entitlement_key_is_not_licensed_is_off_whatever_its_overrides_say()
    {
        var r = FlagResolution.Resolve(true, licensed: false, [F(FlagResolution.Tenant, true), F(FlagResolution.UserGroup, true, GroupG)]);
        Assert.Equal(new ResolvedFlag(false, FlagResolution.Entitlement, null), r);
    }
}
