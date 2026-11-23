using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Metadata;

/// <summary>
/// Label overrides of the customer and its companies (slice S1.15b, FR-ADM-STU-25, FR-ADM-STU-05, FR-KRN-MET-22). One call sets or
/// resets both languages of a key in a layer, so the editor shows English and Urdu side by side and a reset returns the product
/// text. Writes go through the kernel context, so the change log records them (S1.07). The product's own text (layer
/// <c>pack</c>) is read-only here.
/// </summary>
public sealed partial class LabelAdministration(KernelDbContext db, ICurrentActor actor, IClock clock) : ILabelAdministration
{
    private static readonly string[] Languages = ["en", "ur"];

    public async Task<LabelOverrideList> ListAsync(CancellationToken cancellationToken) =>
        new(await RowsAsync(null, null, null, cancellationToken).ConfigureAwait(false));

    public async Task<LabelOverrideList> SetAsync(LabelOverrideWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var key = write.Key?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > MetadataLimits.LabelKeyLength || !KeyPattern().IsMatch(key))
        {
            throw new MetadataRefusedException(400, "errors.labels.invalid_key", "key");
        }

        var layer = write.Layer;
        if (layer is not ("tenant" or "company"))
        {
            throw new MetadataRefusedException(400, "errors.labels.invalid_layer", "layer");
        }

        Guid? scope = null;
        if (layer == "company")
        {
            if (write.CompanyId is not { } company)
            {
                throw new MetadataRefusedException(400, "errors.labels.company_required", "companyId");
            }

            if (!await db.Set<Domain.Generated.Org.Company>().AsNoTracking().AnyAsync(c => c.Id == company && !c.IsDeleted, cancellationToken).ConfigureAwait(false))
            {
                throw new MetadataRefusedException(400, "errors.labels.unknown_company", "companyId");
            }

            scope = company;
        }
        else if (write.CompanyId is not null)
        {
            throw new MetadataRefusedException(400, "errors.labels.company_not_allowed", "companyId");
        }

        if (write.Texts is not { } texts || (texts.En.ValueKind == JsonValueKind.Undefined && texts.Ur.ValueKind == JsonValueKind.Undefined))
        {
            throw new MetadataRefusedException(400, "errors.labels.texts_required", "texts");
        }

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (language, element) in new[] { ("en", texts.En), ("ur", texts.Ur) })
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            wanted[language] = Text(element, "texts." + language);
        }

        var existing = await db.Set<Label>().Where(l => l.Key == key && l.Layer == layer && l.ScopeId == scope).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        foreach (var (language, text) in wanted)
        {
            var row = existing.FirstOrDefault(l => l.Language.Trim() == language);
            if (text is null)
            {
                if (row is not null)
                {
                    db.Remove(row);
                }

                continue;
            }

            if (row is null)
            {
                db.Add(new Label
                {
                    Id = Guid.CreateVersion7(now),
                    Key = key,
                    Language = language,
                    Layer = layer,
                    ScopeId = scope,
                    Text = text,
                    CreatedAt = now,
                    CreatedBy = actor.UserId,
                    UpdatedAt = now,
                    UpdatedBy = actor.UserId,
                    RowVersion = 1,
                });
            }
            else if (row.Text != text)
            {
                row.Text = text;
                row.UpdatedAt = now;
                row.UpdatedBy = actor.UserId;
                row.RowVersion++;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new MetadataRefusedException(409, "errors.data.concurrency_conflict");
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            db.ChangeTracker.Clear();
            throw pg.SqlState == "23514" ? new MetadataRefusedException(400, "errors.labels.text_length", "texts") : new MetadataRefusedException(409, "errors.data.concurrency_conflict");
        }

        return new LabelOverrideList(await RowsAsync(key, layer, scope, cancellationToken).ConfigureAwait(false));
    }

    private static string? Text(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new MetadataRefusedException(400, "errors.labels.text_invalid", field);
        }

        var text = element.GetString()!.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > MetadataLimits.LabelTextLength)
        {
            throw new MetadataRefusedException(400, "errors.labels.text_length", field);
        }

        if (text.Any(char.IsControl))
        {
            throw new MetadataRefusedException(400, "errors.labels.text_invalid", field);
        }

        return text;
    }

    private async Task<IReadOnlyList<LabelOverrideRow>> RowsAsync(string? key, string? layer, Guid? scope, CancellationToken cancellationToken)
    {
        var query = db.Set<Label>().AsNoTracking().AsQueryable();
        if (key is not null)
        {
            query = query.Where(l => l.Key == key && l.Layer == layer && l.ScopeId == scope);
        }

        var rows = await query.OrderBy(l => l.Key).ThenBy(l => l.Layer).ThenBy(l => l.Language).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(r => Languages.Contains(r.Language.Trim(), StringComparer.Ordinal)).Select(r => new LabelOverrideRow(r.Id, r.Key, r.Language.Trim(), r.Layer, r.ScopeId, r.Text, r.RowVersion))];
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]*$")]
    private static partial Regex KeyPattern();
}
