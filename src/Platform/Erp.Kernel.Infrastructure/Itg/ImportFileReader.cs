using System.Data.Common;
using System.Globalization;
using System.Text;
using Sylvan.Data.Csv;
using Sylvan.Data.Excel;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>An import file that cannot be read (not a CSV or Excel file, damaged, or a sheet that does not exist).</summary>
public sealed class ImportFileException(string errorKey) : Exception(errorKey);

/// <summary>The rows of an import file read one at a time (CSV or .xlsx), so a 50,000-row file never sits in memory as cells (slice S1.16a, FR-KRN-INT-04).</summary>
public sealed class ImportSheet : IDisposable
{
    private readonly DbDataReader _reader;
    private readonly Stream _stream;

    internal ImportSheet(DbDataReader reader, Stream stream)
    {
        _reader = reader;
        _stream = stream;
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
        catch (Exception e) when (e is CsvRecordTooLargeException or InvalidDataException or FormatException)
        {
            throw new ImportFileException("errors.import.unsupported_file");
        }
    }

    private string? Cell(int i)
    {
        if (_reader.IsDBNull(i))
        {
            return null;
        }

        var v = _reader.GetValue(i);
        return v switch
        {
            string s => s,
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

    public static ImportSheet Open(byte[] content, string format, TemplateMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(mapping);
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

                return new ImportSheet(excel, stream);
            }

            var delimiter = mapping.Delimiter ?? Sniff(content);
            var text = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var csv = CsvDataReader.Create(text, new CsvDataReaderOptions { Delimiter = delimiter, HasHeaders = true });
            return new ImportSheet(csv, stream);
        }
        catch (Exception e) when (e is InvalidDataException or CsvRecordTooLargeException or FormatException or EndOfStreamException)
        {
            stream.Dispose();
            throw new ImportFileException("errors.import.unsupported_file");
        }
    }

    /// <summary>The number of data rows (rows with no value at all are not counted).</summary>
    public static int CountRows(byte[] content, string format, TemplateMapping mapping, int stopAfter)
    {
        using var sheet = Open(content, format, mapping);
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
