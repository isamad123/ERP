using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// The vacancy rule (slice S1.22 Build item 5, BR-04, ruling Q5; FR-HRM-POS-01): an assignment that places someone in a position
/// whose filled FTE would exceed its budgeted FTE (position model), or in a unit and grade beyond the sanctioned count (headcount
/// model), is refused unless an approved strength exception covers that placement; the exception is then used up and named on the
/// event. Called by the employment services (hire and change) before the event is saved, inside the employment's transaction, so two
/// placements into the last free place cannot both pass (an advisory lock per target).
/// </summary>
/// <remarks>
/// A placement is new when its row has no row just before it with the same target (position code, or unit and grade) and at least
/// its FTE: an unrelated change of someone already over a budget is never refused. The strength is checked on the day the placement
/// starts and on every day another holder's row at the same target starts inside its period (a later arrival recorded earlier is
/// seen). Only a position in force and not frozen can receive a placement; a grade-less sanctioned count covers the whole unit and
/// a grade's count covers that grade (both apply); a unit with no budget row is not limited (CR-078 S1.22a detail g).
/// </remarks>
public static class StrengthGuard
{
    public const string OverBudget = "errors.hrm.strength.over_budget";
    public const string OverSanctioned = "errors.hrm.strength.over_sanctioned";
    public const string PositionNotInForce = "errors.hrm.position.not_in_force";
    public const string PositionFrozen = "errors.hrm.position.frozen";
    public const string ExceptionKey = "strength_exception";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private sealed record Target(string Key, Guid? Position, Guid Unit, Guid? Grade);

