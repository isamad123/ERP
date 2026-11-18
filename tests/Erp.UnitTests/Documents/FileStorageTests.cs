using System.Text;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Infrastructure.Documents;

namespace Erp.UnitTests.Documents;

/// <summary>The file store core of slice S1.11c1 (KRN-DOC-02, KRN-TEN-03): content checks, encryption and the folder store.</summary>
public sealed class FileStorageTests
{
    [Fact]
    public void S111c1_KRN_DOC_02_the_type_comes_from_the_content_not_the_name()
    {
        Assert.Equal("pdf", FileValidation.Detect("%PDF-1.7 rest"u8, "x.txt"));
        Assert.Equal("png", FileValidation.Detect(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "x.pdf"));
        Assert.Equal("jpeg", FileValidation.Detect(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "x.png"));
        Assert.Equal("word", FileValidation.Detect(new byte[] { 0x50, 0x4B, 3, 4 }, "letter.docx"));
        Assert.Null(FileValidation.Detect(new byte[] { 0x50, 0x4B, 3, 4 }, "tool.zip"));
        Assert.Null(FileValidation.Detect(new byte[] { 0x4D, 0x5A, 0x90, 0 }, "setup.pdf"));
        Assert.Equal("csv", FileValidation.Detect("a,b\n1,2\n"u8, "x.csv"));
        Assert.Equal("text", FileValidation.Detect("hello"u8, "x.bin"));
    }

    [Fact]
    public void S111c1_KRN_DOC_02_the_checksum_is_sha256_in_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", FileValidation.Checksum("abc"u8));
    }

    [Fact]
    public async Task S111c1_KRN_TEN_03_a_file_encrypted_for_one_customer_cannot_be_read_with_another_customers_key_or_for_another_file()
    {
        var secrets = new MemorySecrets();
        await TenantFileKeys.EnsureAsync(secrets, "acme", TestContext.Current.CancellationToken);
        await TenantFileKeys.EnsureAsync(secrets, "globex", TestContext.Current.CancellationToken);
        var acme = new FileEncryption(new Tenant("acme"), secrets);
        var globex = new FileEncryption(new Tenant("globex"), secrets);
        var id = Guid.NewGuid();
        var plain = Encoding.UTF8.GetBytes("secret contract");
        var cipher = await acme.EncryptAsync(id, plain, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(cipher), StringComparison.Ordinal);
        Assert.Equal(plain, await acme.DecryptAsync(id, cipher, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => globex.DecryptAsync(id, cipher, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => acme.DecryptAsync(Guid.NewGuid(), cipher, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S111c1_KRN_DOC_02_the_folder_store_keeps_keys_inside_its_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "erp-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFolderFileStore(root);
            await store.PutAsync("tenants/acme/files/1", [1, 2, 3], TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetAsync("tenants/acme/files/1", TestContext.Current.CancellationToken));
            Assert.True(await store.ExistsAsync("tenants/acme/files/1", TestContext.Current.CancellationToken));
            await store.DeleteAsync("tenants/acme/files/1", TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync("tenants/acme/files/1", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("../outside", TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class Tenant(string code) : ITenantContext
    {
        public bool IsResolved => true;

        public string TenantCode => code;

        public TenantAccessMode AccessMode => TenantAccessMode.Full;

        public IReadOnlySet<string> InstalledModules => new HashSet<string>();
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken) => Task.FromResult(_values.GetValueOrDefault(reference));

        public Task PutAsync(string reference, string value, CancellationToken cancellationToken)
        {
            _values[reference] = value;
            return Task.CompletedTask;
        }
    }
}
