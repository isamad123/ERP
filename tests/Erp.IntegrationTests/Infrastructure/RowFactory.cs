using System.Globalization;
using System.Net;
using Erp.CodeGen.Emit;
using Npgsql;
using NpgsqlTypes;

namespace Erp.IntegrationTests.Infrastructure;

/// <summary>
/// Inserts a minimal valid row into any generated table, creating the rows it references first, all inside the
/// caller's transaction. Optional columns are left null; required ones get simple values of the right type; enum
/// columns take their first allowed value. Company-scoped rows all belong to one company, so the composite company
/// foreign keys (README rule 12) hold. Used to prove every table can actually be written and its guards fire.
/// <paramref name="firstValue"/> and <paramref name="companyId"/> let several factories write committed rows into one
/// database without repeating codes (S1.04): each starts its counter where the last stopped and reuses one company.
/// <paramref name="enumChoice"/> picks a later allowed value of enum columns, for a retry after a unique-key clash.
/// </summary>
public sealed class RowFactory(NpgsqlConnection connection, NpgsqlTransaction transaction, ExpectedCatalog catalog, int firstValue = 0, object? companyId = null, int enumChoice = 0)
{
    public static readonly DateOnly Day = new(2026, 1, 15);

    private readonly Dictionary<string, ExpectedTable> _tables = catalog.Tables.ToDictionary(t => t.Database + ":" + t.FullName);
    private object? _companyId = companyId;
    private int _counter = firstValue;

    /// <summary>The company the company-scoped rows of this factory belong to (null until one was needed).</summary>
    public object? CompanyId => _companyId;

    /// <summary>The last value number used; the next factory on the same database starts after it.</summary>
    public int LastValue => _counter;

    /// <summary>Inserts a row; <paramref name="overrides"/> fixes chosen column values (null means SQL null).</summary>
    public async Task<IReadOnlyDictionary<string, object?>> InsertAsync(ExpectedTable table, IReadOnlyDictionary<string, object?>? overrides, CancellationToken ct, int depth = 0)
    {
        if (depth > 25)
        {
            throw new InvalidOperationException($"reference chain too deep at {table.FullName}");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var fk in table.ForeignKeys)
        {
            // The columns this row chooses; company_id and the target's partition column follow the parent row.
            var own = fk.Columns.Where(c => !(c == "company_id" && table.Traits.Contains("co")) && !IsPartitionColumnOfTarget(fk, c)).ToList();
            var allRequired = own.TrueForAll(c => table.Columns.First(x => x.Name == c).NotNull && c != "id");
            var overridden = overrides is not null && own.Exists(overrides.ContainsKey);
            if (!allRequired || overridden || own.TrueForAll(values.ContainsKey))
            {
                continue; // a null column means PostgreSQL does not check the key (MATCH SIMPLE)
            }

            var parent = await InsertAsync(_tables[table.Database + ":" + fk.Target], null, ct, depth + 1).ConfigureAwait(false);
            for (var i = 0; i < fk.Columns.Count; i++)
            {
                values[fk.Columns[i]] = parent[fk.TargetColumns[i]];
            }
        }

        foreach (var column in table.Columns)
        {
            if (column.Generated)
            {
                continue;
            }

            if (overrides is not null && overrides.TryGetValue(column.Name, out var fixedValue))
            {
                values[column.Name] = fixedValue;
                continue;
            }

            if (values.ContainsKey(column.Name))
            {
                continue;
            }

            if (column.Name == "company_id" && table.Traits.Contains("co"))
            {
                values[column.Name] = await CompanyAsync(ct).ConfigureAwait(false);
                continue;
            }

            values[column.Name] = column.NotNull ? Value(column) : null;
        }

        await InsertRowAsync(table, values, ct).ConfigureAwait(false);
        return values;
    }

