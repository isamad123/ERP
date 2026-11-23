using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Kernel.Application.Metadata;
using Erp.Kernel.Contracts.Metadata;

namespace Erp.UnitTests.Metadata;

/// <summary>Slice S1.15b: the pure rules of custom field values and definitions (FR-KRN-MET-01, -02, -05) and the texts of the new services.</summary>
public sealed partial class MetadataUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly JsonElement NoRules = Json("{}");

    private static readonly string[] SourceFolders = ["src/Platform/Erp.Kernel.Infrastructure/Metadata", "src/Platform/Erp.Kernel.Application/Metadata", "src/Platform/Erp.Kernel.Api/Metadata"];

    private static string? Error(string type, string rules, string value, params string[] options) =>
        CustomFieldRules.CheckValue(type, Json(rules), Json(value), options.ToHashSet(StringComparer.Ordinal)).ErrorKey;

    [Theory]
    [InlineData("\"abc\"", null)]
    [InlineData("\"ab\"", "errors.custom.too_short")]
    [InlineData("\"abcdef\"", "errors.custom.too_long")]
    [InlineData("\"AB-12\"", "errors.custom.pattern")]
    [InlineData("12", "errors.custom.invalid_type")]
    public void FR_KRN_MET_02_a_text_follows_length_and_pattern(string value, string? expected) =>
        Assert.Equal(expected, Error("text", "{\"minLength\":3,\"maxLength\":5,\"pattern\":\"[a-z]+\"}", value));

    [Theory]
    [InlineData("5", null)]
    [InlineData("0", "errors.custom.too_small")]
    [InlineData("11", "errors.custom.too_large")]
    [InlineData("2.5", "errors.custom.invalid_type")]
    [InlineData("\"5\"", "errors.custom.invalid_type")]
    public void FR_KRN_MET_02_b_number_is_a_whole_number_between_min_and_max(string value, string? expected) =>
        Assert.Equal(expected, Error("number", "{\"min\":1,\"max\":10}", value));

    [Theory]
    [InlineData("\"12.50\"", "12.50")]
    [InlineData("12.5", "12.5")]
    [InlineData("\"-3\"", "-3")]
    public void FR_KRN_MET_02_c_decimal_is_kept_as_an_exact_string(string value, string stored)
    {
        var result = CustomFieldRules.CheckValue("decimal", NoRules, Json(value), null);
        Assert.Null(result.ErrorKey);
        Assert.Equal(stored, result.Value!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"1e3\"", "errors.custom.invalid_type")]
    [InlineData("\"1.1234567\"", "errors.custom.invalid_type")]
    [InlineData("\"abc\"", "errors.custom.invalid_type")]
    [InlineData("true", "errors.custom.invalid_type")]
    public void FR_KRN_MET_02_d_decimal_refuses_floats_in_exponent_form_and_too_many_places(string value, string expected) =>
        Assert.Equal(expected, Error("decimal", "{}", value));

    [Theory]
    [InlineData("\"2026-06-15\"", null)]
    [InlineData("\"2025-12-31\"", "errors.custom.too_small")]
    [InlineData("\"2027-01-01\"", "errors.custom.too_large")]
    [InlineData("\"15/06/2026\"", "errors.custom.invalid_date")]
    [InlineData("\"2026-02-30\"", "errors.custom.invalid_date")]
    public void FR_KRN_MET_02_e_date_is_an_iso_day_inside_its_limits(string value, string? expected) =>
        Assert.Equal(expected, Error("date", "{\"minDate\":\"2026-01-01\",\"maxDate\":\"2026-12-31\"}", value));

    [Fact]
    public void FR_KRN_MET_02_f_choice_takes_a_code_of_the_list_and_multi_choice_a_list_of_them()
    {
        Assert.Null(Error("choice", "{}", "\"a_pos\"", "a_pos", "b_pos"));
        Assert.Equal("errors.custom.not_in_list", Error("choice", "{}", "\"zzz\"", "a_pos"));
        Assert.Equal("errors.custom.invalid_type", Error("choice", "{}", "3", "a_pos"));
        var many = CustomFieldRules.CheckValue("multi_choice", Json("{\"maxItems\":2}"), Json("[\"a\",\"b\",\"a\"]"), new HashSet<string> { "a", "b" });
        Assert.Null(many.ErrorKey);
        Assert.Equal(["a", "b"], many.Value!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("errors.custom.too_large", Error("multi_choice", "{\"maxItems\":1}", "[\"a\",\"b\"]", "a", "b"));
        Assert.Equal("errors.custom.not_in_list", Error("multi_choice", "{}", "[\"a\",\"x\"]", "a"));
    }

    [Fact]
    public void FR_KRN_MET_02_g_an_empty_value_counts_as_not_given_and_bool_user_employee_check_their_shape()
    {
        foreach (var empty in new[] { "null", "\"\"", "\"  \"", "[]" })
        {
            Assert.True(CustomFieldRules.CheckValue("text", NoRules, Json(empty), null).IsEmpty, empty);
        }

        Assert.Null(Error("bool", "{}", "false"));
        Assert.Equal("errors.custom.invalid_type", Error("bool", "{}", "\"yes\""));
        Assert.Null(Error("user", "{}", $"\"{Guid.NewGuid()}\""));
        Assert.Equal("errors.custom.invalid_type", Error("employee", "{}", "\"not-an-id\""));
        Assert.Equal("errors.custom.invalid_type", Error("employee", "{}", $"\"{Guid.Empty}\""));
    }

    [Fact]
    public void FR_KRN_MET_05_a_validation_settings_belong_to_their_type_and_are_stored_normalized()
    {
        var (ok, error) = CustomFieldRules.NormalizeValidation("decimal", Json("{\"min\":1.5,\"max\":\"20\"}"));
        Assert.Null(error);
        Assert.Equal("1.5", ok!["min"]!.GetValue<string>());
        Assert.Equal("20", ok["max"]!.GetValue<string>());
        Assert.Equal("errors.custom.def.validation_not_for_type", CustomFieldRules.NormalizeValidation("number", Json("{\"maxLength\":3}")).ErrorKey);
        Assert.Equal("errors.custom.def.validation_not_for_type", CustomFieldRules.NormalizeValidation("bool", Json("{\"min\":1}")).ErrorKey);
        Assert.Equal("errors.custom.def.validation_range", CustomFieldRules.NormalizeValidation("number", Json("{\"min\":5,\"max\":2}")).ErrorKey);
        Assert.Equal("errors.custom.def.validation_range", CustomFieldRules.NormalizeValidation("date", Json("{\"minDate\":\"2026-02-01\",\"maxDate\":\"2026-01-01\"}")).ErrorKey);
        Assert.Equal("errors.custom.def.validation_invalid", CustomFieldRules.NormalizeValidation("text", Json("{\"maxLength\":99999}")).ErrorKey);
        Assert.Equal("errors.custom.def.validation_invalid", CustomFieldRules.NormalizeValidation("text", Json("[]")).ErrorKey);
        Assert.Empty(CustomFieldRules.NormalizeValidation("bool", null).Validation!);
    }

    [Theory]
    [InlineData("(?<=a)b")]
    [InlineData("(a)\\1")]
    [InlineData("[")]
    public void FR_KRN_MET_05_b_a_pattern_the_linear_time_engine_cannot_run_is_refused(string pattern) =>
        Assert.Equal("errors.custom.def.pattern_invalid", CustomFieldRules.NormalizeValidation("text", JsonSerializer.SerializeToElement(new { pattern })).ErrorKey);

    [Fact]
    public void FR_KRN_MET_05_c_the_1_0_types_are_the_approved_data_design_and_file_waits_for_the_documents_screens()
    {
        Assert.Equal(["text", "long_text", "number", "decimal", "date", "bool", "choice", "multi_choice", "user", "employee", "file"], CustomFieldTypes.All);
        Assert.DoesNotContain("file", CustomFieldTypes.Available);
        Assert.True(CustomFieldTypes.NeedsOptionList("choice") && CustomFieldTypes.NeedsOptionList("multi_choice") && !CustomFieldTypes.NeedsOptionList("text"));
    }

    [Fact]
    public void FR_ADM_STU_05_the_texts_of_the_metadata_services_exist_in_both_languages_and_every_key_in_use_has_one()
    {
        var folder = Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Metadata/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "metadata.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "metadata.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(MetadataPermissions.All, p => Assert.True(en.ContainsKey(p.DescriptionKey), p.Code));

        var sources = SourceFolders
            .SelectMany(d => Directory.GetFiles(Path.Combine(Root, d), "*.cs"));
        var used = sources.SelectMany(f => ErrorKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);

        // Generic keys (not found, concurrency, access) come from the kernel catalogues; the others are this slice's.
        var own = used.Where(k => k.StartsWith("errors.custom.", StringComparison.Ordinal) || k.StartsWith("errors.labels.", StringComparison.Ordinal)
            || k.StartsWith("errors.option_lists.", StringComparison.Ordinal) || k.StartsWith("errors.metadata.", StringComparison.Ordinal));
        Assert.All(own, k => Assert.True(en.ContainsKey(k), k));
    }

    [GeneratedRegex("\"(errors\\.[a-z_.]+)\"")]
    private static partial Regex ErrorKey();
}
