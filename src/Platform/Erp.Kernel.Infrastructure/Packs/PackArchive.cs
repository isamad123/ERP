using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Contracts.Packs;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>One object file of a pack: where its content comes from, the reminder to verify it, and the value.</summary>
public sealed record PackObjectFile(string Type, string Key, string Source, string Verify, JsonObject Value);

/// <summary>
/// A pack file (slice S1.23 Build items 7 and 8, ruling Q6): a zip with <c>manifest.json</c>, one file per object under
/// <c>objects/</c>, and <c>signature.json</c> once signed. The signature is ECDSA P-256 / SHA-256 (IEEE P1363) over the canonical
/// JSON of the manifest, exactly as entitlements are signed (S1.09b); the manifest holds the SHA-256 of every object file, so a
/// changed byte anywhere is refused. Opening a pack checks sizes, paths, hashes and that nothing unlisted is inside.
/// </summary>
public sealed class PackArchive
{
    public const string ManifestFile = "manifest.json";
    public const string SignatureFile = "signature.json";
    public const string SourceHeaderFile = "pack.json";

    /// <summary>The text every object file carries (Build item 13): pack content is planning input until verified.</summary>
    public const string VerifyNotice = "verify before go-live";

    public const long MaxPackBytes = 32L * 1024 * 1024;
    public const long MaxEntryBytes = 1024L * 1024;
    public const long MaxTotalBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private PackArchive(PackManifest manifest, JsonObject manifestJson, string? keyId, byte[]? signature, IReadOnlyDictionary<string, PackObjectFile> objects)
    {
        Manifest = manifest;
        ManifestJson = manifestJson;
        KeyId = keyId;
        Signature = signature;
        Objects = objects;
    }

    public PackManifest Manifest { get; }

    /// <summary>The manifest as stored in the pack (the signed JSON).</summary>
    public JsonObject ManifestJson { get; }

    public string? KeyId { get; }

    public byte[]? Signature { get; }

    /// <summary>Object files by manifest file path.</summary>
    public IReadOnlyDictionary<string, PackObjectFile> Objects { get; }

    public bool IsSigned => KeyId is not null && Signature is not null;

    public PackObjectFile FileOf(PackObjectEntry entry) => Objects[entry.File];

