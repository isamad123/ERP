using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Contracts.Packs;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>
/// The kernel's pack object handlers (slice S1.23 Build item 9, D-10): setting defaults and labels at the pack layer, option lists
/// and their items, holiday calendars and holidays, banks. Each reads an object back in the shape of the pack's value (plus
/// <c>"active": false</c> when the customer switched it off, <c>"source": "tenant"</c> for a customer's own calendar), so any
/// change by the customer shows as a different hash. Records of the installer's writes go to the change log through the
/// installer; settings write their own audit rows and history (<see cref="SettingWrites"/>).
/// </summary>
public static class KernelPackHandlers
{
    public static IReadOnlyList<IPackObjectHandler> All { get; } =
    [
        new SettingHandler(), new LabelHandler(), new OptionListHandler(), new OptionItemHandler(), new CalendarHandler(), new HolidayHandler(), new BankHandler(),
    ];
}

/// <summary>Shared checks and SQL of the kernel handlers.</summary>
internal abstract partial class KernelPackHandler : IPackObjectHandler
{
    public const string MissingParent = "errors.packs.parent_missing";
    public const string NotAllowed = "errors.packs.object_not_allowed";

    public abstract string ObjectType { get; }

    public string ModuleId => "kernel";

    public virtual PackObjectScope Scope => PackObjectScope.Customer;

    public abstract int Order { get; }

    public abstract string? Validate(string key, JsonObject value);

    public abstract JsonObject Normalize(JsonObject value);

    public abstract Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken);

    protected static NpgsqlConnection Db(PackObjectContext c) => (NpgsqlConnection)c.Connection;

    protected static NpgsqlTransaction Tx(PackObjectContext c) => (NpgsqlTransaction)c.Transaction;

    protected static string? Fields(JsonObject value, params string[] allowed)
    {
        var extra = value.Select(p => p.Key).Where(k => !allowed.Contains(k, StringComparer.Ordinal)).ToList();
        return extra.Count > 0 ? $"unknown fields {string.Join(", ", extra)} (allowed: {string.Join(", ", allowed)})" : null;
    }

    /// <summary>A name per language: an object with an English text and optionally Urdu, each 1 to <paramref name="max"/> characters.</summary>
    protected static string? I18n(JsonNode? node, string field, int max = 200)
    {
        if (node is not JsonObject texts || texts["en"] is null)
        {
            return $"{field} must be an object with an English text (en) and optionally Urdu (ur)";
        }

        foreach (var (language, text) in texts)
        {
            if (language is not ("en" or "ur") || text is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Trim().Length == 0 || s.Length > max || s != s.Trim())
            {
                return $"{field} holds texts in en and ur of 1 to {max} characters without leading or trailing spaces";
            }
        }

        return null;
    }

    protected static JsonObject Texts(JsonNode? node) =>
        new(((JsonObject)node!).Where(p => p.Key is "en" or "ur").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));

    protected static JsonObject? ParseObject(string? json) => json is null ? null : JsonNode.Parse(json) as JsonObject;

    /// <summary>A read of the installer locks the row it reads (`for update` at the end of the query); a view's read (<see cref="PackObjectContext.ReadOnly"/>) does not.</summary>
    protected static NpgsqlCommand Command(PackObjectContext c, string sql, params object?[] values) =>
        Sql.Command(Db(c), Tx(c), c.ReadOnly ? Unlocked(sql) : sql, values);

    internal static string Unlocked(string sql) => LockClause().Replace(sql, string.Empty);

    [GeneratedRegex(@"\s+for update( of [a-z]+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LockClause();

    protected static NpgsqlParameter Jsonb(JsonNode value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value.ToJsonString() };

    protected static async Task<int> ExecAsync(PackObjectContext c, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(c, sql, values);
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is JsonNode node)
            {
                command.Parameters[i] = Jsonb(node);
            }
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected static (string Parent, string Child)? Pair(string key)
    {
        var at = key.IndexOf(':', StringComparison.Ordinal);
        return at <= 0 || at == key.Length - 1 ? null : (key[..at], key[(at + 1)..]);
    }

    protected static JsonObject Active(JsonObject value, bool active)
    {
        if (!active)
        {
            value["active"] = false;
        }

        return value;
    }

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,39}$", RegexOptions.CultureInvariant)]
    protected static partial Regex CodePattern();
}

