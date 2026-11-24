using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Infrastructure.Policies;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Packs;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Packs;

namespace Erp.IntegrationTests.Packs;

/// <summary>One object of a test pack: type, key and value (source and the verify notice are added).</summary>
public sealed record TestObject(string Type, string Key, object Value);

/// <summary>
/// Test packs of slice S1.23b: built from a temporary source folder exactly as <c>pack build</c> does, signed with a pack key registered
/// in the fixture's control plane (purpose <c>pack</c>) as <c>pack sign</c> does, and published with <c>pack publish</c>'s code.
/// Pack ids are unique per test because the catalogue is shared by the fixture.
/// </summary>
public static class PackTestSupport
{
    private static readonly ConcurrentDictionary<TenancyFixture, Lazy<Task<(ECDsa Key, string KeyId)>>> Keys = new();

    /// <summary>The handlers of every module, with the test policy types (as the API host of the tests has them).</summary>
    public static IReadOnlyList<IPackObjectHandler> Handlers { get; } =
        [.. KernelPackHandlers.All, .. HrmPackHandlers.Create(new PolicyTypeCatalog([new PolicyTypeSource(PolicyServices.TypeManifest), new PolicyTypeSource(Policies.PolicyTestSupport.TestTypes)]))];

    public static string NewId(string prefix = "t") => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The fixture's pack signing key (registered once).</summary>
    public static Task<(ECDsa Key, string KeyId)> KeyAsync(TenancyFixture fx) =>
        Keys.GetOrAdd(fx, f => new Lazy<Task<(ECDsa, string)>>(async () =>
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var id = "pack-" + f.Suffix;
            await new ControlPlaneAdmin(f.Options, f.Secrets, f.Clock).AddSigningKeyAsync(f.Admin, id, "pack", key.ExportSubjectPublicKeyInfo(), CancellationToken.None);
            // The API tests' clock is cut to whole seconds and may move back; the key is valid from the day before.
            await using var cp = await f.OpenControlPlaneAsync(CancellationToken.None);
            await TenancyFixture.ExecAsync(cp, "update cp.signing_keys set valid_from = valid_from - interval '1 day' where key_id = $1", CancellationToken.None, id);
            return (key, id);
        })).Value;

    /// <summary>Writes a pack source folder (pack.json and one file per object) and returns its path.</summary>
    public static string Source(TenancyFixture fx, string id, string version, IEnumerable<TestObject> objects, string kind = "country", string? country = "PK",
        string entitlement = "pack.country.pk", string compatibleFrom = "1.0", string? compatibleTo = null, object[]? dependencies = null, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(fx);
        var folder = fx.TempFile("pack-" + id + "-" + version + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(folder, "objects"));
        var header = new JsonObject
        {
            ["format"] = 1, ["id"] = id, ["kind"] = kind, ["version"] = version, ["entitlement"] = entitlement,
            ["title"] = new JsonObject { ["en"] = "Test pack " + id, ["ur"] = "آزمائشی پیک " + id },
            ["releaseNotes"] = new JsonObject { ["en"] = notes ?? "Release " + version },
            ["compatible"] = new JsonObject { ["from"] = compatibleFrom, ["to"] = compatibleTo },
            ["dependencies"] = JsonSerializer.SerializeToNode(dependencies ?? []),
        };
        if (country is not null)
        {
            header["country"] = country;
        }

        File.WriteAllText(Path.Combine(folder, "pack.json"), header.ToJsonString());
        var n = 0;
        foreach (var o in objects)
        {
            var file = new JsonObject
            {
                ["type"] = o.Type, ["key"] = o.Key, ["source"] = "Test data written for slice S1.23b", ["verify"] = PackArchive.VerifyNotice,
                ["value"] = JsonSerializer.SerializeToNode(o.Value),
            };
            Directory.CreateDirectory(Path.Combine(folder, "objects", o.Type));
            File.WriteAllText(Path.Combine(folder, "objects", o.Type, $"{n++:D4}.json"), file.ToJsonString());
        }

        return folder;
    }

    /// <summary>Builds and signs a pack (signed by the fixture's pack key unless another key is given).</summary>
    public static async Task<byte[]> SignedAsync(TenancyFixture fx, string folder, ECDsa? key = null, string? keyId = null)
    {
        var (fixtureKey, fixtureKeyId) = await KeyAsync(fx);
        var (_, pack) = PackArchive.Build(folder, Handlers);
        return PackArchive.Sign(pack, keyId ?? fixtureKeyId, (key ?? fixtureKey).ExportECPrivateKeyPem());
    }

    /// <summary>Builds, signs and publishes a pack; returns the signed file.</summary>
    public static async Task<byte[]> PublishAsync(TenancyFixture fx, string id, string version, IEnumerable<TestObject> objects, string entitlement = "pack.country.pk", string? notes = null)
    {
        var file = await SignedAsync(fx, Source(fx, id, version, objects, entitlement: entitlement, notes: notes));
        await PackCatalog.PublishAsync(fx.Options.ConnectionString, fx.Admin, file, fx.Clock, CancellationToken.None);
        return file;
    }

    /// <summary>The usual content of a test pack: a list with two items, a setting default, a label, a calendar with one holiday and a bank.</summary>
    public static List<TestObject> Content(string id, int fiscalMonth = 7, string itemLabel = "Spouse", int year = 2031) =>
    [
        new("option_list", Code(id), new { name = new { en = "Relations " + id, ur = "رشتے" } }),
        new("option_list_item", Code(id) + ":spouse", new { label = new { en = itemLabel, ur = "شریکِ حیات" }, seq = 10 }),
        new("option_list_item", Code(id) + ":child", new { label = new { en = "Child", ur = "بچہ" }, seq = 20 }),
        new("setting", "cfg.numbering.fiscal_year_start_month", new { value = fiscalMonth }),
        new("label", "labels.test." + Code(id), new { texts = new { en = "Pack term", ur = "پیک کی اصطلاح" } }),
        new("holiday_calendar", Code(id) + "-cal", new { name = new { en = "Federal holidays " + id }, country = "PK" }),
        new("holiday", Code(id) + "-cal:" + year + "-08-14", new { name = new { en = "Independence Day", ur = "یومِ آزادی" }, kind = "public" }),
        new("bank", "PK:" + Code(id), new { name = new { en = "Test Bank " + id }, swiftCode = "TESTPKKA" }),
    ];

    /// <summary>A code from a pack id (option list and calendar codes take letters, digits, '_', '.', '-').</summary>
    public static string Code(string id) => id.Replace("-", "_", StringComparison.Ordinal);
}
