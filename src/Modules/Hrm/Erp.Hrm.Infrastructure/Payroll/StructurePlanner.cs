using System.Text.Json;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>One component of a structure from a date, as the plan keeps it (money as exact decimals).</summary>
public sealed record StructureLine(Guid ComponentId, int EvaluationOrder, decimal? DefaultAmount, decimal? DefaultPercent, bool IsMandatory, string CurrencyCode);

/// <summary>
/// A change of a salary structure: the assign rule, and the full list of components from a date. It is what is held for a second
/// person while <c>hrm.pay.change_approval</c> is on (slice S1.34 Q5), and what is applied when the approver agrees. JSON, read
/// back by <see cref="StructureChangeHandler"/>.
/// </summary>
public sealed record StructurePlan(Guid StructureId, Guid CompanyId, string? AssignRuleJson, DateOnly? From, IReadOnlyList<StructureLine>? Lines);

/// <summary>
/// The rules and the writing of a structure change (slice S1.34b Build item 7; FR-HRM-PAY-04): one pure place used by the service
/// (to refuse early) and by the approval handler (to refuse again and apply). A component list is a new effective-dated set from
/// a date: components that stay unchanged keep their row, changed ones are closed and re-opened, components left out end that
/// day; nothing is deleted.
/// </summary>
internal static class StructurePlanner
{
    public const string OrderDuplicate = "errors.hrm.pay.order_duplicate";
    public const string OrderDependency = "errors.hrm.pay.order_dependency";
    public const string ComponentUnknown = "errors.hrm.pay.structure_component_unknown";
    public const string ComponentDuplicate = "errors.hrm.pay.structure_component_duplicate";
    public const string ComponentInactive = "errors.hrm.pay.structure_component_inactive";
    public const string DefaultBoth = "errors.hrm.pay.default_both";
    public const string EmptyList = "errors.hrm.pay.structure_empty";
    public const string TooMany = "errors.hrm.pay.structure_too_many";
    public const string DatePast = "errors.hrm.pay.structure_date_past";
    public const string InUse = "errors.hrm.pay.structure_component_in_use";
    public const string CurrencyMixed = "errors.hrm.pay.structure_currency_mixed";
    public const int MaxComponents = 200;

    /// <summary>Checks the whole plan against the database as it stands; returns field to error keys (empty when the plan can be applied).</summary>
    public static async Task<Dictionary<string, string[]>> CheckAsync(HrmDbContext db, StructurePlan plan, Erp.Hrm.Contracts.Payroll.IFormulaValidator formulas, DateOnly? notBefore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        if (plan.Lines is null)
        {
            return errors;
        }

        if (plan.From is not { } from)
        {
            Fail("components.validFrom", PayComponentService.ValidFromRequired);
            return errors;
        }

        if (plan.Lines.Count == 0)
        {
            Fail("components.items", EmptyList);
        }

        if (plan.Lines.Count > MaxComponents)
        {
            Fail("components.items", TooMany);
        }

        if (plan.Lines.Select(l => l.ComponentId).Distinct().Count() != plan.Lines.Count)
        {
            Fail("components.items", ComponentDuplicate);
        }

        if (plan.Lines.Select(l => l.EvaluationOrder).Distinct().Count() != plan.Lines.Count)
        {
            Fail("components.items", OrderDuplicate);
        }

        if (plan.Lines.Any(l => l.EvaluationOrder < 1 || l.EvaluationOrder > 9999 || (l.DefaultAmount is < 0) || (l.DefaultPercent is < 0)))
        {
            Fail("components.items", ComponentRules.LineNegative);
        }

        if (plan.Lines.Any(l => l.DefaultAmount is not null && l.DefaultPercent is not null))
        {
            Fail("components.items", DefaultBoth);
        }

        var hasList = await db.Set<SalaryStructureComponent>().AsNoTracking().AnyAsync(r => r.StructureId == plan.StructureId, ct).ConfigureAwait(false);
        if (hasList && notBefore is { } earliest && from < earliest)
        {
            // A change to an existing list dated in the past would alter what was already paid with no record for arrears (review S1.34b M5): the salary
            // revision is the dated, recorded way to change what people were paid.
            Fail("components.validFrom", DatePast);
        }

        if (plan.Lines.Select(l => l.CurrencyCode).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            Fail("components.items", CurrencyMixed);
        }

        // A component the structure drops may not be one that salaries on the structure still use.
        var removed = await db.Set<SalaryStructureComponent>().AsNoTracking()
            .Where(r => r.StructureId == plan.StructureId && r.ValidFrom <= from && (r.ValidTo == null || r.ValidTo > from))
            .Select(r => r.ComponentId).ToListAsync(ct).ConfigureAwait(false);
        removed = [.. removed.Where(c => plan.Lines.All(l => l.ComponentId != c))];
        var day = from;
        if (removed.Count > 0
            && await (from l in db.Set<CompensationLine>().AsNoTracking()
                      join a in db.Set<CompensationAssignment>().AsNoTracking() on l.AssignmentId equals a.Id
                      where a.StructureId == plan.StructureId && (a.ValidTo == null || a.ValidTo > day) && removed.Contains(l.ComponentId)
                      select l.Id).AnyAsync(ct).ConfigureAwait(false))
        {
            Fail("components.items", InUse);
        }

        var ids = plan.Lines.Select(l => l.ComponentId).Distinct().ToList();
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => ids.Contains(c.Id) && c.CompanyId == plan.CompanyId && !c.IsDeleted).ToListAsync(ct).ConfigureAwait(false);
        if (components.Count != ids.Count)
        {
            Fail("components.items", ComponentUnknown);
        }
        else if (components.Any(c => !c.IsActive))
        {
            Fail("components.items", ComponentInactive);
        }

