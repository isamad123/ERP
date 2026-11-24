using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Positions;

namespace Erp.UnitTests.People;

/// <summary>Slice S1.22a: the status of a position on a date (FR-HRM-POS-02), the planning model (FR-HRM-POS-01) and the texts and permissions of the position services.</summary>
public sealed partial class PositionUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    private static readonly string[] OtherCatalogues = ["People/Localization/people.en.json", "Employments/Localization/employment.en.json"];

    [Theory]
    [InlineData("open", 0, 1, 0, "open")]
    [InlineData("open", 0, 1, 0.5, "open")]
    [InlineData("open", 0, 1, 1, "filled")]
    [InlineData("open", 0, 2, 3, "filled")]
    [InlineData("open", 0, 0, 0, "open")] // a budget of 0 (a head position of a headcount company) is never "filled"
    [InlineData("frozen", 0, 1, 1, "frozen")]
    [InlineData("closed", 0, 1, 0, "closed")]
    [InlineData("open", -10, 1, 0, "planned")] // before its first day
    [InlineData("frozen", -10, 1, 0, "frozen")]
    public void FR_HRM_POS_02_the_status_of_a_position_follows_its_dates_holders_and_budget(string stored, int firstDayOffset, double budget, double filled, string expected) =>
        Assert.Equal(expected, PositionRules.EffectiveStatus(stored, Jan1.AddDays(-firstDayOffset), Jan1, (decimal)budget, (decimal)filled));

    [Fact]
    public void FR_HRM_POS_01_the_model_is_headcount_unless_the_setting_says_position()
    {
        Assert.Equal("position", PositionRules.Model(JsonSerializer.SerializeToElement("position")));
        Assert.Equal("headcount", PositionRules.Model(JsonSerializer.SerializeToElement("headcount")));
        Assert.Equal("headcount", PositionRules.Model(JsonSerializer.SerializeToElement("anything")));
        Assert.Equal("headcount", PositionRules.Model(JsonSerializer.SerializeToElement(3)));
    }

    [Fact]
    public void FR_HRM_POS_02_a_position_is_its_code_the_row_in_force_on_a_date_is_the_position_then()
    {
        var first = new Position { PositionCode = "A", ValidFrom = Jan1, ValidTo = Jan1.AddDays(100) };
        var second = new Position { PositionCode = "A", ValidFrom = Jan1.AddDays(100) };
        Assert.Same(first, PositionRules.InForce([first, second], Jan1.AddDays(99)));
        Assert.Same(second, PositionRules.InForce([first, second], Jan1.AddDays(100)));
        Assert.Null(PositionRules.InForce([first, second], Jan1.AddDays(-1)));
    }

    [Fact]
    public void FR_HRM_ROL_01_the_position_permissions_and_every_text_exist_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var declared = manifest.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.All(PositionPermissions.HandWritten, c => Assert.Contains(c, declared));

        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Positions/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "positions.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "positions.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(PositionPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        // Every hrm key the position code answers with has a text (generic ones are in the person, employment and kernel catalogues).
        var others = OtherCatalogues
            .SelectMany(f => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts", f)))!.Keys).ToHashSet(StringComparer.Ordinal);
        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Positions"), "*.cs")
            .SelectMany(f => HrmKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) || others.Contains(k), k));
    }

    [GeneratedRegex("\"(errors\\.hrm\\.[a-z_.]+)\"")]
    private static partial Regex HrmKey();
}
