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

            values[column.Name] = !column.NotNull ? null
                : KnownFormats.TryGetValue(table.FullName + "." + column.Name, out var format) ? format(Interlocked.Increment(ref _formatted))
                : Value(column);
        }

        await InsertRowAsync(table, values, ct).ConfigureAwait(false);
        return values;
    }

    /// <summary>
    /// Columns whose database check asks for a shape the generic value does not have (slice S1.12c: <c>ck_saved_views_list_key</c>
    /// wants a dotted list key; S1.14a: <c>ck_locations_time_zone</c> wants an IANA time zone; S1.20b: a cost allocation summing to 1).
    /// </summary>
    private static readonly Dictionary<string, Func<int, object>> KnownFormats = new(StringComparer.Ordinal)
    {
        ["cfg.saved_views.list_key"] = n => "org.list_" + n.ToString(CultureInfo.InvariantCulture),
        // S1.14a: a location's time zone must be an IANA name the database knows (rules.yaml org.tg_locations_checks).
        ["org.locations.time_zone"] = _ => "Asia/Karachi",
        // S1.15b: a custom field key is lower-case letters, digits and underscore (ck_custom_field_defs_field_key).
        ["cfg.custom_field_defs.field_key"] = n => "f_" + n.ToString(CultureInfo.InvariantCulture),
        // S1.20b: an assignment's cost allocation is a list of shares summing to 1 (rules.yaml tg_employment_assignments_cost_allocation_sum).
        ["hrm.employment_assignments.cost_allocation"] = _ => new Json("[{\"cost_center_ref\": \"CC-1\", \"pct\": 1}]"),
        // S1.20d: a payment account without bank details is a cash one (tg_employee_bank_accounts_bank_fields_required), the remainder
        // (tg_employee_bank_accounts_split) with a priority of 1 to 99; a registration's scheme is a lower-case code and, applicable, has
        // no exemption reason (ck_employment_statutory_registrations_scheme_format, _exemption).
        ["hrm.employee_bank_accounts.payment_mode"] = _ => "cash",
        ["hrm.employee_bank_accounts.split_kind"] = _ => "remainder",
        ["hrm.employee_bank_accounts.priority"] = n => (n % 99) + 1,
        ["hrm.employment_statutory_registrations.scheme"] = n => "scheme_" + n.ToString(CultureInfo.InvariantCulture),
        ["hrm.employment_statutory_registrations.is_applicable"] = _ => true,
        // S1.23a: a policy type is a lower-case code; parameters and rules are JSON objects; version numbers start at 1; precedence is
        // 1 to 9999 (ck_policies_policy_type_format, ck_policy_versions_*, ck_policy_assignments_*).
        ["hrm.policies.policy_type"] = n => "type_" + n.ToString(CultureInfo.InvariantCulture),
        ["hrm.policy_versions.parameters"] = _ => new Json("{}"),
        ["hrm.policy_versions.version"] = n => (n % 1000) + 1,
        ["hrm.policy_assignments.rule"] = _ => new Json("{}"),
        ["hrm.policy_assignments.precedence"] = n => (n % 9999) + 1,
        // S1.34a: a pay group's tax-year start month is 1 to 12 and its cut-off and payment offsets 0 to 31 days (tg_pay_groups_rules).
        ["hrm.pay_groups.tax_year_start_month"] = _ => 7,
        ["hrm.pay_groups.cut_off_day"] = _ => 3,
        ["hrm.pay_groups.payment_day"] = _ => 5,
        // S1.28a: a working day names a shift, any other day none (ck_roster_lines_shift); the generated row has no shift, so it is a weekly off.
        ["hrm.roster_lines.day_type"] = _ => "weekly_off",
        // S1.29b: a new penalty is applied, counts from one, and only a leave penalty carries leave units (tg_attendance_penalties_rules).
        ["hrm.attendance_penalties.status"] = _ => "applied",
        ["hrm.attendance_penalties.action"] = _ => "warning",
        ["hrm.attendance_penalties.occurrence"] = n => (n % 30) + 1,
        ["hrm.attendance_penalties.minutes"] = n => n % 600,
    };

    private int _formatted;

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
