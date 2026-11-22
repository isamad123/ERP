using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Views;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Generated;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Views;

/// <summary>
/// Saved list views (slice S1.12c Build item 17, S1.12 ruling Q12, FR-ADM-NAV-02). A person sees their own views of a list
/// and the views others shared with a role the person holds today; only the person who saved a view changes or deletes
/// it, and sharing needs <see cref="ViewPermissions.Share"/>. Writes go through the kernel context, so the change log
/// records them like every other change (S1.07).
/// </summary>
public sealed partial class SavedViewService(KernelDbContext db, ICurrentActor actor, AccessEngine access, IClock clock) : ISavedViewService
{
    private const int Ok = 200;
    private const int Created = 201;
    private const int BadRequest = 400;
    private const int Forbidden = 403;
    private const int NotFound = 404;
    private const int Conflict = 409;

    public async Task<IReadOnlyList<SavedViewItem>> ListAsync(string listKey, CancellationToken cancellationToken)
    {
        var roles = (await access.RoleIdsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var me = actor.UserId;
        var rows = await db.Set<SavedView>().AsNoTracking()
            .Where(v => v.ListKey == listKey && (v.UserId == me || (v.RoleId != null && roles.Contains(v.RoleId.Value))))
            .OrderBy(v => v.Name).ThenBy(v => v.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ItemsAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ViewOutcome> CreateAsync(SavedViewWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (Check(write, requireListKey: true) is { } invalid)
        {
            return invalid;
        }

        if (await ShareCheckAsync(write.RoleId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var me = actor.UserId;
        var count = await db.Set<SavedView>().CountAsync(v => v.UserId == me && v.ListKey == write.ListKey, cancellationToken).ConfigureAwait(false);
        if (count >= ViewLimits.PerPersonAndList)
        {
            return ViewOutcome.Fail(BadRequest, "errors.views.too_many");
        }

        var now = clock.UtcNow;
        var row = new SavedView
        {
            Id = Guid.CreateVersion7(now),
            UserId = me,
            ListKey = write.ListKey!,
            Name = write.Name!.Trim(),
            Definition = JsonDocument.Parse(write.Definition!.Value.GetRawText()),
            RoleId = write.RoleId,
            CreatedAt = now,
            CreatedBy = me,
            UpdatedAt = now,
            UpdatedBy = me,
            RowVersion = 1,
        };
        db.Add(row);
        return await SaveAsync(row, Created, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ViewOutcome> UpdateAsync(Guid id, SavedViewWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = await OwnAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ViewOutcome.Fail(NotFound, "errors.data.not_found");
        }

        if (Check(write, requireListKey: false) is { } invalid)
        {
            return invalid;
        }

        if (write.RowVersion is not { } version)
        {
            return ViewOutcome.Fail(BadRequest, "errors.data.row_version_required", "rowVersion");
        }

        // Keeping the role a view is already shared with needs no permission; sharing it anew or with another role does.
        if (write.RoleId != row.RoleId && await ShareCheckAsync(write.RoleId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        db.Entry(row).Property(nameof(SavedView.RowVersion)).OriginalValue = version;
        row.Name = write.Name!.Trim();
        row.Definition = JsonDocument.Parse(write.Definition!.Value.GetRawText());
        row.RoleId = write.RoleId;
        row.RowVersion = version + 1;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor.UserId;
        return await SaveAsync(row, Ok, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ViewOutcome> DeleteAsync(Guid id, long rowVersion, CancellationToken cancellationToken)
    {
        var row = await OwnAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ViewOutcome.Fail(NotFound, "errors.data.not_found");
        }

        db.Entry(row).Property(nameof(SavedView.RowVersion)).OriginalValue = rowVersion;
        db.Remove(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ViewOutcome.Fail(Conflict, "errors.data.concurrency_conflict");
        }

        return new ViewOutcome(null, 204);
    }

    public async Task<IReadOnlyList<ShareableRole>> ShareableRolesAsync(CancellationToken cancellationToken)
    {
        if (!await access.HoldsAsync(ViewPermissions.Share, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var roles = await db.Set<Domain.Generated.Iam.Role>().AsNoTracking().Where(r => !r.IsDeleted && r.IsActive)
            .OrderBy(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return roles.Select(r => new ShareableRole(r.Id, r.Code, Names(r.NameI18n))).ToList();
    }

    private async Task<SavedView?> OwnAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = actor.UserId;
        return await db.Set<SavedView>().FirstOrDefaultAsync(v => v.Id == id && v.UserId == me, cancellationToken).ConfigureAwait(false);
    }

    private static ViewOutcome? Check(SavedViewWrite write, bool requireListKey)
    {
        if (requireListKey && (write.ListKey is not { } key || key.Length > ViewLimits.ListKeyLength || !ListKeyPattern().IsMatch(key)))
        {
            return ViewOutcome.Fail(BadRequest, "errors.views.invalid_list", "listKey");
        }

        var name = write.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ViewLimits.NameLength)
        {
            return ViewOutcome.Fail(BadRequest, "errors.views.invalid_name", "name");
        }

        if (write.Definition is not { ValueKind: JsonValueKind.Object } definition || System.Text.Encoding.UTF8.GetByteCount(definition.GetRawText()) > ViewLimits.DefinitionBytes)
        {
            return ViewOutcome.Fail(BadRequest, "errors.views.invalid_definition", "definition");
        }

        return null;
    }

    /// <summary>Sharing with a role needs the share permission and an active role of this customer.</summary>
    private async Task<ViewOutcome?> ShareCheckAsync(Guid? roleId, CancellationToken cancellationToken)
    {
        if (roleId is not { } role)
        {
            return null;
        }

        if (!await access.HoldsAsync(ViewPermissions.Share, cancellationToken).ConfigureAwait(false))
        {
            return ViewOutcome.Fail(Forbidden, "errors.access.forbidden", "roleId");
        }

        var exists = await db.Set<Domain.Generated.Iam.Role>().AnyAsync(r => r.Id == role && !r.IsDeleted && r.IsActive, cancellationToken).ConfigureAwait(false);
        return exists ? null : ViewOutcome.Fail(BadRequest, "errors.views.unknown_role", "roleId");
    }

    private async Task<ViewOutcome> SaveAsync(SavedView row, int status, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ViewOutcome.Fail(Conflict, "errors.data.concurrency_conflict");
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            db.ChangeTracker.Clear();
            var error = PostgresErrors.Translate(pg, ex);
            return error is DataConflictException
                ? ViewOutcome.Fail(Conflict, "errors.views.duplicate_name", "name")
                : ViewOutcome.Fail(BadRequest, error.ErrorCode, null);
        }

        return ViewOutcome.Ok((await ItemsAsync([row], cancellationToken).ConfigureAwait(false))[0], status);
    }

    private async Task<IReadOnlyList<SavedViewItem>> ItemsAsync(IReadOnlyList<SavedView> rows, CancellationToken cancellationToken)
    {
        var roleIds = rows.Where(r => r.RoleId is not null).Select(r => r.RoleId!.Value).Distinct().ToList();
        var ownerIds = rows.Select(r => r.UserId).Distinct().ToList();
        var roles = roleIds.Count == 0
            ? new Dictionary<Guid, SharedRole>()
            : (await db.Set<Domain.Generated.Iam.Role>().AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(r => r.Id, r => new SharedRole(r.Id, r.Code, Names(r.NameI18n)));
        var owners = await db.Set<Domain.Generated.Iam.User>().AsNoTracking().Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        var me = actor.UserId;
        return rows.Select(r => new SavedViewItem(
            r.Id, r.ListKey, r.Name, r.Definition.RootElement.Clone(),
            r.RoleId is { } role && roles.TryGetValue(role, out var shared) ? shared : null,
            r.UserId == me, owners.GetValueOrDefault(r.UserId, string.Empty), r.RowVersion, r.UpdatedAt)).ToList();
    }

    internal static IReadOnlyDictionary<string, string> Names(JsonDocument names) =>
        names.RootElement.ValueKind == JsonValueKind.Object
            ? names.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex ListKeyPattern();
}

/// <summary>
/// Label overrides in force (slice S1.12c Build item 15; FR-KRN-ORG-08, FR-KRN-MET-22): rows of <c>cfg.labels</c> for the
/// pack layer, the customer (<c>tenant</c>) layer and, for one of the person's companies, the company layer; a later layer
/// wins over an earlier one for the same key and language.
/// </summary>
public sealed class LabelOverrideService(KernelDbContext db, AccessEngine access) : ILabelOverrideService
{
    private static readonly string[] Order = ["pack", "tenant", "company"];

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        Guid? company = null;
        if (companyId is { } asked)
        {
            var grants = await access.GrantsAsync(cancellationToken).ConfigureAwait(false);
            if (grants.Any(g => g.Companies is null || g.Companies.Contains(asked)))
            {
                company = asked;
            }
        }

        var rows = await db.Set<Label>().AsNoTracking()
            .Where(l => l.Layer == "pack" || l.Layer == "tenant" || (company != null && l.Layer == "company" && l.ScopeId == company))
            .Select(l => new { l.Key, l.Language, l.Layer, l.Text })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(r => Array.IndexOf(Order, r.Layer)))
        {
            var language = row.Language.Trim();
            if (!result.TryGetValue(language, out var texts))
            {
                result[language] = texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
            }

            texts[row.Key] = row.Text;
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.Ordinal);
    }
}
