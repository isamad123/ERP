using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.Metadata;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Metadata;

/// <summary>
/// The tables that carry custom fields (trait <c>cust</c>): every resource of every module whose field catalogue has the custom
/// container. The entity type of a definition is the table name, for example <c>hrm.employments</c>.
/// </summary>
public static partial class CustomEntities
{
    public static IReadOnlyList<ResourceAccess> Of(ResourceCatalog resources) =>
        [.. resources.All.Where(r => r.Fields.Fields.Any(f => f.IsCustom)).OrderBy(r => r.Resource, StringComparer.Ordinal)];

    public static ResourceAccess? Find(ResourceCatalog resources, string? entityType) =>
        entityType is null ? null : Of(resources).FirstOrDefault(r => string.Equals(r.Resource, entityType, StringComparison.Ordinal));

    public static bool CompanyScoped(ResourceAccess resource) => resource.Fields.Find("company_id") is not null;

    /// <summary>The quoted table name; the resource name comes from the generated catalogue, and the shape is checked all the same.</summary>
    public static string Table(ResourceAccess resource)
    {
        var name = resource.Resource;
        if (!TableName().IsMatch(name))
        {
            throw new InvalidOperationException("Unexpected resource name " + name);
        }

        var parts = name.Split('.');
        return $"\"{parts[0]}\".\"{parts[1]}\"";
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$")]
    private static partial Regex TableName();
}

/// <summary>
/// Checks custom values on every write of a table with custom fields (slice S1.15b, FR-KRN-MET-02, S1.15 Q6 and Q8). Active
/// definitions rule: type, required (a default fills a missing value on create), validation, choices from the option list, users
/// and employees that exist, and "unique" per table (per company for company tables). Values of retired fields are kept and cannot
/// be changed. A unique value is checked under a PostgreSQL advisory lock per table, field and company, held until the caller
/// disposes the result after the save, so two saves at once cannot both pass (custom keys have no database index).
/// </summary>
public sealed class CustomFieldValidator(KernelDbContext db, ResourceCatalog resources, ITenantConnectionProvider tenant, IEnumerable<ICustomReferenceResolver> resolvers) : ICustomFieldValidator
{
    private const string Required = "errors.custom.required";
    private const string Duplicate = "errors.custom.duplicate";
    private const string UnknownReference = "errors.custom.unknown_reference";

