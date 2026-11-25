using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Kernel.Contracts.Packs;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// <c>leave_type</c>: a leave type with source <c>pack</c>, created for every company of the pack's country (slice S1.32 Build item 13, FR-HRM-LVE-12,
/// ruling Q2: the mechanism, with test types only; no statutory leave value is shipped, AS-4, R-145). Key: the type's code; value
/// <c>{"name": {"en", "ur"}, "isPaid", "counting", "unit", "requiresDocument", "isCompOff", "isUnpaidDeduction"}</c>. The types are editable
/// defaults: HR may rename them or change their flags and the installer keeps those changes (it compares with what it left, S1.23b). How much leave a
/// type gives is a <c>leave</c> policy (pack object <c>policy</c>), never part of the type. Runs before policies (order 40), so a pack policy can
/// name its types. Retiring switches the type off; a type is never deleted. Code, counting and unit no longer change once the type has entries (the
/// database refuses it; the installer reports the conflict).
/// </summary>
internal sealed partial class LeaveTypePackHandler : IPackObjectHandler
{
    public const string Refused = "errors.hrm.leave.pack_type_refused";

    private static readonly string[] Countings = ["calendar_days", "working_days", "hours"];
    private static readonly string[] Units = ["day", "half_day", "hour"];
    private static readonly string[] Flags = ["isPaid", "requiresDocument", "isCompOff", "isUnpaidDeduction"];

    public string ObjectType => "leave_type";

    public string ModuleId => "hrm";

    public PackObjectScope Scope => PackObjectScope.CompanyOfCountry;

    public int Order => 40;

    public string? Validate(string key, JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!CodeShape().IsMatch(key))
        {
            return "the key is the leave type's code (letters, digits, '_', '.' and '-', at most 40)";
        }

        var extra = value.Select(p => p.Key).Where(k => k is not ("name" or "counting" or "unit") && !Flags.Contains(k, StringComparer.Ordinal)).ToList();
        if (extra.Count > 0)
        {
            return $"unknown fields {string.Join(", ", extra)} (allowed: name, counting, unit, {string.Join(", ", Flags)})";
        }

        if (value["name"] is not JsonObject name || name["en"] is null || name["ur"] is null
            || name.Any(p => p.Key is not ("en" or "ur") || p.Value is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Trim().Length == 0 || s.Length > 200))
        {
            return "name holds texts in en and ur of 1 to 200 characters";
        }

        if (Text(value, "counting") is not { } counting || !Countings.Contains(counting, StringComparer.Ordinal))
        {
            return "counting is calendar_days, working_days or hours";
        }

        if (Text(value, "unit") is not { } unit || !Units.Contains(unit, StringComparer.Ordinal))
        {
            return "unit is day, half_day or hour";
        }

        if ((counting == "hours") != (unit == "hour"))
        {
            return "a type counted in hours is taken in hours, and only such a type is";
        }

        if (Flags.Any(f => value[f] is not JsonValue b || !b.TryGetValue<bool>(out _)))
        {
            return $"{string.Join(", ", Flags)} are true or false";
        }

