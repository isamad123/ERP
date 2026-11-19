using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Entitlements;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

/// <summary>What the operator supplies to create a customer (FR-KRN-TEN-02, FR-ADM-PLT-02).</summary>
public sealed record ProvisioningRequest(
    string Code,
    string Name,
    IReadOnlyList<string> Hosts,
    string RegionCode,
    string CountryCode,
    string CurrencyCode,
    string SizeProfile,
    string ClusterName,
    string LegalName,
    string AdminEmail,
    string AdminDisplayName,
    string SignedEntitlementJson,
    string DeploymentType = "saas",
    string DefaultLanguage = "en");

/// <summary>Progress of a provisioning run, reported after each completed step (FR-ADM-PLT-02 "progress").</summary>
public interface IProvisioningProgress
{
    ValueTask StepCompletedAsync(string stepName, CancellationToken cancellationToken);

    /// <summary>Another provisioning run of the same customer is in progress; this run waits for it to end (R-031).</summary>
    ValueTask WaitingForOtherRunAsync(string code, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed record ProvisioningResult(Guid TenantId, string Code, string DatabaseName, bool AlreadyProvisioned);

/// <summary>
/// Creates a customer: control-plane records, its own database and logins, kernel and licensed module migrations,
/// the signed entitlement, baseline data, the first administrator (invited), a smoke test, then activation
/// (FR-KRN-TEN-01, 02, 03). Each step checks what is already there, so running it again after a failure resumes
/// and never creates duplicates. Runs for the same customer take turns (<see cref="ProvisioningLock"/>, R-031).
/// Every step is recorded in cp.platform_audit (FR-ADM-PLT-10).
/// </summary>
public sealed class TenantProvisioner(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    public static readonly IReadOnlyList<string> Steps = ["register", "database", "migrate", "entitlement", "seed", "smoke_test", "activate"];

    private readonly ClusterAdministrator _clusters = new(secrets);

    public async Task<ProvisioningResult> ProvisionAsync(PlatformActor actor, ProvisioningRequest request, IProvisioningProgress? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        TenantCodes.Validate(request.Code);
        var entitlement = SignedEntitlement.Parse(request.SignedEntitlementJson);
        if (!string.Equals(entitlement.Payload.Tenant, request.Code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The entitlement is issued to '{entitlement.Payload.Tenant}', not to '{request.Code}'.");
        }

        var modules = entitlement.Payload.Modules;
        _ = migrations.TenantScripts(modules); // unknown modules stop the run before anything is written

        // R-031 (slice S1.11f2): a second run for the same customer waits here, then finds it active (and stops) or still
        // provisioning after a failure (and resumes).
        await using var customerLock = await ProvisioningLock.AcquireAsync(options.ConnectionString, request.Code,
            progress is null ? null : () => progress.WaitingForOtherRunAsync(request.Code, cancellationToken), cancellationToken).ConfigureAwait(false);

        Registration? registration = null;
        var step = Steps[0];
        try
        {
            registration = await RegisterAsync(actor, request, entitlement, cancellationToken).ConfigureAwait(false);
            if (registration.AlreadyActive)
            {
                return new ProvisioningResult(registration.TenantId, request.Code, registration.Tenant.DatabaseName, AlreadyProvisioned: true);
            }

            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);

            step = "database";
            await _clusters.CreateDatabaseAsync(registration.Cluster, request.Code, registration.Tenant.AppSecretRef, cancellationToken).ConfigureAwait(false);
            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);

            step = "migrate";
            await using (var owner = await OpenTenantAsync(registration.Tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
            {
                var result = await TenantSchemaMigrator.ApplyAsync(owner, migrations.TenantScripts(modules), clock, BaselineSeed.SystemUserId, cancellationToken).ConfigureAwait(false);
                await Identity.IdentitySetup.EnsureAsync(owner, secrets, request.Code, migrations.AccessFolders(modules), cancellationToken).ConfigureAwait(false);
                await RecordVersionsAsync(actor, registration.Tenant, result, cancellationToken).ConfigureAwait(false);
            }

            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);

            step = "entitlement";
            await StoreEntitlementAsync(actor, registration, entitlement, cancellationToken).ConfigureAwait(false);
            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);

            step = "seed";
            await SeedAsync(registration, request, modules, cancellationToken).ConfigureAwait(false);
            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);

            step = "smoke_test";
            await SmokeTestAsync(registration.Tenant, cancellationToken).ConfigureAwait(false);
            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);

            step = "activate";
            await ActivateAsync(actor, registration, cancellationToken).ConfigureAwait(false);
            await CompletedAsync(actor, registration, step, progress, customerLock, cancellationToken).ConfigureAwait(false);
            return new ProvisioningResult(registration.TenantId, request.Code, registration.Tenant.DatabaseName, AlreadyProvisioned: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await using var connection = await OpenControlPlaneAsync(CancellationToken.None).ConfigureAwait(false);
            await PlatformAudit.WriteAsync(connection, null, clock, actor, "tenant.provision.failed", registration?.TenantId,
                new { code = request.Code, step, error = ex.Message }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record Registration(Guid TenantId, ResolvedTenant Tenant, ClusterInfo Cluster, bool AlreadyActive);

    private async Task CompletedAsync(PlatformActor actor, Registration registration, string step, IProvisioningProgress? progress, ProvisioningLock customerLock, CancellationToken cancellationToken)
    {
        await using (var connection = await OpenControlPlaneAsync(cancellationToken).ConfigureAwait(false))
        {
            await PlatformAudit.WriteAsync(connection, null, clock, actor, "tenant.provision." + step, registration.TenantId, new { code = registration.Tenant.Code }, cancellationToken).ConfigureAwait(false);
        }

        if (progress is not null)
        {
            await progress.StepCompletedAsync(step, cancellationToken).ConfigureAwait(false);
        }

        // R-031 (S1.11f2 review finding 2): the next step runs only while this run still holds the customer.
        if (step != Steps[^1])
        {
            await customerLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NpgsqlConnection> OpenControlPlaneAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>A connection to the customer's own database as one of its logins (no pooling: provisioning and migration only).</summary>
    public Task<NpgsqlConnection> OpenTenantAsync(ResolvedTenant tenant, string loginKind, CancellationToken cancellationToken) =>
        OpenTenantAsync(secrets, tenant, loginKind, cancellationToken);

    /// <summary>The same, for services that hold only the secret store (the licence loop of the worker host, S1.09b).</summary>
    public static async Task<NpgsqlConnection> OpenTenantAsync(ISecretStore secrets, ResolvedTenant tenant, string loginKind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(tenant);
        var credential = DatabaseCredential.Parse(await secrets.GetAsync(TenantLogins.SecretRef(tenant.AppSecretRef, loginKind), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No {loginKind} credential is stored for customer {tenant.Code}."));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = tenant.ClusterHost,
            Port = tenant.ClusterPort,
            Database = tenant.DatabaseName,
            Username = credential.Username,
            Password = credential.Password,
            Pooling = false,
            ApplicationName = "erp-" + loginKind,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    // ------------------------------------------------------------------ step 1: control-plane registration

    private async Task<Registration> RegisterAsync(PlatformActor actor, ProvisioningRequest request, SignedEntitlement entitlement, CancellationToken cancellationToken)
    {
        var hosts = request.Hosts.Select(h => HostNames.Normalize(h) ?? throw new ArgumentException($"'{h}' is not a valid host name.", nameof(request))).Distinct(StringComparer.Ordinal).ToList();
        if (hosts.Count == 0)
        {
            throw new ArgumentException("At least one host name is required.", nameof(request));
        }

        await using var connection = await OpenControlPlaneAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        // Signing key, signature and claims (slice S1.09b: one verifier for provisioning and `entitlement apply`).
        await EntitlementVerifier.VerifyAsync(connection, transaction, entitlement, now, cancellationToken).ConfigureAwait(false);
        EntitlementApplier.RefuseExpired(entitlement.Payload, LicenceStates.Today(now, LicenceStates.Zone(null)));

        var cluster = await ReadClusterAsync(connection, transaction, request.ClusterName, cancellationToken).ConfigureAwait(false);
        var existing = await TenantDirectory.ReadAsync(connection, TenantDirectory.SelectTenant + " where t.code = $1", request.Code, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Status != "provisioning")
        {
            if (existing.Status == "active")
            {
                return new Registration(existing.TenantId, existing, cluster, AlreadyActive: true);
            }

            throw new InvalidOperationException($"Customer {request.Code} exists with status {existing.Status}; provisioning does not change it.");
        }

        if (existing is not null)
        {
            // Resuming: the database stays on the cluster it was registered on (S1.03 review finding 12).
            var registered = await Sql.ScalarAsync<string>(connection, transaction,
                "select c.name from cp.db_clusters c join cp.tenant_databases d on d.db_cluster_id = c.id where d.id = $1", cancellationToken, existing.TenantDatabaseId).ConfigureAwait(false);
            if (!string.Equals(registered, cluster.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Customer {request.Code} is being provisioned on cluster {registered}; resume it with --cluster {registered}, not {cluster.Name}.");
            }
        }

        var tenantId = existing?.TenantId ?? Guid.CreateVersion7(now);
        if (existing is null)
        {
            var databases = await Sql.ScalarAsync<long>(connection, transaction, "select count(*) from cp.tenant_databases where db_cluster_id = $1 and status <> 'retired'", cancellationToken, cluster.Id).ConfigureAwait(false);
            if (cluster.Status != "active" || databases >= cluster.MaxDatabases)
            {
                throw new InvalidOperationException($"Cluster {cluster.Name} does not accept new databases (status {cluster.Status}, {databases} of {cluster.MaxDatabases}).");
            }

            await Sql.ExecuteAsync(connection, transaction, """
                insert into cp.tenants (id, code, name, region_code, deployment_type, country_code, size_profile, legal_hold, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, false, 'provisioning', $8, $9, $8, $9)
                """, cancellationToken, tenantId, request.Code, request.Name, request.RegionCode, request.DeploymentType, request.CountryCode, request.SizeProfile, now, actor.Id).ConfigureAwait(false);
            await Sql.ExecuteAsync(connection, transaction, """
                insert into cp.tenant_databases (id, tenant_id, db_cluster_id, env_type, database_name, app_secret_ref, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, 'production', $4, $5, 'creating', $6, $7, $6, $7)
                """, cancellationToken, Guid.CreateVersion7(now), tenantId, cluster.Id, TenantLogins.DatabaseName(request.Code), "tenant/" + request.Code, now, actor.Id).ConfigureAwait(false);
        }

        for (var i = 0; i < hosts.Count; i++)
        {
            await Sql.ExecuteAsync(connection, transaction, """
                insert into cp.tenant_domains (id, tenant_id, host, is_primary, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $5, $6) on conflict (host) do nothing
                """, cancellationToken, Guid.CreateVersion7(now), tenantId, hosts[i], i == 0, now, actor.Id).ConfigureAwait(false);
            var owner = await Sql.ScalarAsync<Guid>(connection, transaction, "select tenant_id from cp.tenant_domains where host = $1", cancellationToken, hosts[i]).ConfigureAwait(false);
            if (owner != tenantId)
            {
                throw new InvalidOperationException($"Host {hosts[i]} already belongs to another customer.");
            }
        }

        var tenant = await TenantDirectory.ReadAsync(connection, TenantDirectory.SelectTenant + " where t.code = $1", request.Code, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The customer registration was not stored.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Registration(tenantId, tenant, cluster, AlreadyActive: false);
    }

    internal static async Task<ClusterInfo> ReadClusterAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction,
            "select id, name, host, port, admin_secret_ref, status, max_databases from cp.db_clusters where name = $1", [name]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ClusterInfo(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6))
            : throw new InvalidOperationException($"Cluster {name} is not registered.");
    }

    // ------------------------------------------------------------------ step 3: migrations recorded in the control plane

    internal async Task RecordVersionsAsync(PlatformActor actor, ResolvedTenant tenant, SchemaMigrationResult result, CancellationToken cancellationToken)
    {
        await using var connection = await OpenControlPlaneAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        foreach (var (module, version) in result.ModuleVersions)
        {
            await Sql.ExecuteAsync(connection, transaction, """
                insert into cp.tenant_module_versions (id, tenant_database_id, module_id, installed_version, installed_at, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $5, $6, $5, $6)
                on conflict (tenant_database_id, module_id) do update set installed_version = excluded.installed_version, installed_at = excluded.installed_at,
                    updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = cp.tenant_module_versions.row_version + 1
                where cp.tenant_module_versions.installed_version is distinct from excluded.installed_version
                """, cancellationToken, Guid.CreateVersion7(now), tenant.TenantDatabaseId, module, version, now, actor.Id).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(connection, transaction, """
            update cp.tenant_databases set schema_version = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1
            where id = $1 and schema_version is distinct from $2
            """, cancellationToken, tenant.TenantDatabaseId, result.SchemaVersion, now, actor.Id).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ step 4: entitlement

    private async Task StoreEntitlementAsync(PlatformActor actor, Registration registration, SignedEntitlement entitlement, CancellationToken cancellationToken)
    {
        // The first entitlement is in force from provisioning (ruling B1: a customer is set up before go-live); its state
        // follows from its end date on the customer's day (default time zone until the customer changes org.time_zone).
        var now = clock.UtcNow;
        var payload = entitlement.Payload;
        var state = LicenceStates.Decide(payload.ExpiresAt, EntitlementCatalogs.Parse(payload.Claims).Term, LicenceStates.Today(now, LicenceStates.Zone(null)));
        await using (var connection = await OpenControlPlaneAsync(cancellationToken).ConfigureAwait(false))
        {
            await EntitlementApplier.InsertAsync(connection, null, registration.TenantId, entitlement, state, now, actor.Id, cancellationToken).ConfigureAwait(false);
        }

        await using var tenant = await OpenTenantAsync(registration.Tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false);
        await using var tx = await tenant.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await CustomerLicence.LockCopyAsync(tenant, tx, cancellationToken).ConfigureAwait(false);
        if (await CustomerLicence.CopyVersionAsync(tenant, tx, cancellationToken).ConfigureAwait(false) == 0)
        {
            await CustomerLicence.ReplaceCopyAsync(tenant, tx, payload, entitlement.KeyId, entitlement.Signature, state, now, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ step 5: baseline data and first administrator

    private async Task SeedAsync(Registration registration, ProvisioningRequest request, IReadOnlyCollection<string> modules, CancellationToken cancellationToken)
    {
        var seed = BaselineSeed.Current;
        var country = seed.Countries.GetValueOrDefault(request.CountryCode)
            ?? throw new InvalidOperationException($"No reference data for country {request.CountryCode} in this release.");
        var currency = seed.Currencies.GetValueOrDefault(request.CurrencyCode)
            ?? throw new InvalidOperationException($"No reference data for currency {request.CurrencyCode} in this release.");
        var now = clock.UtcNow;
        var actor = BaselineSeed.SystemUserId;
        var adminEmail = request.AdminEmail.Trim().ToLowerInvariant();

        await using var connection = await OpenTenantAsync(registration.Tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var su = seed.SystemUser;
        await Sql.ExecuteAsync(connection, transaction, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            select $1, $2, $3, $4, $5, $6, true, 'active', $7, $1, $7, $1
            where not exists (select 1 from iam.users where id = $1)
            """, cancellationToken, actor, su.Username, su.Email, su.DisplayName, su.Language, su.TimeZone, now).ConfigureAwait(false);

        await JsonInsertAsync(connection, transaction, "insert into ref.countries (code, name_i18n, is_active) values ($1, $2, true) on conflict (code) do nothing",
            [request.CountryCode, null], 1, country, cancellationToken).ConfigureAwait(false);
        await JsonInsertAsync(connection, transaction, "insert into ref.currencies (code, name_i18n, symbol, decimals, is_active) values ($1, $2, $3, $4, true) on conflict (code) do nothing",
            [request.CurrencyCode, null, currency.Symbol, currency.Decimals], 1, currency.Name, cancellationToken).ConfigureAwait(false);

        // The default company; its default branch is created by the SC-2 trigger in the same transaction.
        await JsonInsertAsync(connection, transaction, """
            insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
            select $1, $2, $3, $4, $5, $6, $7, 'none', 'active', $8, $9, $8, $9
            where not exists (select 1 from org.companies where lower(code) = lower($2) and not is_deleted)
            """, [Guid.CreateVersion7(now), request.Code, null, request.LegalName, request.CountryCode, request.CurrencyCode, request.DefaultLanguage, now, actor], 2,
            new Dictionary<string, string> { [request.DefaultLanguage] = request.Name }, cancellationToken).ConfigureAwait(false);
        var companyId = await Sql.ScalarAsync<Guid>(connection, transaction, "select id from org.companies where lower(code) = lower($1) and not is_deleted", cancellationToken, request.Code).ConfigureAwait(false);

        // Permission registry, baseline data scopes and baseline roles of the installed modules (slice S1.06).
        await Access.AccessSetup.EnsureAsync(connection, transaction, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);

        // Numbering settings and a default scheme per company and registered document type (slice S1.08 Q7).
        await Numbering.NumberingSetup.EnsureAsync(connection, transaction, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);

        // Default approval route per registered document type (slice S1.10, FR-KRN-TEN-02 "seed default workflows").
        await Workflow.WorkflowSetup.EnsureAsync(connection, transaction, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
        await Jobs.JobSetup.EnsureAsync(connection, transaction, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
        await Notifications.NotificationSetup.EnsureAsync(connection, transaction, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);

        // First tenant administrator: invited, no password. Invitation e-mail and sign-in are slices S1.05 and S1.11.
        await Sql.ExecuteAsync(connection, transaction, """
            insert into iam.users (id, username, email, display_name, default_company_id, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            select $1, $2, $2, $3, $4, $5, $6, false, 'invited', $7, $8, $7, $8
            where not exists (select 1 from iam.users where lower(email) = $2 and not is_deleted)
            """, cancellationToken, Guid.CreateVersion7(now), adminEmail, request.AdminDisplayName.Trim(), companyId, seed.UserDefaultsValue.Language, seed.UserDefaultsValue.TimeZone, now, actor).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, """
            insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            select $1, u.id, r.id, null, 'provisioning', $2, $3, $4, $3, $4
            from iam.users u, iam.roles r
            where lower(u.email) = $5 and not u.is_deleted and lower(r.code) = lower($6) and not r.is_deleted
              and not exists (select 1 from iam.user_roles x where x.user_id = u.id and x.role_id = r.id)
            """, cancellationToken, Guid.CreateVersion7(now), DateOnly.FromDateTime(now.UtcDateTime), now, actor, adminEmail, seed.AdminRole).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task JsonInsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object?[] values, int jsonIndex, object json, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(connection, transaction, sql, values);
        command.Parameters[jsonIndex] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(json) };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ steps 6 and 7: smoke test and activation

    /// <summary>Connects as the application login (app_rw) and reads what provisioning must have created.</summary>
    private async Task SmokeTestAsync(ResolvedTenant tenant, CancellationToken cancellationToken)
    {
        await using var connection = await OpenTenantAsync(tenant, TenantLogins.ReadWrite, cancellationToken).ConfigureAwait(false);
        foreach (var (what, sql) in new[]
        {
            ("applied migrations", "select count(*) from meta.schema_history"),
            ("company", "select count(*) from org.companies where not is_deleted"),
            ("branch", "select count(*) from org.branches where not is_deleted"),
            ("tenant administrator", "select count(*) from iam.users where not is_system and not is_deleted"),
        })
        {
            if (await Sql.ScalarAsync<long>(connection, null, sql, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException($"Smoke test failed: no {what} found in {tenant.DatabaseName}.");
            }
        }
    }

    private async Task ActivateAsync(PlatformActor actor, Registration registration, CancellationToken cancellationToken)
    {
        await using var connection = await OpenControlPlaneAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await Sql.ExecuteAsync(connection, transaction,
            "update cp.tenant_databases set status = 'ready', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and status = 'creating'",
            cancellationToken, registration.Tenant.TenantDatabaseId, now, actor.Id).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction,
            "update cp.tenants set status = 'active', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and status = 'provisioning'",
            cancellationToken, registration.TenantId, now, actor.Id).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
