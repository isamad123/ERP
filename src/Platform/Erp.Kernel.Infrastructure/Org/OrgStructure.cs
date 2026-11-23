using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Domain.Generated.Org;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Org;

/// <summary>
/// The organization questions of business modules (contract <c>IOrgStructure</c>, slice S1.23a): subtrees of units on a date through
/// the reporting hierarchies (the same reading as the subtree data scope, <c>AccessEngine</c>) and the records behind ids.
/// </summary>
public sealed class OrgStructure(KernelDbContext db, ITenantConnectionProvider tenant) : IOrgStructure
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> SubtreesAsync(IReadOnlyCollection<Guid> roots, DateOnly asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var result = roots.Distinct().ToDictionary(r => r, r => new HashSet<Guid> { r });
        if (result.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlySet<Guid>>();
        }

        // UNION (not UNION ALL) stops the recursion even if links formed a cycle (the cycle check is S1.14b's trigger).
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            with recursive tree(root_id, unit_id) as (
                select r, r from unnest($1::uuid[]) as r
                union
                select t.root_id, l.unit_id from org.org_unit_links l
                join org.org_hierarchies h on h.id = l.hierarchy_id and h.kind = 'reporting'
                join tree t on l.parent_unit_id = t.unit_id
                where l.valid_from <= $2 and (l.valid_to is null or l.valid_to > $2)
            )
            select root_id, unit_id from tree
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = result.Keys.ToArray() });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = asOf });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetGuid(0)].Add(reader.GetGuid(1));
        }

        return result.ToDictionary(r => r.Key, r => (IReadOnlySet<Guid>)r.Value);
    }

    public async Task<IReadOnlyList<OrgRecord>> FindAsync(OrgRecordKind kind, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = kind switch
        {
            OrgRecordKind.OrgUnit => await db.Set<OrgUnit>().AsNoTracking().Where(u => wanted.Contains(u.Id) && !u.IsDeleted)
                .Select(u => new Row(u.Id, u.CompanyId, u.Code, u.NameI18n, u.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false),
            OrgRecordKind.Branch => await db.Set<Branch>().AsNoTracking().Where(b => wanted.Contains(b.Id) && !b.IsDeleted)
                .Select(b => new Row(b.Id, b.CompanyId, b.Code, b.NameI18n, b.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false),
            OrgRecordKind.Company => await db.Set<Company>().AsNoTracking().Where(c => wanted.Contains(c.Id) && !c.IsDeleted)
                .Select(c => new Row(c.Id, c.Id, c.Code, c.NameI18n, c.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false),
            _ => await db.Set<Location>().AsNoTracking().Where(l => wanted.Contains(l.Id) && !l.IsDeleted)
                .Select(l => new Row(l.Id, l.CompanyId, l.Code, l.NameI18n, l.IsActive)).ToListAsync(cancellationToken).ConfigureAwait(false),
        };
        return [.. rows.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => new OrgRecord(r.Id, kind, r.CompanyId, r.Code, Names(r.Name), r.IsActive))];
    }

    private static Dictionary<string, string> Names(JsonDocument name) =>
        name.RootElement.ValueKind == JsonValueKind.Object
            ? name.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];

    private sealed record Row(Guid Id, Guid CompanyId, string Code, JsonDocument Name, bool IsActive);
}
