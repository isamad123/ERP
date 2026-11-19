using System.Globalization;
using System.Text;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>
/// Code 128 (set B, printable ASCII) as an SVG, for barcodes in documents (KRN-DOC-05). The pattern table is the standard one:
/// six alternating bar and space widths per symbol (stop has seven). Checked in the unit tests (every pattern is 11 modules wide).
/// </summary>
public static class Code128
{
    private const int StartB = 104;
    private const int Stop = 106;

    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212", "112232", "122132", "122231", "113222",
        "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321",
        "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313", "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121",
        "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224",
        "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113",
        "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>The 107 patterns (symbol values 0 to 106), for tests.</summary>
    public static IReadOnlyList<string> Table => Patterns;

    /// <summary>The symbol values of the text: start B, the characters, the check symbol and the stop.</summary>
    public static IReadOnlyList<int> Symbols(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new List<int> { StartB };
        var sum = StartB;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is < ' ' or > '~')
            {
                throw new ArgumentException("A barcode can hold only printable ASCII characters.", nameof(text));
            }

            var value = ch - ' ';
            values.Add(value);
            sum += value * (i + 1);
        }

        values.Add(sum % 103);
        values.Add(Stop);
        return values;
    }

    /// <summary>The barcode as an SVG image (no text below the bars).</summary>
    public static string Svg(string text, int height = 60, int moduleWidth = 2)
    {
        var x = 10 * moduleWidth;
        var bars = new StringBuilder();
        foreach (var symbol in Symbols(text))
        {
            var pattern = Patterns[symbol];
            for (var i = 0; i < pattern.Length; i++)
            {
                var width = (pattern[i] - '0') * moduleWidth;
                if (i % 2 == 0)
                {
                    bars.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"0\" width=\"{width}\" height=\"{height}\"/>");
                }

                x += width;
            }
        }

        var total = x + (10 * moduleWidth);
        return string.Create(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {total} {height}\" width=\"{total}\" height=\"{height}\" role=\"img\"><rect width=\"{total}\" height=\"{height}\" fill=\"#fff\"/><g fill=\"#000\">{bars}</g></svg>");
    }
}
