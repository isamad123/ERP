using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Infrastructure.Itg;
using Sylvan.Data.Excel;

namespace Erp.UnitTests.Itg;

/// <summary>The pure parts of the import engine (slice S1.16a, FR-KRN-INT-03): transformations, typed values, mappings, manifests and the file reader.</summary>
public sealed class ImportUnitTests
{
    private static ImportColumnDefinition Column(string type, string name = "c", bool required = false, params string[] options) =>
        new(name, name, type, required, new Dictionary<string, string>(), null, options.Length == 0 ? null : options, null);

    private static readonly string[] Transform = ["trim", "upper"];
    private static readonly string[] Shout = ["shout"];
    private static readonly string[] KindOnly = ["kind"];

    [Theory]
    [InlineData("  Ali  ", "trim", "Ali")]
    [InlineData("ali", "upper", "ALI")]
    [InlineData("ALI", "lower", "ali")]
    public void FR_KRN_INT_03_text_transformations_apply_in_order(string cell, string transform, string expected) =>
        Assert.Equal(expected, ImportTransforms.Apply(cell, [transform]).Value);

    [Fact]
    public void FR_KRN_INT_03_a_date_and_a_number_are_read_in_the_format_the_template_names()
    {
        Assert.Equal("2027-03-23", ImportTransforms.Apply("23/03/2027", ["date:dd/MM/yyyy"]).Value);
        Assert.Equal("errors.import.not_a_date", ImportTransforms.Apply("2027-03-23", ["date:dd/MM/yyyy"]).Error);
        Assert.Equal("1234.5", ImportTransforms.Apply("1.234,5", ["number:de-DE"]).Value);
        Assert.Equal("errors.import.not_a_number", ImportTransforms.Apply("abc", ["number:de-DE"]).Error);
        Assert.Null(ImportTransforms.Apply(null, Transform).Value);
    }

    [Theory]
    [InlineData("number", "12.50", "12.50")]
    [InlineData("date", "2027-1-5", "\"2027-01-05\"")]
    [InlineData("bool", "yes", "true")]
    [InlineData("bool", "No", "false")]
    [InlineData("text", " x ", "\"x\"")]
    public void FR_KRN_INT_03_a_cell_becomes_the_json_value_of_its_column_type(string type, string cell, string expected) =>
        Assert.Equal(expected, ImportTransforms.ToJson(Column(type), cell).Value!.ToJsonString());

    [Fact]
    public void FR_KRN_INT_03_a_cell_that_does_not_fit_its_type_gives_a_message_key_and_an_option_keeps_its_spelling()
    {
        Assert.Equal("errors.import.not_a_number", ImportTransforms.ToJson(Column("number"), "x").Error);
        Assert.Equal("errors.import.not_a_date", ImportTransforms.ToJson(Column("date"), "32/13/2027").Error);
        Assert.Equal("errors.import.not_a_yes_no", ImportTransforms.ToJson(Column("bool"), "maybe").Error);
        Assert.Equal("errors.import.not_an_option", ImportTransforms.ToJson(Column("option", options: ["public", "religious"]), "other").Error);
        Assert.Equal("\"public\"", ImportTransforms.ToJson(Column("option", options: ["public", "religious"]), "PUBLIC").Value!.ToJsonString());
        Assert.Null(ImportTransforms.ToJson(Column("number"), "  ").Value);
    }

    [Fact]
    public void FR_KRN_INT_03_a_dotted_field_builds_a_nested_object()
    {
        var values = new JsonObject();
        ImportTransforms.Set(values, "nameI18n.en", JsonValue.Create("Finance"));
        ImportTransforms.Set(values, "nameI18n.ur", JsonValue.Create("مالیات"));
        ImportTransforms.Set(values, "code", JsonValue.Create("FIN"));
        Assert.Equal("Finance", values["nameI18n"]!["en"]!.GetValue<string>());
        Assert.Equal("FIN", values["code"]!.GetValue<string>());
    }

    [Fact]
    public void FR_ADM_INT_04_the_kernel_manifest_declares_users_units_holidays_and_locations_with_labels_in_both_languages()
    {
        var types = ImportManifest.Parse(ImportManifest.KernelJson);
        Assert.Equal(["holidays", "locations", "org_units", "users"], types.Select(t => t.Code).Order(StringComparer.Ordinal));
        foreach (var type in types)
        {
            Assert.All(type.Columns, c => Assert.True(c.Label.ContainsKey("en") && c.Label.ContainsKey("ur"), type.Code + "." + c.Name));
            Assert.All(type.KeyColumns, k => Assert.Contains(type.Columns, c => c.Name == k));
            Assert.True(type.Name.ContainsKey("ur"));
        }

        Assert.Equal("invite", types.Single(t => t.Code == "users").PostCommit);
        Assert.False(types.Single(t => t.Code == "users").CanUpdate);
    }

