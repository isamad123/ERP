using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
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
    /// <summary>
    /// The modules of this product with the D-24 feature keys each unlocks (slice S1.09b: from the entitlement key catalogue;
    /// the kernel's key is platform.base). <c>migrate</c> brings cp.module_catalog in step.
    /// </summary>
    public static readonly IReadOnlyList<(string ModuleId, string Name, string Kind, string[] EntitlementKeys)> Modules =
    [
        ("kernel", "Platform kernel", "kernel", [.. Erp.Kernel.Infrastructure.Entitlements.EntitlementCatalogs.Keys.KeysOf("kernel")]),
        ("hrm", "Human resources (HRMIS)", "business", [.. Erp.Kernel.Infrastructure.Entitlements.EntitlementCatalogs.Keys.KeysOf("hrm")]),
    ];

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Applies the pending control-plane scripts and records each with its checksum in cp.schema_history (CR-006, slice
    /// S1.11f2; a recorded script that changed stops the run), closes the database to logins that were not granted
    /// access, and seeds the first platform admin and the module catalogue. <paramref name="migrated"/> receives the
    /// migration counts.
    /// </summary>
    public async Task<PlatformActor> BootstrapAsync(MigrationSet migrations, string adminEmail, string adminName, CancellationToken cancellationToken, Action<SchemaMigrationResult>? migrated = null)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminName);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await ControlPlaneSchemaMigrator.ApplyAsync(connection, migrations.ControlPlaneScripts, clock, BaselineSeed.SystemUserId, cancellationToken).ConfigureAwait(false);
        migrated?.Invoke(result);
        await Sql.ExecuteAsync(connection, null,
            "do $do$ begin execute format('revoke connect on database %I from public', current_database()); end $do$", cancellationToken).ConfigureAwait(false);
        await EnsureApiRoleAsync(connection, cancellationToken).ConfigureAwait(false);

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
            else if (await Sql.ExecuteAsync(connection, null, """
                update cp.module_catalog set entitlement_keys = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1
                where module_id = $1 and entitlement_keys is distinct from $2
                """, cancellationToken, moduleId, keys, now, actor.Id).ConfigureAwait(false) > 0)
            {
                // Slice S1.09b: the keys follow the entitlement key catalogue (D-24); earlier releases stored "hrm".
                await PlatformAudit.WriteAsync(connection, null, clock, actor, "module_catalog.keys", null, new { module_id = moduleId, entitlement_keys = keys }, cancellationToken).ConfigureAwait(false);
            }
        }

        return actor;
    }

    /// <summary>Group role (no login) of the API hosts' control-plane login: read access to what the API reads, nothing else.</summary>
    public const string ApiRole = "cp_api";

    /// <summary>The control-plane tables the API host reads (tenant resolution, licence and usage pages); it writes none.</summary>
    public static readonly IReadOnlyList<string> ApiTables =
        ["cp.tenants", "cp.tenant_domains", "cp.tenant_databases", "cp.db_clusters", "cp.entitlements", "cp.tenant_module_versions", "cp.usage_meters"];

    /// <summary>
    /// S1.03 review finding 11: the API host need not use the migrator's login. <see cref="ApiRole"/> may connect and read
    /// <see cref="ApiTables"/> only; the operator gives the API a login that is a member of it (docs/runbooks/tenant-operations.md).
    /// The role is created only when the migrating login may create roles; otherwise the runbook's statements are run once.
    /// </summary>
    private static async Task EnsureApiRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (await Sql.ScalarAsync<bool>(connection, null, "select rolsuper or rolcreaterole from pg_roles where rolname = current_user", cancellationToken).ConfigureAwait(false))
        {
            await Provisioning.ClusterAdministrator.EnsureNoLoginRolesAsync(connection, [ApiRole], cancellationToken).ConfigureAwait(false);
        }

        if (!await Sql.ScalarAsync<bool>(connection, null, "select exists (select 1 from pg_roles where rolname = $1)", cancellationToken, ApiRole).ConfigureAwait(false))
        {
            return;
        }

        // Constant names only (ApiRole, ApiTables); nothing from input.
        await Sql.ExecuteAsync(connection, null,
            $"do $do$ begin execute format('grant connect on database %I to {ApiRole}', current_database()); end $do$", cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, null, $"grant usage on schema cp to {ApiRole}", cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, null, $"grant select on {string.Join(", ", ApiTables)} to {ApiRole}", cancellationToken).ConfigureAwait(false);
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
    /// Registers (or updates) a PostgreSQL server for customer databases. The administrator credential goes to the secret
    /// store; the table keeps only its reference (KRN-TEN-02). The address of a server that already holds customer
    /// databases changes only with <paramref name="allowAddressChange"/>: it moves every customer on it (S1.03 review finding 6).
    /// </summary>
    public async Task<Guid> AddClusterAsync(PlatformActor actor, string name, string regionCode, string host, int port, int maxDatabases, DatabaseCredential admin, CancellationToken cancellationToken, bool allowAddressChange = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(admin);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var check = Sql.Command(connection, transaction, """
            select c.host, c.port, (select count(*) from cp.tenant_databases d where d.db_cluster_id = c.id and d.status <> 'retired')
            from cp.db_clusters c where c.name = $1 for update
            """, [name]))
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && (reader.GetString(0) != host || reader.GetInt32(1) != port) && reader.GetInt64(2) > 0 && !allowAddressChange)
            {
                throw new InvalidOperationException(
                    $"Cluster {name} holds {reader.GetInt64(2)} customer database(s) at {reader.GetString(0)}:{reader.GetInt32(1)}. Changing its address to {host}:{port} " +
                    "moves all of them; do it only after they have been moved, and repeat with --change-address yes. API hosts must then be restarted.");
            }
        }

        // The secret is written only once the change is known to be allowed; the reference never points at nothing.
        var secretRef = "cluster/" + name + "/admin";
        await secrets.PutAsync(secretRef, admin.ToSecret(), cancellationToken).ConfigureAwait(false);
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
