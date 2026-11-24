using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Org;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>One person in a position (or a place in a unit) on the day: the employment, FTE and whether the person is acting.</summary>
internal sealed record Holder(Guid EmploymentId, Guid PersonId, string EmployeeNo, decimal Fte, bool IsActing);

/// <summary>A position (the row of its code in force on the day) with its holders (slice S1.22b; the counting is the one of <see cref="StrengthGuard"/>).</summary>
internal sealed record PositionLine(Position Row, bool Planned, decimal FilledFte, IReadOnlyList<Holder> Holders)
{
    public decimal Budget => Row.BudgetedFte;

    public bool Frozen => Row.Status == PositionRules.Frozen;

    public string Status => PositionRules.EffectiveStatus(Row.Status, Row.ValidFrom, Planned ? Row.ValidFrom.AddDays(-1) : Row.ValidFrom, Row.BudgetedFte, FilledFte);

    public decimal Vacant => Planned || Frozen ? 0m : Math.Max(0m, Budget - FilledFte);

    public decimal Over => Planned ? 0m : Math.Max(0m, FilledFte - Budget);
}

/// <summary>A person's display data for a box (name and photo are shown only when the caller may read them, ruling Q8).</summary>
internal sealed record PersonCard(string? Name, string? NameLocal, Guid? PhotoFileId);

/// <summary>What the strength report and the org chart both read as of a date: the positions in force and who holds them, and the people's cards.</summary>
internal sealed class StrengthData(HrmDbContext db, IDataScopeFilter scope, IFieldAccess fields, IOrgStructure org)
{
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    /// <summary>Positions as of the day, scoped and filtered. A position that starts after the day is listed as planned (not counted); a closed one is left out.</summary>
    public async Task<List<PositionLine>> PositionsAsync(Guid companyId, DateOnly day, Guid? unitId, Guid? gradeId, Guid? locationId, CancellationToken ct)
    {
        var query = await scope.ApplyAsync(db.Set<Position>().AsNoTracking().Where(p => p.CompanyId == companyId), PositionAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        var chosen = new List<(Position Row, bool Planned)>();
        foreach (var group in rows.GroupBy(r => r.PositionCodeLower))
        {
            var ordered = group.OrderBy(r => r.ValidFrom).ToList();
            var inForce = PositionRules.InForce(ordered, day);
            if (inForce is not null)
            {
                if (inForce.Status != PositionRules.Closed)
                {
                    chosen.Add((inForce, false));
                }
            }
            else if (ordered[0].ValidFrom > day)
            {
                chosen.Add((ordered[0], true));
            }
        }

        var filtered = chosen.Where(c => (unitId is null || c.Row.OrgUnitId == unitId) && (gradeId is null || c.Row.GradeId == gradeId) && (locationId is null || c.Row.LocationId == locationId)).ToList();
        var codeOf = rows.ToDictionary(r => r.Id, r => r.PositionCodeLower);
        var wanted = filtered.Select(c => c.Row.PositionCodeLower).ToHashSet(StringComparer.Ordinal);
        var ids = rows.Where(r => wanted.Contains(r.PositionCodeLower)).Select(r => r.Id).ToList();
        var assignments = await scope.ApplyAsync(db.Set<EmploymentAssignment>().AsNoTracking(), EmploymentAssignmentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var held = await (from a in assignments
                          join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                          where a.CompanyId == companyId && a.PositionId != null && ids.Contains(a.PositionId.Value)
                                && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day)
                                && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= day && (e.SeparationDate == null || e.SeparationDate >= day)
                          orderby e.EmployeeNo
                          select new { a.PositionId, a.EmploymentId, e.PersonId, e.EmployeeNo, a.Fte, a.IsActing }).ToListAsync(ct).ConfigureAwait(false);
        var byCode = held.GroupBy(h => codeOf[h.PositionId!.Value]).ToDictionary(g => g.Key, g => g.Select(h => new Holder(h.EmploymentId, h.PersonId, h.EmployeeNo, h.Fte, h.IsActing)).ToList());
        return [.. filtered.OrderBy(c => c.Row.PositionCode, StringComparer.Ordinal).Select(c =>
        {
            var holders = c.Planned ? [] : byCode.GetValueOrDefault(c.Row.PositionCodeLower) ?? [];
            return new PositionLine(c.Row, c.Planned, holders.Sum(h => h.Fte), holders);
        })];
    }

    /// <summary>Names (and photos, when the caller may read the field) of the persons; one out of the caller's data scope has no name.</summary>
    public async Task<Dictionary<Guid, PersonCard>> CardsAsync(Guid companyId, IReadOnlyCollection<Guid> personIds, CancellationToken ct)
    {
        var cards = new Dictionary<Guid, PersonCard>();
        if (personIds.Count == 0)
        {
            return cards;
        }

        var ids = personIds.Distinct().ToList();
        var access = await fields.ForAsync(PersonAccess.Fields, "read", companyId, ct).ConfigureAwait(false);
        var showPhoto = access.Of("photo_file_id") != FieldAccessLevel.Hidden;
        var query = await scope.ApplyAsync(db.Set<Person>().AsNoTracking().Where(p => ids.Contains(p.Id)), PersonAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        foreach (var p in await query.ToListAsync(ct).ConfigureAwait(false))
        {
            var name = string.Join(' ', new[] { p.FirstName, p.MiddleName, p.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            cards[p.Id] = new PersonCard(name, p.NameLocal, showPhoto ? p.PhotoFileId : null);
        }

        return cards;
    }

    /// <summary>Codes and names of units and locations (kernel records, by id).</summary>
    public async Task<Dictionary<Guid, OrgRecord>> OrgRecordsAsync(IEnumerable<Guid> unitIds, IEnumerable<Guid> locationIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, OrgRecord>();
        foreach (var r in await org.FindAsync(OrgRecordKind.OrgUnit, [.. unitIds.Distinct()], ct).ConfigureAwait(false))
        {
            result[r.Id] = r;
        }

        foreach (var r in await org.FindAsync(OrgRecordKind.Location, [.. locationIds.Distinct()], ct).ConfigureAwait(false))
        {
            result[r.Id] = r;
        }

        return result;
    }

    public async Task<Dictionary<Guid, Grade>> GradesAsync(Guid companyId, CancellationToken ct) =>
        await db.Set<Grade>().AsNoTracking().Where(g => g.CompanyId == companyId && !g.IsDeleted).ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);

    public static IReadOnlyDictionary<string, string> NameOf(OrgRecord? record) =>
        record?.Name ?? new Dictionary<string, string>();
}
