using Npgsql;

namespace Erp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// Applies SQL-first migration files (spec 03 section 9) in file-name order, each in its own transaction.
/// The generated scripts are idempotent, so applying them again changes nothing. Recording applied scripts in
/// meta.schema_history and running per tenant belongs to the migrator (S1.03), which builds on this class.
/// </summary>
public static class SqlScriptRunner
{
    /// <summary>The migration files of one or more folders, ordered by file name (V{yyyymmddHHmm}__...).</summary>
    public static IReadOnlyList<string> Scripts(params string[] folders) =>
        folders
            .SelectMany(f => Directory.GetFiles(f, "V*.sql"))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    public static async Task ApplyAsync(string connectionString, IEnumerable<string> scriptPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scriptPaths);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var path in scriptPaths)
        {
            var sql = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
#pragma warning disable CA2100 // The text is a migration file from the repository, not user input.
                await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 600 };
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex)
            {
                throw new InvalidOperationException($"Migration {Path.GetFileName(path)} failed: {ex.MessageText} (SQLSTATE {ex.SqlState}, position {ex.Position}).", ex);
            }
        }
    }
}
