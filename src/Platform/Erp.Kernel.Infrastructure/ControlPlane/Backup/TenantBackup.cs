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
/// login, encrypted with <see cref="BackupCipher"/>; restore into a new database on the same server, then a table by
/// table comparison of row counts and content checksums with the source. Results are recorded on
/// cp.tenant_databases (last_backup_at, last_restore_test_at) and in cp.platform_audit.
/// </summary>
public sealed partial class TenantBackup(ControlPlaneOptions options, ISecretStore secrets, IClock clock, string? postgresBinFolder = null)
{
    public async Task<long> BackupAsync(PlatformActor actor, string code, string outputPath, byte[] key, CancellationToken cancellationToken)
    {
        var tenant = await FindAsync(code, cancellationToken).ConfigureAwait(false);
        var credential = await CredentialAsync(tenant, TenantLogins.Backup, cancellationToken).ConfigureAwait(false);
        var process = Start("pg_dump", ["--format=custom", "--no-password", "--host", tenant.ClusterHost, "--port", tenant.ClusterPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", credential.Username, "--dbname", tenant.DatabaseName], credential.Password);
        long bytes;
        try
        {
            await using (var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write))
            {
                bytes = await BackupCipher.EncryptAsync(process.Process.StandardOutput.BaseStream, file, key, cancellationToken).ConfigureAwait(false);
            }

            await FinishAsync(process, "pg_dump", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(outputPath); // never leave a partial backup that looks complete
            throw;
        }
        await new TenantLifecycle(options, clock).RecordBackupAsync(actor, code, restoreTest: false, new { code, bytes, file = Path.GetFileName(outputPath) }, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public async Task<RestoreVerification> RestoreAsync(PlatformActor actor, string code, string inputPath, string targetDatabase, byte[] key, CancellationToken cancellationToken)
    {
        if (!TargetName().IsMatch(targetDatabase) || targetDatabase == TenantLogins.DatabaseName(code))
        {
            throw new ArgumentException("The restore target must be a new database name (lower-case letters, digits, underscores).", nameof(targetDatabase));
        }

        var tenant = await FindAsync(code, cancellationToken).ConfigureAwait(false);
        ClusterInfo cluster;
        await using (var cp = new NpgsqlConnection(options.ConnectionString))
        {
            await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
            var clusterName = await Sql.ScalarAsync<string>(cp, null, "select c.name from cp.db_clusters c join cp.tenant_databases d on d.db_cluster_id = c.id where d.id = $1", cancellationToken, tenant.TenantDatabaseId).ConfigureAwait(false);
            cluster = await TenantProvisioner.ReadClusterAsync(cp, null, clusterName!, cancellationToken).ConfigureAwait(false);
        }

        var admin = new ClusterAdministrator(secrets);
        await using (var connection = await admin.OpenAdminAsync(cluster, "postgres", cancellationToken).ConfigureAwait(false))
        {
            if (await Sql.ScalarAsync<bool>(connection, null, "select exists (select 1 from pg_database where datname = $1)", cancellationToken, targetDatabase).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Database {targetDatabase} already exists; a restore never overwrites a database.");
            }

            await Sql.ExecuteAsync(connection, null,
                $"create database {Sql.Identifier(targetDatabase)} owner {Sql.Identifier(TenantLogins.LoginName(code, TenantLogins.Owner))} encoding 'UTF8' template template0", cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(connection, null, $"revoke all on database {Sql.Identifier(targetDatabase)} from public", cancellationToken).ConfigureAwait(false);
        }

        var adminCredential = DatabaseCredential.Parse((await secrets.GetAsync(cluster.AdminSecretRef, cancellationToken).ConfigureAwait(false))!);
        var process = Start("pg_restore", ["--no-password", "--exit-on-error", "--host", cluster.Host, "--port", cluster.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", adminCredential.Username, "--dbname", targetDatabase], adminCredential.Password, redirectInput: true);
        try
        {
            try
            {
                await using var file = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
                await BackupCipher.DecryptAsync(file, process.Process.StandardInput.BaseStream, key, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                process.Process.StandardInput.Close();
            }

            await FinishAsync(process, "pg_restore", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Remove the half-restored database this call created; the source is never touched.
            if (!process.Process.HasExited)
            {
                process.Process.Kill();
            }

            await using var connection = await admin.OpenAdminAsync(cluster, "postgres", CancellationToken.None).ConfigureAwait(false);
            await Sql.ExecuteAsync(connection, null, $"drop database if exists {Sql.Identifier(targetDatabase)} with (force)", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        RestoreVerification verification;
        await using (var source = await admin.OpenAdminAsync(cluster, tenant.DatabaseName, cancellationToken).ConfigureAwait(false))
        await using (var target = await admin.OpenAdminAsync(cluster, targetDatabase, cancellationToken).ConfigureAwait(false))
        {
            var expected = await FingerprintAsync(source, cancellationToken).ConfigureAwait(false);
            var actual = await FingerprintAsync(target, cancellationToken).ConfigureAwait(false);
            var differences = expected.Keys.Union(actual.Keys, StringComparer.Ordinal)
                .Where(t => !expected.TryGetValue(t, out var e) || !actual.TryGetValue(t, out var a) || e != a)
                .Order(StringComparer.Ordinal)
                .ToList();
            verification = new RestoreVerification(targetDatabase, expected.Count, differences);
        }

        if (verification.Verified)
        {
            await new TenantLifecycle(options, clock).RecordBackupAsync(actor, code, restoreTest: true,
                new { code, target_database = targetDatabase, tables = verification.TablesCompared, file = Path.GetFileName(inputPath) }, cancellationToken).ConfigureAwait(false);
        }

        return verification;
    }

    /// <summary>Row count and an order-independent content checksum of every table (partitions are counted through their parent).</summary>
    private static async Task<Dictionary<string, string>> FingerprintAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = Sql.Command(connection, null, """
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
            var fingerprint = await Sql.ScalarAsync<string>(connection, null,
                $"select count(*)::text || ':' || coalesce(md5(string_agg(md5(t::text), '' order by md5(t::text))), '') from {table} t", cancellationToken).ConfigureAwait(false);
            result[table] = fingerprint!;
        }

        return result;
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

    private (Process Process, Task<string> Errors) Start(string tool, IEnumerable<string> arguments, string password, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(postgresBinFolder is null ? tool : Path.Combine(postgresBinFolder, tool))
        {
            RedirectStandardOutput = !redirectInput,
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
}
