using System.Data.Common;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Entitlements;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>A published pack version as the catalogue lists it (no file content).</summary>
public sealed record CatalogPack(string PackId, string Kind, string Version, string CompatibleFrom, DateTimeOffset ReleasedAt, string? ReleaseNotes, JsonObject Manifest)
{
    public PackManifest Parsed => PackManifest.FromJson(Manifest);
}

/// <summary>The outcome of <c>pack publish</c>.</summary>
public sealed record PublishResult(string PackId, string Version, bool AlreadyPublished);

/// <summary>
/// The vendor's pack catalogue in the control plane (<c>cp.pack_catalog</c>; slice S1.23 Build item 8, ruling Q8). Publishing checks
/// the pack completely (structure, hashes, signing key of purpose <c>pack</c>, its validity and revocation, the signature, the
/// licensing key) and stores the signed file; installs read it back and check the signature again with the key as it is then,
/// so a pack whose key was revoked after publishing is refused (Acceptance).
/// </summary>
public static class PackCatalog
{
    /// <summary>Checks the signing key and the signature of <paramref name="pack"/> against cp.signing_keys at <paramref name="now"/>.</summary>
    public static async Task VerifyAsync(NpgsqlConnection controlPlane, NpgsqlTransaction? transaction, PackArchive pack, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.IsSigned)
        {
            throw new InvalidOperationException("The pack is not signed (run `pack sign` with the vendor's pack key first).");
        }

        await using var command = Sql.Command(controlPlane, transaction,
            "select public_key, valid_from, valid_to, revoked_at from cp.signing_keys where key_id = $1 and purpose = 'pack'", [pack.KeyId]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Signing key '{pack.KeyId}' is not a registered pack key.");
        }

        if (!reader.IsDBNull(3))
        {
            throw new InvalidOperationException($"Signing key '{pack.KeyId}' was revoked; packs it signed are refused.");
        }

        if (reader.GetFieldValue<DateTimeOffset>(1) > now || (!reader.IsDBNull(2) && reader.GetFieldValue<DateTimeOffset>(2) <= now))
        {
            throw new InvalidOperationException($"Signing key '{pack.KeyId}' is not valid at this time.");
        }

        if (!pack.Verify(reader.GetFieldValue<byte[]>(0)))
        {
            throw new InvalidOperationException("The pack signature is not valid (the pack was changed after signing, or signed with another key).");
        }
    }

    /// <summary>The licensing key must be a pack key of the entitlement catalogue (D-24), so a customer's claims can name it.</summary>
    public static void CheckEntitlementKey(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!EntitlementCatalogs.Keys.Packs.ContainsKey(manifest.Entitlement))
        {
            throw new InvalidDataException($"Pack manifest: entitlement '{manifest.Entitlement}' is not a pack key of the entitlement catalogue.");
        }
    }

    /// <summary><c>pack publish</c>: verifies and stores a signed pack; the same file again changes nothing.</summary>
    public static async Task<PublishResult> PublishAsync(string controlPlaneConnection, PlatformActor actor, byte[] file, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(clock);
        var pack = PackArchive.Open(file);
        CheckEntitlementKey(pack.Manifest);
        await using var connection = new NpgsqlConnection(controlPlaneConnection);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await VerifyAsync(connection, transaction, pack, now, cancellationToken).ConfigureAwait(false);

        var m = pack.Manifest;
        await using (var existing = Sql.Command(connection, transaction, "select content from cp.pack_catalog where pack_id = $1 and version = $2 for update", [m.Id, m.Version]))
        {
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] stored)
            {
                return stored.AsSpan().SequenceEqual(file)
                    ? new PublishResult(m.Id, m.Version, AlreadyPublished: true)
                    : throw new InvalidOperationException($"Pack {m.Id} {m.Version} is already published with other content; publish a new version instead (versions are never replaced).");
            }
        }

        var latest = await Sql.ScalarAsync<string?>(connection, transaction,
            "select version from cp.pack_catalog where pack_id = $1 order by released_at desc limit 1", cancellationToken, m.Id).ConfigureAwait(false);
        var other = await Sql.ScalarAsync<string?>(connection, transaction, "select kind from cp.pack_catalog where pack_id = $1 limit 1", cancellationToken, m.Id).ConfigureAwait(false);
        if (other is not null && other != m.Kind)
        {
            throw new InvalidOperationException($"Pack {m.Id} is published as a {other} pack; its kind cannot change.");
        }

        if (latest is not null && PackVersions.Compare(m.Version, latest) <= 0)
        {
            throw new InvalidOperationException($"Pack {m.Id} {m.Version} is not newer than the latest published version {latest}.");
        }

        await using (var insert = Sql.Command(connection, transaction, """
            insert into cp.pack_catalog (id, pack_id, kind, version, compatible_from, signature, released_at, release_notes, key_id, manifest, content,
                created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $7, $12, $7, $12)
            """, [Guid.CreateVersion7(now), m.Id, m.Kind, m.Version, m.CompatibleFrom, pack.Signature, now, m.ReleaseNotes.GetValueOrDefault("en"), pack.KeyId,
            pack.ManifestJson.ToJsonString(), file, actor.Id]))
        {
            insert.Parameters[9].NpgsqlDbType = NpgsqlDbType.Jsonb;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await PlatformAudit.WriteAsync(connection, transaction, clock, actor, "pack.publish", null,
            new { pack_id = m.Id, version = m.Version, kind = m.Kind, key_id = pack.KeyId, objects = m.Objects.Count }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PublishResult(m.Id, m.Version, AlreadyPublished: false);
    }

    /// <summary>Every published version (newest first per pack), without file content.</summary>
    public static async Task<IReadOnlyList<CatalogPack>> ListAsync(DbConnection controlPlane, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        await using var command = controlPlane.CreateCommand();
        command.CommandText = """
            select pack_id, kind, version, compatible_from, released_at, release_notes, manifest::text
            from cp.pack_catalog where manifest is not null and content is not null order by pack_id, released_at desc
            """;
        var result = new List<CatalogPack>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CatalogPack(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), JsonNode.Parse(reader.GetString(6)) as JsonObject ?? []));
        }

        return result;
    }

    /// <summary>
    /// The signed file of one version (the latest compatible one when <paramref name="version"/> is null), opened and verified again
    /// against the signing keys as they are now.
    /// </summary>
    public static async Task<PackArchive> LoadAsync(NpgsqlConnection controlPlane, string packId, string? version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        byte[]? content = null;
        foreach (var row in (await ListAsync(controlPlane, cancellationToken).ConfigureAwait(false)).Where(p => p.PackId == packId))
        {
            if (version is null ? !row.Parsed.CompatibleWith(ProductRelease.Version) : row.Version != version)
            {
                continue;
            }

            content = await Sql.ScalarAsync<byte[]?>(controlPlane, null, "select content from cp.pack_catalog where pack_id = $1 and version = $2", cancellationToken, packId, row.Version).ConfigureAwait(false);
            break;
        }

        if (content is null)
        {
            throw new PackNotFoundException(version is null
                ? $"Pack {packId} has no published version compatible with product {ProductRelease.Version}."
                : $"Pack {packId} {version} is not published.");
        }

        var pack = PackArchive.Open(content);
        if (pack.Manifest.Id != packId)
        {
            throw new InvalidDataException("The catalogue row does not match the pack it holds.");
        }

        await VerifyAsync(controlPlane, null, pack, now, cancellationToken).ConfigureAwait(false);
        return pack;
    }
}

/// <summary>The catalogue has no such pack or version.</summary>
public sealed class PackNotFoundException(string message) : InvalidOperationException(message);
