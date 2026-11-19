using System.Security.Cryptography;
using Erp.BuildingBlocks.Infrastructure.Migrations;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.CodeGen.Dictionary;
using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>
/// CR-006 (slice S1.11f2): the control-plane migrator records every file it applies with its checksum in
/// cp.schema_history, like the customer-database migrator (FR-KRN-TEN-02). Each test uses an empty control-plane
/// database of its own on the shared test server.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ControlPlaneMigrationTests(PostgresFixture fx)
{
    /// <summary>The file of this slice that creates cp.schema_history.</summary>
    private const string HistoryScript = "V202611155508__controlplane__design_s1_11f_r2.sql";

    private static readonly MigrationSet Migrations = MigrationSet.FromRoot(MigratedDatabases.Root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Scratch(ScratchDatabase database, string folder) : IAsyncDisposable
    {
        public string ConnectionString => database.ConnectionString;

        public string Folder => folder;

        public ControlPlaneAdmin Admin { get; } = new(
            new ControlPlaneOptions { ConnectionString = database.ConnectionString },
            new EncryptedFileSecretStore(Path.Combine(folder, "secrets.bin"), RandomNumberGenerator.GetBytes(32)),
            new SystemClock());

        public async Task<NpgsqlConnection> OpenAsync()
        {
            var c = new NpgsqlConnection(ConnectionString);
            await c.OpenAsync(Ct);
            return c;
        }

        public async Task<long> CountAsync(string sql, params object[] values)
        {
            await using var c = await OpenAsync();
            return await TenancyFixture.CountAsync(c, sql, Ct, values);
        }

        public async ValueTask DisposeAsync()
        {
            await database.DropAsync(CancellationToken.None);
            Directory.Delete(folder, recursive: true);
        }
    }

    private async Task<Scratch> NewControlPlaneAsync() =>
        new(await ScratchDatabase.CreateAsync(fx.ConnectionString, "erp_it_cpm_" + Guid.NewGuid().ToString("N")[..10], Ct),
            Directory.CreateTempSubdirectory("erp-cpm-").FullName);

    private static async Task<SchemaMigrationResult> BootstrapAsync(Scratch cp, MigrationSet? migrations = null)
    {
        SchemaMigrationResult? result = null;
        await cp.Admin.BootstrapAsync(migrations ?? Migrations, "platform.admin@example.test", "Platform Admin", Ct, r => result = r);
        return result!;
    }

    [Fact]
    public async Task FR_KRN_TEN_02_CR006_a_new_control_plane_records_every_script_with_its_checksum_and_a_second_run_applies_nothing()
    {
        await using var cp = await NewControlPlaneAsync();
        var scripts = Migrations.ControlPlaneScripts;
        Assert.Contains(scripts, s => s.Name == HistoryScript);

        var first = await BootstrapAsync(cp);
        Assert.Equal((scripts.Count, 0), (first.Applied, first.AlreadyApplied));
        foreach (var script in scripts)
        {
            Assert.Equal(1, await cp.CountAsync(
                "select count(*) from cp.schema_history where module_id = 'controlplane' and script_name = $1 and version = $2 and checksum = $3 and created_by = $4",
                script.Name, script.Version, script.Checksum(), BaselineSeed.SystemUserId));
        }

        Assert.Equal(scripts.Count, await cp.CountAsync("select count(*) from cp.schema_history"));
        var second = await BootstrapAsync(cp);
        Assert.Equal((0, scripts.Count), (second.Applied, second.AlreadyApplied));
        Assert.Equal(scripts.Count, await cp.CountAsync("select count(*) from cp.schema_history"));
    }

    /// <summary>
    /// A control plane set up before this slice ran the idempotent files and recorded nothing (S1.03). Its first run of the
    /// new migrator applies them again, creates cp.schema_history and records them all; its data is kept.
    /// </summary>
    [Fact]
    public async Task FR_KRN_TEN_02_CR006_an_existing_control_plane_without_history_migrates_cleanly_and_records_the_scripts_it_already_ran()
    {
        await using var cp = await NewControlPlaneAsync();
        var scripts = Migrations.ControlPlaneScripts;
        var earlier = scripts.Where(s => string.CompareOrdinal(s.Name, HistoryScript) < 0).Select(s => s.Path).ToList();
        Assert.Equal(scripts.Count - 1, earlier.Count);
        await SqlScriptRunner.ApplyAsync(cp.ConnectionString, earlier, Ct); // what the S1.03 migrator did
        var userId = Guid.CreateVersion7();
        await using (var c = await cp.OpenAsync())
        {
            await using var insert = new NpgsqlCommand("""
                insert into cp.platform_users (id, email, display_name, role, mfa_enrolled, status, created_at, created_by, updated_at, updated_by)
                values ($1, 'platform.admin@example.test', 'Platform Admin', 'platform_admin', false, 'active', now(), $1, now(), $1)
                """, c);
            insert.Parameters.Add(new NpgsqlParameter { Value = userId });
            await insert.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(0, await cp.CountAsync("select count(*) from pg_class where oid = to_regclass('cp.schema_history')"));

        var first = await BootstrapAsync(cp);
        Assert.Equal((scripts.Count, 0), (first.Applied, first.AlreadyApplied));
        Assert.Equal(scripts.Count, await cp.CountAsync("select count(*) from cp.schema_history"));
        Assert.Equal(1, await cp.CountAsync("select count(*) from cp.platform_users where id = $1", userId));
        Assert.Equal(1, await cp.CountAsync("select count(*) from cp.platform_users"));

        var second = await BootstrapAsync(cp);
        Assert.Equal((0, scripts.Count), (second.Applied, second.AlreadyApplied));
    }

    [Fact]
    public async Task FR_KRN_TEN_02_CR006_a_control_plane_script_changed_after_it_was_applied_is_refused()
    {
        await using var cp = await NewControlPlaneAsync();
        var folder = Directory.CreateDirectory(Path.Combine(cp.Folder, "migrations")).FullName;
        foreach (var script in Migrations.ControlPlaneScripts)
        {
            File.Copy(script.Path, Path.Combine(folder, script.Name));
        }

        var copy = new MigrationSet(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), [folder]);
        await BootstrapAsync(cp, copy);

        var changed = Path.Combine(folder, "V202611150304__controlplane__design_s1_07.sql");
        await File.AppendAllTextAsync(changed, "\n-- edited after it was applied\n", Ct);
        var error = await Assert.ThrowsAsync<MigrationChecksumException>(() => BootstrapAsync(cp, copy));
        Assert.Contains("V202611150304__controlplane__design_s1_07.sql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_TEN_02_CR006_the_control_plane_history_is_append_only()
    {
        await using var cp = await NewControlPlaneAsync();
        await BootstrapAsync(cp);
        await using var c = await cp.OpenAsync();
        foreach (var sql in new[] { "update cp.schema_history set checksum = 'x'", "delete from cp.schema_history", "truncate cp.schema_history" })
        {
            await using var command = new NpgsqlCommand(sql, c);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal("ER001", error.SqlState);
        }
    }

    /// <summary>Two migrators started at the same moment take turns: every script is applied and recorded once.</summary>
    [Fact]
    public async Task FR_KRN_TEN_02_CR006_two_control_plane_migrators_at_once_apply_and_record_each_script_once()
    {
        await using var cp = await NewControlPlaneAsync();
        var scripts = Migrations.ControlPlaneScripts;
        using var start = new SemaphoreSlim(0, 2);
        var runs = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var c = await cp.OpenAsync();
            await start.WaitAsync(Ct);
            return await ControlPlaneSchemaMigrator.ApplyAsync(c, scripts, new SystemClock(), BaselineSeed.SystemUserId, Ct);
        }).ToList();
        start.Release(2);
        var results = await Task.WhenAll(runs);

        Assert.Equal(scripts.Count, results.Sum(r => r.Applied));
        Assert.All(results, r => Assert.Equal(scripts.Count, r.Applied + r.AlreadyApplied));
        Assert.Equal(scripts.Count, await cp.CountAsync("select count(*) from cp.schema_history"));
    }
}