    private bool IsPartitionColumnOfTarget(ExpectedForeignKey fk, string column) =>
        catalog.Tables.First(t => t.FullName == fk.Target).PartitionColumn == column;

    private async Task<object?> CompanyAsync(CancellationToken ct)
    {
        if (_companyId is null)
        {
            var companies = _tables["tenant:org.companies"];
            var row = await InsertAsync(companies, null, ct).ConfigureAwait(false);
            _companyId = row["id"];
        }

        return _companyId;
    }

    public object Value(ExpectedColumn column)
    {
        var n = ++_counter;
        return column.Kind switch
        {
            "Uuid" => Guid.CreateVersion7(),
            "Code" => "C" + n.ToString(CultureInfo.InvariantCulture),
            "Text" => "t" + n.ToString(CultureInfo.InvariantCulture),
            "Enum" => column.EnumValues[enumChoice % column.EnumValues.Count],
            "FixedText" => Letters(n, int.Parse(column.Type["character(".Length..^1], CultureInfo.InvariantCulture)),
            "I18n" => new Json("{\"en\":\"x\"}"),
            "Json" => new Json("{}"),
            "Date" => Day,
            "Timestamp" => new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero),
            "Int4" => n,
            "BigInteger" => (long)n,
            "Bool" => false,
            "Money" or "Qty" or "Rate" or "Pct" or "Numeric" => 1m,
            "Encrypted" or "Hmac" or "Bytea" => new byte[] { 1, (byte)(n % 256) },
            "TextArray" => Array.Empty<string>(),
            "UuidArray" => Array.Empty<Guid>(),
            "Time" => new TimeOnly(9, 0),
            "Inet" => IPAddress.Parse("10.0.0.1"),
            _ => throw new InvalidOperationException(column.Kind),
        };
    }

    private static string Letters(int n, int length)
    {
        var chars = new char[length];
        for (var i = length - 1; i >= 0; i--)
        {
            chars[i] = (char)('A' + (n % 26));
            n /= 26;
        }

        return new string(chars);
    }

    private async Task InsertRowAsync(ExpectedTable table, Dictionary<string, object?> values, CancellationToken ct)
    {
        var columns = values.Keys.ToList();
        var sql = $"insert into {table.FullName} ({string.Join(", ", columns)}) values ({string.Join(", ", columns.Select((_, i) => "@p" + i.ToString(CultureInfo.InvariantCulture)))})";
#pragma warning disable CA2100 // Table and column names come from the generated catalogue; values are parameters.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(Parameter("p" + i.ToString(CultureInfo.InvariantCulture), values[columns[i]], table.Columns.First(c => c.Name == columns[i])));
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static NpgsqlParameter Parameter(string name, object? value, ExpectedColumn column)
    {
        var type = column.Kind switch
        {
            "Uuid" => NpgsqlDbType.Uuid,
            "I18n" or "Json" => NpgsqlDbType.Jsonb,
            "Date" => NpgsqlDbType.Date,
            "Timestamp" => NpgsqlDbType.TimestampTz,
            "Int4" => NpgsqlDbType.Integer,
            "BigInteger" => NpgsqlDbType.Bigint,
            "Bool" => NpgsqlDbType.Boolean,
            "Money" or "Qty" or "Rate" or "Pct" or "Numeric" => NpgsqlDbType.Numeric,
            "Encrypted" or "Hmac" or "Bytea" => NpgsqlDbType.Bytea,
            "TextArray" => NpgsqlDbType.Array | NpgsqlDbType.Text,
            "UuidArray" => NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            "Time" => NpgsqlDbType.Time,
            "Inet" => NpgsqlDbType.Inet,
            "FixedText" => NpgsqlDbType.Char,
            _ => NpgsqlDbType.Text,
        };
        return new NpgsqlParameter(name, type) { Value = value is Json j ? j.Text : value ?? DBNull.Value };
    }

    /// <summary>A jsonb literal.</summary>
    public sealed record Json(string Text);
}
