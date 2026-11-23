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
        var applier = new EmploymentApplier(db, settings, Policies.PolicyResolver.For(db, services));

        var e = await db.Set<EmploymentEvent>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.employment_event.not_found");
        if (e.Status != "pending_approval")
        {
            throw new WorkflowException("errors.hrm.employment_event.not_pending");
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        if (EmploymentApplier.NextEmployment(e) is { } next)
        {
            await TransferOutcomeAsync(db, applier, e, next, outcome.Outcome == WorkflowOutcomes.Approved, now, by, cancellationToken).ConfigureAwait(false);
            return;
        }

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

        // The manager check and the backdated change check run again on the deciding day (review S1.20b finding 9): the manager may
        // have left meanwhile, and a period may have been locked since the change was recorded; the new report replaces the old one.
        if (EmploymentApplier.Section(e.Changes, "assignment")?[AssignmentKeys.Manager] is { } managerNode
            && Guid.Parse(managerNode.GetValue<string>()) is var manager
            && !await db.Set<Employment>().AnyAsync(x => x.Id == manager && x.CompanyId == employment.CompanyId && (x.Status == "pre_joining" || x.Status == "active" || x.Status == "on_notice"), cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowException(EmploymentService.ManagerNotCurrent);
        }

        var earliest = EmploymentApplier.EarliestAffected(e.EffectiveDate, e.CorrectsEventId, events);
        var report = await new EmploymentChangeCheck(services.GetServices<Erp.Hrm.Contracts.Employments.IEmploymentChangeImpact>())
            .RunAsync(new Erp.Hrm.Contracts.Employments.EmploymentChange(employment.Id, employment.CompanyId, e.Kind, e.EffectiveDate, earliest, IsTermination: false), today, cancellationToken).ConfigureAwait(false);
        var changes = e.Changes is null ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonNode.Parse(e.Changes.RootElement.GetRawText())!.AsObject();
        changes["impact"] = System.Text.Json.JsonSerializer.SerializeToNode(report, System.Text.Json.JsonSerializerOptions.Web);
        e.Changes = System.Text.Json.JsonSerializer.SerializeToDocument(changes);

        var others = events.Where(x => x.Id != e.Id).ToList();
        EmploymentApplier.SetApplied(e, others, today, now, by);
        e.RowVersion++; // a stored row that changed (review S1.20b finding 6)
        if (await applier.SyncRowsAsync(employment, events, now, by, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new WorkflowException(problem);
        }

        await applier.ApplyEmploymentFieldsAsync(employment, e, today, cancellationToken).ConfigureAwait(false);
        if (EmploymentApplier.Announces(e, events, today))
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

    /// <summary>
    /// The decision on a held transfer between companies (S1.20c, FR-XM-30, FR-XM-32). Approved: both employments locked (the first,
    /// then the linked one), the state checked again (the first still current with nothing dated from the transfer on, the linked one
    /// still waiting, the end still allowed and no open item left that the carry-over does not carry) and the transfer put into force
    /// as recorded (<see cref="TransferApplier"/>). Rejected, returned or cancelled: both events and the linked employment are cancelled
    /// (its employee number stays used, FR-XM-06); a transfer to try again is recorded anew.
    /// </summary>
    private async Task TransferOutcomeAsync(HrmDbContext db, EmploymentApplier applier, EmploymentEvent separation, Guid next, bool approved, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {separation.EmploymentId} for update", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {next} for update", ct).ConfigureAwait(false);
        var from = await db.Set<Employment>().FirstAsync(x => x.Id == separation.EmploymentId, ct).ConfigureAwait(false);
        var to = await db.Set<Employment>().FirstAsync(x => x.Id == next, ct).ConfigureAwait(false);
        var start = await db.Set<EmploymentEvent>().FirstAsync(x => x.EmploymentId == next && x.Kind == "hire", ct).ConfigureAwait(false);
        if (!approved)
        {
            foreach (var e in new[] { separation, start }.Where(x => x.Status == "pending_approval"))
            {
                e.Status = "cancelled";
                EmploymentApplier.Bump(e, now, by);
            }

            if (to.Status == "pre_joining")
            {
                to.Status = "cancelled";
                EmploymentApplier.Bump(to, now, by);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        if (from.Status is not ("pre_joining" or "active" or "on_notice"))
        {
            throw new WorkflowException(EmploymentService.NotCurrent);
        }

        if (to.Status != "pre_joining" || start.Status != "pending_approval")
        {
            throw new WorkflowException("errors.hrm.employment_event.not_pending");
        }

        var events = await applier.EventsAsync(from.Id, ct).ConfigureAwait(false);

        // Another change of the first employment waiting for its own approval would be stranded by the end (review S1.20c finding 4).
        if (events.Any(x => x.Id != separation.Id && x.Status == "pending_approval"))
        {
            throw new WorkflowException(EmploymentService.TransferPendingChanges);
        }

        var replaced = EmploymentTimeline.ReplacedBy([.. events.Select(EmploymentApplier.ToTimeline)]);
        if (events.Any(x => x.Id != separation.Id && x.Status is "approved" or "effective" && x.EffectiveDate >= separation.EffectiveDate && !replaced.ContainsKey(x.Id)))
        {
            throw new WorkflowException(EmploymentService.TransferLaterChanges);
        }

        // The end and the open items are asked again on the deciding day (the answers of record time may be weeks old).
        var today = await applier.TodayAsync(from.CompanyId, now, ct).ConfigureAwait(false);
        var check = new EmploymentChangeCheck(services.GetServices<Erp.Hrm.Contracts.Employments.IEmploymentChangeImpact>());
        var report = await check.RunAsync(new Erp.Hrm.Contracts.Employments.EmploymentChange(from.Id, from.CompanyId, EmploymentApplier.SeparationKind, separation.EffectiveDate,
            separation.EffectiveDate, IsTermination: true, TransferApplier.CarryOver(separation)), today, ct).ConfigureAwait(false);
        if (report.TerminationNeedsOverride)
        {
            throw new WorkflowException(ImpactReport.TerminationLocked);
        }

        if (report.OpenItems is { Count: > 0 } open)
        {
            throw new WorkflowException(ImpactReport.TransferOpenItems, [.. open]);
        }

        EmploymentApplier.Bump(separation, now, by);
        EmploymentApplier.Bump(start, now, by);
        if (await TransferApplier.ApplyAsync(db, applier, from, separation, to, start, today, now, by, ct).ConfigureAwait(false) is { } problem)
        {
            throw new WorkflowException(problem);
        }

        from.RowVersion++;
        EmploymentApplier.Stamp(from, now, by);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            throw new WorkflowException(EmploymentService.Refusal(pg).FieldErrors?.Values.FirstOrDefault()?.FirstOrDefault() ?? "errors.data.rejected");
        }
    }
}