    public async Task<CustomCheck> CheckAsync(CustomWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var target = CustomEntities.Find(resources, write.EntityType) ?? throw new InvalidOperationException("Not an extensible table: " + write.EntityType);
        var defs = await db.Set<CustomFieldDef>().AsNoTracking().Where(d => d.EntityType == write.EntityType).ToListAsync(cancellationToken).ConfigureAwait(false);
        var incoming = ObjectOf(write.Values);
        var stored = ObjectOf(write.Stored);
        if (defs.Count == 0 && incoming.Count == 0)
        {
            return new CustomCheck(new Dictionary<string, string>(), Parse(stored), null);
        }

        var byKey = defs.ToDictionary(d => d.FieldKey, StringComparer.Ordinal);
        var options = await OptionsAsync(defs, cancellationToken).ConfigureAwait(false);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new JsonObject();
        var changedReferences = new List<(CustomFieldDef Def, Guid Id)>();

        foreach (var (key, value) in incoming)
        {
            var name = FieldAccessMap.CustomPrefix + key;
            var old = stored[key];
            var unchanged = value is null ? old is null : old is not null && JsonNode.DeepEquals(value, old);
            if (!byKey.TryGetValue(key, out var def))
            {
                if (unchanged && value is not null)
                {
                    result[key] = value.DeepClone();
                }
                else
                {
                    errors[name] = "errors.custom.unknown_field";
                }

                continue;
            }

            if (def.Status == "retired")
            {
                if (unchanged)
                {
                    result[key] = value?.DeepClone();
                }
                else
                {
                    errors[name] = "errors.custom.field_retired";
                }

                continue;
            }

            if (!write.CanEdit(key))
            {
                // Field security kept the stored value (MergeCustom); nothing the caller sent for it counts.
                if (value is not null)
                {
                    result[key] = value.DeepClone();
                }

                continue;
            }

            var element = value is null ? default : JsonDocument.Parse(value.ToJsonString()).RootElement;
            var known = KnownOptions(def, options, old);
            var check = CustomFieldRules.CheckValue(def.DataType, def.Validation.RootElement, element, known);
            if (check.ErrorKey is { } error)
            {
                errors[name] = error;
            }
            else if (!check.IsEmpty && check.Value is not null)
            {
                result[key] = check.Value;
                var isReference = def.DataType is CustomFieldTypes.User or CustomFieldTypes.Employee;
                var same = old is not null && JsonNode.DeepEquals(check.Value, old);
                if (isReference && !same && Guid.TryParse(check.Value.GetValue<string>(), out var id))
                {
                    changedReferences.Add((def, id));
                }
            }
        }

        // Stored values the request did not carry stay: retired and unknown fields, and fields the caller cannot edit.
        foreach (var (key, value) in stored)
        {
            if (result.ContainsKey(key) || incoming.ContainsKey(key) || value is null)
            {
                continue;
            }

            if (!byKey.TryGetValue(key, out var def) || def.Status == "retired" || !write.CanEdit(key))
            {
                result[key] = value.DeepClone();
            }
        }

        foreach (var def in defs.Where(d => d.Status == "active").OrderBy(d => d.Seq))
        {
            if (result.ContainsKey(def.FieldKey) || errors.ContainsKey(FieldAccessMap.CustomPrefix + def.FieldKey))
            {
                continue;
            }

            if (!write.Update && def.DefaultValue is { } fallback)
            {
                var filled = CustomFieldRules.CheckValue(def.DataType, def.Validation.RootElement, fallback.RootElement, KnownOptions(def, options, null));
                if (filled.ErrorKey is null && filled.Value is not null)
                {
                    result[def.FieldKey] = filled.Value;
                    continue;
                }
            }

            if (def.IsRequired && write.CanEdit(def.FieldKey))
            {
                errors[FieldAccessMap.CustomPrefix + def.FieldKey] = Required;
            }
        }

        foreach (var (def, id) in changedReferences)
        {
            if (!errors.ContainsKey(FieldAccessMap.CustomPrefix + def.FieldKey) && !await ExistsAsync(def.DataType, id, cancellationToken).ConfigureAwait(false))
            {
                errors[FieldAccessMap.CustomPrefix + def.FieldKey] = UnknownReference;
            }
        }

        if (errors.Count > 0)
        {
            return new CustomCheck(errors, Parse(result), null);
        }

        var unique = defs.Where(d => d.Status == "active" && d.IsUnique && result[d.FieldKey] is not null
            && !(write.Update && stored[d.FieldKey] is { } before && JsonNode.DeepEquals(before, result[d.FieldKey]))).ToList();
        if (unique.Count == 0)
        {
            return new CustomCheck(errors, Parse(result), null);
        }

        var locks = await UniqueLock.TakeAsync(tenant, write, unique, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var def in unique)
            {
                if (await locks.TakenAsync(target, write, def, result[def.FieldKey]!, cancellationToken).ConfigureAwait(false))
                {
                    errors[FieldAccessMap.CustomPrefix + def.FieldKey] = Duplicate;
                }
            }
        }
        catch
        {
            await locks.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (errors.Count == 0)
        {
            return new CustomCheck(errors, Parse(result), locks);
        }

        await locks.DisposeAsync().ConfigureAwait(false);
        return new CustomCheck(errors, Parse(result), null);
    }

