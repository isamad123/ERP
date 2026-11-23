using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Kernel.Contracts.Setup;
using Erp.Kernel.Infrastructure.Setup;

namespace Erp.UnitTests.Setup;

/// <summary>Slice S1.18a: the set-up manifest rules and the texts of the readiness service (FR-ADM-ONB-01, -02, -03; CR-074).</summary>
public sealed partial class SetupManifestTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static string Manifest(string tasks, string module = "demo") =>
        $$"""{ "module": "{{module}}", "name_key": "setup.modules.{{module}}", "tasks": [ {{tasks}} ] }""";

    private static List<string> Problems(string json) => SetupManifest.Check(SetupManifest.Parse(json));

    [Fact]
    public void FR_ADM_ONB_03_the_kernel_manifest_is_valid_and_lists_steps_and_items()
    {
        var kernel = SetupManifest.Parse(SetupManifest.KernelJson);
        Assert.Empty(SetupManifest.Check(kernel));
        Assert.Equal(7, kernel.Tasks.Count(t => t.Kind == SetupTaskKinds.Step));
        Assert.Contains(kernel.Tasks, t => t is { Key: "numbering", Required: true, Link: "/settings/numbering" });
        Assert.All(kernel.Tasks.Where(t => t.Check is not null), t => Assert.StartsWith("kernel.", t.Check, StringComparison.Ordinal));
        _ = new SetupCatalog(Array.Empty<SetupManifestSource>()); // the kernel's manifest alone builds a catalogue
    }

    [Fact]
    public void FR_ADM_ONB_03_a_step_is_never_required_and_the_go_live_key_is_reserved()
    {
        Assert.Contains(Problems(Manifest("""{ "key": "a", "title_key": "t", "kind": "step", "required": true, "level": "company" }""")), p => p.Contains("never required", StringComparison.Ordinal));
        Assert.Contains(Problems(Manifest("""{ "key": "go_live", "title_key": "t", "kind": "item", "level": "company" }""")), p => p.Contains("go_live", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "key": "Bad Key", "title_key": "t", "kind": "item", "level": "company" }""", "key is lower-case")]
    [InlineData("""{ "key": "a", "title_key": "", "kind": "item", "level": "company" }""", "title_key")]
    [InlineData("""{ "key": "a", "title_key": "t", "kind": "task", "level": "company" }""", "kind is step or item")]
    [InlineData("""{ "key": "a", "title_key": "t", "kind": "item", "level": "world" }""", "level is customer or company")]
    [InlineData("""{ "key": "a", "title_key": "t", "kind": "item", "level": "company", "link": "https://example.com" }""", "link is a path")]
    [InlineData("""{ "key": "a", "title_key": "t", "kind": "item", "level": "company", "link": "//example.com" }""", "link is a path")]
    [InlineData("""{ "key": "a", "title_key": "t", "kind": "item", "level": "company", "check": "kernel.x" }""", "check names a check of the module")]
    public void FR_ADM_ONB_03_a_wrong_task_is_refused_with_the_reason(string task, string expected) =>
        Assert.Contains(Problems(Manifest(task)), p => p.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void FR_ADM_ONB_03_a_duplicate_key_or_module_stops_the_catalogue()
    {
        var twice = Manifest("""{ "key": "a", "title_key": "t", "kind": "item", "level": "company" }, { "key": "a", "title_key": "t", "kind": "item", "level": "company" }""");
        Assert.Contains(Problems(twice), p => p.Contains("duplicate key", StringComparison.Ordinal));
        var module = SetupManifest.Parse(Manifest("""{ "key": "a", "title_key": "t", "kind": "item", "level": "company" }"""));
        Assert.Throws<InvalidDataException>(() => new SetupCatalog([module, module]));
    }

    [Fact]
    public void FR_ADM_ONB_03_a_module_adds_its_items_without_the_kernel_knowing_them()
    {
        var module = Manifest("""{ "key": "leave_policy", "title_key": "setup.items.leave_policy", "kind": "item", "required": true, "level": "company", "check": "demo.leave_policy", "link": "/time/leave" }""");
        var catalog = new SetupCatalog([new SetupManifestSource(module)]);
        Assert.Equal(["kernel", "demo"], catalog.Modules.Select(m => m.ModuleId).ToArray());
        Assert.True(catalog.Find("demo")!.Tasks[0].Required);
    }

    [Fact]
    public void FR_ADM_ONB_03_the_texts_of_the_readiness_service_exist_in_both_languages_and_every_key_in_use_has_one()
    {
        var folder = Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Setup/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "setup.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "setup.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(SetupPermissions.All, p => Assert.True(en.ContainsKey(p.DescriptionKey), p.Code));
        var used = Directory.GetFiles(Path.Combine(Root, "src/Platform/Erp.Kernel.Infrastructure/Setup"), "*.cs")
            .SelectMany(f => ErrorKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.All(used.Where(k => !k.StartsWith("errors.data.", StringComparison.Ordinal)), k => Assert.True(en.ContainsKey(k), k));
    }

    [GeneratedRegex("\"(errors\\.[a-z_]+\\.[a-z_]+)\"")]
    private static partial Regex ErrorKey();
}
