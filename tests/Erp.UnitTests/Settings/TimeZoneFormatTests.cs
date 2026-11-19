using System.Text.Json;
using Erp.Kernel.Domain.Settings;

namespace Erp.UnitTests.Settings;

/// <summary>The <c>time_zone</c> format rule of setting validation (R-019, CR-053): FR-KRN-SET-01.</summary>
public sealed class TimeZoneFormatTests
{
    private static readonly SettingValidation Rule = SettingValidation.Parse(JsonDocument.Parse("""{"format":"time_zone"}""").RootElement);

    private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    [Theory]
    [InlineData("Asia/Karachi")]
    [InlineData("UTC")]
    [InlineData("Europe/London")]
    public void FR_KRN_SET_01_a_known_iana_time_zone_is_accepted(string zone) =>
        Assert.Null(Rule.Problem(SettingTypes.Text, Text(zone)));

    [Theory]
    [InlineData("Mars/Base")]
    [InlineData("")]
    [InlineData("karachi time")]
    public void FR_KRN_SET_01_an_unknown_time_zone_is_refused(string zone) =>
        Assert.Equal("time_zone", Rule.Problem(SettingTypes.Text, Text(zone)));

    [Fact]
    public void FR_KRN_SET_01_a_value_that_is_not_text_is_refused_by_the_type_before_the_format() =>
        Assert.Equal("not_text", Rule.Problem(SettingTypes.Text, JsonSerializer.SerializeToElement(5)));

    [Fact]
    public void FR_KRN_SET_01_the_format_is_kept_in_the_stored_rules_and_only_time_zone_on_text_is_a_valid_definition()
    {
        Assert.Equal("""{"format":"time_zone"}""", Rule.ToJson());
        Assert.Empty(Rule.DefinitionProblems(SettingTypes.Text));
        Assert.NotEmpty(Rule.DefinitionProblems(SettingTypes.WholeNumber));
        Assert.NotEmpty(SettingValidation.Parse(JsonDocument.Parse("""{"format":"nonsense"}""").RootElement).DefinitionProblems(SettingTypes.Text));
    }
}
