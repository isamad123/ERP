using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Platform;

/// <summary>Small helpers for parameterized SQL ($1, $2, ...) used by the control-plane services. No string-built values.</summary>
internal static class Sql
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, object?[] values)
    {
#pragma warning disable CA2100 // constant SQL text; every value is a parameter
        var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return command;
    }

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>A double-quoted SQL identifier, for names that were validated against a strict pattern first.</summary>
    public static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