        var existing = await db.Set<SalaryStructureComponent>().AsNoTracking().Where(r => r.StructureId == plan.StructureId).MaxAsync(r => (DateOnly?)r.ValidFrom, ct).ConfigureAwait(false);
        if (existing is { } latest && from <= latest)
        {
            Fail("components.validFrom", PayComponentService.VersionNotLater);
        }

        // A component computed from another is evaluated after it (FR-HRM-PAY-04, order of evaluation): when a base is in the same
        // structure its order is lower.
        if (errors.Count == 0)
        {
            var order = plan.Lines.ToDictionary(l => l.ComponentId, l => l.EvaluationOrder);
            var allCodes = await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == plan.CompanyId && !c.IsDeleted).ToDictionaryAsync(c => c.Code, c => c.Id, StringComparer.Ordinal, ct).ConfigureAwait(false);
            var versions = await db.Set<PayComponentVersion>().AsNoTracking()
                .Where(v => ids.Contains(v.ComponentId) && v.ValidFrom <= from && (v.ValidTo == null || v.ValidTo > from)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var v in versions)
            {
                var uses = new HashSet<Guid>(v.BaseComponentIds);
                if (!string.IsNullOrWhiteSpace(v.Formula))
                {
                    uses.UnionWith(formulas.Check(v.Formula, allCodes.Keys.ToHashSet(StringComparer.Ordinal)).Components.Where(allCodes.ContainsKey).Select(n => allCodes[n]));
                }

                if (uses.Any(u => order.TryGetValue(u, out var o) && o >= order[v.ComponentId]))
                {
                    Fail("components.items", OrderDependency);
                }
            }
        }

        return errors;
    }

    /// <summary>Writes the plan: the assign rule, and the component rows from the date. The caller saves.</summary>
    public static async Task ApplyAsync(HrmDbContext db, SalaryStructure structure, StructurePlan plan, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        if (plan.AssignRuleJson is not null)
        {
            structure.AssignRule = JsonDocument.Parse(plan.AssignRuleJson);
        }

        if (plan.AssignRuleJson is not null)
        {
            EmploymentApplier.Bump(structure, now, actor);
        }

        if (plan.Lines is null || plan.From is not { } from)
        {
            return;
        }

        var rows = await db.Set<SalaryStructureComponent>().Where(r => r.StructureId == structure.Id && (r.ValidTo == null || r.ValidTo > from)).ToListAsync(ct).ConfigureAwait(false);
        foreach (var line in plan.Lines)
        {
            var current = rows.FirstOrDefault(r => r.ComponentId == line.ComponentId);
            if (current is not null && Same(current, line))
            {
                continue;
            }

            if (current is not null)
            {
                current.ValidTo = from;
                EmploymentApplier.Bump(current, now, actor);
            }

            db.Add(new SalaryStructureComponent
            {
                Id = Guid.CreateVersion7(now),
                StructureId = structure.Id,
                ComponentId = line.ComponentId,
                EvaluationOrder = line.EvaluationOrder,
                DefaultAmount = line.DefaultAmount,
                DefaultPercent = line.DefaultPercent,
                IsMandatory = line.IsMandatory,
                CurrencyCode = line.CurrencyCode,
                ValidFrom = from,
                CreatedAt = now,
                CreatedBy = actor,
                UpdatedAt = now,
                UpdatedBy = actor,
                RowVersion = 1,
            });
        }

        foreach (var left in rows.Where(r => plan.Lines.All(l => l.ComponentId != r.ComponentId)))
        {
            left.ValidTo = from;
            EmploymentApplier.Bump(left, now, actor);
        }
    }

    private static bool Same(SalaryStructureComponent r, StructureLine l) =>
        r.EvaluationOrder == l.EvaluationOrder && r.DefaultAmount == l.DefaultAmount && r.DefaultPercent == l.DefaultPercent && r.IsMandatory == l.IsMandatory && r.CurrencyCode == l.CurrencyCode;
}
