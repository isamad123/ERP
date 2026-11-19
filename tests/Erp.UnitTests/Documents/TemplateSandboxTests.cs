using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Infrastructure.Documents.Templates;

namespace Erp.UnitTests.Documents;

/// <summary>The template sandbox, barcodes and fonts of slice S1.11c2 (KRN-DOC-05, HRM-DOC-08, rulings Q17, Q18, Q26).</summary>
public sealed class TemplateSandboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    private static string Render(string body, Dictionary<string, object?>? values = null, string format = TemplateFormats.Html, bool strict = false) =>
        TemplateSandbox.Render(body, values ?? [], format, "en", Now, strict, CancellationToken.None);

    [Fact]
    public void S111c2_KRN_DOC_05_conditional_blocks_and_tables_render_from_the_supplied_values()
    {
        var values = new Dictionary<string, object?>
        {
            ["employee"] = new Dictionary<string, object?> { ["name"] = "Ayesha", ["grade"] = 7 },
            ["items"] = new List<object?> { new Dictionary<string, object?> { ["label"] = "Basic", ["amount"] = 100m }, new Dictionary<string, object?> { ["label"] = "Bonus", ["amount"] = 5m } },
        };
        var text = Render("{{ if employee.grade > 5 }}Senior {{ employee.name }}{{ else }}Junior{{ end }}<table>{{ for i in items }}<tr><td>{{ i.label }}</td><td>{{ i.amount }}</td></tr>{{ end }}</table>", values);
        Assert.Contains("Senior Ayesha", text, StringComparison.Ordinal);
        Assert.Contains("<td>Basic</td><td>100</td>", text, StringComparison.Ordinal);
        Assert.Contains("<td>Bonus</td><td>5</td>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void S111c2_KRN_DOC_05_supplied_text_is_encoded_in_html_output_but_not_in_text_output()
    {
        var values = new Dictionary<string, object?> { ["name"] = "<script>alert(1)</script>" };
        Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt;", Render("{{ name }}", values));
        Assert.Equal("<script>alert(1)</script>", Render("{{ name }}", values, TemplateFormats.Text));
    }

    [Fact]
    public void S111c2_KRN_DOC_05_the_template_cannot_read_files_or_the_machine_clock()
    {
        Assert.Throws<DocumentException>(() => Render("{{ include '/etc/passwd' }}"));
        Assert.Equal(TemplateErrors.UnknownField, Assert.Throws<DocumentException>(() => Render("{{ date.now }}", strict: true)).Message); // the machine clock is not offered
        Assert.Equal("19 November 2026", Render("{{ now | date.to_string '%d %B %Y' }}")); // the injected clock is
    }

    [Fact]
    public void S111c2_KRN_DOC_05_a_runaway_loop_is_stopped()
    {
        Assert.Throws<DocumentException>(() => Render("{{ for i in 1..100000000 }}x{{ end }}"));
        Assert.Throws<DocumentException>(() => Render("{{ func f; f; end; f }}"));
    }

    [Fact]
    public void S111c2_KRN_DOC_05_a_mistake_in_the_text_and_an_unknown_field_are_refused()
    {
        var syntax = Assert.Throws<DocumentException>(() => Render("{{ if }}"));
        Assert.Equal(TemplateErrors.Syntax, syntax.Message);
        var unknown = Assert.Throws<DocumentException>(() => Render("{{ employee.shoe_size }}", new Dictionary<string, object?> { ["employee"] = new Dictionary<string, object?> { ["name"] = "A" } }, strict: true));
        Assert.Equal(TemplateErrors.UnknownField, unknown.Message);
        Assert.NotEmpty(TemplateSandbox.SyntaxErrors("{{ for }}"));
    }

    [Fact]
    public void S111c2_HRM_DOC_08_fields_a_template_uses_are_found_by_class()
    {
        TemplateFieldDefinition[] fields =
        [
            new("employee.name", "text", TemplateFieldClasses.General),
            new("employee.salary", "number", TemplateFieldClasses.Salary),
            new("earnings[].amount", "number", TemplateFieldClasses.Salary),
        ];
        Assert.Equal(["employee.name"], TemplateSandbox.UsedFields("Dear {{ employee.name }}", fields).Select(f => f.Path));
        Assert.Contains(TemplateSandbox.UsedFields("{{ employee.salary | math.format '0.00' }}", fields), f => f.FieldClass == TemplateFieldClasses.Salary);
        Assert.Contains(TemplateSandbox.UsedFields("{{ for e in earnings }}{{ e.amount }}{{ end }}", fields), f => f.Path == "earnings[].amount");
        Assert.Empty(TemplateSandbox.UsedFields("salary is private (not in a block)", fields));
    }

    [Fact]
    public void S111c2_KRN_DOC_05_sample_values_follow_the_declared_fields_and_make_a_strict_render_pass()
    {
        TemplateFieldDefinition[] fields = [new("employee.name", "text", "general"), new("earnings[].amount", "number", "salary"), new("joined", "date", "general")];
        var sample = TemplateSandbox.SampleValues(fields);
        Assert.Contains("Sample name", Render("{{ employee.name }}", sample, strict: true), StringComparison.Ordinal);
        Assert.Contains("1234.5", Render("{{ for e in earnings }}{{ e.amount }}{{ end }}", sample, strict: true), StringComparison.Ordinal);
    }

    [Fact]
    public void S111c2_KRN_DOC_05_the_barcode_table_is_the_standard_one_and_matches_a_reference_encoding()
    {
        Assert.Equal(107, Code128.Table.Count);
        Assert.All(Code128.Table.Take(106), p => Assert.Equal(11, p.Sum(ch => ch - '0')));
        Assert.Equal(13, Code128.Table[106].Sum(ch => ch - '0'));
        Assert.Equal(107, Code128.Table.Distinct().Count());
        Assert.Equal([104, 40, 69, 76, 76, 79, 0, 17, 18, 19, 98, 106], Code128.Symbols("Hello 123")); // reference: reportlab Code128 (set B)
        Assert.Throws<ArgumentException>(() => Code128.Symbols("Urdu: ا"));
    }

    [Fact]
    public void S111c2_KRN_DOC_05_qr_codes_and_barcodes_are_available_inside_a_template_as_images()
    {
        var html = Render("{{ qr 'https://example.test/verify/42' }}|{{ barcode 'EMP-0042' }}");
        var parts = html.Split('|');
        Assert.StartsWith("<svg", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("<svg", parts[1], StringComparison.Ordinal);
        Assert.Contains("<rect", parts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void S111c2_KRN_DOC_05_urdu_pages_are_right_to_left_and_the_fonts_travel_with_the_product()
    {
        var page = DocumentHtml.Page("<p>x</p>", "ur");
        Assert.Contains("dir=\"rtl\"", page, StringComparison.Ordinal);
        Assert.Contains("Noto Nastaliq Urdu", page, StringComparison.Ordinal);
        Assert.Contains("dir=\"ltr\"", DocumentHtml.Page("<p>x</p>", "en"), StringComparison.Ordinal);
        Assert.True(DocumentFonts.Css.Length > 1_000_000);
        Assert.Contains("default-src 'none'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void S111c2_KRN_DOC_05_the_template_texts_and_permissions_exist_in_english_and_urdu()
    {
        var folder = Path.Combine(Root(), "src/Platform/Erp.Kernel.Contracts/Documents/Localization");
        var en = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "documents.en.json")))!;
        var ur = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "documents.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(TemplatePermissions.All, p => Assert.Contains(p.DescriptionKey, en.Keys));
        Assert.All(typeof(TemplateErrors).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!), key => Assert.Contains(key, en.Keys));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Erp.sln not found.");
    }
}
