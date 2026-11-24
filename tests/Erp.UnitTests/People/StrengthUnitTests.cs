using System.Text.Json;
using Erp.CodeGen;
using Erp.Hrm.Application.Positions;
using Erp.Hrm.Infrastructure.Positions;

namespace Erp.UnitTests.People;

/// <summary>Slice S1.22b: the exports' text rules (FR-HRM-POS-03, -04), the limits of the chart (Q7) and the texts of the new refusals in both languages.</summary>
public sealed class StrengthUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+92300", "'+92300")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FR_HRM_POS_03_a_csv_field_is_quoted_when_needed_and_never_starts_a_formula(string? value, string expected) =>
        Assert.Equal(expected, CsvText.Field(value));

    [Theory]
    [InlineData(1.0000, "1")]
    [InlineData(4.5000, "4.5")]
    [InlineData(0.25, "0.25")]
    [InlineData(0, "0")]
    public void FR_HRM_POS_03_figures_have_no_trailing_zeros_and_a_dot(double value, string expected) =>
        Assert.Equal(expected, CsvText.Number((decimal)value));

    [Fact]
    public void FR_HRM_POS_04_the_pdf_holds_500_boxes_and_a_chart_answer_5000()
    {
        Assert.Equal(500, OrgChartLimits.PdfBoxes);
        Assert.True(OrgChartLimits.ChartBoxes > OrgChartLimits.PdfBoxes);
    }

    [Fact]
    public void FR_HRM_POS_08_every_new_refusal_has_an_english_and_an_urdu_text()
    {
        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Positions/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "positions.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "positions.ur.json")))!;
        string[] keys = [OrgStructureEvents.UnitHasPositions, OrgStructureEvents.UnitHasPeople, "errors.hrm.strength.company_required", "errors.hrm.strength.group_by", "errors.hrm.chart.company_required", "errors.hrm.chart.bad_format", "errors.hrm.chart.too_large_for_pdf"];
        Assert.All(keys, k =>
        {
            Assert.True(en.ContainsKey(k), k);
            Assert.Matches(@"\p{IsArabic}", ur[k]);
        });
    }
}
