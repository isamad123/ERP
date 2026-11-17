#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Text.Json.Nodes;
using Erp.CodeGen;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.Entitlements;

namespace Erp.UnitTests.Entitlements;

/// <summary>
/// Slice S1.09b: the claims format and key catalogue (FR-KRN-ENT-01, rulings Q1 and B3), the licence state machine
/// (FR-KRN-ENT-04, rulings Q4 and B1), the entitlement in force (ruling Q3) and the edition templates (ruling Q2).
/// </summary>
public sealed class EntitlementUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static readonly DateOnly End = new(2026, 12, 31);

    // ------------------------------------------------------------------ licence states (B1 day table)

    [Theory]
    [InlineData("2026-12-01", "active")] // E−30: last Active day
    [InlineData("2026-12-02", "warning")] // E−29: Warning, the last 30 days up to and including E
    [InlineData("2026-12-31", "warning")] // E
    [InlineData("2027-01-01", "grace")] // E+1
    [InlineData("2027-01-14", "grace")] // E+14
    [InlineData("2027-01-15", "read_only")] // E+15
    [InlineData("2027-02-13", "read_only")] // E+44
    [InlineData("2027-02-14", "locked")] // E+45
    [InlineData("2030-01-01", "locked")]
    public void FR_KRN_ENT_04_each_state_starts_on_the_day_the_default_periods_give(string day, string expected) =>
        Assert.Equal(expected, LicenceStates.Decide(End, LicenceTerm.Default, DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void FR_KRN_ENT_04_a_perpetual_entitlement_stays_active_with_no_next_change()
    {
        Assert.Equal(("active", (DateOnly?)null, (string?)null), LicenceStates.At(null, LicenceTerm.Default, new DateOnly(2099, 1, 1)));
    }

    [Fact]
    public void FR_KRN_ENT_04_the_next_change_names_the_next_state_and_its_first_day()
    {
        Assert.Equal(("active", (DateOnly?)new DateOnly(2026, 12, 2), (string?)"warning"), LicenceStates.At(End, LicenceTerm.Default, new DateOnly(2026, 11, 17)));
        Assert.Equal(("grace", (DateOnly?)new DateOnly(2027, 1, 15), (string?)"read_only"), LicenceStates.At(End, LicenceTerm.Default, new DateOnly(2027, 1, 3)));
        Assert.Equal(("locked", (DateOnly?)null, (string?)null), LicenceStates.At(End, LicenceTerm.Default, new DateOnly(2027, 3, 1)));
    }

    [Fact]
    public void FR_KRN_ENT_04_periods_follow_the_contract_and_a_zero_day_period_is_skipped_but_read_only_still_precedes_locked()
    {
        var term = new LicenceTerm(0, 0, 10, null);
        Assert.Equal("active", LicenceStates.Decide(End, term, End));
        Assert.Equal("read_only", LicenceStates.Decide(End, term, End.AddDays(1)));
        Assert.Equal("locked", LicenceStates.Decide(End, term, End.AddDays(11)));
        Assert.Equal(("active", (DateOnly?)End.AddDays(1), (string?)"read_only"), LicenceStates.At(End, term, End));
    }

    [Fact]
    public void FR_KRN_ENT_04_a_day_ends_at_midnight_in_the_customer_time_zone()
    {
        var karachi = LicenceStates.Zone("Asia/Karachi");
        Assert.Equal(new DateOnly(2027, 1, 1), LicenceStates.Today(new DateTimeOffset(2026, 12, 31, 19, 0, 0, TimeSpan.Zero), karachi)); // 00:00 in Karachi
        Assert.Equal(new DateOnly(2026, 12, 31), LicenceStates.Today(new DateTimeOffset(2026, 12, 31, 18, 59, 59, TimeSpan.Zero), karachi));
        Assert.Equal("Asia/Karachi", LicenceStates.Zone("Not/AZone").Id); // an unknown setting value never fails a request
    }

    // ------------------------------------------------------------------ the entitlement in force (Q3)

    [Fact]
    public void FR_ADM_PLT_03_a_scheduled_entitlement_is_in_force_from_its_start_day_and_not_before()
    {
        var current = Record(1, "active", new DateOnly(2026, 1, 1));
        var scheduled = Record(2, "scheduled", new DateOnly(2026, 12, 1));
        Assert.Equal(1, EntitlementSelection.InForce([current, scheduled], new DateOnly(2026, 11, 30))!.Version);
        Assert.Equal(2, EntitlementSelection.InForce([current, scheduled], new DateOnly(2026, 12, 1))!.Version);
        Assert.Null(EntitlementSelection.InForce([Record(1, "superseded", new DateOnly(2026, 1, 1))], new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void FR_KRN_ENT_04_no_readable_entitlement_is_locked_fail_closed()
    {
        var broken = Record(1, "active", new DateOnly(2026, 1, 1)) with { ClaimsJson = """{"modules":[]}""" };
        Assert.Equal("locked", LicenceSnapshots.Compute([broken], "Asia/Karachi", DateTimeOffset.UnixEpoch.AddYears(56), LicenceSnapshots.TryParse).State);
        Assert.Equal("locked", LicenceSnapshots.Compute([], "Asia/Karachi", DateTimeOffset.UnixEpoch.AddYears(56), LicenceSnapshots.TryParse).State);
    }

    // ------------------------------------------------------------------ claims (Q1, B3)

    [Fact]
    public void FR_KRN_ENT_01_valid_claims_are_read_with_limits_term_and_flags()
    {
        var claims = EntitlementCatalogs.Parse(Claims());
        Assert.Equal("saas", claims.Deployment);
        Assert.True(claims.HasModule("hrm") && claims.HasModule("kernel"));
        Assert.True(claims.HasFeature("hrm.payroll"));
        Assert.Equal(25, claims.Limit("users.named"));
        Assert.Null(claims.Limit("companies")); // a missing limit means no limit
        Assert.Equal(new LicenceTerm(30, 14, 30, new DateOnly(2027, 6, 30)), claims.Term);
        Assert.Equal("standard", claims.SupportTier);
        Assert.False(claims.Source);
    }

    [Theory]
    [InlineData("features", """["hrm.core"]""", "platform.base")]
    [InlineData("features", """["platform.base","hrm.payroll"]""", "hrm.core")]
    [InlineData("features", """["platform.base","hrm.core","hrm.typo"]""", "not a key of the entitlement key catalogue")]
    [InlineData("modules", """[]""", "needs module 'hrm'")]
    [InlineData("modules", """["fin"]""", "not a module of this product")]
    [InlineData("modules", """["kernel"]""", "business modules only")]
    [InlineData("limits", """{"users":5}""", "not a limit key")]
    [InlineData("limits", """{"users.named":-1}""", "0 or more")]
    [InlineData("deployment", "\"cloud\"", "saas or on_prem")]
    [InlineData("packs", """["pack.country.xx"]""", "not a key")]
    [InlineData("addons", """["ai.nlq"]""", "needs its parent key 'ai.gateway'")]
    [InlineData("term", """{"grace_days":-3}""", "grace_days")]
    [InlineData("note", "\"hello\"", "Unknown entitlement claims")]
    public void FR_KRN_ENT_01_claims_that_break_the_key_catalogue_are_refused_with_the_reason(string claim, string json, string reason)
    {
        var claims = Claims();
        claims[claim] = JsonNode.Parse(json);
        var error = Assert.Throws<InvalidDataException>(() => EntitlementCatalogs.Parse(claims));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR_KRN_ENT_01_a_module_needs_its_base_key_but_not_every_feature_of_it()
    {
        var claims = Claims();
        claims["features"] = new JsonArray("platform.base");
        Assert.Contains("needs its feature key 'hrm.core'", Assert.Throws<InvalidDataException>(() => EntitlementCatalogs.Parse(claims)).Message, StringComparison.Ordinal);
        claims["features"] = new JsonArray("platform.base", "hrm.core");
        Assert.False(EntitlementCatalogs.Parse(claims).HasFeature("hrm.payroll")); // core without payroll can be sold (ruling B3)
    }

    [Fact]
    public void KRN_ENT_03_the_module_catalogue_keys_follow_D24()
    {
        Assert.Equal(["platform.base"], EntitlementCatalogs.Keys.KeysOf("kernel"));
        Assert.Equal(["hrm.core", "hrm.learning", "hrm.payroll", "hrm.performance", "hrm.recruitment", "hrm.relations", "hrm.us"], EntitlementCatalogs.Keys.KeysOf("hrm"));
    }

    [Fact]
    public void FR_KRN_ENT_01_an_entitlement_cannot_end_before_it_starts_or_be_applied_after_its_end()
    {
        var payload = new JsonObject { ["tenant"] = "t1", ["version"] = 1, ["edition"] = "business", ["effective_from"] = "2026-12-01", ["expires_at"] = "2026-11-30", ["claims"] = Claims() };
        Assert.Throws<InvalidDataException>(() => EntitlementPayload.FromJson(payload));
        var ok = new EntitlementPayload("t1", 1, "business", new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 16), Claims());
        Assert.Contains("already expired", Assert.Throws<InvalidOperationException>(() => EntitlementApplier.RefuseExpired(ok, new DateOnly(2026, 11, 17))).Message, StringComparison.Ordinal);
        EntitlementApplier.RefuseExpired(ok, new DateOnly(2026, 11, 16)); // the last day is still licensed
    }

    // ------------------------------------------------------------------ templates (Q2, SC-3)

    [Theory]
    [InlineData("starter")]
    [InlineData("business")]
    [InlineData("enterprise")]
    public void D26_each_edition_template_is_marked_placeholder_and_passes_the_key_catalogue(string edition)
    {
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "src", "Host", "Erp.Host.Migrator", "EntitlementTemplates", edition + ".json")))!.AsObject();
        Assert.Equal(edition, template["edition"]!.GetValue<string>());
        Assert.Contains("placeholder, to be set by the product owner", template["_placeholder"]!.GetValue<string>(), StringComparison.Ordinal);
        var claims = EntitlementCatalogs.Parse(template["claims"]!.AsObject());
        Assert.True(claims.HasFeature("platform.base"));
    }

    [Fact]
    public void Spec16_SC3_no_product_code_branches_on_the_edition_name()
    {
        // Behaviour follows keys and limits only (ruling Q2): the edition is stored, shown and validated, never compared.
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.EndsWith(".g.cs", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"\bEdition\s*(==|!=|is\s|switch\b)|\.Edition\.Equals\(|edition\s*(==|!=)\s*""(starter|business|enterprise)"""))
                {
                    offenders.Add($"{Path.GetRelativePath(Root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static EntitlementRecord Record(int version, string status, DateOnly from) =>
        new(Guid.CreateVersion7(), version, status, "business", from, null, Claims().ToJsonString());

    private static JsonObject Claims() => new()
    {
        ["deployment"] = "saas",
        ["modules"] = new JsonArray("hrm"),
        ["features"] = new JsonArray("platform.base", "hrm.core", "hrm.payroll"),
        ["packs"] = new JsonArray("pack.country.pk"),
        ["addons"] = new JsonArray(),
        ["limits"] = new JsonObject { ["users.named"] = 25 },
        ["term"] = new JsonObject { ["maintenance_expires_at"] = "2027-06-30", ["warning_days"] = 30, ["grace_days"] = 14, ["read_only_days"] = 30 },
        ["support_tier"] = "standard",
        ["source"] = false,
    };
}
