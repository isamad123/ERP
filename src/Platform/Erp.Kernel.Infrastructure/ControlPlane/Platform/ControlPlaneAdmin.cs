using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Migrations;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Platform;

/// <summary>
/// Set-up of the control-plane database (S1.03 build item 1): migrations, the first platform admin, the module catalogue,
/// database clusters and public signing keys. Every write is idempotent and keyed by a stable code.
/// </summary>
public sealed class ControlPlaneAdmin(ControlPlaneOptions options, ISecretStore secrets, IClock clock)
{
    /// <summary>Modules the platform knows (mirrors module manifests). Display names here are operator-facing only.</summary>
    public static readonly IReadOnlyList<(string ModuleId, string Name, string Kind, string[] EntitlementKeys)> Modules =
    [
        ("kernel", "Platform kernel", "kernel", []),
        ("hrm", "Human resources (HRMIS)", "business", ["hrm"]),
    ];

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Applies the control-plane scripts (idempotent; the control-plane design has no history table, so they are
    /// re-applied each time), closes the database to logins that were not granted access, and seeds the first platform
    /// admin and the module catalogue.
    /// </summary>
    public async Task<PlatformActor> BootstrapAsync(MigrationSet migrations, string adminEmail, string adminName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminName);
        await SqlScriptRunner.ApplyAsync(options.ConnectionString, migrations.ControlPlaneScripts, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, null,
            "do $do$ begin execute format('revoke connect on database %I from public', current_database()); end $do$", cancellationToken).ConfigureAwait(false);

        var email = adminEmail.Trim().ToLowerInvariant();
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        await Sql.ExecuteAsync(connection, null, """
            insert into cp.platform_users (id, email, display_name, role, mfa_enrolled, status, created_at, created_by, updated_at, updated_by)
            select $1, $2, $3, 'platform_admin', false, 'active', $4, $1, $4, $1
            where not exists (select 1 from cp.platform_users where email = $2 and not is_deleted)
            """, cancellationToken, id, email, adminName.Trim(), now).ConfigureAwait(false);
        var actor = await FindActorAsync(connection, email, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Platform user {email} exists but is disabled.");

        foreach (var (moduleId, name, kind, keys) in Modules)
        {
            var inserted = await Sql.ExecuteAsync(connection, null, """
                insert into cp.module_catalog (id, module_id, name, kind, entitlement_keys, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $6, $7) on conflict (module_id) do nothing
                """, cancellationToken, Guid.CreateVersion7(now), moduleId, name, kind, keys, now, actor.Id).ConfigureAwait(false);
            if (inserted > 0)
            {
                await PlatformAudit.WriteAsync(connection, null, clock, actor, "module_catalog.add", null, new { module_id = moduleId }, cancellationToken).ConfigureAwait(false);
            }
        }

        return actor;
    }

    /// <summary>The active platform user an operator command runs as.</summary>
    public async Task<PlatformActor> ActorAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FindActorAsync(connection, email.Trim().ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{email} is not an active platform user.");
    }

    private static async Task<PlatformActor?> FindActorAsync(NpgsqlConnection connection, string email, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, null,
            "select id, email, role from cp.platform_users where email = $1 and not is_deleted and status = 'active'", [email]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PlatformActor(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    /// <summary>
    /// Registers (or updates the address of) a PostgreSQL server for customer databases. The administrator credential
    /// goes to the secret store; the table keeps only its reference (KRN-TEN-02).
    /// </summary>
    public async Task<Guid> AddClusterAsync(PlatformActor actor, string name, string regionCode, string host, int port, int maxDatabases, DatabaseCredential admin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(admin);
        var secretRef = "cluster/" + name + "/admin";
        await secrets.PutAsync(secretRef, admin.ToSecret(), cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var id = await Sql.ScalarAsync<Guid>(connection, transaction, """
            insert into cp.db_clusters (id, name, region_code, host, port, max_databases, admin_secret_ref, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, 'active', $8, $9, $8, $9)
            on conflict (name) do update set region_code = excluded.region_code, host = excluded.host, port = excluded.port,
                max_databases = excluded.max_databases, admin_secret_ref = excluded.admin_secret_ref,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = cp.db_clusters.row_version + 1
            returning id
            """, cancellationToken, Guid.CreateVersion7(now), name, regionCode, host, port, maxDatabases, secretRef, now, actor.Id).ConfigureAwait(false);
        await PlatformAudit.WriteAsync(connection, transaction, clock, actor, "cluster.register", null, new { name, region_code = regionCode, host, port, max_databases = maxDatabases }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Registers a public signing key (cp.signing_keys); a key id that already exists keeps its key.</summary>
    public async Task AddSigningKeyAsync(PlatformActor actor, string keyId, string purpose, byte[] subjectPublicKeyInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var inserted = await Sql.ExecuteAsync(connection, transaction, """
            insert into cp.signing_keys (id, key_id, purpose, public_key, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $5, $6, $5, $6) on conflict (key_id) do nothing
            """, cancellationToken, Guid.CreateVersion7(now), keyId, purpose, subjectPublicKeyInfo, now, actor.Id).ConfigureAwait(false);
        if (inserted > 0)
        {
            await PlatformAudit.WriteAsync(connection, transaction, clock, actor, "signing_key.add", null, new { key_id = keyId, purpose }, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