/// <summary>
/// <c>setting</c>: a setting's default at the pack layer (FR-KRN-SET-01 layer "country pack"). Key: the setting key; value
/// <c>{"value": …}</c>. The present value is the customer's tenant-layer value when there is one, else the pack layer's. Writing
/// sets the pack layer and removes a tenant-layer value, so the pack's value is the effective one.
/// </summary>
internal sealed partial class SettingHandler : KernelPackHandler
{
    public override string ObjectType => "setting";

    public override int Order => 10;

    public override string? Validate(string key, JsonObject value) =>
        !SettingKey().IsMatch(key) ? "the key is a setting key such as org.time_zone"
        : Fields(value, "value") ?? (value.ContainsKey("value") ? null : "value is required");

    public override JsonObject Normalize(JsonObject value) => new() { ["value"] = value["value"]?.DeepClone() };

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        await using var command = Command(context,
            "select id, layer, value::text from cfg.setting_values where setting_key = $1 and layer in ('pack', 'tenant') and scope_id is null order by layer = 'tenant' desc limit 1", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PackObjectState(new JsonObject { ["value"] = JsonNode.Parse(reader.GetString(2)) }, "cfg.setting_values", reader.GetGuid(0))
            : null;
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var definitions = await SettingsStore.DefinitionsAsync(Db(context), Tx(context), [key], cancellationToken).ConfigureAwait(false);
        if (!definitions.TryGetValue(key, out var row))
        {
            throw new PackObjectRefusedException(NotAllowed, $"setting {key} is not registered by any installed module");
        }

        if (!row.Definition.Allows(SettingLayers.Pack))
        {
            throw new PackObjectRefusedException(NotAllowed, $"setting {key} does not allow the pack layer");
        }

        using var document = JsonDocument.Parse(value["value"]?.ToJsonString() ?? "null");
        if (row.Definition.Validation.Problem(row.DataType, document.RootElement) is { } problem)
        {
            throw new PackObjectRefusedException(NotAllowed, $"setting {key}: {problem}");
        }

        // SettingWrites records history, the event and the audit rows of both changes (FR-KRN-SET-04).
        await SettingWrites.ApplyAsync(Db(context), Tx(context), key, SettingLayers.Pack, null, document.RootElement.GetRawText(), "pack", null, null, context.Now, context.ActorUserId, cancellationToken).ConfigureAwait(false);
        await SettingWrites.ApplyAsync(Db(context), Tx(context), key, SettingLayers.Tenant, null, null, "pack", null, null, context.Now, context.ActorUserId, cancellationToken).ConfigureAwait(false);
        return [];
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        await SettingWrites.ApplyAsync(Db(context), Tx(context), key, SettingLayers.Pack, null, null, "pack", null, null, context.Now, context.ActorUserId, cancellationToken).ConfigureAwait(false);
        return [];
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+){1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKey();
}

/// <summary>
/// <c>label</c>: a label at the pack layer (<c>cfg.labels.layer = pack</c>). Key: the label key; value <c>{"texts": {"en", "ur"}}</c>
/// (one or both). The present text of a language is the customer's (tenant layer) when there is one, else the pack's.
/// </summary>
internal sealed partial class LabelHandler : KernelPackHandler
{
    private static readonly string[] Languages = ["en", "ur"];

    public override string ObjectType => "label";

    public override int Order => 10;

    public override string? Validate(string key, JsonObject value)
    {
        if (key.Length > MetadataLimits.LabelKeyLength || !LabelKey().IsMatch(key))
        {
            return "the key is a label key (letters, digits, '_', '.', '-')";
        }

        if (Fields(value, "texts") is { } extra)
        {
            return extra;
        }

        return value["texts"] is not JsonObject texts || texts.Count == 0
                || texts.Any(p => !Languages.Contains(p.Key, StringComparer.Ordinal) || p.Value is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Trim().Length == 0 || s.Length > 300)
            ? "texts holds an English and/or Urdu text of 1 to 300 characters"
            : null;
    }