    private async Task<Dictionary<Guid, HashSet<string>>> OptionsAsync(List<CustomFieldDef> defs, CancellationToken cancellationToken)
    {
        var lists = defs.Where(d => d.Status == "active" && d.OptionListId is not null).Select(d => d.OptionListId!.Value).Distinct().ToList();
        if (lists.Count == 0)
        {
            return [];
        }

        var items = await db.Set<OptionListItem>().AsNoTracking().Where(i => lists.Contains(i.ListId) && i.IsActive).Select(i => new { i.ListId, i.Code }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return items.GroupBy(i => i.ListId).ToDictionary(g => g.Key, g => g.Select(i => i.Code).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The codes a choice field takes: the list's active items, plus the codes the record already holds (so an old, now inactive code stays valid).</summary>
    private static HashSet<string>? KnownOptions(CustomFieldDef def, Dictionary<Guid, HashSet<string>> options, JsonNode? old)
    {
        if (!CustomFieldTypes.NeedsOptionList(def.DataType))
        {
            return null;
        }

        var set = def.OptionListId is { } id && options.TryGetValue(id, out var active) ? new HashSet<string>(active, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        switch (old)
        {
            case JsonValue v when v.TryGetValue<string>(out var one):
                set.Add(one);
                break;
            case JsonArray many:
                foreach (var code in many.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var s) ? s : null).OfType<string>())
                {
                    set.Add(code);
                }

                break;
        }

        return set;
    }

    private async Task<bool> ExistsAsync(string dataType, Guid id, CancellationToken cancellationToken)
    {
        if (dataType == CustomFieldTypes.User)
        {
            return await db.Set<Domain.Generated.Iam.User>().AsNoTracking().AnyAsync(u => u.Id == id && !u.IsDeleted, cancellationToken).ConfigureAwait(false);
        }

        var resolver = resolvers.FirstOrDefault(r => r.DataType == dataType);
        return resolver is not null && await resolver.ExistsAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject ObjectOf(JsonDocument? document) =>
        document is { RootElement.ValueKind: JsonValueKind.Object } ? JsonNode.Parse(document.RootElement.GetRawText())!.AsObject() : [];

    private static JsonDocument Parse(JsonObject value) => JsonDocument.Parse(value.ToJsonString());

    /// <summary>One connection holding the advisory locks of the unique fields of a save; disposing releases them.</summary>
    private sealed class UniqueLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;

        private UniqueLock(NpgsqlConnection connection) => _connection = connection;

        public static async Task<UniqueLock> TakeAsync(ITenantConnectionProvider tenant, CustomWrite write, IReadOnlyList<CustomFieldDef> fields, CancellationToken cancellationToken)
        {
            var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var result = new UniqueLock(connection);
            try
            {
                // Always in the same order, so two saves with several unique fields cannot wait for each other.
                foreach (var name in fields.Select(f => LockName(write, f)).Order(StringComparer.Ordinal))
                {
                    await using var command = new NpgsqlCommand("select pg_advisory_lock(hashtextextended(@name, 0))", connection);
                    command.Parameters.AddWithValue("name", name);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                await result.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return result;
        }

        private static string LockName(CustomWrite write, CustomFieldDef field) => $"custom:{write.EntityType}:{field.FieldKey}:{write.CompanyId:N}";

        /// <summary>True when another row of the table already holds the value.</summary>
        public async Task<bool> TakenAsync(ResourceAccess target, CustomWrite write, CustomFieldDef def, JsonNode value, CancellationToken cancellationToken)
        {
            var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
            var compare = def.DataType switch
            {
                CustomFieldTypes.Text => "lower(custom ->> @key) = lower(@value)",
                CustomFieldTypes.DecimalNumber => "(custom ->> @key)::numeric = @value::numeric",
                _ => "custom ->> @key = @value",
            };
            var sql = $"select exists (select 1 from {CustomEntities.Table(target)} where {compare}"
                + (write.RowId is null ? string.Empty : " and id <> @id")
                + (write.CompanyId is null || !CustomEntities.CompanyScoped(target) ? string.Empty : " and company_id = @company")
                + (target.Fields.Find("is_deleted") is null ? string.Empty : " and not is_deleted")
                + ")";
            await using var command = new NpgsqlCommand(sql, _connection);
            command.Parameters.AddWithValue("key", def.FieldKey);
            command.Parameters.AddWithValue("value", text);
            if (write.RowId is { } id)
            {
                command.Parameters.AddWithValue("id", id);
            }

            if (write.CompanyId is { } company && CustomEntities.CompanyScoped(target))
            {
                command.Parameters.AddWithValue("company", company);
            }

            return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("select pg_advisory_unlock_all()", _connection);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
