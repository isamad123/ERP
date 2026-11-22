using System.Net;
using System.Security.Cryptography;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Backup;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>Fixes of the S1.03 review (docs/reviews/S1.03-review.md); each test reproduces the finding's scenario.</summary>
[Collection(TenancyGroup.Name)]
public sealed class S103ReviewFixTests(TenancyFixture fx)
{
    private TenantBackup Backup() => new(fx.Options, fx.Secrets, fx.Clock, Environment.GetEnvironmentVariable("ERP_PG_BIN"));

    private async Task<bool> DatabaseExistsAsync(string name, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(fx.AdminConnectionString);
        await admin.OpenAsync(ct);
        return await TenancyFixture.CountAsync(admin, "select count(*) from pg_database where datname = $1", ct, name) == 1;
    }

    // ---------------------------------------------------------------- finding 1

    [Fact]
    public async Task FR_ADM_DAT_08_backup_to_an_existing_file_fails_and_leaves_that_file_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = fx.TempFile("earlier-backup-" + Guid.NewGuid().ToString("N")[..6] + ".enc");
        await File.WriteAllTextAsync(file, "an earlier backup the operator wants to keep", ct);

        await Assert.ThrowsAsync<IOException>(() => Backup().BackupAsync(fx.Admin, fx.CodeA, file, RandomNumberGenerator.GetBytes(32), ct));
        Assert.Equal("an earlier backup the operator wants to keep", await File.ReadAllTextAsync(file, ct));
    }

    // ---------------------------------------------------------------- findings 2, 5 and 12 (restore)

    [Fact]
    public async Task FR_ADM_DAT_08_restore_drill_verifies_against_the_backup_even_when_the_customer_changed_afterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "d" + fx.Suffix; // a customer of its own: other tests add tables to A that its backup login may not read
        await fx.ProvisionAsync(code, ct);
        var key = RandomNumberGenerator.GetBytes(32);
        var file = fx.TempFile(code + ".drill.enc");
        await Backup().BackupAsync(fx.Admin, code, file, key, ct);

        // A write after the backup (the worker does this every minute): the live database now differs from the backup.
        await using (var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, ct))
        {
            await TenancyFixture.ExecAsync(rw, "update org.companies set legal_name = legal_name || ' (after backup)' where code = $1", ct, code);
        }

        var target = TenancyFixture.RestoreTarget(code);
        var result = await Backup().RestoreAsync(fx.Admin, code, file, target, key, ct);
        Assert.True(result.Verified, string.Join(", ", result.Differences));
        Assert.True(result.TablesCompared > 150, $"only {result.TablesCompared} tables compared");

        // The copy is the backup, not the live database.
        var restored = new NpgsqlConnectionStringBuilder(fx.AdminConnectionString) { Database = target, Pooling = false };
        await using var copy = new NpgsqlConnection(restored.ConnectionString);
        await copy.OpenAsync(ct);
        Assert.Equal(0, await TenancyFixture.CountAsync(copy, "select count(*) from org.companies where legal_name like '% (after backup)'", ct));
    }

    [Fact]
    public async Task FR_ADM_DAT_08_restore_refuses_another_customers_file_a_file_without_manifest_a_damaged_file_and_a_customer_database_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = RandomNumberGenerator.GetBytes(32);
        var fileOfB = fx.TempFile(fx.CodeB + ".other.enc");
        await Backup().BackupAsync(fx.Admin, fx.CodeB, fileOfB, key, ct);

        // Customer B's file restored as customer A: refused, and the database it created is removed again (finding 5).
        var target = "erp_r_x" + fx.Suffix;
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => Backup().RestoreAsync(fx.Admin, fx.CodeA, fileOfB, target, key, ct));
        Assert.Contains(fx.CodeB, wrong.Message, StringComparison.Ordinal);
        Assert.False(await DatabaseExistsAsync(target, ct));

        // A file made before manifests existed (a plain encrypted dump): it cannot be verified, so it is refused.
        var legacy = fx.TempFile("legacy.enc");
        await using (var output = File.Create(legacy))
        using (var plain = new MemoryStream("PGDMP-an-old-style-archive"u8.ToArray()))
        {
            await BackupCipher.EncryptAsync(plain, output, key, ct);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Backup().RestoreAsync(fx.Admin, fx.CodeB, legacy, target, key, ct));
        Assert.False(await DatabaseExistsAsync(target, ct));

        // A damaged file: refused, and the half-restored database is dropped (test gap T2).
        var damaged = await File.ReadAllBytesAsync(fileOfB, ct);
        damaged[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(fileOfB + ".bad", damaged, ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup().RestoreAsync(fx.Admin, fx.CodeB, fileOfB + ".bad", target, key, ct));
        Assert.False(await DatabaseExistsAsync(target, ct));

        // A drill never takes a customer-database name (finding 12).
        await Assert.ThrowsAsync<ArgumentException>(() => Backup().RestoreAsync(fx.Admin, fx.CodeB, fileOfB, "erp_t_future" + fx.Suffix, key, ct));
        Assert.False(await DatabaseExistsAsync("erp_t_future" + fx.Suffix, ct));
    }

    // ---------------------------------------------------------------- finding 3

    [Fact]
    public async Task FR_KRN_TEN_04_unknown_host_names_cost_no_control_plane_query_and_no_cache_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new TenantDirectory(new ControlPlaneOptions { ConnectionString = fx.Options.ConnectionString, HostCacheSeconds = 300 });
        for (var i = 0; i < 2000; i++)
        {
            Assert.Null(await directory.FindByHostAsync($"r{i}-{Guid.NewGuid():N}.erp.test", ct));
        }

        Assert.Equal(0, directory.CachedEntries);
        Assert.Equal(1, directory.Queries); // the one load of the registered host names

        var known = await directory.FindByHostAsync(TenancyFixture.HostOf(fx.CodeA), ct);
        Assert.Equal(fx.CodeA, known!.Code);
        Assert.Equal(1, directory.CachedEntries);
        Assert.Equal(2, directory.Queries);
    }

    // ---------------------------------------------------------------- finding 4

    [Fact]
    public async Task SC2_two_concurrent_branch_removals_cannot_leave_a_company_without_a_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var company = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var actor = BaselineSeed.SystemUserId;
        await using var a = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadWrite, ct);
        await using var b = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadWrite, ct);
        await TenancyFixture.ExecAsync(a, """
            insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"x"}', 'x', 'PK', 'PKR', 'en', 'none', 'active', now(), $3, now(), $3)
            """, ct, company, "SC2R" + fx.Suffix.ToUpperInvariant(), actor);
        await TenancyFixture.ExecAsync(a, """
            insert into org.branches (id, company_id, code, name_i18n, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'B2', '{"en":"b2"}', now(), $3, now(), $3)
            """, ct, second, company, actor);
        var pidB = await TenancyFixture.CountAsync(b, "select pg_backend_pid()", ct);

        await using var ta = await a.BeginTransactionAsync(ct);
        await using (var first = new NpgsqlCommand("update org.branches set is_deleted = true where company_id = $1 and id <> $2", a, ta))
        {
            first.Parameters.Add(new NpgsqlParameter { Value = company });
            first.Parameters.Add(new NpgsqlParameter { Value = second });
            Assert.Equal(1, await first.ExecuteNonQueryAsync(ct));
        }

        // The other removal waits for the first (the company row is locked), then sees it and is refused.
        await using var tb = await b.BeginTransactionAsync(ct);
        await using var other = new NpgsqlCommand("update org.branches set is_deleted = true where id = $1", b, tb);
        other.Parameters.Add(new NpgsqlParameter { Value = second });
        var waiting = other.ExecuteNonQueryAsync(ct);
        await using (var watch = new NpgsqlConnection(fx.AdminConnectionString)) // only a superuser sees another login's wait state
        {
            await watch.OpenAsync(ct);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (await TenancyFixture.CountAsync(watch, "select count(*) from pg_stat_activity where pid = $1 and wait_event_type = 'Lock'", ct, (int)pidB) == 0)
            {
                Assert.False(waiting.IsCompleted, "the second removal did not wait for the first");
                Assert.True(DateTime.UtcNow < deadline, "the second removal never waited for the first");
                await Task.Delay(50, ct);
            }
        }

        await ta.CommitAsync(ct);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => waiting);
        Assert.Equal("23514", refused.SqlState);
        await tb.RollbackAsync(ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(a, "select count(*) from org.branches where company_id = $1 and not is_deleted", ct, company));
    }

    // ---------------------------------------------------------------- finding 6

    [Fact]
    public async Task FR_KRN_TEN_02_cluster_add_refuses_to_move_a_cluster_that_holds_customer_databases()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new NpgsqlConnectionStringBuilder(fx.AdminConnectionString);
        var admin = new ControlPlaneAdmin(fx.Options, fx.Secrets, fx.Clock);
        var secretBefore = await fx.Secrets.GetAsync("cluster/" + fx.ClusterName + "/admin", ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.AddClusterAsync(fx.Admin, fx.ClusterName, "pk-1", "elsewhere.example.test", server.Port, 160, new DatabaseCredential("someone", "other"), ct));
        Assert.Contains("--change-address yes", error.Message, StringComparison.Ordinal);
        Assert.Equal(secretBefore, await fx.Secrets.GetAsync("cluster/" + fx.ClusterName + "/admin", ct)); // nothing written
        await using (var cp = await fx.OpenControlPlaneAsync(ct))
        {
            Assert.Equal(server.Host, await TenancyFixture.ScalarTextAsync(cp, "select host from cp.db_clusters where name = $1", ct, fx.ClusterName));
        }

        // The same address (a credential or capacity update) is still accepted.
        await admin.AddClusterAsync(fx.Admin, fx.ClusterName, "pk-1", server.Host!, server.Port, 160, new DatabaseCredential(server.Username!, server.Password!), ct);

        // An empty cluster may move freely.
        var empty = "it-empty-" + fx.Suffix;
        await admin.AddClusterAsync(fx.Admin, empty, "pk-1", server.Host!, server.Port, 5, new DatabaseCredential(server.Username!, server.Password!), ct);
        await admin.AddClusterAsync(fx.Admin, empty, "pk-1", "elsewhere.example.test", server.Port, 5, new DatabaseCredential(server.Username!, server.Password!), ct);
    }

    // ---------------------------------------------------------------- finding 7

    [Fact]
    public async Task CR004_customer_logins_are_created_from_a_scram_verifier_that_postgres_accepts()
    {
        var ct = TestContext.Current.CancellationToken;

        // The verifier computed in C# equals the one PostgreSQL computes itself for the same password and salt.
        await using var admin = new NpgsqlConnection(fx.AdminConnectionString);
        await admin.OpenAsync(ct);
        var role = "erp_scram_" + fx.Suffix;
        await TenancyFixture.ExecAsync(admin, $"set password_encryption = 'scram-sha-256'; create role {role} nologin password 'Plain-Password_123'", ct);
        try
        {
            var stored = await TenancyFixture.ScalarTextAsync(admin, "select rolpassword from pg_authid where rolname = $1", ct, role);
            var parts = stored.Split('$', ':');
            var salt = Convert.FromBase64String(parts[2]);
            Assert.Equal(stored, ClusterAdministrator.ScramVerifier("Plain-Password_123", salt, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            await TenancyFixture.ExecAsync(admin, $"drop role {role}", ct);
        }

        // Every login of a provisioned customer has a SCRAM verifier, and its stored password still opens a connection.
        Assert.Equal(TenantLogins.All.Count, await TenancyFixture.CountAsync(admin,
            "select count(*) from pg_authid where rolname like $1 and rolpassword like 'SCRAM-SHA-256$4096:%'", ct, "erp_t_" + fx.CodeA + "\\_%"));
        await using var rw = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadWrite, ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(rw, "select 1", ct));
    }

    // ---------------------------------------------------------------- finding 8

    [Fact]
    public async Task Spec03_3_7_R038_the_application_login_cannot_record_a_migration_as_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rw = await fx.OpenTenantAsync(fx.CodeA, TenantLogins.ReadWrite, ct);
        Assert.True(await TenancyFixture.CountAsync(rw, "select count(*) from meta.schema_history", ct) > 0); // still readable
        var refused = await Assert.ThrowsAsync<PostgresException>(() => TenancyFixture.ExecAsync(rw,
            "insert into meta.schema_history (id, module_id, version, script_name, checksum, duration_ms, created_at, created_by) values ($1, 'kernel', '999912312359', 'V999912312359__kernel__fake.sql', 'x', 0, now(), $2)",
            ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    // ---------------------------------------------------------------- finding 9

    [Fact]
    public async Task FR_ADM_PLT_01_a_customer_with_a_scheduled_entitlement_is_listed_once_with_the_entitlement_in_force()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "l" + fx.Suffix;
        await fx.ProvisionAsync(code, ct);
        await using (var cp = await fx.OpenControlPlaneAsync(ct))
        {
            await TenancyFixture.ExecAsync(cp, """
                insert into cp.entitlements (id, tenant_id, version, edition, claims, signature, signing_key_id, effective_from, expires_at, state_since, status, created_at, created_by, updated_at, updated_by)
                select $1, e.tenant_id, 2, 'enterprise', e.claims, e.signature, e.signing_key_id, date '2099-01-01', null, now(), 'scheduled', now(), e.created_by, now(), e.created_by
                from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $2 and e.version = 1
                """, ct, Guid.CreateVersion7(), code);
        }

        var rows = (await new TenantLifecycle(fx.Options, fx.Clock).ListAsync(ct)).Where(r => r.Code == code).ToList();
        var row = Assert.Single(rows);
        Assert.Equal("business", row.Edition);
    }

    // ---------------------------------------------------------------- finding 11

    [Fact]
    public async Task FR_KRN_TEN_03_the_api_serves_customers_with_the_read_only_cp_api_login()
    {
        var ct = TestContext.Current.CancellationToken;
        var login = "erp_cpapi_" + fx.Suffix;
        await using (var admin = new NpgsqlConnection(fx.AdminConnectionString))
        {
            await admin.OpenAsync(ct);
            await TenancyFixture.ExecAsync(admin, $"create role {login} login password 'cp-api-test' in role {ControlPlaneAdmin.ApiRole}", ct);
        }

        var connection = new NpgsqlConnectionStringBuilder(fx.Options.ConnectionString) { Username = login, Password = "cp-api-test" }.ConnectionString;
        try
        {
            await using (var app = await fx.StartAppAsync(null, ct, controlPlaneConnectionString: connection))
            {
                using var client = TenancyFixture.Client(app, TenancyFixture.HostOf(fx.CodeA), fx.Token(fx.CodeA));
                foreach (var path in new[] { "/api/org/companies", "/api/kernel/capabilities", "/api/cfg/licence", "/api/cfg/licence/usage" })
                {
                    using var response = await client.GetAsync(path, ct);
                    Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
                }
            }

            // Read access to what the API reads, nothing else.
            await using var limited = new NpgsqlConnection(connection);
            await limited.OpenAsync(ct);
            var secret = await Assert.ThrowsAsync<PostgresException>(() => TenancyFixture.CountAsync(limited, "select count(*) from cp.platform_users", ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, secret.SqlState);
            var write = await Assert.ThrowsAsync<PostgresException>(() => TenancyFixture.ExecAsync(limited, "update cp.tenants set status = 'suspended' where code = $1", ct, fx.CodeA));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, write.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fx.AdminConnectionString);
            await admin.OpenAsync(ct);
            await TenancyFixture.ExecAsync(admin, $"drop role if exists {login}", ct);
        }
    }

    // ---------------------------------------------------------------- finding 12 (provisioning)

    [Fact]
    public async Task FR_KRN_TEN_02_resuming_provisioning_on_another_cluster_is_refused_and_resumes_on_the_registered_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "pk" + fx.Suffix;
        fx.Track(code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code), new FailAfterStep("register"), ct));

        var server = new NpgsqlConnectionStringBuilder(fx.AdminConnectionString);
        var other = "it-other-" + fx.Suffix;
        await new ControlPlaneAdmin(fx.Options, fx.Secrets, fx.Clock).AddClusterAsync(fx.Admin, other, "pk-1", server.Host!, server.Port, 5, new DatabaseCredential(server.Username!, server.Password!), ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code) with { ClusterName = other }, null, ct));
        Assert.Contains("--cluster " + fx.ClusterName, error.Message, StringComparison.Ordinal);

        var done = await fx.Provisioner().ProvisionAsync(fx.Admin, fx.Request(code), null, ct);
        Assert.False(done.AlreadyProvisioned);
    }

    [Fact]
    public async Task FR_KRN_TEN_02_provisioning_does_not_take_over_a_database_it_did_not_create()
    {
        var ct = TestContext.Current.CancellationToken;
        var code = "po" + fx.Suffix;
        fx.Track(code);
        await using (var admin = new NpgsqlConnection(fx.AdminConnectionString))
        {
            await admin.OpenAsync(ct);
            await TenancyFixture.ExecAsync(admin, $"create database {TenantLogins.DatabaseName(code)}", ct); // owned by the server administrator
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.ProvisionAsync(code, ct));
        Assert.Contains("does not take it over", error.Message, StringComparison.Ordinal);
        Assert.Equal("provisioning", (await fx.TenantAsync(code, ct)).Status);
    }

    // ---------------------------------------------------------------- test gap T3

    [Fact]
    public async Task CR004_no_shared_group_role_and_not_public_may_connect_to_a_customer_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var admin = new NpgsqlConnection(fx.AdminConnectionString);
        await admin.OpenAsync(ct);
        foreach (var code in new[] { fx.CodeA, fx.CodeB })
        {
            var database = TenantLogins.DatabaseName(code);
            foreach (var role in TenantLogins.GroupRoles)
            {
                Assert.Equal(0, await TenancyFixture.CountAsync(admin, "select count(*) from pg_database where datname = $1 and has_database_privilege($2::name, oid, 'connect')", ct, database, role));
            }

            // public: no entry for it (0) in the database's access list
            Assert.Equal(0, await TenancyFixture.CountAsync(admin,
                "select count(*) from pg_database d, aclexplode(d.datacl) a where d.datname = $1 and a.grantee = 0 and a.privilege_type = 'CONNECT'", ct, database));
        }
    }

    private sealed class FailAfterStep(string failingStep) : IProvisioningProgress
    {
        public ValueTask StepCompletedAsync(string stepName, CancellationToken cancellationToken) =>
            stepName == failingStep ? throw new InvalidOperationException($"forced failure after {stepName}") : ValueTask.CompletedTask;
    }
}
