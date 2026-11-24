using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Packs;
using Erp.Kernel.Infrastructure.Packs;

namespace Erp.UnitTests.Packs;

/// <summary>
/// The pack format of slice S1.23b (Build items 7 and 8, rulings Q6, Q7, Q11): FR-KRN-CFG-01 (a signed, versioned bundle with a manifest of
/// contents, compatible product versions, dependencies and merge rules), GHR-08 and GHR-15 (no statutory content outside a validated
/// pack release; AS-4, R-002), and the shape checks of every object handler.
/// </summary>
public sealed class PackFormatTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("erp-pack-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static IReadOnlyList<IPackObjectHandler> Handlers { get; } = [.. KernelPackHandlers.All, .. HrmPackHandlers.Default];

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string Source(params (string Type, string Key, object Value)[] objects) => Source(null, objects);

    private string Source(Action<JsonObject>? header, params (string Type, string Key, object Value)[] objects)
    {
        var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, "objects"));
        var h = new JsonObject
        {
            ["id"] = "country-xx", ["kind"] = "country", ["version"] = "1.0.0", ["country"] = "PK", ["entitlement"] = "pack.country.pk",
            ["title"] = new JsonObject { ["en"] = "Test" }, ["compatible"] = new JsonObject { ["from"] = "1.0" },
        };
        header?.Invoke(h);
        File.WriteAllText(Path.Combine(folder, "pack.json"), h.ToJsonString());
        var n = 0;
        foreach (var (type, key, value) in objects)
        {
            File.WriteAllText(Path.Combine(folder, "objects", $"{n++:D3}.json"), new JsonObject
            {
                ["type"] = type, ["key"] = key, ["source"] = "unit test", ["verify"] = PackArchive.VerifyNotice, ["value"] = JsonSerializer.SerializeToNode(value),
            }.ToJsonString());
        }

        return folder;
    }

    private static (string, string, object) Holiday(string date = "2031-08-14") => ("holiday", "pk-federal:" + date, new { name = new { en = "Independence Day" }, kind = "public" });

    [Fact]
    public void FR_KRN_CFG_01_build_writes_a_manifest_with_the_hash_of_every_file_and_a_merge_rule_per_type_and_the_signature_verifies()
    {
        var folder = Source(("holiday_calendar", "pk-federal", new { name = new { en = "Federal" }, country = "PK" }), Holiday());
        var (manifest, pack) = PackArchive.Build(folder, Handlers);
        Assert.True(File.Exists(Path.Combine(folder, PackArchive.ManifestFile)));
        Assert.Equal(2, manifest.Objects.Count);
        Assert.All(manifest.Objects, o => Assert.Matches("^[0-9a-f]{64}$", o.Sha256));
        Assert.Equal(PackManifest.KeepTenant, manifest.MergeRules["holiday"]);
        Assert.False(PackArchive.Open(pack).IsSigned);

        var signed = PackArchive.Sign(pack, "k1", _key.ExportECPrivateKeyPem());
        var opened = PackArchive.Open(signed);
        Assert.Equal("k1", opened.KeyId);
        Assert.True(opened.Verify(_key.ExportSubjectPublicKeyInfo()));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(opened.Verify(other.ExportSubjectPublicKeyInfo()));
        var again = PackArchive.Open(PackArchive.Sign(signed, "k2", _key.ExportECPrivateKeyPem())); // signing a signed pack replaces its signature
        Assert.Equal("k2", again.KeyId);
        Assert.True(again.Verify(_key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void FR_KRN_CFG_01_a_changed_byte_an_unlisted_file_or_an_unsafe_path_is_refused()
    {
        var (_, pack) = PackArchive.Build(Source(("holiday_calendar", "pk-federal", new { name = new { en = "Federal" } }), Holiday()), Handlers);
        Assert.Contains("does not match its hash", Assert.Throws<InvalidDataException>(() => PackArchive.Open(Change(pack, e => e.StartsWith("objects/", StringComparison.Ordinal), b => { b[^2] ^= 1; return b; }))).Message, StringComparison.Ordinal);
        Assert.Contains("does not list", Assert.Throws<InvalidDataException>(() => PackArchive.Open(Add(pack, "objects/extra.json", "{}"u8.ToArray()))).Message, StringComparison.Ordinal);
        Assert.Contains("unsafe", Assert.Throws<InvalidDataException>(() => PackArchive.Open(Add(pack, "../evil.json", "{}"u8.ToArray()))).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => PackArchive.Open("not a zip"u8.ToArray()));
        Assert.False(PackArchive.SafePath("a/../b"));
        Assert.False(PackArchive.SafePath("/abs"));
        Assert.True(PackArchive.SafePath("objects/holiday/0001.json"));
    }

    [Fact]
    public void GHR_08_GHR_15_statutory_object_types_and_unknown_types_are_refused_at_build()
    {
        foreach (var type in new[] { "tax_table", "contribution_rate", "leave_statute", "working_time_limit", "statutory_form" })
        {
            var error = Assert.Throws<InvalidDataException>(() => PackArchive.Build(Source((type, "pk:2027", new { rate = 1 })), Handlers));
            Assert.Contains("statutory content", error.Message, StringComparison.Ordinal);
        }

        Assert.Contains("no module handles", Assert.Throws<InvalidDataException>(() => PackArchive.Build(Source(("gadget", "x", new { })), Handlers)).Message, StringComparison.Ordinal);
        Assert.All(PackManifest.StatutoryTypes, t => Assert.DoesNotContain(Handlers, h => h.ObjectType == t));
    }

    [Fact]
    public void FR_KRN_CFG_01_every_object_file_names_its_source_and_carries_the_verify_notice()
    {
        var folder = Source(Holiday());
        var file = Directory.GetFiles(Path.Combine(folder, "objects"))[0];
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["verify"] = "checked";
        File.WriteAllText(file, json.ToJsonString());
        Assert.Contains("verify", Assert.Throws<InvalidDataException>(() => PackArchive.Build(folder, Handlers)).Message, StringComparison.Ordinal);
        json["verify"] = PackArchive.VerifyNotice;
        json.Remove("source");
        File.WriteAllText(file, json.ToJsonString());
        Assert.Contains("'source' is required", Assert.Throws<InvalidDataException>(() => PackArchive.Build(folder, Handlers)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version", "1.0")]
    [InlineData("id", "PK")]
    [InlineData("kind", "statute")]
    [InlineData("country", "Pakistan")]
    public void FR_KRN_CFG_01_the_manifest_is_checked_field_by_field(string field, string value)
    {
        var folder = Source(h => h[field] = value, Holiday());
        Assert.Throws<InvalidDataException>(() => PackArchive.Build(folder, Handlers));
    }

    [Fact]
    public void FR_KRN_CFG_01_compatible_versions_dependencies_and_merge_rules()
    {
        var (manifest, _) = PackArchive.Build(Source(h =>
        {
            h["compatible"] = new JsonObject { ["from"] = "1.0", ["to"] = "1.2" };
            h["dependencies"] = new JsonArray(new JsonObject { ["id"] = "country-base", ["from"] = "1.0.0" });
        }, Holiday()), Handlers);
        Assert.True(manifest.CompatibleWith("1.0"));
        Assert.True(manifest.CompatibleWith("1.2"));
        Assert.False(manifest.CompatibleWith("1.3"));
        Assert.False(manifest.CompatibleWith("0.9"));
        Assert.Equal("country-base", manifest.Dependencies.Single().Id);
        Assert.Throws<InvalidDataException>(() => PackArchive.Build(Source(h => h["mergeRules"] = new JsonObject { ["holiday"] = "take_pack" }, Holiday()), Handlers));
        Assert.Throws<InvalidDataException>(() => PackArchive.Build(Source(h => h["dependencies"] = new JsonArray(new JsonObject { ["id"] = "country-xx", ["from"] = "1.0.0" }), Holiday()), Handlers));
        Assert.True(PackVersions.Compare("1.0.10", "1.0.9") > 0);
        Assert.True(PackVersions.Compare("2.0.0", "10.0.0") < 0);
    }

    [Theory]
    [InlineData("holiday", "pk-federal:2031-02-30", """{"name":{"en":"x"},"kind":"public"}""")]
    [InlineData("holiday", "pk-federal:2031-02-01", """{"name":{"en":"x"},"kind":"festival"}""")]
    [InlineData("bank", "pk:hbl", """{"name":{"en":"HBL"}}""")]
    [InlineData("bank", "PK:hbl", """{"name":{"en":"HBL"},"swiftCode":"hbl"}""")]
    [InlineData("setting", "Time Zone", """{"value":"Asia/Karachi"}""")]
    [InlineData("setting", "org.time_zone", """{"value":"Asia/Karachi","extra":1}""")]
    [InlineData("label", "labels.x", """{"texts":{"fr":"x"}}""")]
    [InlineData("option_list_item", "relations", """{"label":{"en":"x"},"seq":1}""")]
    [InlineData("option_list_item", "relations:spouse", """{"label":{"ur":"x"},"seq":1}""")]
    [InlineData("holiday_calendar", "pk federal", """{"name":{"en":"x"}}""")]
    [InlineData("policy", "probation:PK", """{"name":{"en":"x"},"parameters":{"months":-1}}""")]
    [InlineData("policy", "no_such_type:PK", """{"name":{"en":"x"},"parameters":{}}""")]
    public void FR_KRN_CFG_01_each_handler_refuses_a_bad_key_or_value(string type, string key, string value)
    {
        var handler = Handlers.Single(h => h.ObjectType == type);
        Assert.NotNull(handler.Validate(key, JsonNode.Parse(value)!.AsObject()));
    }

    [Theory]
    [InlineData("holiday", "pk-federal:2031-03-23", """{"name":{"en":"Pakistan Day","ur":"یوم پاکستان"},"kind":"public","tentative":false}""")]
    [InlineData("bank", "PK:hbl", """{"name":{"en":"Habib Bank Limited"},"swiftCode":"HABBPKKA"}""")]
    [InlineData("setting", "org.time_zone", """{"value":"Asia/Karachi"}""")]
    [InlineData("label", "labels.hrm.employments.grade_id", """{"texts":{"en":"Scale"}}""")]
    [InlineData("option_list_item", "relations:spouse", """{"label":{"en":"Spouse"},"seq":1}""")]
    public void FR_KRN_CFG_01_valid_objects_pass_and_normalize_to_the_shape_they_are_read_back_in(string type, string key, string value)
    {
        var handler = Handlers.Single(h => h.ObjectType == type);
        var json = JsonNode.Parse(value)!.AsObject();
        Assert.Null(handler.Validate(key, json));
        var normal = handler.Normalize(json);
        Assert.Equal(PackArchive.Hash(normal), PackArchive.Hash(handler.Normalize(normal))); // normalizing twice changes nothing
    }

    [Fact]
    public void FR_HRM_POL_06_item_keys_of_company_objects_split_into_the_key_and_the_company()
    {
        var company = Guid.CreateVersion7();
        Assert.Equal(("probation:PK", (Guid?)company), PackInstaller.SplitKey("probation:PK@" + company.ToString("D")));
        Assert.Equal(("pk-federal:2031-08-14", (Guid?)null), PackInstaller.SplitKey("pk-federal:2031-08-14"));
        Assert.True(PackInstaller.Changed(null, "abc"));
        var state = new PackObjectState(new JsonObject { ["value"] = 7 }, "cfg.setting_values", Guid.Empty);
        Assert.False(PackInstaller.Changed(state, PackArchive.Hash(new JsonObject { ["value"] = 7 })));
        Assert.True(PackInstaller.Changed(state, PackArchive.Hash(new JsonObject { ["value"] = 1 })));
    }

    [Fact]
    public void FR_ADM_PKG_01_the_texts_of_the_pack_manager_exist_in_both_languages_and_every_key_in_use_has_one()
    {
        var root = Erp.CodeGen.RepositoryRoot.Find(AppContext.BaseDirectory);
        var folder = Path.Combine(root, "src/Platform/Erp.Kernel.Contracts/Packs/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "packs.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "packs.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("after_start", en["_load"]); // loaded after start-up with the module texts, not in the shell (S1.23b, budget)
        Assert.All(ur.Where(p => !p.Key.StartsWith('_')).Select(p => p.Value), v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(PackPermissions.All, p => Assert.True(en.ContainsKey(p.DescriptionKey), p.Code));
        var used = Directory.GetFiles(Path.Combine(root, "src/Platform/Erp.Kernel.Infrastructure/Packs"), "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.packs\\.[a-z_]+)\"").Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
    }

    private static byte[] Change(byte[] pack, Func<string, bool> which, Func<byte[], byte[]> change) => Rebuild(pack, (name, bytes) => which(name) ? change(bytes) : bytes, null, null);

    private static byte[] Add(byte[] pack, string name, byte[] bytes) => Rebuild(pack, (_, b) => b, name, bytes);

    private static byte[] Rebuild(byte[] pack, Func<string, byte[], byte[]> map, string? extraName, byte[]? extra)
    {
        using var source = new ZipArchive(new MemoryStream(pack), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var changed = false;
            foreach (var entry in source.Entries)
            {
                using var s = entry.Open();
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                var bytes = copy.ToArray();
                if (!changed)
                {
                    var mapped = map(entry.FullName, bytes);
                    changed = !ReferenceEquals(mapped, bytes) || !mapped.AsSpan().SequenceEqual(bytes);
                    bytes = mapped;
                }

                using var t = zip.CreateEntry(entry.FullName).Open();
                t.Write(bytes);
            }

            if (extraName is not null)
            {
                using var t = zip.CreateEntry(extraName).Open();
                t.Write(extra);
            }
        }

        return output.ToArray();
    }
}
