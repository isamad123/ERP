using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Metadata;

/// <summary>
/// Option lists and their items (slice S1.15b, S1.15 Q7; FR-KRN-MET-01, FR-HRM-EMP-03). Codes are fixed once created. Items are
/// deactivated, never deleted, so a stored value keeps its meaning; a list is never removed, and an item the product itself ships
/// (attribute <c>system</c>) cannot be deactivated. A system list can be extended by the customer.
/// </summary>
public sealed partial class OptionListAdministration(KernelDbContext db, ICurrentActor actor, IClock clock) : IOptionListAdministration
{
    public async Task<IReadOnlyList<OptionListSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var lists = await db.Set<OptionList>().AsNoTracking().OrderBy(l => l.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await db.Set<OptionListItem>().AsNoTracking().GroupBy(i => i.ListId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        return [.. lists.Select(l => new OptionListSummary(l.Id, l.Code, NamesOf(l.NameI18n), l.IsSystem, counts.GetValueOrDefault(l.Id), l.RowVersion))];
    }

    public async Task<OptionListDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var list = await db.Set<OptionList>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false);
        return list is null ? null : await DetailAsync(list, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OptionListDetail> CreateAsync(OptionListCreate write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var code = write.Code?.Trim();
        if (string.IsNullOrEmpty(code) || !ListCode().IsMatch(code))
        {
            throw new MetadataRefusedException(400, "errors.option_lists.invalid_code", "code");
        }

        var name = NameJson(write.Name, "name", required: true);
        var now = clock.UtcNow;
        var row = new OptionList
        {
            Id = Guid.CreateVersion7(now),
            Code = code,
            NameI18n = name!,
            IsSystem = false,
            IsActive = true,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        db.Add(row);
        await SaveAsync("errors.option_lists.code_taken", "code", cancellationToken).ConfigureAwait(false);
        return await DetailAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OptionListDetail> EditAsync(Guid id, OptionListEdit write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = await db.Set<OptionList>().FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        var version = write.RowVersion ?? throw new MetadataRefusedException(400, "errors.data.row_version_required", "rowVersion");
        var name = NameJson(write.Name, "name", required: true);
        Touch(row, version);
        row.NameI18n = name!;
        await SaveAsync(null, null, cancellationToken).ConfigureAwait(false);
        return await DetailAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OptionListDetail> AddItemAsync(Guid id, OptionItemCreate write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var list = await db.Set<OptionList>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        var code = write.Code?.Trim();
        if (string.IsNullOrEmpty(code) || !ItemCode().IsMatch(code))
        {
            throw new MetadataRefusedException(400, "errors.option_lists.invalid_code", "code");
        }

        var label = NameJson(write.Label, "label", required: true);
        var last = await db.Set<OptionListItem>().Where(i => i.ListId == id).Select(i => (int?)i.Seq).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var now = clock.UtcNow;
        db.Add(new OptionListItem
        {
            Id = Guid.CreateVersion7(now),
            ListId = id,
            Code = code,
            LabelI18n = label!,
            Seq = last + 10,
            IsActive = write.IsActive ?? true,
            Attributes = JsonDocument.Parse("{}"),
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        });
        await SaveAsync("errors.option_lists.code_taken", "code", cancellationToken).ConfigureAwait(false);
        return await DetailAsync(list, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OptionListDetail> EditItemAsync(Guid id, Guid itemId, OptionItemEdit write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var list = await db.Set<OptionList>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        var row = await db.Set<OptionListItem>().FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == id, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        var version = write.RowVersion ?? throw new MetadataRefusedException(400, "errors.data.row_version_required", "rowVersion");
        var label = NameJson(write.Label, "label", required: true);
        var active = write.IsActive ?? row.IsActive;
        if (row.IsActive && !active && row.Attributes.RootElement.ValueKind == JsonValueKind.Object
            && row.Attributes.RootElement.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.True)
        {
            throw new MetadataRefusedException(400, "errors.option_lists.system_item", "isActive");
        }

        Touch(row, version);
        row.LabelI18n = label!;
        row.IsActive = active;
        await SaveAsync(null, null, cancellationToken).ConfigureAwait(false);
        return await DetailAsync(list, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OptionListDetail> OrderAsync(Guid id, OptionOrder write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var list = await db.Set<OptionList>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        var items = await db.Set<OptionListItem>().Where(i => i.ListId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var order = write.ItemIds ?? throw new MetadataRefusedException(400, "errors.option_lists.order_invalid", "itemIds");
        if (order.Count != items.Count || order.Distinct().Count() != order.Count || !order.All(o => items.Exists(i => i.Id == o)))
        {
            throw new MetadataRefusedException(400, "errors.option_lists.order_invalid", "itemIds");
        }

        var now = clock.UtcNow;
        for (var position = 0; position < order.Count; position++)
        {
            var item = items.First(i => i.Id == order[position]);
            var seq = (position + 1) * 10;
            if (item.Seq != seq)
            {
                item.Seq = seq;
                item.UpdatedAt = now;
                item.UpdatedBy = actor.UserId;
                item.RowVersion++;
            }
        }

        await SaveAsync(null, null, cancellationToken).ConfigureAwait(false);
        return await DetailAsync(list, cancellationToken).ConfigureAwait(false);
    }

    private void Touch(IVersioned row, long version)
    {
        db.Entry(row).Property(nameof(IVersioned.RowVersion)).OriginalValue = version;
        row.RowVersion = version + 1;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor.UserId;
    }

    private async Task SaveAsync(string? conflictKey, string? conflictField, CancellationToken cancellationToken)
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
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            throw new MetadataRefusedException(409, conflictKey ?? "errors.data.concurrency_conflict", conflictField);
        }
    }

    private async Task<OptionListDetail> DetailAsync(OptionList list, CancellationToken cancellationToken)
    {
        var items = await db.Set<OptionListItem>().AsNoTracking().Where(i => i.ListId == list.Id).OrderBy(i => i.Seq).ThenBy(i => i.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var fresh = await db.Set<OptionList>().AsNoTracking().FirstAsync(l => l.Id == list.Id, cancellationToken).ConfigureAwait(false);
        return new OptionListDetail(fresh.Id, fresh.Code, NamesOf(fresh.NameI18n), fresh.IsSystem, fresh.RowVersion,
            [.. items.Select(i => new OptionItemView(i.Id, i.Code, NamesOf(i.LabelI18n), i.Seq, i.IsActive, i.RowVersion))]);
    }

    internal static Names NamesOf(JsonDocument document)
    {
        var root = document.RootElement;
        string? Read(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Names(Read("en"), Read("ur"));
    }

    /// <summary>The two-language JSON of a name; English is required, Urdu optional; blank texts are dropped.</summary>
    internal static JsonDocument? NameJson(Names? names, string field, bool required)
    {
        var en = names?.En?.Trim();
        var ur = names?.Ur?.Trim();
        if (required && string.IsNullOrEmpty(en))
        {
            throw new MetadataRefusedException(400, "errors.metadata.name_required", field);
        }

        if ((en?.Length ?? 0) > MetadataLimits.NameLength || (ur?.Length ?? 0) > MetadataLimits.NameLength)
        {
            throw new MetadataRefusedException(400, "errors.metadata.name_length", field);
        }

        if (string.IsNullOrEmpty(en) && string.IsNullOrEmpty(ur))
        {
            return null;
        }

        var node = new JsonObject();
        if (!string.IsNullOrEmpty(en))
        {
            node["en"] = en;
        }

        if (!string.IsNullOrEmpty(ur))
        {
            node["ur"] = ur;
        }

        return JsonDocument.Parse(node.ToJsonString());
    }

    private static MetadataRefusedException NotFound() => new(404, "errors.data.not_found");

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ListCode();

    [GeneratedRegex("^[a-z0-9][a-z0-9_\\-]{0,39}$")]
    private static partial Regex ItemCode();
}
