using System.Text.Json;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>One line of a compensation plan: a component with an amount, a percentage or units (money as an exact decimal).</summary>
public sealed record CompensationLineDto(Guid ComponentId, decimal? Amount, decimal? Percent, decimal? Units);

/// <summary>
/// A salary arrangement or revision for an employee from a date: what the service asks for, what is held for a second person while
/// <c>hrm.pay.change_approval</c> is on, and what the approval applies (slice S1.34b, FR-HRM-PAY-04, -06).
/// </summary>
public sealed record CompensationPlan(
    Guid EmploymentId,
    Guid CompanyId,
    Guid StructureId,
    DateOnly ValidFrom,
    string ReasonCode,
    string? Notes,
    string CurrencyCode,
    IReadOnlyList<CompensationLineDto> Lines);

/// <summary>What a backdated revision leaves for arrears (S1.39): the period and its state when the revision was recorded.</summary>
public sealed record AffectedPeriod(Guid PayPeriodId, int PeriodNo, int TaxYear, string Status);

/// <summary>
/// The rules and the writing of a salary arrangement or revision (slice S1.34b Build item 8; FR-HRM-PAY-04, -06, CMP-03), in one
/// place used by the service (to refuse early) and by the approval handler (to refuse again and apply). A revision is never an
/// edit: the row in force ends where the new one starts, and the employment event of kind <c>salary_revision</c> records the old
/// and the new structure, the date, the reason and, for a backdated one, the periods already opened that the date reaches (the
/// event raised for S1.39 carries them; arrears are not computed here). Every check is repeated when the approver agrees.
/// </summary>
internal static class CompensationPlanner
{
    public const string NotLater = "errors.hrm.pay.compensation_not_later";
    public const string BeforeJoining = "errors.hrm.pay.compensation_before_joining";
    public const string StructureUnknown = "errors.hrm.pay.compensation_structure_unknown";
    public const string StructureEmpty = "errors.hrm.pay.compensation_structure_empty";
    public const string LineComponent = "errors.hrm.pay.compensation_line_component";
    public const string LineDuplicate = "errors.hrm.pay.compensation_line_duplicate";
    public const string MandatoryMissing = "errors.hrm.pay.compensation_mandatory_missing";
    public const string CurrencyMismatch = "errors.hrm.pay.compensation_currency";
    public const string ReasonUnknown = "errors.hrm.pay.compensation_reason";
    public const string AfterEnd = "errors.hrm.pay.compensation_after_end";
    public const string NotesTooLong = "errors.hrm.field.too_long";
    public const int NotesLength = 2000;

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    /// <summary>Checks the plan against the database as it stands: field to error keys (empty when it can be applied).</summary>
    public static async Task<(Dictionary<string, string[]> Errors, string? Problem)> CheckAsync(HrmDbContext db, Employment employment, CompensationPlan plan, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        if (!CurrentStatuses.Contains(employment.Status))
        {
            return (errors, EmploymentService.NotCurrent);
        }

        if (plan.ValidFrom < employment.JoiningDate)
        {
            Fail("validFrom", BeforeJoining);
        }

        // Nothing starts on or after a recorded end (as the employment change service refuses, review S1.34b M6).
        if (await db.Set<EmploymentEvent>().AsNoTracking().AnyAsync(e => e.EmploymentId == employment.Id && e.Kind == EmploymentApplier.SeparationKind
                && (e.Status == "pending_approval" || e.Status == "approved" || e.Status == "effective") && e.EffectiveDate <= plan.ValidFrom, ct).ConfigureAwait(false))
        {
            Fail("validFrom", AfterEnd);
        }

        if (!EmploymentCatalogue.Default.IsReason("salary_revision", plan.ReasonCode))
        {
            Fail("reasonCode", ReasonUnknown);
        }

        if (plan.Notes is { Length: > NotesLength })
        {
            Fail("notes", NotesTooLong);
        }

        var structure = await db.Set<SalaryStructure>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == plan.StructureId && s.CompanyId == plan.CompanyId && !s.IsDeleted && s.IsActive, ct).ConfigureAwait(false);
        if (structure is null)
        {
            Fail("structureId", StructureUnknown);
            return (errors, null);
        }

        var inForce = await db.Set<SalaryStructureComponent>().AsNoTracking()
            .Where(r => r.StructureId == structure.Id && r.ValidFrom <= plan.ValidFrom && (r.ValidTo == null || r.ValidTo > plan.ValidFrom)).ToListAsync(ct).ConfigureAwait(false);
        if (inForce.Count == 0)
        {
            Fail("structureId", StructureEmpty);
        }

        if (inForce.Any(r => !string.Equals(r.CurrencyCode, plan.CurrencyCode, StringComparison.Ordinal)))
        {
            Fail("structureId", CurrencyMismatch);
        }

        if (plan.Lines.Select(l => l.ComponentId).Distinct().Count() != plan.Lines.Count)
        {
            Fail("lines", LineDuplicate);
        }

