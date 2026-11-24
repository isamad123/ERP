using System.Net;
using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Backup;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>S1.03 acceptance: provisioning is resumable without duplicates; the migrator isolates a failing customer; backup and restore round-trip.</summary>
[Collection(TenancyGroup.Name)]
public sealed class ProvisioningAndMigrationTests(TenancyFixture fx)
{
    private const string ControlPlaneCounts = """
        select (select count(*) from cp.tenants where code = $1)
             + 100 * (select count(*) from cp.tenant_domains d join cp.tenants t on t.id = d.tenant_id where t.code = $1)
             + 10000 * (select count(*) from cp.tenant_databases d join cp.tenants t on t.id = d.tenant_id where t.code = $1)
             + 1000000 * (select count(*) from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1)
        """;

    private static readonly string[] TenantTables =
        ["iam.users", "iam.roles", "iam.user_roles", "org.companies", "org.branches", "ref.countries", "ref.currencies", "meta.schema_history", "meta.local_entitlement"];

    private sealed class FailAfter(string failingStep) : IProvisioningProgress
    {
        public ValueTask StepCompletedAsync(string stepName, CancellationToken cancellationToken) =>
            stepName == failingStep ? throw new InvalidOperationException($"forced failure after {stepName}") : ValueTask.CompletedTask;
    }

    private async Task<Dictionary<string, long>> TenantRowCountsAsync(string code, CancellationToken ct)
    {
        await using var db = await fx.OpenTenantAsync(code, TenantLogins.Owner, ct);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in TenantTables)
        {
            counts[table] = await TenancyFixture.CountAsync(db, $"select count(*) from {table}", ct);
        }

