using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Backup;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;

namespace Erp.UnitTests.Tenancy;

/// <summary>Slice S1.03: the pure rules of tenancy, signatures, secrets and encrypted backups.</summary>
public sealed class TenancyUnitTests
{
    [Theory]
    [InlineData("active", "ready", "active", TenantAccessMode.Full)]
    [InlineData("active", "ready", "warning", TenantAccessMode.Full)]
    [InlineData("active", "ready", "grace", TenantAccessMode.Full)]
    [InlineData("active", "migrating", "active", TenantAccessMode.Full)]
    [InlineData("active", "ready", "read_only", TenantAccessMode.ReadOnly)]
    [InlineData("active", "ready", "locked", TenantAccessMode.Locked)]
    [InlineData("active", "ready", null, TenantAccessMode.Locked)]
    [InlineData("suspended", "ready", "active", TenantAccessMode.Locked)]
    [InlineData("suspended", "ready", "read_only", TenantAccessMode.Locked)]
    [InlineData("active", "maintenance", "active", TenantAccessMode.Unavailable)]
    [InlineData("provisioning", "creating", "active", TenantAccessMode.Unavailable)]
    [InlineData("offboarding", "ready", "active", TenantAccessMode.Unavailable)]
    [InlineData("deleted", "ready", "active", TenantAccessMode.NotFound)]
    [InlineData("active", "retired", "active", TenantAccessMode.NotFound)]
    [InlineData("something_new", "ready", "active", TenantAccessMode.NotFound)]
    public void FR_KRN_TEN_09_access_mode_is_the_most_restrictive_of_tenant_database_and_entitlement(string tenant, string database, string? entitlement, TenantAccessMode expected) =>
        Assert.Equal(expected, TenantAccessPolicy.Decide(tenant, database, entitlement));

    [Theory]
    [InlineData("Acme.ERP.example.com", "acme.erp.example.com")]
    [InlineData("acme.erp.example.com:8443", "acme.erp.example.com")]
    [InlineData("acme.erp.example.com.", "acme.erp.example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("[::1]:80", null)]
    [InlineData("acme..example.com", null)]
    [InlineData("acme_x.example.com", null)]
    [InlineData("-acme.example.com", null)]
    [InlineData("acme.example.com:abc", null)]
    [InlineData("acme.example.com/../b", null)]
    [InlineData("127.0.0.1", null)] // S1.03 review finding 12: customers are reached by name only
    [InlineData("10.0.0.5:8080", null)]
    [InlineData("2024.erp.example.com", "2024.erp.example.com")]
    public void FR_KRN_TEN_04_host_names_are_normalized_or_refused(string host, string? expected) =>
        Assert.Equal(expected, HostNames.Normalize(host));

    [Theory]
    [InlineData("acme", true)]
    [InlineData("a1", true)]
    [InlineData("a", false)]
    [InlineData("1acme", false)]
    [InlineData("Acme", false)]
    [InlineData("ac-me", false)]
    [InlineData("ac_me", false)]
    [InlineData("acme\"; drop database x; --", false)]
    [InlineData("abcdefghijabcdefghijabcdefghijk", false)]
    public void S103_customer_codes_are_safe_for_host_database_and_login_names(string code, bool valid)
    {
        var error = Record.Exception(() => TenantCodes.Validate(code));
        Assert.Equal(valid, error is null);
    }

    private static EntitlementPayload Payload(string tenant = "acme") =>
        new(tenant, 1, "business", new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31),
            new JsonObject { ["modules"] = new JsonArray("hrm"), ["limits"] = new JsonObject { ["employees"] = 250, ["users"] = 50 }, ["note"] = "اردو" });

    [Fact]
    public void FR_KRN_ENT_01_a_signed_entitlement_verifies_with_its_public_key_only()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = SignedEntitlement.Sign(Payload(), "k1", issuer.ExportECPrivateKeyPem());
        var file = signed.ToFileJson();

        var parsed = SignedEntitlement.Parse(file);
        Assert.True(parsed.Verify(issuer.ExportSubjectPublicKeyInfo()));
        Assert.False(parsed.Verify(other.ExportSubjectPublicKeyInfo()));
        Assert.Equal(["hrm"], parsed.Payload.Modules);
    }

