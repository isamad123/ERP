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

    public static async Task<SchemaMigrationResult> ApplyAsync(NpgsqlConnection connection, IReadOnlyList<MigrationScript> scripts, IClock clock, Guid actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(clock);
        await ExecuteAsync(connection, null, "select pg_advisory_lock($1)", cancellationToken, MigrationLockKey).ConfigureAwait(false);
        try
        {
            var history = await ReadHistoryAsync(connection, cancellationToken).ConfigureAwait(false);
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
                    // meta.schema_history is itself created by a kernel migration: record everything once it exists.
                    if (await HistoryExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                    {
                        foreach (var (s, c, ms) in unrecorded)
                        {
                            await RecordAsync(connection, transaction, s, c, ms, clock, actor, cancellationToken).ConfigureAwait(false);
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
            await ExecuteAsync(connection, null, "select pg_advisory_unlock($1)", CancellationToken.None, MigrationLockKey).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadHistoryAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var history = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!await HistoryExistsAsync(connection, null, cancellationToken).ConfigureAwait(false))
        {
            return history;
        }

        await using var command = new NpgsqlCommand("select script_name, checksum from meta.schema_history", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history[reader.GetString(0)] = reader.GetString(1);
        }

        return history;
    }

    private static async Task<bool> HistoryExistsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select to_regclass('meta.schema_history') is not null", connection, transaction);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MigrationScript script, string checksum, long ms, IClock clock, Guid actor, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return ExecuteAsync(connection, transaction,
            "insert into meta.schema_history (id, module_id, version, script_name, checksum, duration_ms, created_at, created_by) values ($1, $2, $3, $4, $5, $6, $7, $8)",
            cancellationToken, Guid.CreateVersion7(now), script.Module, script.Version, script.Name, checksum, (int)Math.Min(ms, int.MaxValue), now, actor);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object[] values)
    {
#pragma warning disable CA2100 // constant SQL text with parameters
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