    /// <summary>SHA-256 (lower-case hex) of the canonical JSON of a value; the installer's measure of "the same value".</summary>
    public static string Hash(JsonNode? value) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(value)));

    /// <summary>A relative path with forward slashes, no empty, '.' or '..' part, and only safe characters.</summary>
    public static bool SafePath(string path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 240 && !path.StartsWith('/') && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
        && path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

    /// <summary>True when the signature is valid for the public key (DER SubjectPublicKeyInfo, as stored in cp.signing_keys).</summary>
    public bool Verify(byte[] subjectPublicKeyInfo)
    {
        if (Signature is null)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return key.VerifyData(CanonicalJson.Bytes(ManifestJson), Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    // ------------------------------------------------------------------ build and sign (operator commands, vendor side)

    /// <summary>
    /// <c>pack build</c>: reads a pack source folder (<c>pack.json</c> and <c>objects/**/*.json</c>), checks every object with its
    /// handler, writes <c>manifest.json</c> into the folder and returns the unsigned pack file.
    /// </summary>
    public static (PackManifest Manifest, byte[] Pack) Build(string folder, IReadOnlyCollection<IPackObjectHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, SourceHeaderFile))) as JsonObject
            ?? throw new InvalidDataException("pack.json is not a JSON object.");
        var byType = handlers.ToDictionary(h => h.ObjectType, StringComparer.Ordinal);
        var root = Path.GetFullPath(folder);
        var objectsRoot = Path.Combine(root, "objects");
        var entries = new List<(PackObjectEntry Entry, byte[] Bytes)>();
        var sources = Directory.Exists(objectsRoot)
            ? Directory.EnumerateFiles(objectsRoot, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [];
        foreach (var file in sources)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!SafePath(relative))
            {
                throw new InvalidDataException($"{relative}: file names use letters, digits, '-', '_' and '.' only.");
            }

            var bytes = File.ReadAllBytes(file);
            var parsed = ParseObject(relative, bytes);
            if (!byType.TryGetValue(parsed.Type, out var handler))
            {
                throw new InvalidDataException(PackManifest.StatutoryTypes.Contains(parsed.Type)
                    ? $"{relative}: object type '{parsed.Type}' is statutory content; no pack may carry it before its slice and practitioner validation (AS-4, R-002)."
                    : $"{relative}: no module handles object type '{parsed.Type}'.");
            }

            if (handler.Validate(parsed.Key, parsed.Value) is { } problem)
            {
                throw new InvalidDataException($"{relative} ({parsed.Type} '{parsed.Key}'): {problem}");
            }

            entries.Add((new PackObjectEntry(parsed.Type, parsed.Key, relative, Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes));
        }

        var manifestJson = (JsonObject)header.DeepClone();
        manifestJson["format"] ??= PackManifest.CurrentFormat;
        manifestJson["mergeRules"] = new JsonObject(entries.Select(e => e.Entry.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(t => KeyValuePair.Create(t, (JsonNode?)(header["mergeRules"]?[t]?.GetValue<string>() ?? PackManifest.KeepTenant))));
        manifestJson["objects"] = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["type"] = e.Entry.Type, ["key"] = e.Entry.Key, ["file"] = e.Entry.File, ["sha256"] = e.Entry.Sha256,
        })]);
        var manifest = PackManifest.FromJson(manifestJson);
        File.WriteAllText(Path.Combine(root, ManifestFile), manifestJson.ToJsonString(Indented) + "\n");

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, ManifestFile, Encoding.UTF8.GetBytes(manifestJson.ToJsonString(Indented)));
            foreach (var (entry, bytes) in entries)
            {
                Add(zip, entry.File, bytes);
            }
        }

        return (manifest, output.ToArray());
    }

    /// <summary>
    /// <c>pack sign</c>: adds <c>signature.json</c> (key id and signature over the canonical manifest) to a built pack; every other
    /// file is copied byte for byte (their hashes are what the manifest, and so the signature, covers). A signed pack is signed again.
    /// </summary>
    public static byte[] Sign(byte[] pack, string keyId, string privateKeyPem)
    {
        var archive = Open(pack);
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(CanonicalJson.Bytes(archive.ManifestJson), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var source = new ZipArchive(new MemoryStream(pack, writable: false), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            long total = 0;
            foreach (var entry in source.Entries.Where(e => e.FullName != SignatureFile))
            {
                Add(zip, entry.FullName, ReadLimited(entry, ref total));
            }

            Add(zip, SignatureFile, Encoding.UTF8.GetBytes(new JsonObject { ["key_id"] = keyId, ["signature"] = Convert.ToBase64String(signature) }.ToJsonString(Indented)));
        }

        return output.ToArray();
    }

    // ------------------------------------------------------------------ open (publish and install)

    /// <summary>Opens a pack file and checks its structure and every hash; the signature is checked by <see cref="Verify"/>.</summary>
    public static PackArchive Open(byte[] pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Length > MaxPackBytes)
        {
            throw new InvalidDataException($"A pack file is at most {MaxPackBytes / 1024 / 1024} MB.");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(pack, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > PackManifest.MaxObjects + 2)
            {
                throw new InvalidDataException("The pack holds more files than a manifest may list.");
            }

            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (!SafePath(entry.FullName) || !files.TryAdd(entry.FullName, []))
                {
                    throw new InvalidDataException($"The pack holds a file with an unsafe or repeated name: '{entry.FullName}'.");
                }

                files[entry.FullName] = ReadLimited(entry, ref total);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            throw new InvalidDataException("The file is not a readable pack (zip): " + ex.Message, ex);
        }

        var manifestJson = files.TryGetValue(ManifestFile, out var manifestBytes)
            ? ParseJsonObject(manifestBytes, ManifestFile)
            : throw new InvalidDataException("The pack has no manifest.json.");
        var manifest = PackManifest.FromJson(manifestJson);

        string? keyId = null;
        byte[]? signature = null;
        if (files.TryGetValue(SignatureFile, out var signatureBytes))
        {
            var s = ParseJsonObject(signatureBytes, SignatureFile);
            keyId = s["key_id"]?.GetValue<string>() ?? throw new InvalidDataException("signature.json has no key_id.");
            try
            {
                signature = Convert.FromBase64String(s["signature"]?.GetValue<string>() ?? throw new InvalidDataException("signature.json has no signature."));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("signature.json: the signature is not base64.", ex);
            }
        }

        var objects = new Dictionary<string, PackObjectFile>(StringComparer.Ordinal);
        foreach (var entry in manifest.Objects)
        {
            if (!files.TryGetValue(entry.File, out var bytes))
            {
                throw new InvalidDataException($"The manifest lists {entry.File}, which is not in the pack.");
            }

            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{entry.File} does not match its hash in the manifest (the pack was changed after it was built).");
            }

            var parsed = ParseObject(entry.File, bytes);
            if (parsed.Type != entry.Type || parsed.Key != entry.Key)
            {
                throw new InvalidDataException($"{entry.File} holds {parsed.Type} '{parsed.Key}', but the manifest lists {entry.Type} '{entry.Key}'.");
            }

            objects[entry.File] = parsed;
        }

        var unlisted = files.Keys.Where(f => f is not ManifestFile and not SignatureFile && !objects.ContainsKey(f)).ToList();
        return unlisted.Count > 0
            ? throw new InvalidDataException($"The pack holds files the manifest does not list: {string.Join(", ", unlisted.Take(5))}.")
            : new PackArchive(manifest, manifestJson, keyId, signature, objects);
    }

    private static byte[] ReadLimited(ZipArchiveEntry entry, ref long total)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException($"{entry.FullName} is larger than {MaxEntryBytes / 1024} KB.");
        }

        using var stream = entry.Open();
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            copy.Write(buffer, 0, read);
            total += read;
            if (copy.Length > MaxEntryBytes || total > MaxTotalBytes)
            {
                throw new InvalidDataException("The pack's files are larger than allowed when unpacked.");
            }
        }

        return copy.ToArray();
    }

    private static PackObjectFile ParseObject(string file, byte[] bytes)
    {
        var json = ParseJsonObject(bytes, file);
        string Field(string name) => json[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new InvalidDataException($"{file}: '{name}' is required.");
        var verify = Field("verify");
        if (!string.Equals(verify, VerifyNotice, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{file}: 'verify' must read \"{VerifyNotice}\" (Build item 13).");
        }

        var value = json["value"] as JsonObject ?? throw new InvalidDataException($"{file}: 'value' must be a JSON object.");
        var extra = json.Select(p => p.Key).Where(k => k is not ("type" or "key" or "source" or "verify" or "value")).ToList();
        return extra.Count > 0
            ? throw new InvalidDataException($"{file}: unknown fields {string.Join(", ", extra)}.")
            : new PackObjectFile(Field("type"), Field("key"), Field("source"), verify, value);
    }

    private static JsonObject ParseJsonObject(byte[] bytes, string file)
    {
        try
        {
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
                ?? throw new InvalidDataException($"{file} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} is not valid JSON: {ex.Message}", ex);
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // deterministic pack files
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}
