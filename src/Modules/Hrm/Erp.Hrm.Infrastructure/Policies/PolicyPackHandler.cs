using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Kernel.Contracts.Packs;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Hrm.Infrastructure.Policies;

/// <summary>The HR module's pack object handlers (slice S1.23 Build item 9, D-10): registered by every host that installs packs.</summary>
public static class HrmPackHandlers
{
    public static IReadOnlyList<IPackObjectHandler> Create(PolicyTypeCatalog types) => [new PolicyPackHandler(types)];

    /// <summary>The handlers with the HR module's own policy-type manifest (the migrator and provisioning, which have no service container).</summary>
    public static IReadOnlyList<IPackObjectHandler> Default { get; } = Create(new PolicyTypeCatalog([new PolicyTypeSource(PolicyServices.TypeManifest)]));
}

/// <summary>
/// <c>policy</c>: a policy of a registered type with source <c>pack</c>, created for every company of the pack's country (ruling Q9;
/// FR-HRM-POL-04, POL-06). Key <c>type:code</c>; value <c>{"name", "parameters"}</c>, the parameters checked against the type's schema.
/// Writing creates the policy and activates a version from the install date (or the day after the latest version's start); a version
/// in force is never edited (S1.23a). The present value is the name and the parameters of the version in force today, so a customer's
/// own version (<c>is_overridden</c>) or a renamed policy shows as changed. Retiring is refused while an assignment still uses the policy
/// (it is then left as it is). The pack creates no assignment: HR assigns pack policies (S1.23b detail).
/// </summary>
internal sealed partial class PolicyPackHandler(PolicyTypeCatalog types) : IPackObjectHandler
{
    public const string UnknownType = "errors.hrm.policy.unknown_type";

    public string ObjectType => "policy";

    public string ModuleId => "hrm";

    public PackObjectScope Scope => PackObjectScope.CompanyOfCountry;

    public int Order => 50;

    public string? Validate(string key, JsonObject value)
    {
        var at = key.IndexOf(':', StringComparison.Ordinal);
        if (at <= 0 || !CodeShape().IsMatch(key[(at + 1)..]))
        {
            return "the key is type:code (the policy code: letters, digits, '_' and '-', at most 40)";
        }

        if (types.Find(key[..at]) is not { } type)
        {
            return $"policy type {key[..at]} is not registered by any module";
        }

        var extra = value.Select(p => p.Key).Where(k => k is not ("name" or "parameters")).ToList();
        if (extra.Count > 0)
        {
            return $"unknown fields {string.Join(", ", extra)} (allowed: name, parameters)";
        }

        if (value["name"] is not JsonObject name || name["en"] is null
            || name.Any(p => p.Key is not ("en" or "ur") || p.Value is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Trim().Length == 0 || s.Length > 200))
        {
            return "name holds texts in en (required) and ur of 1 to 200 characters";
        }

        if (value["parameters"] is not JsonObject parameters)
        {
            return "parameters is an object";
        }

        using var document = JsonDocument.Parse(parameters.ToJsonString());
        var problems = PolicySchema.Check(type.Schema, document.RootElement);
        return problems.Count == 0 ? null : "parameters do not fit the type's schema: " + string.Join("; ", problems.Select(p => p.Key + " " + string.Join(",", p.Value)));
    }

    public JsonObject Normalize(JsonObject value) => new()
    {
        ["name"] = new JsonObject(((JsonObject)value["name"]!).Where(p => p.Key is "en" or "ur").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
        ["parameters"] = value["parameters"]!.DeepClone(),
    };

    public async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var (type, code) = Split(key);
        await using var command = Command(context, """
            select p.id, p.name_i18n::text, p.is_active, p.source,
                   (select v.parameters::text from hrm.policy_versions v where v.policy_id = p.id and v.status <> 'draft' and v.valid_from <= $4
                        and (v.valid_to is null or v.valid_to > $4) order by v.valid_from desc limit 1),
                   (select v.parameters::text from hrm.policy_versions v where v.policy_id = p.id and v.status <> 'draft' order by v.valid_from desc limit 1)
            from hrm.policies p where p.company_id = $1 and p.policy_type = $2 and lower(p.code) = lower($3) and not p.is_deleted for update of p
            """, context.CompanyId, type, code, context.Today);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var name = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? [];
        var value = new JsonObject
        {
            ["name"] = new JsonObject(name.Where(p => p.Key is "en" or "ur").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
            // The version in force today; before the first one starts (a dated activation), the latest one.
            ["parameters"] = reader.IsDBNull(4) ? reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5)) : JsonNode.Parse(reader.GetString(4)),
        };
        if (!reader.GetBoolean(2))
        {
            value["active"] = false;
        }

        if (reader.GetString(3) != "pack")
        {
            value["source"] = reader.GetString(3);
        }

