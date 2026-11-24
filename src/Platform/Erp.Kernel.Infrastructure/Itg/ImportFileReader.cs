using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Sylvan.Data.Csv;
using Sylvan.Data.Excel;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>An import file that cannot be read (not a CSV or Excel file, damaged, or a sheet that does not exist).</summary>
public sealed class ImportFileException(string errorKey) : Exception(errorKey);

/// <summary>
/// What a file may take (S1.16a review finding 2): a file is a compressed package, so the unpacked size, the compression ratio, the number of
/// physical rows (blank ones too) and the length of one cell are bounded before and while it is read.
/// </summary>
public sealed record ImportLimits(long MaxUnpackedBytes, int MaxPhysicalRows, int MaxCellChars = ImportLimits.DefaultCellChars)
{
    public const int DefaultCellChars = 4000;
    public const int MaxRatio = 100;
    public const int BlankRowMargin = 10_000;

    /// <summary>The unpacked size of all the parts of a package: ten times the upload limit, at most 200 MB.</summary>
    public static long UnpackedFor(long maxUploadBytes) => Math.Min(maxUploadBytes * 10, 200L * 1024 * 1024);

    public static ImportLimits For(long maxUploadBytes, int maxRows) => new(UnpackedFor(maxUploadBytes), maxRows + BlankRowMargin);

    /// <summary>Limits for tools and tests that read a small file already checked (the whole file counts as unpacked content).</summary>
    public static ImportLimits Unbounded { get; } = new(long.MaxValue, int.MaxValue);
}

/// <summary>The rows of an import file read one at a time (CSV or .xlsx), so a 50,000-row file never sits in memory as cells (slice S1.16a, FR-KRN-INT-04).</summary>
public sealed class ImportSheet : IDisposable
{
    private readonly DbDataReader _reader;
    private readonly Stream _stream;
    private readonly ImportLimits _limits;

    internal ImportSheet(DbDataReader reader, Stream stream, ImportLimits limits)
    {
        _reader = reader;
        _stream = stream;
        _limits = limits;
        Headers = [.. Enumerable.Range(0, reader.FieldCount).Select(i => (reader.GetName(i) ?? string.Empty).Trim())];
    }

    /// <summary>The trimmed header texts, in file order.</summary>
    public IReadOnlyList<string> Headers { get; }

