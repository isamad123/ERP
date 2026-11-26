using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// The end of an approval of a loan application (document type <c>hrm.loan_application</c>, slice S1.41 Build items 2 and 3; FR-HRM-LON-02, LON-03, FR-XM-32), on the deciding
/// transaction. Approved: the installment schedule is built from the loan's own frozen terms (principal, method, rate and count are the loan's, not the policy's today), one
/// installment to each open pay period of the employee's pay group from the first the application named (or the next one open to a run when that one has gone); if the calendar
/// does not reach the last installment the approval is refused until the calendar is generated further. Rejected: the loan is rejected. Withdrawn or cancelled: the loan is cancelled.
/// Returned: nothing changes, the application stays open for its requester to withdraw.
/// </summary>
internal sealed class LoanApplicationHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public const string NotPending = "errors.hrm.loan.not_pending";
    public const string CalendarShort = "errors.hrm.loan.calendar_short";
    public const string NoPayGroup = "errors.hrm.loan.no_pay_group";

    public string DocumentType => LoanEvents.ApplicationDocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        await LoanLedger.LockAsync(db, outcome.DocumentId, cancellationToken).ConfigureAwait(false);
        var loan = await db.Set<Loan>().FirstOrDefaultAsync(l => l.Id == outcome.DocumentId && l.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.data.not_found");
        if (outcome.Outcome == WorkflowOutcomes.Returned)
        {
            return;
        }

        if (loan.Status != "applied")
        {
            throw new WorkflowException(NotPending);
        }

        var now = clock.UtcNow;
        if (outcome.Outcome != WorkflowOutcomes.Approved)
        {
            loan.Status = outcome.Outcome == WorkflowOutcomes.Rejected ? "rejected" : "cancelled";
            EmploymentApplier.Bump(loan, now, actor.UserId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var applier = ActivatorUtilities.CreateInstance<EmploymentApplier>(services, db);
        var facts = new LoanFacts(db, services.GetRequiredService<ISettings>());
        var today = await applier.TodayAsync(loan.CompanyId, now, cancellationToken).ConfigureAwait(false);
        var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == loan.EmploymentId, cancellationToken).ConfigureAwait(false);
        var context = await facts.ReadAsync(employment, today, cancellationToken).ConfigureAwait(false);
        if (context.PayGroupId is not { } payGroup)
        {
            throw new WorkflowException(NoPayGroup);
        }

        var periods = await facts.PeriodsAsync(payGroup, loan.StartPeriodId, today, loan.Installments, cancellationToken).ConfigureAwait(false);
        if (!periods.Enough)
        {
            throw new WorkflowException(CalendarShort);
        }

        // The schedule of the application's own terms: the same function as the preview, so what was shown is what is built.
        var schedule = LoanSchedule.Build(loan.Principal, loan.InterestMethod ?? LoanMethods.None, loan.InterestRate, loan.Installments, LoanSchedule.PeriodsPerYear(context.Frequency!), context.Decimals, context.Mode);
        for (var i = 0; i < schedule.Count; i++)
        {
            db.Add(new LoanInstallment
            {
                Id = Guid.CreateVersion7(now), CompanyId = loan.CompanyId, LoanId = loan.Id, Seq = schedule[i].Seq, PayPeriodId = periods.Periods[i].Id,
                PrincipalAmount = schedule[i].Principal, InterestAmount = schedule[i].Interest, Status = "scheduled",
                CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
            });
        }

        loan.StartPeriodId = periods.Periods[0].Id;
        loan.Status = "approved";
        EmploymentApplier.Bump(loan, now, actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
