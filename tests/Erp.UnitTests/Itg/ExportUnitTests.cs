using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Erp.Kernel.Infrastructure.Itg;
using Sylvan.Data.Excel;

namespace Erp.UnitTests.Itg;

/// <summary>The pure parts of the export engine (slice S1.16b, FR-ADM-DAT-04, Q2, Q7): cell text, formula neutralising, the CSV and Excel files and their watermark.</summary>
public sealed class ExportUnitTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+92300", "'+92300")]
    [InlineData("-5 days", "'-5 days")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tTab", "'\tTab")]
    [InlineData("Plain = text", "Plain = text")]
    public void FR_ADM_DAT_04_text_that_a_spreadsheet_would_run_as_a_formula_is_written_as_text(string value, string expected) =>
        Assert.Equal(expected, ExportWriters.Safe(new ExportCell(value, false)));

    [Fact]
    public void FR_ADM_DAT_04_a_number_is_never_neutralised_so_a_negative_amount_stays_a_number() =>
        Assert.Equal("-1250.5000", ExportWriters.Safe(new ExportCell("-1250.5000", true)));

    [Fact]
    public void FR_ADM_DAT_04_values_become_cells_by_the_language_of_the_person_two_language_names_joined_lists_and_exact_decimals()
    {
        Assert.Equal("Lahore", ExportWriters.CellOf(JsonNode.Parse("""{"en":"Lahore","ur":"لاہور"}"""), "en").Text);
        Assert.Equal("لاہور", ExportWriters.CellOf(JsonNode.Parse("""{"en":"Lahore","ur":"لاہور"}"""), "ur").Text);
        Assert.Equal("Lahore", ExportWriters.CellOf(JsonNode.Parse("""{"en":"Lahore"}"""), "ur").Text);
        Assert.Equal("a; b", ExportWriters.CellOf(JsonNode.Parse("""["a","b"]"""), "en").Text);
        var money = ExportWriters.CellOf(JsonNode.Parse("1234.5000"), "en");
        Assert.True(money.IsNumber);
        Assert.Equal("1234.5000", money.Text);
        Assert.Equal("true", ExportWriters.CellOf(JsonNode.Parse("true"), "en").Text);
        Assert.Null(ExportWriters.CellOf(null, "en").Text);
    }

    [Fact]
    public void FR_ADM_DAT_04_the_csv_file_has_the_watermark_line_then_the_headings_and_quotes_what_needs_it()
    {
        var bytes = ExportWriters.Csv("Exported by Ali on 2026-11-24 from acme; confidential", ["Code", "Name"],
        [
            [new ExportCell("A-1", false), new ExportCell("Say \"hi\", ok", false)],
            [new ExportCell("=1+1", false), new ExportCell("12.5", true)],
        ]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Exported by Ali on 2026-11-24 from acme; confidential", lines[0]);
        Assert.Equal("Code,Name", lines[1]);
        Assert.Equal("A-1,\"Say \"\"hi\"\", ok\"", lines[2]);
        Assert.Equal("'=1+1,12.5", lines[3]);
    }

    [Fact]
    public void FR_ADM_DAT_04_the_excel_file_opens_has_text_cells_numbers_the_watermark_row_and_footer_and_no_formula()
    {
        var bytes = ExportWriters.Xlsx("Exported by A & B; confidential", ["Code", "Amount"],
        [
            [new ExportCell("=1+1", false), new ExportCell("-12.5", true)],
            [new ExportCell("Zeta", false), default],
        ]);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            var xml = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
            Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
            Assert.Contains("Exported by A &amp; B; confidential", xml, StringComparison.Ordinal);
            Assert.Contains("<oddFooter>&amp;LExported by A &amp;&amp; B; confidential</oddFooter>", xml, StringComparison.Ordinal);
        }

        using var reader = ExcelDataReader.Create(new MemoryStream(bytes), ExcelWorkbookType.ExcelXml, new ExcelDataReaderOptions { Schema = ExcelSchema.NoHeaders });
        Assert.True(reader.Read());
        Assert.Equal("Exported by A & B; confidential", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("Code", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("'=1+1", reader.GetString(0));
        Assert.Equal("-12.5", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.Equal("Zeta", reader.GetString(0));
        Assert.False(reader.Read());
    }
}
