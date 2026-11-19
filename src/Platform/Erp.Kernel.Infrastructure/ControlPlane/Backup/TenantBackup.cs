using System.Diagnostics;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Backup;

/// <summary>Result of a restore drill: tables compared and any table whose row count or content differs.</summary>
public sealed record RestoreVerification(string TargetDatabase, int TablesCompared, IReadOnlyList<string> Differences)
{
    public bool Verified => Differences.Count == 0;
}

/// <summary>
/// Per-customer backup and restore (S1.03 build item 7, FR-ADM-DAT-08): pg_dump of one customer database as its backup
/// login, encrypted with <see cref="BackupCipher"/>, with a <see cref="BackupManifest"/> (customer, and every table's row count
/// and content checksum taken in the dump's own snapshot) in front of the dump. Restore goes into a new database on the same
/// server, refuses another customer's file, and compares the copy table by table with the manifest. Results are recorded on
/// cp.tenant_databases (last_backup_at, last_restore_test_at) and in cp.platform_audit.
/// </summary>
public sealed partial class TenantBackup(ControlPlaneOptions options, ISecretStore secrets, IClock clock, string? postgresBinFolder = null)
{
    public async Task<long> BackupAsync(PlatformActor actor, string code, string outputPath, byte[] key, CancellationToken cancellationToken)
    {
        var tenant = await FindAsync(code, cancellationToken).ConfigureAwait(false);
        var credential = await CredentialAsync(tenant, TenantLogins.Backup, cancellationToken).ConfigureAwait(false);

        // Created before anything else and outside the clean-up below: an existing file makes the command fail and is
        // never touched (S1.03 review finding 1). Only the file this call created is deleted on failure.
        var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        (Process Process, Task<string> Errors)? dump = null;
        long bytes;
        try
        {
            // The fingerprints are taken in the transaction whose snapshot pg_dump then uses, so they describe exactly what
            // is dumped, however busy the customer is (S1.03 review finding 2).
            await using var snapshot = await OpenAsync(tenant.ClusterHost, tenant.ClusterPort, tenant.DatabaseName, credential, cancellationToken).ConfigureAwait(false);
            await EnsureToolMatchesServerAsync("pg_dump", snapshot, cancellationToken).ConfigureAwait(false);
            await using var transaction = await snapshot.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(snapshot, transaction, "set transaction read only", cancellationToken).ConfigureAwait(false);
            await FixOutputSettingsAsync(snapshot, transaction, cancellationToken).ConfigureAwait(false);
            var snapshotId = (await Sql.ScalarAsync<string>(snapshot, transaction, "select pg_export_snapshot()", cancellationToken).ConfigureAwait(false))!;
            var manifest = new BackupManifest(code, tenant.DatabaseName, clock.UtcNow, await FingerprintAsync(snapshot, transaction, cancellationToken).ConfigureAwait(false));

            dump = Start("pg_dump", ["--format=custom", "--no-password", "--snapshot", snapshotId, "--host", tenant.ClusterHost, "--port", tenant.ClusterPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", credential.Username, "--dbname", tenant.DatabaseName], credential.Password);
            await using (var plain = new PrefixedStream(manifest.ToBytes(), dump.Value.Process.StandardOutput.BaseStream))
            {
                bytes = await BackupCipher.EncryptAsync(plain, file, key, cancellationToken).ConfigureAwait(false);
            }

            await FinishAsync(dump.Value, "pg_dump", cancellationToken).ConfigureAwait(false);
            dump = null;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await file.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            Stop(dump);
            await file.DisposeAsync().ConfigureAwait(false);
            File.Delete(outputPath); // never leave a partial backup that looks complete
            throw;
        }

        await new TenantLifecycle(options, clock).RecordBackupAsync(actor, code, restoreTest: false, new { code, bytes, file = Path.GetFileName(outputPath) }, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public async Task<RestoreVerification> RestoreAsync(PlatformActor actor, string code, string inputPath, string targetDatabase, byte[] key, CancellationToken cancellationToken)
    {
        // A drill never takes a name of the customer-database form (erp_t_…): provisioning would later treat such a database
        // as a customer's own (S1.03 review finding 12).
        if (!TargetName().IsMatch(targetDatabase) || targetDatabase.StartsWith(TenantLogins.DatabaseName(string.Empty), StringComparison.Ordinal))
        {
            throw new ArgumentException("The restore target must be a new database name (lower-case letters, digits, underscores) that does not start with erp_t_.", nameof(targetDatabase));
        }

        var tenant = await FindAsync(code, cancellationToken).ConfigureAwait(false);
        ClusterInfo cluster;
        await using (var cp = new NpgsqlConnection(options.ConnectionString))
        {
            await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
            var clusterName = await Sql.ScalarAsync<string>(cp, null, "select c.name from cp.db_clusters c join cp.tenant_databases d on d.db_cluster_id = c.id where d.id = $1", cancellationToken, tenant.TenantDatabaseId).ConfigureAwait(false);
            cluster = await TenantProvisioner.ReadClusterAsync(cp, null, clusterName!, cancellationToken).ConfigureAwait(false);
        }

        // Read before the target exists, so a missing secret leaves nothing behind (S1.03 review finding 5).
        var adminCredential = DatabaseCredential.Parse(await secrets.GetAsync(cluster.AdminSecretRef, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No administrator credential is stored for cluster {cluster.Name}."));
        var admin = new ClusterAdministrator(secrets);
        await using (var connection = await admin.OpenAdminAsync(cluster, "postgres", cancellationToken).ConfigureAwait(false))
        {
            await EnsureToolMatchesServerAsync("pg_restore", connection, cancellationToken).ConfigureAwait(false);
            if (await Sql.ScalarAsync<bool>(connection, null, "select exists (select 1 from pg_database where datname = $1)", cancellationToken, targetDatabase).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Database {targetDatabase} already exists; a restore never overwrites a database.");
            }

            await Sql.ExecuteAsync(connection, null,
                $"create database {Sql.Identifier(targetDatabase)} owner {Sql.Identifier(TenantLogins.LoginName(code, TenantLogins.Owner))} encoding 'UTF8' template template0", cancellationToken).ConfigureAwait(false);
        }

        BackupManifest manifest;
        (Process Process, Task<string> Errors)? restore = null;
        try
        {
            await using (var connection = await admin.OpenAdminAsync(cluster, "postgres", cancellationToken).ConfigureAwait(false))
            {
                await Sql.ExecuteAsync(connection, null, $"revoke all on database {Sql.Identifier(targetDatabase)} from public", cancellationToken).ConfigureAwait(false);
            }

            restore = Start("pg_restore", ["--no-password", "--exit-on-error", "--host", cluster.Host, "--port", cluster.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", adminCredential.Username, "--dbname", targetDatabase], adminCredential.Password, redirectInput: true);
            try
            {
                await using var file = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
                await using var splitter = new ManifestSplitter(restore.Value.Process.StandardInput.BaseStream, m =>
                {
                    if (!string.Equals(m.Tenant, code, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"This backup belongs to customer {m.Tenant}, not to {code}.");
                    }
                });
                await BackupCipher.DecryptAsync(file, splitter, key, cancellationToken).ConfigureAwait(false);
                manifest = splitter.Manifest ?? throw new InvalidDataException("The backup file has no manifest; it cannot be verified.");
            }
            finally
            {
                restore.Value.Process.StandardInput.Close();
            }

            await FinishAsync(restore.Value, "pg_restore", cancellationToken).ConfigureAwait(false);
            restore = null;
        }
        catch
        {
            // Remove the half-restored database this call created; the source is never touched.
            Stop(restore);
            await using var connection = await admin.OpenAdminAsync(cluster, "postgres", CancellationToken.None).ConfigureAwait(false);
            await Sql.ExecuteAsync(connection, null, $"drop database if exists {Sql.Identifier(targetDatabase)} with (force)", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The copy is compared with what the backup recorded when it was taken, not with the live database (finding 2).
        RestoreVerification verification;
        await using (var target = await admin.OpenAdminAsync(cluster, targetDatabase, cancellationToken).ConfigureAwait(false))
        await using (var transaction = await target.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false))
        {
            await FixOutputSettingsAsync(target, transaction, cancellationToken).ConfigureAwait(false);
            var expected = manifest.Tables;
            var actual = await FingerprintAsync(target, transaction, cancellationToken).ConfigureAwait(false);
            var differences = expected.Keys.Union(actual.Keys, StringComparer.Ordinal)
                .Where(t => !expected.TryGetValue(t, out var e) || !actual.TryGetValue(t, out var a) || e != a)
                .Order(StringComparer.Ordinal)
                .ToList();
            verification = new RestoreVerification(targetDatabase, expected.Count, differences);
        }

        if (verification.Verified)
        {
            await new TenantLifecycle(options, clock).RecordBackupAsync(actor, code, restoreTest: true,
                new { code, target_database = targetDatabase, tables = verification.TablesCompared, file = Path.GetFileName(inputPath), backup_taken_at = manifest.TakenAt }, cancellationToken).ConfigureAwait(false);
        }

        return verification;
    }

    /// <summary>Row text does not depend on the session: the same output settings for the backup and the restored copy.</summary>
    private static Task<int> FixOutputSettingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connection, transaction,
            "set local timezone = 'UTC'; set local datestyle = 'ISO, YMD'; set local intervalstyle = 'postgres'; set local extra_float_digits = 3; set local bytea_output = 'hex'",
            cancellationToken);

    /// <summary>
    /// Row count and an order-independent content checksum of every table (partitions are counted through their parent):
    /// the sum of the first 8 bytes of each row's md5, so the memory used does not grow with the table.
    /// </summary>
    private static async Task<Dictionary<string, string>> FingerprintAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = Sql.Command(connection, transaction, """
            select format('%I.%I', n.nspname, c.relname) from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p') and not c.relispartition and n.nspname not in ('pg_catalog', 'information_schema') and n.nspname not like 'pg_toast%'
            order by 1
            """, []))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            // The name comes from pg_class quoted by format('%I'); it is not user input.
            var fingerprint = await Sql.ScalarAsync<string>(connection, transaction,
                $"select count(*)::text || ':' || coalesce(sum(('x' || left(md5(t::text), 16))::bit(64)::bigint), 0)::text from {table} t", cancellationToken).ConfigureAwait(false);
            result[table] = fingerprint!;
        }

        return result;
    }

    /// <summary>Stops a tool that is still running after a failure and releases its process (finding 5).</summary>
    private static void Stop((Process Process, Task<string> Errors)? run)
    {
        if (run is not { } r)
        {
            return;
        }

        try
        {
            if (!r.Process.HasExited)
            {
                r.Process.Kill(entireProcessTree: true);
                r.Process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        finally
        {
            r.Process.Dispose();
        }
    }

    /// <summary>
    /// The client tools must have the server's major version: a newer pg_dump writes archives that an older server cannot
    /// restore (for example settings that do not exist there), and an older one cannot dump a newer server.
    /// </summary>
    private async Task EnsureToolMatchesServerAsync(string tool, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var serverMajor = int.Parse((await Sql.ScalarAsync<string>(connection, null, "show server_version_num", cancellationToken).ConfigureAwait(false))!, System.Globalization.CultureInfo.InvariantCulture) / 10000;
        var run = Start(tool, ["--version"], string.Empty, captureOutput: true);
        var version = await run.Process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await FinishAsync(run, tool, cancellationToken).ConfigureAwait(false);
        var match = ToolVersion().Match(version);
        if (!match.Success || int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != serverMajor)
        {
            throw new InvalidOperationException(
                $"{tool} reports '{version.Trim()}' but the server is PostgreSQL {serverMajor}; install the PostgreSQL {serverMajor} client tools and point ERP_PG_BIN at them.");
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(string host, int port, string database, DatabaseCredential credential, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = database, Username = credential.Username, Password = credential.Password, Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<ResolvedTenant> FindAsync(string code, CancellationToken cancellationToken)
    {
        TenantCodes.Validate(code);
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await TenantDirectory.FindByCodeAsync(connection, code, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {code} is not registered.");
    }

    private async Task<DatabaseCredential> CredentialAsync(ResolvedTenant tenant, string kind, CancellationToken cancellationToken) =>
        DatabaseCredential.Parse(await secrets.GetAsync(TenantLogins.SecretRef(tenant.AppSecretRef, kind), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No {kind} credential is stored for customer {tenant.Code}."));

    private (Process Process, Task<string> Errors) Start(string tool, IEnumerable<string> arguments, string password, bool redirectInput = false, bool captureOutput = false)
    {
        var info = new ProcessStartInfo(postgresBinFolder is null ? tool : Path.Combine(postgresBinFolder, tool))
        {
            RedirectStandardOutput = !redirectInput || captureOutput,
            RedirectStandardInput = redirectInput,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["PGPASSWORD"] = password; // passed to the child only; never on the command line or in logs
        var process = Process.Start(info) ?? throw new InvalidOperationException($"{tool} could not be started.");
        return (process, process.StandardError.ReadToEndAsync()); // read concurrently so a chatty tool never blocks
    }

    private static async Task FinishAsync((Process Process, Task<string> Errors) run, string tool, CancellationToken cancellationToken)
    {
        var (process, errorText) = run;
        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var errors = await errorText.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{tool} failed (exit {process.ExitCode}): {errors.Trim()}");
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,62}$")]
    private static partial Regex TargetName();

    [GeneratedRegex(@"\) (\d+)(\.|\s)")]
    private static partial Regex ToolVersion();
}
