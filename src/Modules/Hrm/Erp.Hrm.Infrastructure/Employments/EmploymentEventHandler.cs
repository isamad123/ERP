using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The end of an approval of an employment event (document type <c>hrm.employment_event</c>; slice S1.20 ruling Q11, FR-XM-32),
/// on the deciding transaction: approved puts the event into force exactly as recorded (rows, employment fields, status, event
/// <c>hrm.employee_changed</c> once it is effective; an approved event dated in the future waits for the daily job, ruling Q12);
/// rejected or cancelled cancels it; returned puts it back to draft (a new event is recorded to try again). The kernel refuses the
/// requester and the employee as approvers. A problem the events now have (an event recorded meanwhile makes it impossible) is a
/// <see cref="WorkflowException"/>, so the decision is rolled back and the approver sees why.
/// </summary>
internal sealed class EmploymentEventHandler(IServiceProvider services, IClock clock, ICurrentActor actor, ISettings settings) : IWorkflowOutcomeHandler
{
    public string DocumentType => EmploymentService.DocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        var applier = new EmploymentApplier(db, settings);

        var e = await db.Set<EmploymentEvent>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.employment_event.not_found");
        if (e.Status != "pending_approval")
        {
            throw new WorkflowException("errors.hrm.employment_event.not_pending");
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        if (outcome.Outcome != WorkflowOutcomes.Approved)
        {
            e.Status = outcome.Outcome == WorkflowOutcomes.Returned ? "draft" : "cancelled";
            EmploymentApplier.Bump(e, now, by);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {e.EmploymentId} for update", cancellationToken).ConfigureAwait(false);
        var employment = await db.Set<Employment>().FirstAsync(x => x.Id == e.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (employment.Status is not ("pre_joining" or "active" or "on_notice"))
        {
            throw new WorkflowException(EmploymentService.NotCurrent);
        }

        var events = await applier.EventsAsync(employment.Id, cancellationToken).ConfigureAwait(false);
        if (e.CorrectsEventId is { } target && (EmploymentTimeline.Replaced([.. events.Where(x => x.Id != e.Id).Select(EmploymentApplier.ToTimeline)]).Contains(target)
            || events.FirstOrDefault(x => x.Id == target) is not { } replaced || !EmploymentTimeline.IsApplied(replaced.Status)))
        {
            throw new WorkflowException(EmploymentService.AlreadyCorrected);
        }

        var today = await applier.TodayAsync(employment.CompanyId, now, cancellationToken).ConfigureAwait(false);
        var others = events.Where(x => x.Id != e.Id).ToList();
        EmploymentApplier.SetApplied(e, others, today, now, by);
        e.RowVersion++; // a stored row that changed (review S1.20b finding 6)
        if (await applier.SyncRowsAsync(employment, events, now, by, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new WorkflowException(problem);
        }

        await applier.ApplyEmploymentFieldsAsync(employment, e, today, cancellationToken).ConfigureAwait(false);
        if (e.Status == "effective")
        {
            applier.RaiseChanged(employment, e, events, now, by);
        }

        employment.RowVersion++;
        EmploymentApplier.Stamp(employment, now, by);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            throw new WorkflowException(EmploymentService.Refusal(pg).FieldErrors?.Values.FirstOrDefault()?.FirstOrDefault() ?? "errors.data.rejected");
        }
    }
}
