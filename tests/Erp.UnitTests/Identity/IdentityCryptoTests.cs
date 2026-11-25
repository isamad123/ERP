using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Erp.UnitTests.Identity;

/// <summary>Slice S1.05: password hashing, field encryption, opaque tokens, token signing and the message catalogue.</summary>
public sealed class IdentityCryptoTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void FR_KRN_IAM_01_argon2id_hashes_are_salted_verifiable_and_record_their_parameters()
    {
        using var hasher = new Argon2idPasswordHasher();
        var one = hasher.Hash("Correct-Horse-7");
        var two = hasher.Hash("Correct-Horse-7");
        Assert.NotEqual(one, two); // per-user salt
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", one, StringComparison.Ordinal);
        Assert.True(hasher.Verify("Correct-Horse-7", one, "argon2id-v1", out var rehash));
        Assert.False(rehash);
        Assert.False(hasher.Verify("correct-horse-7", one, "argon2id-v1", out _));
        Assert.False(hasher.Verify("Correct-Horse-7", one, "bcrypt", out _)); // unknown algorithm label
        Assert.False(hasher.Verify("Correct-Horse-7", "$argon2id$v=19$m=19456,t=2,p=1$broken", "argon2id-v1", out _));
    }

    [Fact]
    public void FR_KRN_IAM_01_a_hash_made_with_weaker_parameters_is_accepted_and_flagged_for_rehash()
    {
        using var hasher = new Argon2idPasswordHasher();
        var salt = RandomNumberGenerator.GetBytes(16);
        var parameters = new NSec.Cryptography.Argon2Parameters { DegreeOfParallelism = 1, MemorySize = 12 * 1024, NumberOfPasses = 1 };
        var hash = NSec.Cryptography.PasswordBasedKeyDerivationAlgorithm.Argon2id(in parameters).DeriveBytes("Old-Password-1", salt, 32);
        var phc = $"$argon2id$v=19$m={12 * 1024},t=1,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}";
        Assert.True(hasher.Verify("Old-Password-1", phc, "argon2id-v1", out var rehash));
        Assert.True(rehash);
    }

    private static readonly string[] OkOrBusy = ["ok", "busy"];

    [Fact]
    public void S105_review5_password_hashing_is_capped_and_refuses_with_busy_instead_of_exhausting_memory()
    {
        // R-317: the old form released eight threads together and hoped that one hash (tens of milliseconds) was still running when the
        // others asked; on a loaded machine the threads woke up one after the other and all eight answered "ok". Now the one place is
        // held by the test itself, so the refusal is certain, and the cap and its release are checked in order.
        using var hasher = new Argon2idPasswordHasher(maxConcurrent: 1, maxWait: TimeSpan.FromMilliseconds(1));
        var gate = (SemaphoreSlim)typeof(Argon2idPasswordHasher).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hasher)!;
        gate.Wait(TestContext.Current.CancellationToken); // a hash in progress
        try
        {
            using var start = new Barrier(8);
            var outcomes = new string[8];
            var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    hasher.Hash("Correct-Horse-7");
                    outcomes[i] = "ok";
                }
                catch (Erp.Kernel.Application.Identity.PasswordHasherBusyException)
                {
                    outcomes[i] = "busy";
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.All(outcomes, outcome => Assert.Equal("busy", outcome)); // every caller is refused while the one place is taken
        }
        finally
        {
            gate.Release();
        }

        // With the place free again, real concurrent calls each get either a hash or a refusal, never a failure of another kind, and one always succeeds.
        using var start2 = new Barrier(8);
        var results = new string[8];
        var overlapping = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            start2.SignalAndWait();
            try
            {
                hasher.Hash("Correct-Horse-7");
                results[i] = "ok";
            }
            catch (Erp.Kernel.Application.Identity.PasswordHasherBusyException)
            {
                results[i] = "busy";
            }
        })).ToList();
        overlapping.ForEach(t => t.Start());
        overlapping.ForEach(t => t.Join());
        Assert.All(results, outcome => Assert.Contains(outcome, OkOrBusy));
        Assert.Contains("ok", results);
        Assert.True(hasher.Verify("Correct-Horse-7", hasher.Hash("Correct-Horse-7"), "argon2id-v1", out _)); // the gate is released again
    }

    [Fact]
    public void S105_P7_field_encryption_round_trips_binds_the_purpose_and_never_repeats_a_ciphertext()
    {
        var encryptor = new AesGcmFieldEncryptor(RandomNumberGenerator.GetBytes(32), 1);
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var a = encryptor.Encrypt(secret, "iam.mfa_devices.secret");
        var b = encryptor.Encrypt(secret, "iam.mfa_devices.secret");
        Assert.NotEqual(a, b);
        Assert.Equal(1, a[0]);
        Assert.Equal(secret, encryptor.Decrypt(a, "iam.mfa_devices.secret"));
        Assert.ThrowsAny<CryptographicException>(() => encryptor.Decrypt(a, "another.column"));
        Assert.ThrowsAny<CryptographicException>(() => new AesGcmFieldEncryptor(RandomNumberGenerator.GetBytes(32), 1).Decrypt(a, "iam.mfa_devices.secret"));
        a[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => encryptor.Decrypt(a, "iam.mfa_devices.secret"));
    }

    [Fact]
    public void CR010_tokens_are_random_and_only_their_sha256_is_stored_and_recovery_codes_are_typeable()
    {
        var token = OpaqueTokens.New();
        Assert.NotEqual(token, OpaqueTokens.New());
        Assert.Equal(43, token.Length); // 256 bits, base64url
        Assert.Matches("^[0-9a-f]{64}$", OpaqueTokens.Hash(token));
        var code = OpaqueTokens.NewRecoveryCode();
        Assert.Matches("^[A-Z2-7]{5}-[A-Z2-7]{5}$", code);
        Assert.Equal(code, OpaqueTokens.NormalizeRecoveryCode(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FR_KRN_IAM_04_access_tokens_are_es256_with_key_id_and_the_S103_claims_and_keys_rotate()
    {
        var folder = Directory.CreateTempSubdirectory("erp-s105-").FullName;
        try
        {
            using var secrets = new EncryptedFileSecretStore(Path.Combine(folder, "s.bin"), RandomNumberGenerator.GetBytes(32));
            var ct = TestContext.Current.CancellationToken;
            Assert.Null(await TokenSigningKeys.LoadAsync(secrets, ct));
            await TokenSigningKeys.EnsureAsync(secrets, "k1", ct);
            await TokenSigningKeys.EnsureAsync(secrets, "k-ignored", ct); // already present: nothing changes
            var keys = (await TokenSigningKeys.LoadAsync(secrets, ct))!;
            Assert.Equal("k1", keys.Current.KeyId);

            var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
            var user = Guid.CreateVersion7();
            var session = Guid.CreateVersion7();
            var (text, expires) = new JwtAccessTokenIssuer(keys, "https://id.test", "erp-api").Issue(user, "acme", session, now);
            var jwt = new JsonWebToken(text);
            Assert.Equal("ES256", jwt.Alg);
            Assert.Equal("k1", jwt.Kid);
            Assert.Equal(user.ToString(), jwt.Subject);
            Assert.Equal("acme", jwt.GetClaim("tenant").Value);
            Assert.Equal(session.ToString(), jwt.GetClaim("sid").Value);
            Assert.Equal(now.AddMinutes(5), expires);

            await TokenSigningKeys.RotateAsync(secrets, "k2", ct);
            var rotated = (await TokenSigningKeys.LoadAsync(secrets, ct))!;
            Assert.Equal("k2", rotated.Current.KeyId);
            Assert.Equal(["k2", "k1"], rotated.Validation.Select(k => k.KeyId)); // old tokens still validate
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void S105_every_identity_message_key_has_english_and_urdu_text()
    {
        var folder = Path.Combine(Root, "src/Platform/Erp.Kernel.Contracts/Identity/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "identity.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "identity.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));

        var used = Directory.GetFiles(Path.Combine(Root, "src/Platform"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Identity{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"((?:errors|permissions)\\.[a-z_.]+)\"").Select(m => m.Groups[1].Value))
            .Concat(IdentityPermissions.All.Select(p => p.DescriptionKey))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.DoesNotContain(used, k => !en.ContainsKey(k));
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