    public override JsonObject Normalize(JsonObject value) => new() { ["texts"] = Texts(value["texts"]) };

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(context, key, cancellationToken).ConfigureAwait(false);
        var texts = new JsonObject();
        Guid? id = null;
        foreach (var language in Languages)
        {
            var row = rows.FirstOrDefault(r => r.Language == language && r.Layer == "tenant") ?? rows.FirstOrDefault(r => r.Language == language && r.Layer == "pack");
            if (row is not null)
            {
                texts[language] = row.Text;
                id ??= row.Id;
            }
        }

        return id is { } first ? new PackObjectState(new JsonObject { ["texts"] = texts }, "cfg.labels", first) : null;
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(context, key, cancellationToken).ConfigureAwait(false);
        var changes = new List<PackObjectChange>();
        var texts = (JsonObject)value["texts"]!;
        foreach (var language in Languages)
        {
            var wanted = texts[language]?.GetValue<string>();
            var pack = rows.FirstOrDefault(r => r.Language == language && r.Layer == "pack");
            if (wanted is null && pack is not null)
            {
                await ExecAsync(context, "delete from cfg.labels where id = $1", cancellationToken, pack.Id).ConfigureAwait(false);
                changes.Add(new PackObjectChange("cfg.labels", pack.Id, null, "delete", new JsonObject { ["text"] = pack.Text }, null));
            }
            else if (wanted is not null && pack is null)
            {
                var id = Guid.CreateVersion7(context.Now);
                await ExecAsync(context, """
                    insert into cfg.labels (id, key, language, layer, scope_id, text, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $3, 'pack', null, $4, $5, $6, $5, $6)
                    """, cancellationToken, id, key, language, wanted, context.Now, context.ActorUserId).ConfigureAwait(false);
                changes.Add(new PackObjectChange("cfg.labels", id, null, "create", null, new JsonObject { ["key"] = key, ["language"] = language, ["layer"] = "pack", ["text"] = wanted }));
            }
            else if (wanted is not null && pack!.Text != wanted)
            {
                await ExecAsync(context, "update cfg.labels set text = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1",
                    cancellationToken, pack.Id, wanted, context.Now, context.ActorUserId).ConfigureAwait(false);
                changes.Add(new PackObjectChange("cfg.labels", pack.Id, null, "update", new JsonObject { ["text"] = pack.Text }, new JsonObject { ["text"] = wanted }));
            }

            // The pack's text becomes the effective one: the customer's own text of this language (tenant layer) goes.
            if (rows.FirstOrDefault(r => r.Language == language && r.Layer == "tenant") is { } tenant)
            {
                await ExecAsync(context, "delete from cfg.labels where id = $1", cancellationToken, tenant.Id).ConfigureAwait(false);
                changes.Add(new PackObjectChange("cfg.labels", tenant.Id, null, "delete", new JsonObject { ["layer"] = "tenant", ["text"] = tenant.Text }, null));
            }
        }

        return changes;
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var changes = new List<PackObjectChange>();
        foreach (var row in (await RowsAsync(context, key, cancellationToken).ConfigureAwait(false)).Where(r => r.Layer == "pack"))
        {
            await ExecAsync(context, "delete from cfg.labels where id = $1", cancellationToken, row.Id).ConfigureAwait(false);
            changes.Add(new PackObjectChange("cfg.labels", row.Id, null, "delete", new JsonObject { ["text"] = row.Text }, null));
        }

        return changes;
    }

    private sealed record LabelRow(Guid Id, string Language, string Layer, string Text);

    private static async Task<List<LabelRow>> RowsAsync(PackObjectContext context, string key, CancellationToken ct)
    {
        var rows = new List<LabelRow>();
        await using var command = Command(context,
            "select id, btrim(language), layer, text from cfg.labels where key = $1 and layer in ('pack', 'tenant') and scope_id is null order by id for update", key);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new LabelRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.\\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelKey();
}

/// <summary><c>option_list</c>: a shared list (a system list: customers extend it, never remove it). Key: the list code; value <c>{"name"}</c>.</summary>
internal sealed class OptionListHandler : KernelPackHandler
{
    public override string ObjectType => "option_list";

    public override int Order => 20;

    public override string? Validate(string key, JsonObject value) =>
        !CodePattern().IsMatch(key) ? "the key is a list code (lower-case letters, digits, '_', '.', '-'; at most 40)" : Fields(value, "name") ?? I18n(value["name"], "name");