    /// <summary>The data rows with their row number in the file (the header is row 1, the first data row 2). Rows with no value at all are skipped.</summary>
    public IEnumerable<(int RowNo, string?[] Cells)> Rows()
    {
        var rowNo = 1;
        while (Read())
        {
            rowNo++;
            if (rowNo - 1 > _limits.MaxPhysicalRows)
            {
                // Blank rows count: a file of nothing but blank rows must not keep a thread busy (finding 2).
                throw new ImportFileException("errors.import.too_many_rows");
            }

            var cells = new string?[Headers.Count];
            var any = false;
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = Cell(i);
                any |= !string.IsNullOrWhiteSpace(cells[i]);
            }

            if (any)
            {
                yield return (rowNo, cells);
            }
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    private bool Read()
    {
        try
        {
            return _reader.Read();
        }
        catch (Exception e) when (e is CsvRecordTooLargeException or InvalidDataException or FormatException or XmlException or EndOfStreamException)
        {
            throw new ImportFileException("errors.import.unsupported_file");
        }
    }

    private string? Cell(int i)
    {
        object v;
        try
        {
            // An Excel reader loads shared strings when a cell first needs them, so a damaged or cut part shows up here, not in Read.
            if (_reader.IsDBNull(i))
            {
                return null;
            }

            v = _reader.GetValue(i);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or XmlException or EndOfStreamException)
        {
            throw new ImportFileException("errors.import.unsupported_file");
        }

        return v switch
        {
            // One character over the limit is kept, so the row check can tell "too long" from "exactly at the limit" (errors.import.cell_too_long).
            string s => s.Length > _limits.MaxCellChars + 1 ? s[..(_limits.MaxCellChars + 1)] : s,
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>Opens CSV (UTF-8, comma or semicolon) and Excel (.xlsx) files as streams of rows (slice S1.16a, Q2, Q3).</summary>
public static class ImportFileReader
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";

    /// <summary>The format of the content, by what the first bytes say and the file name; null when it is neither.</summary>
    public static string? Detect(byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = fileName ?? string.Empty;
        if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B && content[2] == 0x03 && content[3] == 0x04)
        {
            return name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || !name.Contains('.', StringComparison.Ordinal) ? Xlsx : null;
        }

        if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sample = content.AsSpan(0, Math.Min(content.Length, 4096));
        return sample.Contains((byte)0) ? null : Csv;
    }

    /// <summary>Reads the zip directory of an .xlsx and refuses a package that would unpack to too much (finding 2). Nothing is unpacked.</summary>
    public static void CheckPackage(byte[] content, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            long unpacked = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > 64 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > ImportLimits.MaxRatio)
                {
                    throw new ImportFileException("errors.import.too_large");
                }

                // Every part counts, whatever its name: the package's relationships may give the shared strings or a sheet any path
                // (xl/strings.xml), so a check by name could be walked round (S1.16a review follow-up). The declared size is enough:
                // the zip reader stops unpacking a part at its declared size, so a part that declares less than it holds is cut short
                // (and then refused as unreadable), never unpacked in full.
                unpacked += entry.Length;
                if (unpacked > limits.MaxUnpackedBytes)
                {
                    throw new ImportFileException("errors.import.too_large");
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
        {
            throw new ImportFileException("errors.import.unsupported_file");
        }
    }

    public static ImportSheet Open(byte[] content, string format, TemplateMapping mapping, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(limits);
        if (format == Xlsx)
        {
            CheckPackage(content, limits);
        }

        var stream = new MemoryStream(content, writable: false);
        try
        {
            if (format == Xlsx)
            {
                var excel = ExcelDataReader.Create(stream, ExcelWorkbookType.ExcelXml, new ExcelDataReaderOptions { Schema = ExcelSchema.Default });
                if (mapping.Sheet is { } sheet)
                {
                    while (!string.Equals(excel.WorksheetName, sheet, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!excel.NextResult())
                        {
                            excel.Dispose();
                            throw new ImportFileException("errors.import.unsupported_file");
                        }
                    }
                }

                return new ImportSheet(excel, stream, limits);
            }

            var delimiter = mapping.Delimiter ?? Sniff(content);
            var text = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var csv = CsvDataReader.Create(text, new CsvDataReaderOptions { Delimiter = delimiter, HasHeaders = true, MaxBufferSize = 1024 * 1024 });
            return new ImportSheet(csv, stream, limits);
        }
        catch (Exception e) when (e is InvalidDataException or CsvRecordTooLargeException or FormatException or EndOfStreamException or XmlException)
        {
            stream.Dispose();
            throw new ImportFileException("errors.import.unsupported_file");
        }
    }

    /// <summary>The number of data rows (rows with no value at all are not counted); reading stops one row past <paramref name="stopAfter"/> and at the limits' physical row cap.</summary>
    public static int CountRows(byte[] content, string format, TemplateMapping mapping, int stopAfter, ImportLimits limits)
    {
        using var sheet = Open(content, format, mapping, limits);
        var count = 0;
        foreach (var _ in sheet.Rows())
        {
            if (++count > stopAfter)
            {
                break;
            }
        }

        return count;
    }

    private static char Sniff(byte[] content)
    {
        var end = Array.IndexOf(content, (byte)'\n');
        var line = Encoding.UTF8.GetString(content, 0, end < 0 ? Math.Min(content.Length, 4096) : Math.Min(end, 4096));
        return line.Count(c => c == ';') > line.Count(c => c == ',') ? ';' : ',';
    }
}
