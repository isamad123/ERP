using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>One cell of an export file: its text and whether it is a number (a number is never neutralised; text is, FR-ADM-DAT-04, Q7).</summary>
internal readonly record struct ExportCell(string? Text, bool IsNumber);

/// <summary>
/// Turns shaped list values into cells and cells into CSV or Excel files (slice S1.16b, Q2, Q7). Text that starts with <c>=</c>, <c>+</c>,
/// <c>-</c>, <c>@</c>, a tab or a carriage return gets a leading apostrophe so a spreadsheet never runs it as a formula; both formats carry
/// the watermark line. Excel files are written by hand (zip and XML) with inline strings, so no formula can be made from a cell.
/// </summary>
internal static class ExportWriters
{
    public const string CsvType = "text/csv; charset=utf-8";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int MaxCellChars = 32000;

    /// <summary>The text a person sees for a value of the list's answer (a two-language name by the language, a list joined, a number as it is).</summary>
    public static ExportCell CellOf(JsonNode? node, string? language)
    {
        switch (node)
        {
            case null:
                return default;
            case JsonValue value:
                var kind = value.GetValueKind();
                return kind switch
                {
                    JsonValueKind.Number => new ExportCell(value.ToJsonString(), true),
                    JsonValueKind.True => new ExportCell("true", false),
                    JsonValueKind.False => new ExportCell("false", false),
                    JsonValueKind.Null => default,
                    _ => new ExportCell(value.TryGetValue<string>(out var text) ? text : value.ToJsonString().Trim('"'), false),
                };
            case JsonObject obj:
                var lang = string.IsNullOrEmpty(language) ? "en" : language![..Math.Min(2, language.Length)].ToLowerInvariant();
                foreach (var key in new[] { lang, "en" })
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    {
                        return new ExportCell(s, false);
                    }
                }

                return obj.FirstOrDefault(p => p.Value is JsonValue pv && pv.TryGetValue<string>(out _)) is { Value: JsonValue first } && first.TryGetValue<string>(out var f)
                    ? new ExportCell(f, false) : new ExportCell(obj.ToJsonString(), false);
            case JsonArray array:
                return new ExportCell(string.Join("; ", array.Select(item => CellOf(item, language).Text ?? string.Empty)), false);
            default:
                return new ExportCell(node.ToJsonString(), false);
        }
    }

    /// <summary>The text of a cell as written: text that a spreadsheet would run as a formula is written as text with a leading apostrophe.</summary>
    public static string Safe(ExportCell cell)
    {
        var text = cell.Text ?? string.Empty;
        if (!cell.IsNumber && text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.Length > MaxCellChars ? text[..MaxCellChars] : text;
    }

    public static byte[] Csv(string watermark, IReadOnlyList<string> headers, IReadOnlyList<ExportCell[]> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 64 * 1024, leaveOpen: true) { NewLine = "\r\n" })
        {
            writer.WriteLine(Quote(Safe(new ExportCell(watermark, false))));
            writer.WriteLine(string.Join(',', headers.Select(h => Quote(Safe(new ExportCell(h, false))))));
            foreach (var row in rows)
            {
                writer.WriteLine(string.Join(',', row.Select(c => Quote(Safe(c)))));
            }
        }

        return stream.ToArray();
    }

    private static string Quote(string text) => text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;

    public static byte[] Xlsx(string watermark, IReadOnlyList<string> headers, IReadOnlyList<ExportCell[]> rows)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Part(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Part(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Part(zip, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Export" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Part(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Part(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>
                """);
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
            using var sheet = new StreamWriter(entry.Open(), new UTF8Encoding(false), 64 * 1024);
            sheet.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            WriteRow(sheet, 1, [new ExportCell(watermark, false)], style: 0);
            WriteRow(sheet, 2, headers.Select(h => new ExportCell(h, false)).ToArray(), style: 1);
            var rowNo = 3;
            foreach (var row in rows)
            {
                WriteRow(sheet, rowNo++, row, style: 0);
            }

            sheet.Write("</sheetData><headerFooter><oddFooter>&amp;L");
            sheet.Write(Xml(watermark.Replace("&", "&&", StringComparison.Ordinal)));
            sheet.Write("</oddFooter></headerFooter></worksheet>");
        }

        return stream.ToArray();
    }

    private static void Part(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(xml.TrimStart());
    }

    private static void WriteRow(StreamWriter w, int rowNo, ExportCell[] cells, int style)
    {
        w.Write("<row r=\"");
        w.Write(rowNo.ToString(CultureInfo.InvariantCulture));
        w.Write("\">");
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var reference = Column(i) + rowNo.ToString(CultureInfo.InvariantCulture);
            if (cell.IsNumber && cell.Text is { Length: > 0 } number && decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                w.Write("<c r=\"" + reference + "\" s=\"" + style.ToString(CultureInfo.InvariantCulture) + "\"><v>" + Xml(number) + "</v></c>");
            }
            else if (cell.Text is null || (cell.Text.Length == 0 && !cell.IsNumber))
            {
                continue;
            }
            else
            {
                w.Write("<c r=\"" + reference + "\" s=\"" + style.ToString(CultureInfo.InvariantCulture) + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Xml(Safe(cell)) + "</t></is></c>");
            }
        }

        w.Write("</row>");
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index; n >= 0; n = (n / 26) - 1)
        {
            name = (char)('A' + (n % 26)) + name;
        }

        return name;
    }

    private static string Xml(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != '￾' && ch != '￿' && !char.IsSurrogate(ch)) || char.IsSurrogate(ch))
            {
                builder.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
            }
        }

        return builder.ToString();
    }
}