        foreach (var line in plan.Lines)
        {
            if (inForce.All(r => r.ComponentId != line.ComponentId))
            {
                Fail("lines", LineComponent);
            }
            else if (ComponentRules.CheckLine(line.Amount, line.Percent, line.Units) is { } problem)
            {
                Fail("lines", problem);
            }
        }

        if (inForce.Any(r => r.IsMandatory && plan.Lines.All(l => l.ComponentId != r.ComponentId)))
        {
            Fail("lines", MandatoryMissing);
        }

        var latest = await db.Set<CompensationAssignment>().AsNoTracking().Where(a => a.EmploymentId == employment.Id).MaxAsync(a => (DateOnly?)a.ValidFrom, ct).ConfigureAwait(false);
        if (latest is { } last && plan.ValidFrom <= last)
        {
            Fail("validFrom", NotLater);
        }

        return (errors, null);
    }

    /// <summary>
    /// Writes the event, closes the row in force, opens the new row with its lines and raises <c>hrm.compensation.changed</c>. The
    /// caller has checked the plan and saves. Returns the new assignment and what a backdated date reaches.
    /// </summary>
    public static async Task<(CompensationAssignment Assignment, IReadOnlyList<AffectedPeriod> Affected)> ApplyAsync(
        HrmDbContext db, EmploymentApplier applier, Employment employment, CompensationPlan plan, DateOnly today, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var previous = await db.Set<CompensationAssignment>().Where(a => a.EmploymentId == employment.Id && (a.ValidTo == null || a.ValidTo > plan.ValidFrom)).OrderByDescending(a => a.ValidFrom).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var kinds = await db.Set<PayComponent>().AsNoTracking().Where(c => plan.Lines.Select(l => l.ComponentId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Kind, ct).ConfigureAwait(false);
        var payGroupId = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employment.Id && a.ValidFrom <= plan.ValidFrom && (a.ValidTo == null || a.ValidTo > plan.ValidFrom)).Select(a => a.PayGroupId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var affected = payGroupId is { } group
            ? await db.Set<PayPeriod>().AsNoTracking().Where(p => p.PayGroupId == group && p.EndDate >= plan.ValidFrom && p.Status != "future").OrderBy(p => p.StartDate)
                .Select(p => new AffectedPeriod(p.Id, p.PeriodNo, p.TaxYear, p.Status)).ToListAsync(ct).ConfigureAwait(false)
            : [];

        var evt = new EmploymentEvent
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = plan.CompanyId,
            EmploymentId = employment.Id,
            Kind = "salary_revision",
            ReasonCode = plan.ReasonCode,
            EffectiveDate = plan.ValidFrom,
            Notes = plan.Notes,
            Status = plan.ValidFrom <= today ? "effective" : "approved",
            Changes = JsonSerializer.SerializeToDocument(new
            {
                compensation = new
                {
                    previousStructureId = previous?.StructureId,
                    structureId = plan.StructureId,
                    validFrom = plan.ValidFrom,
                    backdated = plan.ValidFrom < today || affected.Count > 0,
                    affectedPeriods = affected,
                },
            }, JsonSerializerOptions.Web),
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
            RowVersion = 1,
        };
        db.Add(evt);
        if (previous is not null)
        {
            previous.ValidTo = plan.ValidFrom; // the old row ends where the new one starts (before the new row exists: the periods may not overlap)
            EmploymentApplier.Bump(previous, now, actor);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false); // the event first: the assignment refers to it

        var gross = plan.Lines.Where(l => l.Amount is not null && kinds.GetValueOrDefault(l.ComponentId) == "earning").Sum(l => l.Amount!.Value);
        var assignment = new CompensationAssignment
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = plan.CompanyId,
            EmploymentId = employment.Id,
            StructureId = plan.StructureId,
            EventId = evt.Id,
            CurrencyCode = plan.CurrencyCode,
            GrossAmount = ComponentRules.Money(gross),
            ValidFrom = plan.ValidFrom,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
            RowVersion = 1,
        };
        db.Add(assignment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false); // the lines refer to the assignment
        foreach (var line in plan.Lines)
        {
            db.Add(new CompensationLine
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = plan.CompanyId,
                AssignmentId = assignment.Id,
                ComponentId = line.ComponentId,
                Amount = line.Amount is { } a ? ComponentRules.Money(a) : null,
                Percent = line.Percent,
                Units = line.Units,
                CreatedAt = now,
                CreatedBy = actor,
                UpdatedAt = now,
                UpdatedBy = actor,
                RowVersion = 1,
            });
        }

        applier.RaiseFor(PayEvents.CompensationChanged, "hrm.employment", employment.Id, now, actor, new
        {
            employmentId = employment.Id,
            companyId = plan.CompanyId,
            eventId = evt.Id,
            assignmentId = assignment.Id,
            previousAssignmentId = previous?.Id,
            structureId = plan.StructureId,
            previousStructureId = previous?.StructureId,
            validFrom = plan.ValidFrom,
            reasonCode = plan.ReasonCode,
            backdated = plan.ValidFrom < today || affected.Count > 0,
            earliestDate = plan.ValidFrom,
            affectedPeriodIds = affected.Select(p => p.PayPeriodId).ToList(),
        });
        return (assignment, affected);
    }
}
