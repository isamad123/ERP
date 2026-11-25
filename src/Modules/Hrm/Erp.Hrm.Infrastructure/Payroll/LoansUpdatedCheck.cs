using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// The readiness item "loans updated" (slice S1.41, FR-HRM-PAY-38, FR-HRM-LON-03): it fails for employees of the pay group who have an approved loan with an installment due in
/// this period or earlier that was never paid out. Payroll takes installments only of loans that were paid out, so such an installment would be left out of the run by mistake.
/// </summary>
internal sealed class LoansUpdatedCheck(HrmDbContext db, IDataScopeFilter scope) : IPayReadinessCheck
{
    public string Code => "loans_updated";

    public async Task<ReadinessOutcome> CheckAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var inGroup = await PayReadinessQueries.InGroupAsync(db, scope, context, cancellationToken).ConfigureAwait(false);
        var unpaid = from l in db.Set<Loan>().AsNoTracking()
                     where l.Status == "approved"
                           && db.Set<LoanInstallment>().Any(i => i.LoanId == l.Id && db.Set<PayPeriod>().Any(p => p.Id == i.PayPeriodId && p.StartDate <= context.End))
                     select l.EmploymentId;
        return await PayReadinessQueries.OutcomeAsync(inGroup.Where(e => unpaid.Contains(e.Id)), cancellationToken).ConfigureAwait(false);
    }
}
