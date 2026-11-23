using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Metadata;

/// <summary>
/// The custom-field definitions of a table for module rules (contract <c>ICustomFieldCatalog</c>, slice S1.23a): every definition, active
/// or retired, with the active items of a choice field's option list. Definitions only; values stay with the records.
/// </summary>
public sealed class CustomFieldCatalog(KernelDbContext db) : ICustomFieldCatalog
{
    public async Task<IReadOnlyList<CustomFieldInfo>> FieldsAsync(string entityType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityType);
        var defs = await db.Set<CustomFieldDef>().AsNoTracking().Where(d => d.EntityType == entityType).OrderBy(d => d.Seq).ThenBy(d => d.FieldKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lists = defs.Where(d => d.OptionListId is not null).Select(d => d.OptionListId!.Value).Distinct().ToList();
        var items = lists.Count == 0 ? [] : await db.Set<OptionListItem>().AsNoTracking().Where(i => lists.Contains(i.ListId) && i.IsActive)
            .OrderBy(i => i.Seq).ThenBy(i => i.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. defs.Select(d => new CustomFieldInfo(d.FieldKey, d.DataType, d.Status, OptionListAdministration.NamesOf(d.LabelI18n),
            [.. items.Where(i => i.ListId == d.OptionListId).Select(i => new CustomOption(i.Code, OptionListAdministration.NamesOf(i.LabelI18n)))]))];
    }
}
