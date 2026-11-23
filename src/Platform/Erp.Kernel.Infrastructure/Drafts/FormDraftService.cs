using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Drafts;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Drafts;

/// <summary>
/// Form drafts (slice S1.21a, CR-077, ruling Q7; FR-ADM-UXA-05). A draft belongs to the person who saved it: every read, save and
/// delete is by that person and a draft of someone else is "not found". The content is encrypted with the customer's field key
/// (it holds personal data); saving renews the keeping time from <c>cfg.drafts.keep_days</c> (default 30).
/// </summary>
public sealed partial class FormDraftService(KernelDbContext db, ITenantConnectionProvider tenant, ICurrentActor actor, IFieldEncryptorProvider encryptors, IClock clock) : IFormDraftService
{
    /// <summary>The purpose the draft content is encrypted for (a different purpose cannot decrypt it).</summary>
    public const string DataPurpose = "cfg.form_drafts.data";

    private const int DefaultKeepDays = 30;

    public async Task<IReadOnlyList<DraftItem>> ListAsync(string? formKey, CancellationToken cancellationToken)
    {
        var me = actor.UserId;
        var now = clock.UtcNow;
        var query = db.Set<FormDraft>().AsNoTracking().Where(d => d.UserId == me && d.ExpiresAt > now);
        if (!string.IsNullOrWhiteSpace(formKey))
        {
            query = query.Where(d => d.FormKey == formKey);
        }

        var rows = await query.OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => Item(r, encryptor))];
    }

    public async Task<DraftOutcome> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = actor.UserId;
        var now = clock.UtcNow;
        var row = await db.Set<FormDraft>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.UserId == me && d.ExpiresAt > now, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DraftOutcome.Fail(404, "errors.data.not_found");
        }

        return DraftOutcome.Ok(Item(row, await encryptors.GetAsync(cancellationToken).ConfigureAwait(false)));
    }

    public async Task<DraftOutcome> SaveAsync(DraftWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.FormKey is not { } key || key.Length > DraftLimits.FormKeyLength || !FormKeyPattern().IsMatch(key))
        {
            return DraftOutcome.Fail(400, "errors.drafts.invalid_form", "formKey");
        }

        if (write.Data is not { ValueKind: JsonValueKind.Object } data || System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > DraftLimits.DataBytes)
        {
            return DraftOutcome.Fail(400, "errors.drafts.invalid_data", "data");
        }

        var me = actor.UserId;
        var now = clock.UtcNow;
        var expires = now.AddDays(await KeepDaysAsync(now, cancellationToken).ConfigureAwait(false));
        var encryptor = await encryptors.GetAsync(cancellationToken).ConfigureAwait(false);
        var cipher = encryptor.Encrypt(System.Text.Encoding.UTF8.GetBytes(data.GetRawText()), DataPurpose);
        var record = write.RecordId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var row = await db.Set<FormDraft>().FirstOrDefaultAsync(d => d.UserId == me && d.FormKey == key && d.RecordId == record, cancellationToken).ConfigureAwait(false);
            var created = row is null;
            if (row is null)
            {
                var count = await db.Set<FormDraft>().CountAsync(d => d.UserId == me && d.FormKey == key, cancellationToken).ConfigureAwait(false);
                if (count >= DraftLimits.PerPersonAndForm)
                {
                    return DraftOutcome.Fail(400, "errors.drafts.too_many");
                }

                row = new FormDraft { Id = Guid.CreateVersion7(now), UserId = me, FormKey = key, RecordId = record, CreatedAt = now, CreatedBy = me, RowVersion = 1 };
                db.Add(row);
            }
            else
            {
                db.Entry(row).Property(nameof(FormDraft.RowVersion)).OriginalValue = row.RowVersion;
                row.RowVersion++;
            }

            row.Data = cipher;
            row.ExpiresAt = expires;
            row.UpdatedAt = now;
            row.UpdatedBy = me;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return DraftOutcome.Ok(Item(row, encryptor), created ? 201 : 200);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // two saves of the same draft at once: the second looks again and replaces it
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // the first save of a new draft raced another: it exists now
            }
        }

        return DraftOutcome.Fail(409, "errors.data.concurrency_conflict");
    }

    public async Task<DraftOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = actor.UserId;
        var row = await db.Set<FormDraft>().FirstOrDefaultAsync(d => d.Id == id && d.UserId == me, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DraftOutcome.Fail(404, "errors.data.not_found");
        }

        db.Remove(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // already gone: the result is the same
        }

        return new DraftOutcome(null, 204);
    }

    private async Task<int> KeepDaysAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var values = await SettingsStore.ResolveAsync(c, null, [KernelSettingKeys.CfgDraftsKeepDays], null, actor.UserId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);
        return values.TryGetValue(KernelSettingKeys.CfgDraftsKeepDays, out var v) && v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var days) && days is >= 1 and <= 365 ? days : DefaultKeepDays;
    }

    private static DraftItem Item(FormDraft row, IFieldEncryptor encryptor)
    {
        using var data = JsonDocument.Parse(encryptor.Decrypt(row.Data, DataPurpose));
        return new DraftItem(row.Id, row.FormKey, row.RecordId, data.RootElement.Clone(), row.ExpiresAt, row.UpdatedAt, row.RowVersion);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FormKeyPattern();
}