        return Flag(value, "isPaid") && Flag(value, "isUnpaidDeduction") ? "a paid type cannot be an unpaid-leave deduction" : null;
    }

    public JsonObject Normalize(JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var o = new JsonObject
        {
            ["name"] = new JsonObject(((JsonObject)value["name"]!).Where(p => p.Key is "en" or "ur").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
            ["counting"] = Text(value, "counting"),
            ["unit"] = Text(value, "unit"),
        };
        foreach (var f in Flags)
        {
            o[f] = Flag(value, f);
        }

        return o;
    }

    public async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Command(context, """
            select id, name_i18n::text, counting, unit, is_paid, requires_document, is_comp_off, is_unpaid_deduction, is_active, source
            from hrm.leave_types where company_id = $1 and lower(code) = lower($2) and not is_deleted for update
            """, context.CompanyId, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var name = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? [];
        var value = new JsonObject
        {
            ["name"] = new JsonObject(name.Where(p => p.Key is "en" or "ur").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
            ["counting"] = reader.GetString(2),
            ["unit"] = reader.GetString(3),
            ["isPaid"] = reader.GetBoolean(4),
            ["requiresDocument"] = reader.GetBoolean(5),
            ["isCompOff"] = reader.GetBoolean(6),
            ["isUnpaidDeduction"] = reader.GetBoolean(7),
        };
        if (!reader.GetBoolean(8))
        {
            value["active"] = false;
        }

        if (reader.GetString(9) != "pack")
        {
            value["source"] = reader.GetString(9);
        }

        return new PackObjectState(value, "hrm.leave_types", reader.GetGuid(0));
    }

    public async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);
        var company = context.CompanyId ?? throw new PackObjectRefusedException(Refused, "a leave type belongs to a company");
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        var after = Normalize(value);
        after["source"] = "pack";
        try
        {
            if (current is null)
            {
                var id = Guid.CreateVersion7(context.Now);
                await ExecAsync(context, """
                    insert into hrm.leave_types (id, company_id, code, name_i18n, is_paid, counting, unit, requires_document, is_comp_off, is_unpaid_deduction, source, is_active, is_deleted,
                                                 created_at, created_by, updated_at, updated_by, row_version)
                    values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'pack', true, false, $11, $12, $11, $12, 1)
                    """, cancellationToken, id, company, key, value["name"], Flag(value, "isPaid"), Text(value, "counting"), Text(value, "unit"), Flag(value, "requiresDocument"),
                    Flag(value, "isCompOff"), Flag(value, "isUnpaidDeduction"), context.Now, context.ActorUserId).ConfigureAwait(false);
                return [new PackObjectChange("hrm.leave_types", id, company, "create", null, after)];
            }

            var before = current.Value.DeepClone().AsObject();
            await ExecAsync(context, """
                update hrm.leave_types set name_i18n = $2, is_paid = $3, counting = $4, unit = $5, requires_document = $6, is_comp_off = $7, is_unpaid_deduction = $8,
                       source = 'pack', is_active = true, updated_at = $9, updated_by = $10, row_version = row_version + 1
                where id = $1
                """, cancellationToken, current.EntityId, value["name"], Flag(value, "isPaid"), Text(value, "counting"), Text(value, "unit"), Flag(value, "requiresDocument"),
                Flag(value, "isCompOff"), Flag(value, "isUnpaidDeduction"), context.Now, context.ActorUserId).ConfigureAwait(false);
            return [new PackObjectChange("hrm.leave_types", current.EntityId, company, "update", before, after)];
        }
        catch (PostgresException pg) when (pg.ConstraintName is "ck_leave_types_rules" or "uq_leave_types_company_id_code")
        {
            throw new PackObjectRefusedException(Refused, $"leave type {key}: {pg.MessageText}");
        }
    }

    public async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value["active"] is not null || current.Value["source"] is not null)
        {
            return []; // already off, or the customer's own type
        }

        await ExecAsync(context, "update hrm.leave_types set is_active = false, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("hrm.leave_types", current.EntityId, context.CompanyId, "update", new JsonObject { ["is_active"] = true }, new JsonObject { ["is_active"] = false })];
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

#pragma warning disable CA2100 // constant SQL text; every value is a parameter
    private static NpgsqlCommand Command(PackObjectContext context, string sql, params object?[] values)
    {
        var text = context.ReadOnly ? sql.Replace(" for update", string.Empty, StringComparison.Ordinal) : sql;
        var command = new NpgsqlCommand(text, (NpgsqlConnection)context.Connection, (NpgsqlTransaction)context.Transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(value is JsonNode node
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = node.ToJsonString() }
                : new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return command;
    }
#pragma warning restore CA2100

    private static async Task ExecAsync(PackObjectContext context, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(context, sql, values);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();
}
