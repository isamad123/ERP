using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Positions;
using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// Asking for a strength exception (slice S1.22 Build item 5, ruling Q5, BR-04, FR-XM-32): one person, one placement (unit, grade,
/// position, day), a reason. It is decided through the workflow engine (document type <c>hrm.strength_exception</c>; the person placed
/// and the requester never approve it) and used by the employment event that places the person (<see cref="StrengthGuard"/>).
/// </summary>
internal sealed class StrengthExceptionService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IWorkflowService workflow) : IStrengthExceptionService
{
    public const string NotCurrent = "errors.hrm.employment.not_current";
    private const int ReasonLength = 500;

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> RequestAsync(StrengthExceptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PositionPermissions.ExceptionsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.EmploymentId is null)
        {
            errors["employmentId"] = [PersonService.Required];
        }

        if (request.OrgUnitId is null)
        {
            errors["orgUnitId"] = [PersonService.Required];
        }

        if (request.EffectiveDate is null)
        {
            errors["effectiveDate"] = [PersonService.Required];
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            errors["reason"] = [PersonService.Required];
        }
        else if (reason.Length > ReasonLength)
        {
            errors["reason"] = [PersonService.TooLong];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (employment.Status is not ("pre_joining" or "active" or "on_notice"))
        {
            return PeopleResult.Conflict(NotCurrent);
        }

        var positionCode = string.Empty;
        if (request.PositionId is { } positionId)
        {
            var position = await db.Set<Position>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == positionId && p.CompanyId == employment.CompanyId, cancellationToken).ConfigureAwait(false);
            if (position is null)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["positionId"] = [PositionService.NotInCompany] });
            }

            positionCode = position.PositionCode;
        }

        var now = clock.UtcNow;
        var row = new StrengthException
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            OrgUnitId = request.OrgUnitId!.Value,
            GradeId = request.GradeId,
            PositionId = request.PositionId,
            EffectiveDate = request.EffectiveDate!.Value,
            Reason = reason!,
            Status = "pending_approval",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, StrengthExceptionAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            db.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["company"] = JsonSerializer.SerializeToElement(employment.CompanyId.ToString()),
                ["effective_date"] = JsonSerializer.SerializeToElement(row.EffectiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                ["employee_no"] = JsonSerializer.SerializeToElement(employment.EmployeeNo),
                ["unit"] = JsonSerializer.SerializeToElement(row.OrgUnitId.ToString()),
                ["position"] = JsonSerializer.SerializeToElement(positionCode),
            };
            if (employment.UserRef is { } user)
            {
                values["employee"] = JsonSerializer.SerializeToElement(user.ToString());
            }

            var started = await workflow.StartAsync(new WorkflowStartRequest(OrgModels.DocumentType, row.Id, employment.CompanyId,
                employment.UserRef is { } subject ? [subject] : [], values), db.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            row.WorkflowInstanceId = started.InstanceId;
            row.RowVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Pending, Body: new { id = row.Id, status = row.Status, instanceId = started.InstanceId, rowVersion = row.RowVersion });
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return pg.ConstraintName switch
            {
                "fk_strength_exceptions_org_unit_id" => PeopleResult.Fields(new Dictionary<string, string[]> { ["orgUnitId"] = [PositionService.NotInCompany] }),
                "fk_strength_exceptions_grade_id" => PeopleResult.Fields(new Dictionary<string, string[]> { ["gradeId"] = [PositionService.NotInCompany] }),
                _ => PersonService.Refusal(pg),
            };
        }
    }
}

/// <summary>
/// The end of an approval of a strength exception (document type <c>hrm.strength_exception</c>), on the deciding transaction:
/// approved lets one placement through; rejected, returned or cancelled ends it (a new request is made to try again).
/// </summary>
internal sealed class StrengthExceptionHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public string DocumentType => OrgModels.DocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        var row = await db.Set<StrengthException>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.strength_exception.not_found");
        if (row.Status != "pending_approval")
        {
            throw new WorkflowException("errors.hrm.strength_exception.not_pending");
        }

        row.Status = outcome.Outcome switch
        {
            WorkflowOutcomes.Approved => "approved",
            WorkflowOutcomes.Rejected => "rejected",
            _ => "cancelled",
        };
        row.RowVersion++;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor.UserId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