        return counts;
    }

    [Theory]
    [InlineData("register")]
    [InlineData("database")] // S1.03 review test gap T1
    [InlineData("migrate")]
    [InlineData("entitlement")] // S1.03 review test gap T1
    [InlineData("seed")]
    [InlineData("smoke_test")]
    public async Task FR_KRN_TEN_02_rerunning_provisioning_after_a_forced_failure_completes_without_duplicates(string failingStep)
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "p" + failingStep[..2] + fx.Suffix;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.ProvisionAsync(code, ct, new FailAfter(failingStep)));
        Assert.Contains("forced failure", error.Message, StringComparison.Ordinal);
        Assert.Equal("provisioning", (await fx.TenantAsync(code, ct)).Status);

        var result = await fx.ProvisionAsync(code, ct);
        Assert.False(result.AlreadyProvisioned);
        var tenant = await fx.TenantAsync(code, ct);
        Assert.Equal(("active", "ready", "active"), (tenant.Status, tenant.DatabaseStatus, tenant.Entitlements.Single().Status));

        await using var cp = await fx.OpenControlPlaneAsync(ct);
        Assert.Equal(1010101, await TenancyFixture.CountAsync(cp, ControlPlaneCounts, ct, code));
        var counts = await TenantRowCountsAsync(code, ct);
        Assert.Equal(1, counts["org.companies"]);
        Assert.Equal(1, counts["org.branches"]);
        Assert.Equal(11, counts["iam.roles"]); // baseline roles: 6 kernel (spec 02 section 23, HRM-ROL-06) + 4 HR (spec 06a 28.1), slice S1.06, + payroll_manager (S1.34b, CR-087 a: the maker and checker of salary changes)
        Assert.Equal(2, counts["iam.users"]); // reserved system user and the invited first administrator
        Assert.Equal(1, counts["iam.user_roles"]);
        Assert.Equal(1, counts["meta.local_entitlement"]);
        Assert.Equal(fx.Migrations.TenantScripts(["hrm"]).Count, counts["meta.schema_history"]);

        // A third run on an active customer changes nothing.
        var again = await fx.ProvisionAsync(code, ct);
        Assert.True(again.AlreadyProvisioned);
        Assert.Equal(counts, await TenantRowCountsAsync(code, ct));
        Assert.Equal(1010101, await TenancyFixture.CountAsync(cp, ControlPlaneCounts, ct, code));
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = 'tenant.provision.failed'", ct, code));
    }

    [Fact]
    public async Task FR_KRN_TEN_02_provisioning_seeds_the_baseline_and_an_invited_first_administrator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadOnly, ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(db, """
            select count(*) from iam.users u join iam.user_roles ur on ur.user_id = u.id join iam.roles r on r.id = ur.role_id
            where u.email = $1 and u.status = 'invited' and r.code = 'tenant_admin' and ur.company_id is null
            """, ct, "admin@" + fx.CodeA + ".example.test"));
        Assert.Equal(0, await TenancyFixture.CountAsync(db, "select count(*) from iam.user_credentials", ct)); // no password before S1.05
        Assert.Equal(11, await TenancyFixture.CountAsync(db, "select count(*) from iam.roles where source = 'module' and code = baseline_code", ct));
        Assert.Equal(1, await TenancyFixture.CountAsync(db, "select count(*) from org.companies where code = $1 and country_code = 'PK' and base_currency_code = 'PKR'", ct, fx.CodeA));

        await using var cp = await fx.OpenControlPlaneAsync(ct);
        foreach (var step in TenantProvisioner.Steps)
        {
            Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = $2", ct, fx.CodeA, "tenant.provision." + step));
        }

        Assert.Equal(2, await TenancyFixture.CountAsync(cp, "select count(*) from cp.tenant_module_versions m join cp.tenant_databases d on d.id = m.tenant_database_id join cp.tenants t on t.id = d.tenant_id where t.code = $1", ct, fx.CodeA));
    }

    [Fact]
    public async Task FR_KRN_TEN_02_an_entitlement_with_a_bad_signature_or_for_another_customer_stops_provisioning_before_anything_is_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "x" + fx.Suffix;
        fx.Track(code);
        using var stranger = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code, signer: stranger), null, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code, entitlementTenant: fx.CodeA), null, ct));
        await using var cp = await fx.OpenControlPlaneAsync(ct);
        Assert.Equal(0, await TenancyFixture.CountAsync(cp, "select count(*) from cp.tenants where code = $1", ct, code));
    }

    [Fact]
    public async Task FR_ADM_PLT_10_the_platform_admin_seed_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = new ControlPlaneAdmin(fx.Options, fx.Secrets, fx.Clock);
        var again = await admin.BootstrapAsync(fx.Migrations, "Platform.Admin@Example.test", "Someone Else", ct);
        Assert.Equal(fx.Admin.Id, again.Id);
        await using var cp = await fx.OpenControlPlaneAsync(ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_users", ct));
        Assert.Equal(2, await TenancyFixture.CountAsync(cp, "select count(*) from cp.module_catalog", ct));
    }

    [Fact]
    public async Task FR_ADM_PLT_01_the_customer_list_shows_status_cluster_edition_modules_and_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = await new TenantLifecycle(fx.Options, fx.Clock).ListAsync(ct);
        var a = Assert.Single(rows, r => r.Code == fx.CodeA);
        Assert.Equal(("active", "ready", fx.ClusterName, "business", "active"), (a.Status, a.DatabaseStatus, a.Cluster, a.Edition, a.EntitlementStatus));
        Assert.Equal(["hrm", "kernel"], a.Modules);
        Assert.Equal(fx.Migrations.TenantScripts(["hrm"]).Max(s => s.Version), a.SchemaVersion);
    }

    [Fact]
    public async Task S103_migrator_failure_on_one_customer_puts_it_in_maintenance_and_the_others_are_migrated()
    {
        var ct = TestContext.Current.CancellationToken;
        var good = "mg" + fx.Suffix;
        var bad = "mb" + fx.Suffix;
        await fx.ProvisionAsync(good, ct);
        await fx.ProvisionAsync(bad, ct);

        // A new migration that fails only in the "bad" customer's database.
        var folder = Directory.CreateDirectory(fx.TempFile("fleet-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "V209912310000__kernel__s103_fleet_probe.sql"), $"""
            do $do$ begin
                if current_database() = '{TenantLogins.DatabaseName(bad)}' then raise exception 'forced migration failure'; end if;
            end $do$;
            create table if not exists meta.s103_fleet_probe (id int primary key);
            """, ct);
        var withProbe = MigrationSet.FromRoot(MigratedDatabases.Root, [folder]);
        var migrator = new FleetMigrator(fx.Options, fx.Secrets, fx.Clock, withProbe);

        var outcomes = (await migrator.MigrateAsync(fx.Admin, bad, ct)).Concat(await migrator.MigrateAsync(fx.Admin, good, ct)).ToList();
        Assert.False(Assert.Single(outcomes, o => o.Code == bad).Succeeded);
        var ok = Assert.Single(outcomes, o => o.Code == good);
        Assert.True(ok.Succeeded);
        Assert.Equal(1, ok.Applied);
        Assert.Equal("209912310000", ok.SchemaVersion);
        Assert.Equal("maintenance", (await fx.TenantAsync(bad, ct)).DatabaseStatus);
        Assert.Equal("ready", (await fx.TenantAsync(good, ct)).DatabaseStatus);

        using (var client = fx.Client(TenancyFixture.HostOf(bad), fx.Token(bad)))
        using (var response = await client.GetAsync("/api/org/companies", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using (var client = fx.Client(TenancyFixture.HostOf(good), fx.Token(good)))
        using (var response = await client.GetAsync("/api/org/companies", ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The release without the broken file brings the customer back on the next run.
        var retry = Assert.Single(await new FleetMigrator(fx.Options, fx.Secrets, fx.Clock, fx.Migrations).MigrateAsync(fx.Admin, bad, ct));
        Assert.True(retry.Succeeded);
        Assert.Equal("ready", (await fx.TenantAsync(bad, ct)).DatabaseStatus);
        await using var cp = await fx.OpenControlPlaneAsync(ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = 'tenant.migrate.failed'", ct, bad));
    }

    [Fact]
    public async Task S103_migrator_records_checksums_and_refuses_a_migration_that_was_changed_after_it_was_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateDirectory(fx.TempFile("checksum-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var script = Path.Combine(folder, "V209912310001__kernel__s103_checksum_probe.sql");
        await File.WriteAllTextAsync(script, "create table if not exists meta.s103_checksum_probe (id int primary key);", ct);
        var set = MigrationSet.FromRoot(MigratedDatabases.Root, [folder]);
        await using var owner = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.Owner, ct);

        var first = await TenantSchemaMigrator.ApplyAsync(owner, set.TenantScripts(["hrm"]), fx.Clock, BaselineSeed.SystemUserId, ct);
        Assert.Equal(1, first.Applied);
        var second = await TenantSchemaMigrator.ApplyAsync(owner, set.TenantScripts(["hrm"]), fx.Clock, BaselineSeed.SystemUserId, ct);
        Assert.Equal(0, second.Applied);
        Assert.Equal(1, await TenancyFixture.CountAsync(owner, "select count(*) from meta.schema_history where script_name = $1 and length(checksum) = 64", ct, Path.GetFileName(script)));

        await File.WriteAllTextAsync(script, "create table if not exists meta.s103_checksum_probe (id bigint primary key);", ct);
        await Assert.ThrowsAsync<MigrationChecksumException>(() => TenantSchemaMigrator.ApplyAsync(owner, set.TenantScripts(["hrm"]), fx.Clock, BaselineSeed.SystemUserId, ct));
    }

    [Fact]
    public async Task FR_ADM_DAT_08_backup_of_one_customer_restores_into_a_new_database_and_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var file = fx.TempFile(fx.CodeB + ".dump.enc");
        var backup = new TenantBackup(fx.Options, fx.Secrets, fx.Clock, Environment.GetEnvironmentVariable("ERP_PG_BIN"));

        var bytes = await backup.BackupAsync(fx.Admin, fx.CodeB, file, key, ct);
        Assert.True(bytes > 0);
        Assert.DoesNotContain("PGDMP", await File.ReadAllTextAsync(file, ct), StringComparison.Ordinal); // encrypted, not a plain dump

        var result = await backup.RestoreAsync(fx.Admin, fx.CodeB, file, TenancyFixture.RestoreTarget(fx.CodeB), key, ct);
        Assert.True(result.Verified, string.Join(", ", result.Differences));
        Assert.True(result.TablesCompared > 150, $"only {result.TablesCompared} tables compared");

        var tenant = await fx.TenantAsync(fx.CodeB, ct);
        await using var cp = await fx.OpenControlPlaneAsync(ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.tenant_databases where id = $1 and last_backup_at is not null and last_restore_test_at is not null", ct, tenant.TenantDatabaseId));

        // The restored copy keeps the customer's own logins and privileges.
        var restored = new NpgsqlConnectionStringBuilder(fx.AdminConnectionString) { Database = TenancyFixture.RestoreTarget(fx.CodeB), Pooling = false };
        await using var check = new NpgsqlConnection(restored.ConnectionString);
        await check.OpenAsync(ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(check, "select count(*) from org.companies where code = $1", ct, fx.CodeB));
        Assert.Equal(TenantLogins.LoginName(fx.CodeB, TenantLogins.Owner), await ScalarTextAsync(check, "select pg_get_userbyid(datdba) from pg_database where datname = current_database()", ct));

        // A damaged file is refused.
        var damaged = await File.ReadAllBytesAsync(file, ct);
        damaged[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(file + ".bad", damaged, ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(fx.Admin, fx.CodeB, file + ".bad", TenancyFixture.RestoreTarget(fx.CodeB) + "_bad", key, ct));
    }

    private static async Task<string?> ScalarTextAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (string?)await command.ExecuteScalarAsync(ct);
    }
}
