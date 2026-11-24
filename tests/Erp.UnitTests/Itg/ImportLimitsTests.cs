using System.Data;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.Itg;
using Sylvan.Data.Excel;

namespace Erp.UnitTests.Itg;

/// <summary>What an import file may unpack to and how many rows it may hold (slice S1.16a review finding 2, FR-KRN-INT-03, FR-KRN-INT-04): crafted files are small and the limits stop them.</summary>
public sealed class ImportLimitsTests
{
    private static readonly TemplateMapping Mapping = TemplateMapping.Parse(JsonSerializer.SerializeToElement(new { columns = Array.Empty<object>() }));

    private static byte[] Package(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, data) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(data);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void FR_KRN_INT_03_a_shared_strings_bomb_is_refused_by_its_compression_ratio_before_anything_is_unpacked()
    {
        var bomb = Package(("xl/sharedStrings.xml", new byte[8 * 1024 * 1024]));
        Assert.True(bomb.Length < 64 * 1024);
        var refused = Assert.Throws<ImportFileException>(() => ImportFileReader.CheckPackage(bomb, ImportLimits.Unbounded));
        Assert.Equal("errors.import.too_large", refused.Message);
        Assert.Throws<ImportFileException>(() => ImportFileReader.Open(bomb, "xlsx", Mapping, ImportLimits.For(20 * 1024 * 1024, 50_000)));
    }

    [Fact]
    public void FR_KRN_INT_03_a_package_over_the_unpacked_size_limit_is_refused_even_when_it_compresses_poorly()
    {
        var noise = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(noise);
        var package = Package(("xl/worksheets/sheet1.xml", noise));
        Assert.Throws<ImportFileException>(() => ImportFileReader.CheckPackage(package, new ImportLimits(1024 * 1024, 100)));
        ImportFileReader.CheckPackage(package, new ImportLimits(4 * 1024 * 1024, 100)); // inside the limit it passes
        Assert.Equal(10L * 20 * 1024 * 1024, ImportLimits.UnpackedFor(20L * 1024 * 1024));
        Assert.Equal(200L * 1024 * 1024, ImportLimits.UnpackedFor(100L * 1024 * 1024)); // never more than 200 MB
    }

    [Fact]
    public void FR_KRN_INT_03_a_real_small_excel_file_passes_the_package_check_and_is_read()
    {
        using var table = new DataTable();
        table.Columns.Add("a", typeof(string));
        table.Rows.Add("one");
        using var stream = new MemoryStream();
        using (var writer = ExcelDataWriter.Create(stream, ExcelWorkbookType.ExcelXml))
        using (var reader = table.CreateDataReader())
        {
            writer.Write(reader, "Sheet");
        }

        using var sheet = ImportFileReader.Open(stream.ToArray(), "xlsx", Mapping, ImportLimits.For(20 * 1024 * 1024, 50_000));
        Assert.Equal("one", sheet.Rows().Single().Cells[0]);
    }

    [Fact]
    public void FR_KRN_INT_04_a_file_of_nothing_but_blank_rows_stops_at_the_physical_row_limit()
    {
        var csv = Encoding.UTF8.GetBytes("a,b\r\n" + string.Concat(Enumerable.Repeat(",\r\n", 5000)));
        using var sheet = ImportFileReader.Open(csv, "csv", Mapping, new ImportLimits(long.MaxValue, 1000));
        var refused = Assert.Throws<ImportFileException>(() => sheet.Rows().ToList());
        Assert.Equal("errors.import.too_many_rows", refused.Message);
        Assert.Throws<ImportFileException>(() => ImportFileReader.CountRows(csv, "csv", Mapping, 10, new ImportLimits(long.MaxValue, 1000)));
    }

    [Fact]
    public void FR_KRN_INT_03_a_cell_is_cut_one_character_over_the_limit_so_the_row_check_can_refuse_it()
    {
        var csv = Encoding.UTF8.GetBytes("a\r\n" + new string('x', 6000) + "\r\n");
        using var sheet = ImportFileReader.Open(csv, "csv", Mapping, ImportLimits.Unbounded with { MaxCellChars = ImportLimits.DefaultCellChars });
        Assert.Equal(ImportLimits.DefaultCellChars + 1, sheet.Rows().Single().Cells[0]!.Length);
    }

    [Fact]
    public void FR_KRN_INT_03_a_spreadsheet_formula_is_written_as_text_but_a_plain_number_is_left_alone()
    {
        Assert.Equal("'=1+1", ImportService.CsvCell("=1+1", ','));
        Assert.Equal("'\t=1", ImportService.CsvCell("\t=1", ','));
        Assert.Equal("'+92 300 1234567", ImportService.CsvCell("+92 300 1234567", ','));
        Assert.Equal("-5", ImportService.CsvCell("-5", ','));
        Assert.Equal("+923001234567", ImportService.CsvCell("+923001234567", ','));
        Assert.Equal("'-North", ImportService.CsvCell("-North", ','));
        Assert.Equal("\"a,b\"", ImportService.CsvCell("a,b", ','));
    }
}
