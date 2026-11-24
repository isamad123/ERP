using System.Globalization;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>Text of the CSV exports of slice S1.22b (strength report, chart reporting lines).</summary>
public static class CsvText
{
    /// <summary>A figure without trailing zeros and with a dot, whatever the culture (an FTE of 1.0000 is 1).</summary>
    public static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// One CSV field: quoted when it holds a comma, a quote or a line break, and a leading formula character (= + - @, tab, return) is
    /// neutralised with an apostrophe so a spreadsheet never runs a code or a name as a formula.
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return text.Contains(',', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }
}