    public override JsonObject Normalize(JsonObject value) => new() { ["name"] = Texts(value["name"]) };

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        await using var command = Command(context, "select id, name_i18n::text, is_active from cfg.option_lists where lower(code) = lower($1) for update", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PackObjectState(Active(new JsonObject { ["name"] = Texts(JsonNode.Parse(reader.GetString(1))) }, reader.GetBoolean(2)), "cfg.option_lists", reader.GetGuid(0))
            : null;
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        var name = value["name"]!.DeepClone();
        if (current is null)
        {
            var id = Guid.CreateVersion7(context.Now);
            await ExecAsync(context, """
                insert into cfg.option_lists (id, code, name_i18n, is_system, is_active, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, true, true, $4, $5, $4, $5)
                """, cancellationToken, id, key, name, context.Now, context.ActorUserId).ConfigureAwait(false);
            return [new PackObjectChange("cfg.option_lists", id, null, "create", null, new JsonObject { ["code"] = key, ["name_i18n"] = name.DeepClone(), ["is_system"] = true })];
        }

        await ExecAsync(context, "update cfg.option_lists set name_i18n = $2, is_active = true, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, name, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("cfg.option_lists", current.EntityId, null, "update", current.Value, value)];
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value["active"] is not null)
        {
            return [];
        }

        await ExecAsync(context, "update cfg.option_lists set is_active = false, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("cfg.option_lists", current.EntityId, null, "update", new JsonObject { ["is_active"] = true }, new JsonObject { ["is_active"] = false })];
    }
}

/// <summary>
/// <c>option_list_item</c>: one item of a list. Key <c>list:item</c>; value <c>{"label", "seq", "attributes"}</c>. Items the customer
/// added are not the pack's and are never touched.
/// </summary>
internal sealed class OptionItemHandler : KernelPackHandler
{
    public override string ObjectType => "option_list_item";

    public override int Order => 30;

    public override string? Validate(string key, JsonObject value)
    {
        if (Pair(key) is not { } pair || !CodePattern().IsMatch(pair.Parent) || !CodePattern().IsMatch(pair.Child))
        {
            return "the key is list:item, both codes of lower-case letters, digits, '_', '.', '-'";
        }

        return Fields(value, "label", "seq", "attributes") ?? I18n(value["label"], "label")
            ?? (value["seq"] is JsonValue seq && seq.TryGetValue<int>(out var n) && n is >= 0 and <= 100000 ? null : "seq is a whole number from 0 to 100000")
            ?? (value["attributes"] is null or JsonObject ? null : "attributes is an object");
    }

    public override JsonObject Normalize(JsonObject value) => new()
    {
        ["label"] = Texts(value["label"]),
        ["seq"] = value["seq"]!.GetValue<int>(),
        ["attributes"] = value["attributes"]?.DeepClone() ?? new JsonObject(),
    };

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var (list, code) = Pair(key)!.Value;
        await using var command = Command(context, """
            select i.id, i.label_i18n::text, i.seq, i.attributes::text, i.is_active from cfg.option_list_items i join cfg.option_lists l on l.id = i.list_id
            where lower(l.code) = lower($1) and lower(i.code) = lower($2) for update of i
            """, list, code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PackObjectState(Active(new JsonObject
            {
                ["label"] = Texts(JsonNode.Parse(reader.GetString(1))),
                ["seq"] = reader.GetInt32(2),
                ["attributes"] = JsonNode.Parse(reader.GetString(3)),
            }, reader.GetBoolean(4)), "cfg.option_list_items", reader.GetGuid(0))
            : null;
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var (list, code) = Pair(key)!.Value;
        var listId = await Sql.ScalarAsync<Guid?>(Db(context), Tx(context), "select id from cfg.option_lists where lower(code) = lower($1)", cancellationToken, list).ConfigureAwait(false)
            ?? throw new PackObjectRefusedException(MissingParent, $"option list {list} does not exist (the pack must hold it, or the customer must have it)");
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            var id = Guid.CreateVersion7(context.Now);
            await ExecAsync(context, """
                insert into cfg.option_list_items (id, list_id, code, label_i18n, seq, is_active, attributes, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, true, $6, $7, $8, $7, $8)
                """, cancellationToken, id, listId, code, value["label"]!.DeepClone(), value["seq"]!.GetValue<int>(), value["attributes"]!.DeepClone(), context.Now, context.ActorUserId).ConfigureAwait(false);
            return [new PackObjectChange("cfg.option_list_items", id, null, "create", null, value)];
        }

