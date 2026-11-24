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
    public void FR_KRN_INT_03_every_part_counts_against_the_unpacked_size_limit_whatever_its_name()
    {
        // The workbook's relationships may give the shared strings any path; a check by name was walked round with xl/strings.xml
        // (a 17 MB file reached 3 GB in the reader). Poorly compressible content keeps the ratio check out of the way.
        var noise = new byte[2 * 1024 * 1024];
        new Random(11).NextBytes(noise);
        foreach (var name in new[] { "xl/strings.xml", "xl/data/part.bin", "customXml/item1.xml" })
        {
            var package = Package((name, noise));
            var refused = Assert.Throws<ImportFileException>(() => ImportFileReader.CheckPackage(package, new ImportLimits(1024 * 1024, 100)));
            Assert.Equal("errors.import.too_large", refused.Message);
        }
    }

    [Fact]
    public void FR_KRN_INT_03_a_part_that_declares_less_than_it_holds_is_cut_short_and_refused_as_unreadable()
    {
        // 4 MB of shared strings that the zip directory says is 1 KB: the ratio and size checks read the declared size, the zip reader
        // stops at it, and the cut XML must come back as a clear refusal, not as an XML error (a 500).
        var strings = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"2\" uniqueCount=\"2\"><si><t>a</t></si><si><t>"
            + new string('x', 4 * 1024 * 1024) + "</t></si></sst>");
        var package = Lie(Workbook(strings), "xl/sharedStrings.xml", 1024);
        ImportFileReader.CheckPackage(package, ImportLimits.For(20 * 1024 * 1024, 50_000)); // the declared sizes pass
        var refused = Assert.Throws<ImportFileException>(() =>
        {
            using var sheet = ImportFileReader.Open(package, "xlsx", Mapping, ImportLimits.For(20 * 1024 * 1024, 50_000));
            _ = sheet.Rows().ToList();
        });
        Assert.Equal("errors.import.unsupported_file", refused.Message);
    }

    /// <summary>A minimal workbook of one sheet whose two cells point at the shared strings given.</summary>
    private static byte[] Workbook(byte[] sharedStrings) => Package(
        ("[Content_Types].xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/></Types>")),
        ("_rels/.rels", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>")),
        ("xl/workbook.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
        ("xl/_rels/workbook.xml.rels", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>")),
        ("xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c></row></sheetData></worksheet>")),
        ("xl/sharedStrings.xml", sharedStrings));

    /// <summary>Rewrites the uncompressed size of one part, in its local header and in the central directory, to <paramref name="declared"/>.</summary>
    private static byte[] Lie(byte[] package, string part, uint declared)
    {
        var data = (byte[])package.Clone();
        var name = Encoding.UTF8.GetBytes(part);
        var patched = 0;
        for (var i = 0; i + 46 < data.Length; i++)
        {
            if (data[i] != 0x50 || data[i + 1] != 0x4B)
            {
                continue;
            }

            if (data[i + 2] == 3 && data[i + 3] == 4 && Named(data, i + 30, BitConverter.ToUInt16(data, i + 26), name))
            {
                BitConverter.TryWriteBytes(data.AsSpan(i + 22, 4), declared);
                patched++;
            }
            else if (data[i + 2] == 1 && data[i + 3] == 2 && Named(data, i + 46, BitConverter.ToUInt16(data, i + 28), name))
            {
                BitConverter.TryWriteBytes(data.AsSpan(i + 24, 4), declared);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        return data;
    }

    private static bool Named(byte[] data, int at, int length, byte[] name) =>
        length == name.Length && at + length <= data.Length && data.AsSpan(at, length).SequenceEqual(name);

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