        return new PackObjectState(value, "hrm.policies", reader.GetGuid(0));
    }

    public async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var (type, code) = Split(key);
        if (types.Find(type) is null)
        {
            throw new PackObjectRefusedException(UnknownType, $"policy type {type} is not registered");
        }

        var company = context.CompanyId ?? throw new PackObjectRefusedException(UnknownType, "a policy belongs to a company");
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        var changes = new List<PackObjectChange>();
        var name = value["name"]!;
        var parameters = value["parameters"]!;
        Guid policyId;
        if (current is null)
        {
            policyId = Guid.CreateVersion7(context.Now);
            await ExecAsync(context, """
                insert into hrm.policies (id, company_id, code, name_i18n, policy_type, source, is_active, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, 'pack', true, $6, $7, $6, $7)
                """, cancellationToken, policyId, company, code, name, type, context.Now, context.ActorUserId).ConfigureAwait(false);
            changes.Add(new PackObjectChange("hrm.policies", policyId, company, "create", null, new JsonObject { ["code"] = code, ["policy_type"] = type, ["name_i18n"] = name.DeepClone(), ["source"] = "pack" }));
        }
        else
        {
            policyId = current.EntityId;
            if (JsonNode.DeepEquals(current.Value["name"], name) is false || current.Value["active"] is not null || current.Value["source"] is not null)
            {
                await ExecAsync(context, "update hrm.policies set name_i18n = $2, is_active = true, source = 'pack', updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1",
                    cancellationToken, policyId, name, context.Now, context.ActorUserId).ConfigureAwait(false);
                changes.Add(new PackObjectChange("hrm.policies", policyId, company, "update", new JsonObject { ["name_i18n"] = current.Value["name"]?.DeepClone() }, new JsonObject { ["name_i18n"] = name.DeepClone(), ["source"] = "pack" }));
            }
        }

        if (current is null || !JsonNode.DeepEquals(current.Value["parameters"], parameters))
        {
            changes.Add(await ActivateAsync(context, policyId, company, parameters, cancellationToken).ConfigureAwait(false));
        }

        return changes;
    }

    public async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value["active"] is not null)
        {
            return [];
        }

        await using (var used = Command(context, "select exists (select 1 from hrm.policy_assignments where policy_id = $1 and (valid_to is null or valid_to > $2))", current.EntityId, context.Today))
        {
            if (await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return []; // still assigned: left as it is (HR moves the assignments first)
            }
        }

        await ExecAsync(context, """
            update hrm.policy_versions set valid_to = greatest($2, valid_from + 1), status = 'retired', updated_at = $3, updated_by = $4, row_version = row_version + 1
            where policy_id = $1 and status = 'active' and valid_to is null
            """, cancellationToken, current.EntityId, context.Today, context.Now, context.ActorUserId).ConfigureAwait(false);
        await ExecAsync(context, "delete from hrm.policy_versions where policy_id = $1 and status = 'draft'", cancellationToken, current.EntityId).ConfigureAwait(false);
        await ExecAsync(context, "update hrm.policies set is_active = false, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("hrm.policies", current.EntityId, context.CompanyId, "update", new JsonObject { ["is_active"] = true }, new JsonObject { ["is_active"] = false })];
    }

    /// <summary>A new active version from today, or from the day after the latest version's start (a version in force is never edited).</summary>
    private static async Task<PackObjectChange> ActivateAsync(PackObjectContext context, Guid policyId, Guid company, JsonNode parameters, CancellationToken ct)
    {
        DateOnly? latestStart = null;
        Guid? latestId = null;
        int latestVersion = 0;
        await using (var command = Command(context, """
            select id, valid_from, (select coalesce(max(version), 0) from hrm.policy_versions where policy_id = $1)
            from hrm.policy_versions where policy_id = $1 and status <> 'draft' order by valid_from desc limit 1
            """, policyId))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                latestId = reader.GetGuid(0);
                latestStart = reader.GetFieldValue<DateOnly>(1);
                latestVersion = reader.GetInt32(2);
            }
        }

        if (latestId is null)
        {
            latestVersion = await ScalarIntAsync(context, "select coalesce(max(version), 0) from hrm.policy_versions where policy_id = $1", ct, policyId).ConfigureAwait(false);
        }

        var from = latestStart is { } start && start >= context.Today ? start.AddDays(1) : context.Today;
        if (latestId is { } previous)
        {
            await ExecAsync(context, "update hrm.policy_versions set valid_to = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1 and valid_to is null",
                ct, previous, from, context.Now, context.ActorUserId).ConfigureAwait(false);
        }

        var id = Guid.CreateVersion7(context.Now);
        await ExecAsync(context, """
            insert into hrm.policy_versions (id, policy_id, version, parameters, is_overridden, status, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, false, 'active', $5, $6, $7, $6, $7)
            """, ct, id, policyId, latestVersion + 1, parameters, from, context.Now, context.ActorUserId).ConfigureAwait(false);
        return new PackObjectChange("hrm.policy_versions", id, company, "create", null, new JsonObject
        {
            ["version"] = latestVersion + 1, ["parameters"] = parameters.DeepClone(), ["valid_from"] = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["status"] = "active",
        });
    }

    private static (string Type, string Code) Split(string key)
    {
        var at = key.IndexOf(':', StringComparison.Ordinal);
        return (key[..at], key[(at + 1)..]);
    }

#pragma warning disable CA2100 // constant SQL text; every value is a parameter
    private static NpgsqlCommand Command(PackObjectContext context, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Connection, (NpgsqlTransaction)context.Transaction);
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

    private static async Task<int> ScalarIntAsync(PackObjectContext context, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(context, sql, values);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();
}