        await ExecAsync(context, """
            update cfg.option_list_items set label_i18n = $2, seq = $3, attributes = $4, is_active = true, updated_at = $5, updated_by = $6, row_version = row_version + 1 where id = $1
            """, cancellationToken, current.EntityId, value["label"]!.DeepClone(), value["seq"]!.GetValue<int>(), value["attributes"]!.DeepClone(), context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("cfg.option_list_items", current.EntityId, null, "update", current.Value, value)];
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value["active"] is not null)
        {
            return [];
        }

        await ExecAsync(context, "update cfg.option_list_items set is_active = false, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("cfg.option_list_items", current.EntityId, null, "update", new JsonObject { ["is_active"] = true }, new JsonObject { ["is_active"] = false })];
    }
}

/// <summary>
/// <c>holiday_calendar</c>: a holiday calendar of source <c>pack</c> (FR-KRN-LOC-06), keyed by code (country calendars are named for their
/// country, ruling Q9). Value <c>{"name", "country"}</c>.
/// </summary>
internal sealed partial class CalendarHandler : KernelPackHandler
{
    public override string ObjectType => "holiday_calendar";

    public override int Order => 20;

    public override string? Validate(string key, JsonObject value) =>
        !CodePattern().IsMatch(key) ? "the key is a calendar code (lower-case letters, digits, '_', '.', '-'; at most 40)"
        : Fields(value, "name", "country") ?? I18n(value["name"], "name")
            ?? (value["country"] is null || (value["country"] is JsonValue v && v.TryGetValue<string>(out var s) && Country().IsMatch(s)) ? null : "country is two capital letters");

    public override JsonObject Normalize(JsonObject value)
    {
        var result = new JsonObject { ["name"] = Texts(value["name"]) };
        if (value["country"] is { } country)
        {
            result["country"] = country.DeepClone();
        }

        return result;
    }

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        await using var command = Command(context, "select id, name_i18n::text, btrim(country_code), source, is_active from ref.calendars where lower(code) = lower($1) for update", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var value = new JsonObject { ["name"] = Texts(JsonNode.Parse(reader.GetString(1))) };
        if (!reader.IsDBNull(2))
        {
            value["country"] = reader.GetString(2);
        }

        if (reader.GetString(3) != "pack")
        {
            value["source"] = reader.GetString(3);
        }

        return new PackObjectState(Active(value, reader.GetBoolean(4)), "ref.calendars", reader.GetGuid(0));
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        var name = value["name"]!.DeepClone();
        var country = value["country"]?.GetValue<string>();
        if (current is null)
        {
            var id = Guid.CreateVersion7(context.Now);
            await ExecAsync(context, """
                insert into ref.calendars (id, code, name_i18n, country_code, source, is_active, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, 'pack', true, $5, $6, $5, $6)
                """, cancellationToken, id, key, name, country, context.Now, context.ActorUserId).ConfigureAwait(false);
            return [new PackObjectChange("ref.calendars", id, null, "create", null, new JsonObject { ["code"] = key, ["name_i18n"] = name.DeepClone(), ["country_code"] = country, ["source"] = "pack" })];
        }

        await ExecAsync(context, """
            update ref.calendars set name_i18n = $2, country_code = $3, source = 'pack', is_active = true, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
            """, cancellationToken, current.EntityId, name, country, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("ref.calendars", current.EntityId, null, "update", current.Value, value)];
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value["active"] is not null)
        {
            return [];
        }

        await ExecAsync(context, "update ref.calendars set is_active = false, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("ref.calendars", current.EntityId, null, "update", new JsonObject { ["is_active"] = true }, new JsonObject { ["is_active"] = false })];
    }

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Country();
}

/// <summary>
/// <c>holiday</c>: one holiday of a calendar. Key <c>calendar:yyyy-mm-dd</c>; value <c>{"name", "kind", "tentative"}</c> (Islamic dates
/// marked tentative, confirmed by the customer; S1.14, ruling Q12). Retiring removes a future holiday and keeps a past one (attendance
/// already counted it).
/// </summary>
internal sealed class HolidayHandler : KernelPackHandler
{
    private static readonly string[] Kinds = ["public", "religious", "optional", "restricted"];

