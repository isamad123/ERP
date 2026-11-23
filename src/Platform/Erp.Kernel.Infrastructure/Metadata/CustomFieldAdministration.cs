using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Metadata;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Metadata;

/// <summary>
/// Definitions of custom fields (slice S1.15b, FR-KRN-MET-01, -02, -05; S1.15 Q5 to Q8). A definition belongs to a table with the
/// <c>cust</c> trait. Its key, type and option list are fixed once created; it is retired (hidden, values kept) and never deleted.
/// Money, date-time, lookup to any entity and formula are not offered in 1.0 (CR-071 Q5), nor is the <c>file</c> type until the
/// documents screens (S1.24). Writes go through the kernel context, so the change log records them.
/// </summary>
public sealed partial class CustomFieldAdministration(
    KernelDbContext db, ResourceCatalog resources, ICurrentActor actor, IClock clock, IPermissionChecker permissions, IFieldAccess fieldAccess, ITenantConnectionProvider tenant)
    : ICustomFieldAdministration
{
    private const int MaxPerEntity = 100;

    public Task<CustomEntityList> EntitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CustomEntityList([.. CustomEntities.Of(resources).Select(r => new CustomEntity(r.Resource, "entities." + r.Resource, CustomEntities.CompanyScoped(r)))]));

    public async Task<CustomFieldDefList> ListAsync(string? entityType, CancellationToken cancellationToken)
    {
        var query = db.Set<CustomFieldDef>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(entityType))
        {
            query = query.Where(d => d.EntityType == entityType);
        }

        var rows = await query.OrderBy(d => d.EntityType).ThenBy(d => d.Seq).ThenBy(d => d.FieldKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CustomFieldDefList([.. rows.Select(View)]);
    }

    public async Task<CustomFieldDefView> CreateAsync(CustomFieldCreate write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var entity = CustomEntities.Find(resources, write.EntityType) ?? throw new MetadataRefusedException(400, "errors.custom.def.entity_unknown", "entityType");
        var key = write.FieldKey?.Trim();
        if (string.IsNullOrEmpty(key) || !FieldKey().IsMatch(key))
        {
            throw new MetadataRefusedException(400, "errors.custom.def.key_invalid", "fieldKey");
        }

        var type = write.DataType ?? string.Empty;
        if (type == CustomFieldTypes.File)
        {
            throw new MetadataRefusedException(400, "errors.custom.def.type_not_available", "dataType");
        }

        if (!CustomFieldTypes.Available.Contains(type, StringComparer.Ordinal))
        {
            throw new MetadataRefusedException(400, "errors.custom.def.type_invalid", "dataType");
        }

        var optionList = await CheckOptionListAsync(type, write.OptionListId, cancellationToken).ConfigureAwait(false);
        var existing = await db.Set<CustomFieldDef>().Where(d => d.EntityType == entity.Resource).Select(d => d.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count >= MaxPerEntity)
        {
            throw new MetadataRefusedException(400, "errors.custom.def.too_many", "fieldKey");
        }

        var parts = await BuildAsync(type, write.Label, write.Help, write.Validation, write.DefaultValue, write.IsRequired ?? false, write.IsUnique ?? false, write.Section, optionList, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var row = new CustomFieldDef
        {
            Id = Guid.CreateVersion7(now),
            EntityType = entity.Resource,
            FieldKey = key,
            DataType = type,
            OptionListId = write.OptionListId,
            Seq = write.Seq ?? (existing.Count == 0 ? 10 : existing.Max() + 10),
            Version = 1,
            Status = "active",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        Apply(row, parts);
        db.Add(row);
        await SaveAsync(true, cancellationToken).ConfigureAwait(false);
        return View(row);
    }

    public async Task<CustomFieldDefView> EditAsync(Guid id, CustomFieldEdit write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = await db.Set<CustomFieldDef>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new MetadataRefusedException(404, "errors.data.not_found");
        var version = write.RowVersion ?? throw new MetadataRefusedException(400, "errors.data.row_version_required", "rowVersion");
        var parts = await BuildAsync(row.DataType, write.Label, write.Help, write.Validation, write.DefaultValue, write.IsRequired ?? row.IsRequired, write.IsUnique ?? row.IsUnique, write.Section, row.OptionListId, cancellationToken).ConfigureAwait(false);
        if (parts.Unique && !row.IsUnique && await HasDuplicatesAsync(row, cancellationToken).ConfigureAwait(false))
        {
            throw new MetadataRefusedException(400, "errors.custom.def.existing_duplicates", "isUnique");
        }

        Touch(row, version);
        Apply(row, parts);
        row.Seq = write.Seq ?? row.Seq;
        row.Version++;
        await SaveAsync(false, cancellationToken).ConfigureAwait(false);
        return View(row);
    }

    public async Task<CustomFieldDefView> SetStateAsync(Guid id, CustomFieldState write, bool restore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = await db.Set<CustomFieldDef>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new MetadataRefusedException(404, "errors.data.not_found");
        var version = write.RowVersion ?? throw new MetadataRefusedException(400, "errors.data.row_version_required", "rowVersion");
        Touch(row, version);
        row.Status = restore ? "active" : "retired";
        row.Version++;
        await SaveAsync(false, cancellationToken).ConfigureAwait(false);
        return View(row);
    }

    public async Task<FormCustomFieldList?> FormAsync(string entityType, Guid? companyId, CancellationToken cancellationToken)
    {
        var entity = CustomEntities.Find(resources, entityType);
        if (entity is null || !await permissions.HasPermissionAsync(entity.Resource + ".read", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var access = await fieldAccess.ForAsync(entity.Fields, "read", companyId, cancellationToken).ConfigureAwait(false);
        var defs = await db.Set<CustomFieldDef>().AsNoTracking().Where(d => d.EntityType == entity.Resource).OrderBy(d => d.Seq).ThenBy(d => d.FieldKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        // An active field offers the active items; a retired field is shown read-only, so its list is given whole (an item since switched off still has its name).
        var lists = defs.Where(d => d.OptionListId is not null).Select(d => d.OptionListId!.Value).Distinct().ToList();
        var items = lists.Count == 0
            ? []
            : await db.Set<OptionListItem>().AsNoTracking().Where(i => lists.Contains(i.ListId)).OrderBy(i => i.Seq).ThenBy(i => i.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<FormCustomField>();
        foreach (var def in defs)
        {
            var level = access.Of(FieldAccessMap.CustomPrefix + def.FieldKey);
            if (level == FieldAccessLevel.Hidden)
            {
                continue;
            }

            var retired = def.Status == "retired";
            var options = def.OptionListId is { } list
                ? items.Where(i => i.ListId == list && (retired || i.IsActive)).Select(i => new CustomOption(i.Code, OptionListAdministration.NamesOf(i.LabelI18n))).ToList()
                : [];
            result.Add(new FormCustomField(
                def.FieldKey, def.DataType, Names(def.LabelI18n), def.HelpI18n is null ? null : Names(def.HelpI18n), def.Validation.RootElement.Clone(), def.DefaultValue?.RootElement.Clone(),
                def.IsRequired && !retired, def.IsUnique, def.Section, def.Seq, retired,
                retired || level == FieldAccessLevel.ReadOnly ? "readonly" : level == FieldAccessLevel.Masked ? "masked" : "editable", options));
        }

        return new FormCustomFieldList(result);
    }

    private sealed record Parts(JsonDocument Label, JsonDocument? Help, JsonDocument Validation, JsonDocument? Default, bool Required, bool Unique, string? Section);

    private async Task<Parts> BuildAsync(
        string type, Names? label, Names? help, JsonElement? validation, JsonElement? defaultValue, bool required, bool unique, string? section, Guid? optionList, CancellationToken cancellationToken)
    {
        var labelJson = OptionListAdministration.NameJson(label, "label", required: true);
        var helpJson = OptionListAdministration.NameJson(help, "help", required: false);
        if (helpJson is not null && (OptionListAdministration.NamesOf(helpJson) is var h) && ((h.En?.Length ?? 0) > MetadataLimits.HelpLength || (h.Ur?.Length ?? 0) > MetadataLimits.HelpLength))
        {
            throw new MetadataRefusedException(400, "errors.metadata.help_length", "help");
        }

        var (rules, error) = CustomFieldRules.NormalizeValidation(type, validation);
        if (rules is null)
        {
            throw new MetadataRefusedException(400, error!, "validation");
        }

        if (unique && type is CustomFieldTypes.MultiChoice or CustomFieldTypes.Bool or CustomFieldTypes.LongText or CustomFieldTypes.File)
        {
            throw new MetadataRefusedException(400, "errors.custom.def.unique_not_for_type", "isUnique");
        }

        var cleanSection = section?.Trim();
        if (cleanSection is { Length: > MetadataLimits.SectionLength })
        {
            throw new MetadataRefusedException(400, "errors.metadata.section_length", "section");
        }

        JsonDocument? fallback = null;
        if (defaultValue is { } given && !CustomFieldRules.IsEmpty(given))
        {
            if (unique)
            {
                throw new MetadataRefusedException(400, "errors.custom.def.unique_default", "defaultValue");
            }

            if (type is CustomFieldTypes.User or CustomFieldTypes.Employee)
            {
                throw new MetadataRefusedException(400, "errors.custom.def.default_not_for_type", "defaultValue");
            }

            var codes = optionList is { } id ? await ActiveCodesAsync(id, cancellationToken).ConfigureAwait(false) : null;
            var check = CustomFieldRules.CheckValue(type, JsonDocument.Parse(rules.ToJsonString()).RootElement, given, codes);
            if (check.ErrorKey is not null || check.Value is null)
            {
                throw new MetadataRefusedException(400, "errors.custom.def.default_invalid", "defaultValue");
            }

            fallback = JsonDocument.Parse(check.Value.ToJsonString());
        }

        return new Parts(labelJson!, helpJson, JsonDocument.Parse(rules.ToJsonString()), fallback, required, unique, string.IsNullOrEmpty(cleanSection) ? null : cleanSection);
    }

    private static void Apply(CustomFieldDef row, Parts parts)
    {
        row.LabelI18n = parts.Label;
        row.HelpI18n = parts.Help;
        row.Validation = parts.Validation;
        row.DefaultValue = parts.Default;
        row.IsRequired = parts.Required;
        row.IsUnique = parts.Unique;
        row.Section = parts.Section;
    }

    private async Task<Guid?> CheckOptionListAsync(string type, Guid? optionList, CancellationToken cancellationToken)
    {
        if (!CustomFieldTypes.NeedsOptionList(type))
        {
            return optionList is null ? null : throw new MetadataRefusedException(400, "errors.custom.def.option_list_not_for_type", "optionListId");
        }

        if (optionList is not { } id)
        {
            throw new MetadataRefusedException(400, "errors.custom.def.option_list_required", "optionListId");
        }

        if (!await db.Set<OptionList>().AsNoTracking().AnyAsync(l => l.Id == id && l.IsActive, cancellationToken).ConfigureAwait(false))
        {
            throw new MetadataRefusedException(400, "errors.custom.def.option_list_unknown", "optionListId");
        }

        return id;
    }

    private async Task<HashSet<string>> ActiveCodesAsync(Guid listId, CancellationToken cancellationToken) =>
        (await db.Set<OptionListItem>().AsNoTracking().Where(i => i.ListId == listId && i.IsActive).Select(i => i.Code).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when two rows already hold the same value, so "unique" could not be kept (checked when it is switched on).</summary>
    private async Task<bool> HasDuplicatesAsync(CustomFieldDef def, CancellationToken cancellationToken)
    {
        var target = CustomEntities.Find(resources, def.EntityType);
        if (target is null)
        {
            return false;
        }

        var value = def.DataType == CustomFieldTypes.Text ? "lower(custom ->> @key)" : def.DataType == CustomFieldTypes.DecimalNumber ? "(custom ->> @key)::numeric" : "custom ->> @key";
        var group = CustomEntities.CompanyScoped(target) ? "company_id, " + value : value;
        var sql = $"select exists (select 1 from {CustomEntities.Table(target)} where custom ? @key"
            + (target.Fields.Find("is_deleted") is null ? string.Empty : " and not is_deleted")
            + $" group by {group} having count(*) > 1)";
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", def.FieldKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private void Touch(CustomFieldDef row, long version)
    {
        db.Entry(row).Property(nameof(CustomFieldDef.RowVersion)).OriginalValue = version;
        row.RowVersion = version + 1;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor.UserId;
    }

    private async Task SaveAsync(bool creating, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new MetadataRefusedException(409, "errors.data.concurrency_conflict");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            throw new MetadataRefusedException(409, creating ? "errors.custom.def.key_taken" : "errors.data.concurrency_conflict", creating ? "fieldKey" : null);
        }
    }

    private static Names Names(JsonDocument document) => OptionListAdministration.NamesOf(document);

    private static CustomFieldDefView View(CustomFieldDef d) => new(
        d.Id, d.EntityType, d.FieldKey, d.DataType, Names(d.LabelI18n), d.HelpI18n is null ? null : Names(d.HelpI18n), d.Validation.RootElement.Clone(), d.DefaultValue?.RootElement.Clone(),
        d.IsRequired, d.IsUnique, d.OptionListId, d.Section, d.Seq, d.Status, d.Version, d.RowVersion);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FieldKey();
}