    /// <summary>
    /// Checks the placements of <paramref name="planned"/> (the employment's rows with the event applied) dated from
    /// <paramref name="from"/>. Returns null when all are within budget or covered (the covering exception's id is put on
    /// <paramref name="changes"/>, to be stored on the event; <see cref="UseAsync"/> then uses it up), else the refusal.
    /// </summary>
    public static async Task<PeopleResult?> EnforceAsync(
        HrmDbContext db, ISettings settings, Guid companyId, Guid employmentId, IReadOnlyList<PlannedRow> planned, DateOnly from,
        JsonObject changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(changes);
        var model = await PositionRules.ModelAsync(settings, companyId, ct).ConfigureAwait(false);
        foreach (var row in planned.Where(p => p.ValidFrom >= from).OrderBy(p => p.ValidFrom))
        {
            var target = TargetOf(row, model);
            if (target is null)
            {
                continue;
            }

            var before = planned.FirstOrDefault(p => p.ValidTo == row.ValidFrom);
            if (before is not null && TargetOf(before, model) is { } previous && previous.Key == target.Key && Fte(before) >= Fte(row))
            {
                continue; // the same place as before, not a new placement
            }

            if (db.Database.CurrentTransaction is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"strength:" + companyId + ":" + target.Key}, 0))", ct).ConfigureAwait(false);
            }

            var refusal = model == OrgModels.Position
                ? await CheckPositionAsync(db, companyId, employmentId, row, target, ct).ConfigureAwait(false)
                : await CheckHeadcountAsync(db, companyId, employmentId, row, target, ct).ConfigureAwait(false);
            if (refusal is null)
            {
                continue;
            }

            if (refusal.Outcome == PeopleOutcome.Conflict && refusal.Error is OverBudget or OverSanctioned
                && await CoveringAsync(db, companyId, employmentId, row.ValidFrom, target, ct).ConfigureAwait(false) is { } covering)
            {
                changes[ExceptionKey] = covering.Id.ToString();
                continue;
            }

            return refusal;
        }

        return null;
    }

    private static Target? TargetOf(PlannedRow row, string model)
    {
        var unit = Guid.Parse(row.Values[AssignmentKeys.OrgUnit]!.GetValue<string>());
        var grade = row.Values[AssignmentKeys.Grade] is { } g ? Guid.Parse(g.GetValue<string>()) : (Guid?)null;
        if (model == OrgModels.Position)
        {
            return row.Values[AssignmentKeys.Position] is { } p
                ? new Target("p:" + p.GetValue<string>(), Guid.Parse(p.GetValue<string>()), unit, grade)
                : null;
        }

        return new Target("u:" + unit + ":" + grade, null, unit, grade);
    }

    private static decimal Fte(PlannedRow row) => row.Values[AssignmentKeys.Fte]?.GetValue<decimal>() ?? 1m;

    private static PeopleResult Over(string key, Dictionary<string, object?> details) => PeopleResult.Conflict(key, details);

    // ------------------------------------------------------------------ position model

    private static async Task<PeopleResult?> CheckPositionAsync(HrmDbContext db, Guid companyId, Guid employmentId, PlannedRow row, Target target, CancellationToken ct)
    {
        var chosen = await db.Set<Position>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == target.Position && p.CompanyId == companyId, ct).ConfigureAwait(false);
        if (chosen is null)
        {
            return null; // the database's foreign key answers for a position of another company
        }

        var rows = await db.Set<Position>().AsNoTracking().Where(p => p.CompanyId == companyId && p.PositionCodeLower == chosen.PositionCodeLower).ToListAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.Id).ToList();
        var dates = new List<DateOnly> { row.ValidFrom };
        dates.AddRange(await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.PositionId != null && ids.Contains(a.PositionId.Value) && a.EmploymentId != employmentId
                        && a.ValidFrom > row.ValidFrom && (row.ValidTo == null || a.ValidFrom < row.ValidTo))
            .Select(a => a.ValidFrom).Distinct().ToListAsync(ct).ConfigureAwait(false));
        foreach (var day in dates.Distinct().Order())
        {
            var inForce = PositionRules.InForce(rows, day);
            if (inForce is null)
            {
                return Over(PositionNotInForce, new() { ["position"] = chosen.PositionCode, ["date"] = day });
            }

            if (inForce.Status == PositionRules.Frozen)
            {
                return Over(PositionFrozen, new() { ["position"] = chosen.PositionCode, ["date"] = day });
            }

            var filled = await FilledFteAsync(db, companyId, employmentId, ids, day, ct).ConfigureAwait(false);
            if (filled + Fte(row) > inForce.BudgetedFte)
            {
                return Over(OverBudget, new() { ["position"] = chosen.PositionCode, ["date"] = day, ["budgetedFte"] = inForce.BudgetedFte, ["filledFte"] = filled });
            }
        }

        return null;
    }

    /// <summary>The FTE the other employments hold in the position rows on a day (current employments only).</summary>
    private static async Task<decimal> FilledFteAsync(HrmDbContext db, Guid companyId, Guid employmentId, List<Guid> positionIds, DateOnly day, CancellationToken ct) =>
        await (from a in db.Set<EmploymentAssignment>().AsNoTracking()
               join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
               where a.CompanyId == companyId && a.PositionId != null && positionIds.Contains(a.PositionId.Value) && a.EmploymentId != employmentId
                     && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day)
                     && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= day && (e.SeparationDate == null || e.SeparationDate >= day)
               select (decimal?)a.Fte).SumAsync(ct).ConfigureAwait(false) ?? 0m;

    // ------------------------------------------------------------------ headcount model

    private static async Task<PeopleResult?> CheckHeadcountAsync(HrmDbContext db, Guid companyId, Guid employmentId, PlannedRow row, Target target, CancellationToken ct)
    {
        var dates = new List<DateOnly> { row.ValidFrom };
        dates.AddRange(await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.OrgUnitId == target.Unit && a.EmploymentId != employmentId
                        && a.ValidFrom > row.ValidFrom && (row.ValidTo == null || a.ValidFrom < row.ValidTo))
            .Select(a => a.ValidFrom).Distinct().ToListAsync(ct).ConfigureAwait(false));
        foreach (var day in dates.Distinct().Order())
        {
            var budgets = await db.Set<HeadcountBudget>().AsNoTracking()
                .Where(b => b.CompanyId == companyId && b.OrgUnitId == target.Unit && b.ValidFrom <= day && (b.ValidTo == null || b.ValidTo > day)
                            && (b.GradeId == null || b.GradeId == target.Grade))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var budget in budgets)
            {
                var filled = await (from a in db.Set<EmploymentAssignment>().AsNoTracking()
                                    join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                                    where a.CompanyId == companyId && a.OrgUnitId == target.Unit && a.EmploymentId != employmentId
                                          && (budget.GradeId == null || a.GradeId == budget.GradeId)
                                          && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day)
                                          && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= day && (e.SeparationDate == null || e.SeparationDate >= day)
                                    select a.EmploymentId).Distinct().CountAsync(ct).ConfigureAwait(false);
                if (filled + 1 > budget.SanctionedCount)
                {
                    return Over(OverSanctioned, new() { ["unit"] = target.Unit, ["grade"] = budget.GradeId, ["date"] = day, ["sanctionedCount"] = budget.SanctionedCount, ["filledCount"] = filled });
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ exceptions

    /// <summary>The approved, unused exception for this person, day and target (tracked, so it can be used up in the same save).</summary>
    private static async Task<StrengthException?> CoveringAsync(HrmDbContext db, Guid companyId, Guid employmentId, DateOnly day, Target target, CancellationToken ct)
    {
        var candidates = await db.Set<StrengthException>()
            .Where(x => x.CompanyId == companyId && x.EmploymentId == employmentId && x.Status == "approved" && x.EffectiveDate == day && x.OrgUnitId == target.Unit)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        foreach (var x in candidates)
        {
            if (target.Position is null)
            {
                if (x.GradeId == target.Grade)
                {
                    return x;
                }

                continue;
            }

            if (x.PositionId is { } p && (p == target.Position || await SameCodeAsync(db, companyId, p, target.Position.Value, ct).ConfigureAwait(false)))
            {
                return x;
            }
        }

        return null;
    }

    private static async Task<bool> SameCodeAsync(HrmDbContext db, Guid companyId, Guid a, Guid b, CancellationToken ct)
    {
        var codes = await db.Set<Position>().AsNoTracking().Where(p => p.CompanyId == companyId && (p.Id == a || p.Id == b)).Select(p => p.PositionCodeLower).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return codes.Count == 1;
    }

    /// <summary>
    /// Uses up the exception named on the event's <paramref name="changes"/> (if any): status <c>used</c>, the event recorded on it. Called
    /// right after the event is added to the context, so both are saved together.
    /// </summary>
    public static async Task UseAsync(HrmDbContext db, JsonObject changes, Guid eventId, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes[ExceptionKey]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var id))
        {
            return;
        }

        var row = await db.Set<StrengthException>().FirstAsync(x => x.Id == id, ct).ConfigureAwait(false);
        row.Status = "used";
        row.UsedEventId = eventId;
        row.RowVersion++;
        row.UpdatedAt = now;
        row.UpdatedBy = by;
    }

    /// <summary>The hire's single starting row, as the planned row the rule reads.</summary>
    public static PlannedRow HireRow(EmploymentEvent hire, JsonObject assignment)
    {
        ArgumentNullException.ThrowIfNull(hire);
        ArgumentNullException.ThrowIfNull(assignment);
        return new PlannedRow(hire.EffectiveDate, null, hire.Id, assignment);
    }

    /// <summary>The event's <c>changes</c> as an object (empty when it has none).</summary>
    public static JsonObject ChangesOf(JsonDocument? changes) =>
        changes is null ? [] : JsonNode.Parse(changes.RootElement.GetRawText())!.AsObject();
}