    [Fact]
    public void FR_ADM_INT_04_a_mapping_is_checked_against_its_type()
    {
        var type = ImportManifest.Parse(ImportManifest.KernelJson).Single(t => t.Code == "users");
        var good = TemplateMapping.Parse(JsonSerializer.SerializeToElement(TemplateMapping.DefaultFor(type)));
        Assert.Empty(good.Check(type));

        var bad = TemplateMapping.Parse(JsonSerializer.SerializeToElement(new
        {
            columns = new object[]
            {
                new { field = "username", source = "Login", transform = Shout },
                new { field = "nothing", source = "X" },
            },
        }));
        var problems = bad.Check(type);
        Assert.Contains(problems, p => p.Contains("unknown transformation shout", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("nothing: not a column", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("email: required column is not mapped", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => TemplateMapping.Parse(JsonSerializer.SerializeToElement(new { columns = new object[] { new { source = "x" } } })));
    }

    [Fact]
    public void FR_KRN_INT_03_a_manifest_with_a_key_that_is_not_a_column_stops_start_up()
    {
        const string json = """{"module":"x","types":[{"code":"t","table":"a.b","permission":"a.b.create","keyColumns":["nope"],"columns":[{"name":"c","type":"text"}]}]}""";
        Assert.Throws<InvalidDataException>(() => ImportManifest.Parse(json));
    }

    [Fact]
    public void FR_KRN_INT_04_csv_is_read_with_a_comma_or_a_semicolon_with_quotes_a_bom_and_empty_rows_skipped()
    {
        var mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(new { columns = Array.Empty<object>() }));
        var comma = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("a,b\r\n1,\"x,y\"\r\n,\r\n2,z\r\n")).ToArray();
        using (var sheet = ImportFileReader.Open(comma, "csv", mapping))
        {
            Assert.Equal(["a", "b"], sheet.Headers);
            var rows = sheet.Rows().ToList();
            Assert.Equal([2, 4], rows.Select(r => r.RowNo));
            Assert.Equal("x,y", rows[0].Cells[1]);
        }

        using var semicolon = ImportFileReader.Open(Encoding.UTF8.GetBytes("a;b\r\n1;2\r\n"), "csv", mapping);
        Assert.Equal(["a", "b"], semicolon.Headers);
        Assert.Equal("2", semicolon.Rows().Single().Cells[1]);
    }

    [Fact]
    public void FR_KRN_INT_04_excel_is_read_row_by_row_with_dates_numbers_and_the_chosen_sheet()
    {
        using var table = new DataTable("First");
        table.Columns.Add("code", typeof(string));
        table.Columns.Add("day", typeof(DateTime));
        table.Columns.Add("amount", typeof(double));
        table.Rows.Add("A", new DateTime(2027, 3, 23), 12.5);
        using var other = new DataTable("Second");
        other.Columns.Add("x", typeof(string));
        other.Rows.Add("only here");
        using var stream = new MemoryStream();
        using (var writer = ExcelDataWriter.Create(stream, ExcelWorkbookType.ExcelXml))
        {
            using (var r1 = table.CreateDataReader())
            {
                writer.Write(r1, "First");
            }

            using var r2 = other.CreateDataReader();
            writer.Write(r2, "Second");
        }

        var bytes = stream.ToArray();
        Assert.Equal("xlsx", ImportFileReader.Detect(bytes, "f.xlsx"));
        var open = TemplateMapping.Parse(JsonSerializer.SerializeToElement(new { columns = Array.Empty<object>() }));
        using (var sheet = ImportFileReader.Open(bytes, "xlsx", open))
        {
            var row = sheet.Rows().Single();
            Assert.Equal(["A", "2027-03-23", "12.5"], row.Cells);
        }

        var second = TemplateMapping.Parse(JsonSerializer.SerializeToElement(new { sheet = "Second", columns = Array.Empty<object>() }));
        using (var sheet = ImportFileReader.Open(bytes, "xlsx", second))
        {
            Assert.Equal("only here", sheet.Rows().Single().Cells[0]);
        }

        Assert.Throws<ImportFileException>(() => ImportFileReader.Open(bytes, "xlsx", TemplateMapping.Parse(JsonSerializer.SerializeToElement(new { sheet = "Missing", columns = Array.Empty<object>() }))));
    }

    [Fact]
    public void FR_KRN_INT_04_the_kind_of_a_file_is_told_by_its_first_bytes_and_its_name()
    {
        Assert.Equal("csv", ImportFileReader.Detect(Encoding.UTF8.GetBytes("a,b\r\n"), "x.csv"));
        Assert.Null(ImportFileReader.Detect(Encoding.UTF8.GetBytes("a,b\r\n"), "x.exe"));
        Assert.Null(ImportFileReader.Detect([0x4D, 0x5A, 0, 0, 0], "x.csv"));
        Assert.Null(ImportFileReader.Detect([0x50, 0x4B, 3, 4], "x.docx"));
    }

    [Fact]
    public void FR_KRN_INT_04_a_missing_header_a_required_column_reads_is_named()
    {
        var type = ImportManifest.Parse(ImportManifest.KernelJson).Single(t => t.Code == "holidays");
        var mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(TemplateMapping.DefaultFor(type)));
        Assert.Equal(KindOnly, ImportRowPipeline.MissingHeaders(type, mapping, ["calendar", "holiday_date", "name_en"]));
        Assert.Empty(ImportRowPipeline.MissingHeaders(type, mapping, ["CALENDAR", " holiday_date ", "name_en", "kind"]));
    }
}