    [Theory]
    [InlineData("\"employees\": 250", "\"employees\": 9999")]
    [InlineData("\"tenant\": \"acme\"", "\"tenant\": \"globex\"")]
    [InlineData("\"edition\": \"business\"", "\"edition\": \"enterprise\"")]
    [InlineData("\"expires_at\": \"2027-10-31\"", "\"expires_at\": \"2099-12-31\"")]
    public void FR_KRN_ENT_01_any_change_to_a_signed_entitlement_breaks_the_signature(string from, string to)
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var file = SignedEntitlement.Sign(Payload(), "k1", issuer.ExportECPrivateKeyPem()).ToFileJson();
        Assert.Contains(from, file, StringComparison.Ordinal);
        Assert.False(SignedEntitlement.Parse(file.Replace(from, to, StringComparison.Ordinal)).Verify(issuer.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void FR_KRN_ENT_01_the_signature_does_not_depend_on_key_order_or_white_space()
    {
        var a = JsonNode.Parse("""{"b": 1, "a": {"y": [1, 2], "x": "ü"}}""");
        var b = JsonNode.Parse("""{ "a" : { "x" : "ü", "y" : [ 1 , 2 ] } , "b" : 1 }""");
        Assert.Equal(CanonicalJson.Bytes(a), CanonicalJson.Bytes(b));
        // Non-ASCII text is escaped as \u00FC, so the signed bytes are plain ASCII and identical on every platform.
        Assert.Equal("{\"a\":{\"x\":\"\\u00FC\",\"y\":[1,2]},\"b\":1}", Encoding.UTF8.GetString(CanonicalJson.Bytes(a)));
    }

    [Fact]
    public void FR_KRN_ENT_01_an_entitlement_without_modules_or_with_an_unknown_edition_is_refused()
    {
        var noModules = Payload().ToJson();
        noModules["claims"]!.AsObject().Remove("modules");
        Assert.Throws<InvalidDataException>(() => EntitlementPayload.FromJson(noModules));
        var badEdition = Payload().ToJson();
        badEdition["edition"] = "platinum";
        Assert.Throws<InvalidDataException>(() => EntitlementPayload.FromJson(badEdition));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1048576)]
    [InlineData(1048577)]
    [InlineData(3 * 1048576 + 17)]
    public async Task ADM_DAT_08_backup_encryption_round_trips_any_size(int size)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = RandomNumberGenerator.GetBytes(size);
        using var encrypted = new MemoryStream();
        await BackupCipher.EncryptAsync(new MemoryStream(plain), encrypted, key, ct);
        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        Assert.Equal(size, await BackupCipher.DecryptAsync(encrypted, decrypted, key, ct));
        Assert.Equal(plain, decrypted.ToArray());
    }

    [Fact]
    public async Task ADM_DAT_08_a_backup_that_is_altered_truncated_or_read_with_the_wrong_key_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = RandomNumberGenerator.GetBytes(32);
        using var encrypted = new MemoryStream();
        await BackupCipher.EncryptAsync(new MemoryStream(RandomNumberGenerator.GetBytes(2 * 1048576 + 5)), encrypted, key, ct);
        var bytes = encrypted.ToArray();

        var altered = bytes.ToArray();
        altered[100] ^= 1;
        var truncated = bytes[..(1048576 + 13 + 21)]; // the first chunk only: its "final" flag is off
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCipher.DecryptAsync(new MemoryStream(altered), Stream.Null, key, ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCipher.DecryptAsync(new MemoryStream(truncated), Stream.Null, key, ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCipher.DecryptAsync(new MemoryStream(bytes), Stream.Null, RandomNumberGenerator.GetBytes(32), ct));
    }

    [Fact]
    public async Task S103_secret_store_keeps_values_encrypted_and_refuses_the_wrong_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("erp-secrets-").FullName;
        try
        {
            var path = Path.Combine(folder, "secrets.bin");
            var key = RandomNumberGenerator.GetBytes(32);
            using (var store = new EncryptedFileSecretStore(path, key))
            {
                await store.PutAsync("tenant/acme/rw", "s3cret-value", ct);
                await store.PutAsync("tenant/acme/ro", "other", ct);
                Assert.Equal("s3cret-value", await store.GetAsync("tenant/acme/rw", ct));
                Assert.Null(await store.GetAsync("tenant/globex/rw", ct));
            }

            Assert.DoesNotContain("s3cret-value", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct)), StringComparison.Ordinal);
            using (var reopened = new EncryptedFileSecretStore(path, key))
            {
                Assert.Equal("other", await reopened.GetAsync("tenant/acme/ro", ct));
            }

            using var wrong = new EncryptedFileSecretStore(path, RandomNumberGenerator.GetBytes(32));
            await Assert.ThrowsAsync<InvalidDataException>(() => wrong.GetAsync("tenant/acme/rw", ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void S103_database_credentials_never_print_their_password()
    {
        var credential = new DatabaseCredential("erp_t_acme_rw", DatabaseCredential.NewPassword());
        Assert.DoesNotContain(credential.Password, credential.ToString(), StringComparison.Ordinal);
        Assert.True(credential.Password.Length >= 40);
        Assert.Equal(credential, DatabaseCredential.Parse(credential.ToSecret()));
    }

    [Theory]
    [InlineData("V202611150002__kernel__org.sql", "kernel", "202611150002")]
    [InlineData("V202611150101__hrm__grants.sql", "hrm", "202611150101")]
    public void S103_migration_file_names_give_module_and_version(string name, string module, string version)
    {
        var script = MigrationScript.FromPath("/x/" + name);
        Assert.Equal((module, version, name), (script.Module, script.Version, script.Name));
    }

    [Fact]
    public void S103_migration_files_that_do_not_follow_the_naming_rule_are_refused() =>
        Assert.Throws<InvalidDataException>(() => MigrationScript.FromPath("/x/001_create_tables.sql"));
}
