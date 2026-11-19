using System.Diagnostics;
using Erp.BuildingBlocks.Domain.Time;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Migration;

/// <summary>Result of migrating one customer database.</summary>
public sealed record SchemaMigrationResult(int Applied, int AlreadyApplied, string? SchemaVersion, IReadOnlyDictionary<string, string> ModuleVersions);

/// <summary>A migration file changed after it was applied (CLAUDE.md: never edit a migration after it is merged).</summary>
public sealed class MigrationChecksumException(string script) :
    Exception($"Migration {script} was changed after it was applied to this database; write a new migration instead.");

/// <summary>
/// Applies pending migration files to one customer database, each in its own transaction, and records every applied
/// file with its checksum in meta.schema_history (FR-KRN-TEN-02). Files already recorded are skipped; a recorded file
/// whose content changed stops the run. An advisory lock stops two migrators working on the same database.
/// </summary>
public static class TenantSchemaMigrator
{
    private const long MigrationLockKey = 0x45_52_50_4D_49_47; // "ERPMIG"

    public static Task<SchemaMigrationResult> ApplyAsync(NpgsqlConnection connection, IReadOnlyList<MigrationScript> scripts, IClock clock, Guid actor, CancellationToken cancellationToken) =>
        RecordedSchemaMigrator.ApplyAsync(connection, scripts, "meta.schema_history", MigrationLockKey, clock, actor, cancellationToken);
}

/// <summary>
/// The control-plane database's migrator (CR-006, slice S1.11f2): the same rules as <see cref="TenantSchemaMigrator"/>,
/// recorded in cp.schema_history. A control plane set up before this table existed re-runs its idempotent files once
/// (as every run did before) and records them all when the file that creates the table has run.
/// </summary>
public static class ControlPlaneSchemaMigrator
{
    /// <summary>The session advisory-lock key that serializes control-plane migrators.</summary>
    public const long MigrationLockKey = 0x45_52_50_43_50_4D; // "ERPCPM"

    public static Task<SchemaMigrationResult> ApplyAsync(NpgsqlConnection connection, IReadOnlyList<MigrationScript> scripts, IClock clock, Guid actor, CancellationToken cancellationToken) =>
        RecordedSchemaMigrator.ApplyAsync(connection, scripts, "cp.schema_history", MigrationLockKey, clock, actor, cancellationToken);
}

/// <summary>
/// Applies pending migration files, each in its own transaction, and records every applied file with its checksum in
/// the given history table. Files already recorded are skipped; a recorded file whose content changed stops the run.
/// A session advisory lock stops two migrators working on the same database at once.
/// </summary>
internal static class RecordedSchemaMigrator
{
    public static async Task<SchemaMigrationResult> ApplyAsync(NpgsqlConnection connection, IReadOnlyList<MigrationScript> scripts, string historyTable, long lockKey, IClock clock, Guid actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(clock);
        if (historyTable is not ("meta.schema_history" or "cp.schema_history"))
        {
            throw new ArgumentOutOfRangeException(nameof(historyTable), historyTable, "Unknown migration history table.");
        }

        await ExecuteAsync(connection, null, "select pg_advisory_lock($1)", cancellationToken, lockKey).ConfigureAwait(false);
        try
        {
            var history = await ReadHistoryAsync(connection, historyTable, cancellationToken).ConfigureAwait(false);
            var unrecorded = new List<(MigrationScript Script, string Checksum, long Ms)>();
            int applied = 0, skipped = 0;
            foreach (var script in scripts)
            {
                var checksum = script.Checksum();
                if (history.TryGetValue(script.Name, out var recorded))
                {
                    if (!string.Equals(recorded, checksum, StringComparison.Ordinal))
                    {
                        throw new MigrationChecksumException(script.Name);
                    }

                    skipped++;
                    continue;
                }

                var sql = await File.ReadAllTextAsync(script.Path, cancellationToken).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var watch = Stopwatch.StartNew();
                try
                {
#pragma warning disable CA2100 // migration file from the release, not user input
                    await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 })
#pragma warning restore CA2100
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    unrecorded.Add((script, checksum, watch.ElapsedMilliseconds));
                    // The history table is itself created by a migration: record everything once it exists.
                    if (await HistoryExistsAsync(connection, transaction, historyTable, cancellationToken).ConfigureAwait(false))
                    {
                        foreach (var (s, c, ms) in unrecorded)
                        {
                            await RecordAsync(connection, transaction, historyTable, s, c, ms, clock, actor, cancellationToken).ConfigureAwait(false);
                        }

                        unrecorded.Clear();
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    applied++;
                }
                catch (PostgresException ex)
                {
                    throw new InvalidOperationException($"Migration {script.Name} failed: {ex.MessageText} (SQLSTATE {ex.SqlState}).", ex);
                }
            }

            var versions = scripts.GroupBy(s => s.Module, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Version).Max(StringComparer.Ordinal)!, StringComparer.Ordinal);
            var schemaVersion = scripts.Count == 0 ? null : scripts.Select(s => s.Version).Max(StringComparer.Ordinal);
            return new SchemaMigrationResult(applied, skipped, schemaVersion, versions);
        }
        finally
        {
            await ExecuteAsync(connection, null, "select pg_advisory_unlock($1)", CancellationToken.None, lockKey).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadHistoryAsync(NpgsqlConnection connection, string historyTable, CancellationToken cancellationToken)
    {
        var history = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!await HistoryExistsAsync(connection, null, historyTable, cancellationToken).ConfigureAwait(false))
        {
            return history;
        }

#pragma warning disable CA2100 // the table name is one of two constants checked in ApplyAsync
        await using var command = new NpgsqlCommand($"select script_name, checksum from {historyTable}", connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history[reader.GetString(0)] = reader.GetString(1);
        }

        return history;
    }

    private static async Task<bool> HistoryExistsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string historyTable, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select to_regclass($1) is not null", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = historyTable });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string historyTable, MigrationScript script, string checksum, long ms, IClock clock, Guid actor, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return ExecuteAsync(connection, transaction,
            $"insert into {historyTable} (id, module_id, version, script_name, checksum, duration_ms, created_at, created_by) values ($1, $2, $3, $4, $5, $6, $7, $8)",
            cancellationToken, Guid.CreateVersion7(now), script.Module, script.Version, script.Name, checksum, (int)Math.Min(ms, int.MaxValue), now, actor);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object[] values)
    {
#pragma warning disable CA2100 // constant SQL text with parameters (the history table name is one of two constants)
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