    public override string ObjectType => "holiday";

    public override int Order => 30;

    public override string? Validate(string key, JsonObject value)
    {
        if (Pair(key) is not { } pair || !CodePattern().IsMatch(pair.Parent) || !DateOnly.TryParseExact(pair.Child, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return "the key is calendar:yyyy-mm-dd";
        }

        return Fields(value, "name", "kind", "tentative") ?? I18n(value["name"], "name")
            ?? (value["kind"] is JsonValue k && k.TryGetValue<string>(out var kind) && Kinds.Contains(kind, StringComparer.Ordinal) ? null : "kind is public, religious, optional or restricted")
            ?? (value["tentative"] is null || (value["tentative"] is JsonValue t && t.GetValueKind() is JsonValueKind.True or JsonValueKind.False) ? null : "tentative is true or false");
    }

    public override JsonObject Normalize(JsonObject value) => new()
    {
        ["name"] = Texts(value["name"]),
        ["kind"] = value["kind"]!.GetValue<string>(),
        ["tentative"] = value["tentative"]?.GetValue<bool>() ?? false,
    };

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var (calendar, date) = Parts(key);
        await using var command = Command(context, """
            select h.id, h.name_i18n::text, h.kind, h.is_tentative from ref.holidays h join ref.calendars c on c.id = h.calendar_id
            where lower(c.code) = lower($1) and h.holiday_date = $2 for update of h
            """, calendar, date);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PackObjectState(new JsonObject { ["name"] = Texts(JsonNode.Parse(reader.GetString(1))), ["kind"] = reader.GetString(2), ["tentative"] = reader.GetBoolean(3) },
                "ref.holidays", reader.GetGuid(0))
            : null;
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var (calendar, date) = Parts(key);
        var calendarId = await Sql.ScalarAsync<Guid?>(Db(context), Tx(context), "select id from ref.calendars where lower(code) = lower($1)", cancellationToken, calendar).ConfigureAwait(false)
            ?? throw new PackObjectRefusedException(MissingParent, $"holiday calendar {calendar} does not exist (the pack must hold it, or the customer must have it)");
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        var name = value["name"]!.DeepClone();
        var kind = value["kind"]!.GetValue<string>();
        var tentative = value["tentative"]!.GetValue<bool>();
        if (current is null)
        {
            var id = Guid.CreateVersion7(context.Now);
            await ExecAsync(context, """
                insert into ref.holidays (id, calendar_id, holiday_date, name_i18n, kind, is_tentative, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $7, $8)
                """, cancellationToken, id, calendarId, date, name, kind, tentative, context.Now, context.ActorUserId).ConfigureAwait(false);
            return [new PackObjectChange("ref.holidays", id, null, "create", null, new JsonObject { ["calendar"] = calendar, ["holiday_date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["name_i18n"] = name.DeepClone(), ["kind"] = kind, ["is_tentative"] = tentative })];
        }

        await ExecAsync(context, "update ref.holidays set name_i18n = $2, kind = $3, is_tentative = $4, updated_at = $5, updated_by = $6, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, name, kind, tentative, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("ref.holidays", current.EntityId, null, "update", current.Value, value)];
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var (_, date) = Parts(key);
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || date <= context.Today)
        {
            return [];
        }

        await ExecAsync(context, "delete from ref.holidays where id = $1", cancellationToken, current.EntityId).ConfigureAwait(false);
        return [new PackObjectChange("ref.holidays", current.EntityId, null, "delete", current.Value, null)];
    }

    private static (string Calendar, DateOnly Date) Parts(string key)
    {
        var (calendar, date) = Pair(key)!.Value;
        return (calendar, DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}

/// <summary><c>bank</c>: a bank of a country (<c>ref.banks</c>; FR-HRM-EMP-06). Key <c>CC:code</c>; value <c>{"name", "swiftCode", "ibanPrefix"}</c>.</summary>
internal sealed partial class BankHandler : KernelPackHandler
{
    public override string ObjectType => "bank";

    public override int Order => 20;

    public override string? Validate(string key, JsonObject value)
    {
        if (Pair(key) is not { } pair || !Country().IsMatch(pair.Parent) || !CodePattern().IsMatch(pair.Child))
        {
            return "the key is CC:code (country, then the bank code in lower case)";
        }

        return Fields(value, "name", "swiftCode", "ibanPrefix") ?? I18n(value["name"], "name")
            ?? (value["swiftCode"] is null || (value["swiftCode"] is JsonValue s && s.TryGetValue<string>(out var swift) && Swift().IsMatch(swift)) ? null : "swiftCode is 8 or 11 capital letters and digits")
            ?? (value["ibanPrefix"] is null || (value["ibanPrefix"] is JsonValue i && i.TryGetValue<string>(out var iban) && IbanPrefix().IsMatch(iban)) ? null : "ibanPrefix is 1 to 8 capital letters and digits");
    }

    public override JsonObject Normalize(JsonObject value)
    {
        var result = new JsonObject { ["name"] = Texts(value["name"]) };
        foreach (var field in new[] { "swiftCode", "ibanPrefix" })
        {
            if (value[field] is { } v)
            {
                result[field] = v.DeepClone();
            }
        }

        return result;
    }

    public override async Task<PackObjectState?> ReadAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var (country, code) = Pair(key)!.Value;
        await using var command = Command(context,
            "select id, name_i18n::text, swift_code, iban_prefix, is_active from ref.banks where country_code = $1 and lower(code) = lower($2) for update", country, code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var value = new JsonObject { ["name"] = Texts(JsonNode.Parse(reader.GetString(1))) };
        if (!reader.IsDBNull(2))
        {
            value["swiftCode"] = reader.GetString(2);
        }

        if (!reader.IsDBNull(3))
        {
            value["ibanPrefix"] = reader.GetString(3);
        }

        return new PackObjectState(Active(value, reader.GetBoolean(4)), "ref.banks", reader.GetGuid(0));
    }

    public override async Task<IReadOnlyList<PackObjectChange>> WriteAsync(PackObjectContext context, string key, JsonObject value, CancellationToken cancellationToken)
    {
        var (country, code) = Pair(key)!.Value;
        if (!await Sql.ScalarAsync<bool>(Db(context), Tx(context), "select exists (select 1 from ref.countries where code = $1)", cancellationToken, country).ConfigureAwait(false))
        {
            throw new PackObjectRefusedException(MissingParent, $"country {country} is not set up in this customer's reference data");
        }

        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        var name = value["name"]!.DeepClone();
        var swift = value["swiftCode"]?.GetValue<string>();
        var iban = value["ibanPrefix"]?.GetValue<string>();
        if (current is null)
        {
            var id = Guid.CreateVersion7(context.Now);
            await ExecAsync(context, """
                insert into ref.banks (id, code, name_i18n, country_code, swift_code, iban_prefix, is_active, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, true, $7, $8, $7, $8)
                """, cancellationToken, id, code, name, country, swift, iban, context.Now, context.ActorUserId).ConfigureAwait(false);
            return [new PackObjectChange("ref.banks", id, null, "create", null, new JsonObject { ["code"] = code, ["country_code"] = country, ["name_i18n"] = name.DeepClone(), ["swift_code"] = swift, ["iban_prefix"] = iban })];
        }

        await ExecAsync(context, """
            update ref.banks set name_i18n = $2, swift_code = $3, iban_prefix = $4, is_active = true, updated_at = $5, updated_by = $6, row_version = row_version + 1 where id = $1
            """, cancellationToken, current.EntityId, name, swift, iban, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("ref.banks", current.EntityId, null, "update", current.Value, value)];
    }

    public override async Task<IReadOnlyList<PackObjectChange>> RetireAsync(PackObjectContext context, string key, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value["active"] is not null)
        {
            return [];
        }

        await ExecAsync(context, "update ref.banks set is_active = false, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1",
            cancellationToken, current.EntityId, context.Now, context.ActorUserId).ConfigureAwait(false);
        return [new PackObjectChange("ref.banks", current.EntityId, null, "update", new JsonObject { ["is_active"] = true }, new JsonObject { ["is_active"] = false })];
    }

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Country();

    [GeneratedRegex("^[A-Z0-9]{8}([A-Z0-9]{3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Swift();

    [GeneratedRegex("^[A-Z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPrefix();
}
