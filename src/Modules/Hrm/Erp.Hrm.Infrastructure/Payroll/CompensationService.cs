using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Salary arrangements and revisions (slice S1.34b Build item 8; FR-HRM-PAY-04, -06, FR-HRM-CMP-03): assigns a structure with the
/// amount of each component to an employee from a date, with a reason; a later one is a revision, never an edit. The employment
/// event of kind <c>salary_revision</c> records the old and new structure, the date and the reason, and for a backdated date the
/// periods already opened that it reaches (arrears are S1.39). While <c>hrm.pay.change_approval</c> is on the change is held for a
/// second person (FR-XM-32). Amounts are salary-class data (GHR-06): reading needs <c>hrm.pay.compensation.read</c> and the field
/// class; writing needs edit access to it.
/// </summary>
internal sealed class CompensationService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISettings settings,
    IHeldChanges held,
    IFieldEncryptorProvider keys,
    EmploymentApplier applier) : ICompensationService
{
    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> AssignAsync(Guid employmentId, CompensationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.CompensationCreate, cancellationToken).ConfigureAwait(false)
            || !await permissions.HasPermissionAsync(PayPermissions.CompensationLinesCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        // Nobody sets their own pay (FR-XM-32; the approval handler refuses it as well, review S1.34b L7).
        if (employment.UserRef is { } own && own == actor.UserId)
        {
            return PeopleResult.Forbidden(CompensationChangeHandler.OwnPay);
        }

        var probe = new CompensationAssignment { CompanyId = employment.CompanyId, EmploymentId = employment.Id, CurrencyCode = string.Empty };
        if (!await scope.AllowsAsync(probe, CompensationAssignmentAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.StructureId is null)
        {
            errors["structureId"] = [PersonService.Required];
        }

        if (request.ValidFrom is null)
        {
            errors["validFrom"] = [PayComponentService.ValidFromRequired];
        }

        if (string.IsNullOrWhiteSpace(request.ReasonCode))
        {
            errors["reasonCode"] = [PersonService.Required];
        }

        if (request.Lines is not { Count: > 0 })
        {
            errors["lines"] = [PersonService.Required];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var structureId = request.StructureId!.Value;
        var from = request.ValidFrom!.Value;

        // Amounts are salary-class data: the caller needs edit access to them (GHR-06).
        var map = await fields.ForAsync(CompensationLineAccess.Fields, "create", employment.CompanyId, cancellationToken).ConfigureAwait(false);

        // Every new row writes amount and percent (empty is a value too), so both need edit access whatever the lines carry: a revision
        // with units only would otherwise wipe the amounts of someone who cannot edit them (review S1.34b M1).
        string[] touched = ["amount", "percent"];
        if (touched.Where(k => !map.CanEdit(k)).ToList() is { Count: > 0 } denied)
        {
            return new PeopleResult(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));
        }

        // One currency per structure from the date (the structure's components say it); the company's when they disagree (then refused).
        var currencies = await db.Set<SalaryStructureComponent>().AsNoTracking()
            .Where(r => r.StructureId == structureId && r.ValidFrom <= from && (r.ValidTo == null || r.ValidTo > from)).Select(r => r.CurrencyCode).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var currency = currencies.Count == 1
            ? currencies[0]
            : (await db.Database.SqlQuery<string>($"select base_currency_code::text as \"Value\" from org.companies where id = {employment.CompanyId}").ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault() ?? string.Empty;
        var plan = new CompensationPlan(
            employment.Id,
            employment.CompanyId,
            structureId,
            from,
            request.ReasonCode!.Trim(),
            string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            currency,
            [.. request.Lines!.Select(l => new CompensationLineDto(l.ComponentId ?? Guid.Empty, l.Amount is { } a ? ComponentRules.Money(a) : null, l.Percent, l.Units))]);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employmentId} for update", cancellationToken).ConfigureAwait(false);
            var (problems, problem) = await CompensationPlanner.CheckAsync(db, employment, plan, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return PeopleResult.Conflict(problem);
            }

            if (problems.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return PeopleResult.Fields(problems);
            }

            var approval = await settings.GetAsync(HrmSettingKeys.HrmPayChangeApproval, (Guid?)employment.CompanyId, cancellationToken).ConfigureAwait(false);
            if (approval.Value.ValueKind != JsonValueKind.False)
            {
                var payload = PayHeldChanges.Seal(await keys.GetAsync(cancellationToken).ConfigureAwait(false), plan);
                var requestId = await held.SubmitAsync(
                    new HeldChangeRequest(PayHeldChanges.CompensationKind, EmploymentAccess.Scope.Resource, employment.Id, payload, string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()),
                    tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId });
            }

            var today = await applier.TodayAsync(employment.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var tracked = await db.Set<Employment>().FirstAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
            var (assignment, affected) = await CompensationPlanner.ApplyAsync(db, applier, tracked, plan, today, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: new { status = "applied", id = assignment.Id, validFrom = assignment.ValidFrom, backdated = plan.ValidFrom < today || affected.Count > 0, affectedPeriods = affected });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }
    }

    public async Task<PeopleResult> GetAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.CompensationRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var day = asOf ?? await applier.TodayAsync(employment.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var assignments = await db.Set<CompensationAssignment>().AsNoTracking().Where(a => a.EmploymentId == employmentId).OrderBy(a => a.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = assignments.Select(a => a.Id).ToList();
        var lines = await (from l in db.Set<CompensationLine>().AsNoTracking()
                           join c in db.Set<PayComponent>().AsNoTracking() on l.ComponentId equals c.Id
                           where ids.Contains(l.AssignmentId)
                           orderby c.PayslipOrder, c.Code
                           select new { Line = l, c.Code, c.Kind }).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Salary-class fields: hidden without the field class, per row under the caller's data scope (GHR-06).
        var assignmentPlan = await fields.PlanAsync(CompensationAssignmentAccess.Fields, CompensationAssignmentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var assignmentAccess = await assignmentPlan.ResolveAsync(assignments, Source, cancellationToken).ConfigureAwait(false);
        var linePlan = await fields.PlanAsync(CompensationLineAccess.Fields, CompensationLineAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var lineAccess = await linePlan.ResolveAsync([.. lines.Select(l => l.Line)], Source, cancellationToken).ConfigureAwait(false);

        var events = await db.Set<EmploymentEvent>().AsNoTracking().Where(e => e.EmploymentId == employmentId && e.Kind == "salary_revision").ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        var views = new List<Dictionary<string, object?>>();
        for (var i = 0; i < assignments.Count; i++)
        {
            var a = assignments[i];
            var view = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = a.Id,
                ["structureId"] = a.StructureId,
                ["eventId"] = a.EventId,
                ["currencyCode"] = a.CurrencyCode,
                ["validFrom"] = a.ValidFrom,
                ["validTo"] = a.ValidTo,
                ["current"] = a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day),
                ["reasonCode"] = events.GetValueOrDefault(a.EventId)?.ReasonCode,
                ["revision"] = events.GetValueOrDefault(a.EventId)?.Changes is { } changes ? JsonSerializer.Deserialize<JsonElement>(changes.RootElement.GetRawText()) : null,
            };
            if (assignmentAccess[i].Of("gross_amount") != FieldAccessLevel.Hidden)
            {
                view["grossAmount"] = a.GrossAmount;
            }

            view["lines"] = lines.Select((l, j) => (l, j)).Where(x => x.l.Line.AssignmentId == a.Id).Select(x =>
            {
                var line = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = x.l.Line.Id,
                    ["componentId"] = x.l.Line.ComponentId,
                    ["componentCode"] = x.l.Code,
                    ["kind"] = x.l.Kind,
                    ["units"] = x.l.Line.Units,
                };
                if (lineAccess[x.j].Of("amount") != FieldAccessLevel.Hidden)
                {
                    line["amount"] = x.l.Line.Amount;
                }

                if (lineAccess[x.j].Of("percent") != FieldAccessLevel.Hidden)
                {
                    line["percent"] = x.l.Line.Percent;
                }

                return line;
            }).ToList();
            views.Add(view);
        }

        var waiting = await held.PendingAsync(PayHeldChanges.CompensationKind, employmentId, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { employmentId, asOf = day, current = views.FirstOrDefault(v => (bool)v["current"]!), history = views, pending = waiting.Count });
    }

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "ex_compensation_assignments_employment_id" => PeopleResult.Fields(new Dictionary<string, string[]> { ["validFrom"] = [CompensationPlanner.NotLater] }),
        "uq_compensation_lines_assignment_id_component_id" => PeopleResult.Fields(new Dictionary<string, string[]> { ["lines"] = [CompensationPlanner.LineDuplicate] }),
        _ => PersonService.Refusal(pg),
    };
}
