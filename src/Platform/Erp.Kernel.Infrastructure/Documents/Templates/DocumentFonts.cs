using System.Reflection;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>
/// The fonts printed documents use: Noto Sans (Latin) and Noto Nastaliq Urdu, both SIL Open Font Licence 1.1 (ruling Q18, CR-045).
/// They are embedded in the product and put into every page as data, so a document looks the same on every server.
/// </summary>
public static class DocumentFonts
{
    private static readonly Lazy<string> FontFaces = new(Build);

    /// <summary>The <c>@font-face</c> rules.</summary>
    public static string Css => FontFaces.Value;

    private static string Build()
    {
        var assembly = typeof(DocumentFonts).Assembly;
        return string.Concat(Face(assembly, "Noto Sans", "NotoSans-Regular.ttf"), Face(assembly, "Noto Nastaliq Urdu", "NotoNastaliqUrdu-Regular.ttf"));
    }

    private static string Face(Assembly assembly, string family, string file)
    {
        using var stream = assembly.GetManifestResourceStream("Erp.Kernel.Fonts." + file) ?? throw new InvalidOperationException($"The font {file} is not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return $"@font-face{{font-family:'{family}';src:url(data:font/ttf;base64,{Convert.ToBase64String(buffer.ToArray())}) format('truetype');font-weight:normal;font-style:normal;}}";
    }
}
