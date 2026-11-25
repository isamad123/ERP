using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Loan installments as payroll inputs (slice S1.41 Build item 4, FR-HRM-LON-03, FR-HRM-PAY-07): the installments of active loans that fall due in the run's period, and those an earlier
/// run did not take (skipped, or whose period passed with no run for the employee), each as an amount of the loan type's deduction component. Only a regular run recovers loans
/// (a final settlement recovers what is outstanding itself, S1.43). The answer depends only on stored data (FR-HRM-PAY-31). When a result is saved the result line is stored on
/// each installment it used; the installment becomes <c>deducted</c> and the ledger gets its recovery only when the run locks (<see cref="LoanRecoveryPosting"/>), so a
/// run that is recalculated or cancelled leaves the loan as it was. How much of pay may go to loans is the customer's setting <c>hrm.loan.max_recovery_percent</c>: it warns on the loan form
/// and never changes what payroll recovers (R-181).
/// </summary>
internal sealed class LoanInputProvider(HrmDbContext db, IClock clock, ICurrentActor actor) : IPayInputProvider
{
    public const string ProviderCode = "loans";
    public const string Source = "hrm.loan_installments";

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await db.Set<PayRun>().AsNoTracking().Where(r => r.Id == request.PayRunId).Select(r => r.Kind).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) != "regular")
        {
            return [];
        }

        var ids = request.EmploymentIds.ToList();
        var due = await (from i in db.Set<LoanInstallment>().AsNoTracking()
                         join l in db.Set<Loan>().AsNoTracking() on i.LoanId equals l.Id
                         join t in db.Set<LoanType>().AsNoTracking() on l.LoanTypeId equals t.Id
                         join p in db.Set<PayPeriod>().AsNoTracking() on i.PayPeriodId equals p.Id
                         where ids.Contains(l.EmploymentId) && l.Status == "active" && (i.Status == "scheduled" || i.Status == "skipped")
                               && (i.PayPeriodId == request.PayPeriodId || p.EndDate < request.PeriodStart)
                         orderby i.Id
                         select new { i.Id, l.EmploymentId, t.DeductionComponentId, i.PrincipalAmount, i.InterestAmount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. due.Select(d => new PayInputItem(d.EmploymentId, ProviderCode, Source, d.Id, ComponentId: d.DeductionComponentId, Amount: d.PrincipalAmount + d.InterestAmount))];
    }

    public async Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        var lines = consumed.Items.Where(c => c.Item.Provider == ProviderCode && c.Item.SourceId is not null && c.ResultLineId is not null)
            .ToDictionary(c => c.Item.SourceId!.Value, c => c.ResultLineId!.Value);
        if (lines.Count == 0)
        {
            return;
        }

        var keys = lines.Keys.ToList();
        var now = clock.UtcNow;
        foreach (var row in await db.Set<LoanInstallment>().Where(i => keys.Contains(i.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (row.Status is "scheduled" or "skipped" && row.PayResultLineId != lines[row.Id])
            {
                row.PayResultLineId = lines[row.Id];
                EmploymentApplier.Bump(row, now, actor.UserId);
            }
        }
    }
}

/// <summary>
/// Posts the loan ledger when a pay run locks (slice S1.41 Build item 4; FR-HRM-LON-03, LON-07; the data design: recovery entries are written only when the run locks).
/// For each installment whose result line is in the run: the ledger gets a <c>recovery</c> of its principal (negative; the unique key makes a second posting of the same
/// installment impossible), the installment becomes <c>deducted</c> and Finance gets <c>hrm.loan.installment_recovered</c>; a loan whose balance reaches zero is settled. An installment of
/// the run's period that no line took (the employee had no pay, or no result) becomes <c>skipped</c>: it carries forward and is recovered by the next regular run (Q4). S1.38's lock
/// calls this in the locking transaction; without a transaction of the caller it opens its own.
/// </summary>
internal sealed class LoanRecoveryPosting(HrmDbContext db, IClock clock) : ILoanRecoveryPosting
{
    public const string NotLocked = "errors.hrm.loan.run_not_locked";
    private static readonly string[] Final = ["locked", "paid", "posted", "closed"];

    public async Task<LoanPostingResult> PostRunAsync(Guid payRunId, CancellationToken cancellationToken)
    {
        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == payRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The pay run does not exist.");
        if (!Final.Contains(run.Status))
        {
            throw new InvalidOperationException(NotLocked);
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return await PostAsync(run, cancellationToken).ConfigureAwait(false);
        }

        LoanPostingResult? result = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            result = await PostAsync(run, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result!;
    }

    private async Task<LoanPostingResult> PostAsync(PayRun run, CancellationToken ct)
    {
        if (run.Kind != "regular")
        {
            return new LoanPostingResult(0, 0, 0);
        }

        var now = clock.UtcNow;
        var actor = run.UpdatedBy; // the one who locked the run (the posting is part of the lock)
        var date = await db.Set<PayPeriod>().AsNoTracking().Where(p => p.Id == run.PayPeriodId).Select(p => p.PaymentDate).FirstAsync(ct).ConfigureAwait(false);
        // No AsNoTracking anywhere in this query: tracking is a setting of the whole query, and the installments are changed below.
        var lineIds = db.Set<PayResultLine>().Where(l => db.Set<PayResult>().Any(r => r.Id == l.ResultId && r.PayRunId == run.Id)).Select(l => l.Id);
        var taken = await db.Set<LoanInstallment>().Where(i => (i.Status == "scheduled" || i.Status == "skipped") && i.PayResultLineId != null && lineIds.Contains(i.PayResultLineId.Value))
            .OrderBy(i => i.LoanId).ThenBy(i => i.Seq).ToListAsync(ct).ConfigureAwait(false);
        int recovered = 0, settled = 0;
        foreach (var group in taken.GroupBy(i => i.LoanId))
        {
            await LoanLedger.LockAsync(db, group.Key, ct).ConfigureAwait(false);
            var loan = await db.Set<Loan>().FirstAsync(l => l.Id == group.Key, ct).ConfigureAwait(false);
            foreach (var i in group)
            {
                if (i.PrincipalAmount > 0m)
                {
                    db.Add(LoanLedger.Entry(loan, "recovery", -i.PrincipalAmount, date, LoanLedger.SourceInstallment, i.Id, null, now, actor));
                }

                i.Status = "deducted";
                EmploymentApplier.Bump(i, now, actor);
                db.Add(LoanLedger.Event(LoanEvents.InstallmentRecovered, loan, new
                {
                    loanId = loan.Id, loanNo = loan.LoanNo, companyId = loan.CompanyId, employmentId = loan.EmploymentId, kind = "payroll", principalAmount = i.PrincipalAmount,
                    interestAmount = i.InterestAmount, currencyCode = loan.CurrencyCode, payRunId = run.Id, installmentId = i.Id, txnDate = LoanLedger.Iso(date),
                }, now, actor));
                recovered++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (await LoanLedger.BalanceAsync(db, loan.Id, ct).ConfigureAwait(false) == 0m && loan.Status == "active")
            {
                loan.Status = "settled";
                EmploymentApplier.Bump(loan, now, actor);
                settled++;
            }
        }

        // What the run's period holds that no line took waits for the next regular run.
        var left = await (from i in db.Set<LoanInstallment>()
                          join l in db.Set<Loan>() on i.LoanId equals l.Id
                          where i.PayPeriodId == run.PayPeriodId && i.Status == "scheduled" && i.PayResultLineId == null && l.Status == "active"
                          select i).ToListAsync(ct).ConfigureAwait(false);
        foreach (var i in left)
        {
            i.Status = "skipped";
            EmploymentApplier.Bump(i, now, actor);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new LoanPostingResult(recovered, left.Count, settled);
    }
}

/// <summary>The loans an employee still owes (slice S1.41 Build item 5; FR-HRM-LON-05, FR-XM-29): the clearance lists them and the final settlement is blocked by them.</summary>
internal sealed class OutstandingLoans(HrmDbContext db) : IOutstandingLoans
{
    public async Task<IReadOnlyList<OutstandingLoan>> ForAsync(IReadOnlyCollection<Guid> employmentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employmentIds);
        var ids = employmentIds.ToList();
        var loans = await (from l in db.Set<Loan>().AsNoTracking()
                           join t in db.Set<LoanType>().AsNoTracking() on l.LoanTypeId equals t.Id
                           where ids.Contains(l.EmploymentId) && (l.Status == "approved" || l.Status == "active")
                           orderby l.LoanNo
                           select new { l.Id, l.LoanNo, l.EmploymentId, l.Status, l.CurrencyCode, TypeCode = t.Code, t.Kind }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var loanIds = loans.Select(l => l.Id).ToList();
        var balances = await db.Set<LoanTransaction>().AsNoTracking().Where(t => loanIds.Contains(t.LoanId)).GroupBy(t => t.LoanId)
            .Select(g => new { LoanId = g.Key, Balance = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.LoanId, x => x.Balance, cancellationToken).ConfigureAwait(false);
        var open = await (from i in db.Set<LoanInstallment>().AsNoTracking()
                          join p in db.Set<PayPeriod>().AsNoTracking() on i.PayPeriodId equals p.Id
                          where loanIds.Contains(i.LoanId) && (i.Status == "scheduled" || i.Status == "skipped")
                          select new { i.LoanId, i.PrincipalAmount, i.InterestAmount, p.StartDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<OutstandingLoan>();
        foreach (var l in loans)
        {
            var mine = open.Where(o => o.LoanId == l.Id).ToList();
            var balance = balances.GetValueOrDefault(l.Id);
            if (l.Status == "active" && balance <= 0m && mine.Count == 0)
            {
                continue;
            }

            list.Add(new OutstandingLoan(l.Id, l.LoanNo, l.EmploymentId, l.TypeCode, l.Kind, l.CurrencyCode, balance, mine.Sum(o => o.PrincipalAmount + o.InterestAmount), mine.Count,
                mine.Count == 0 ? null : mine.Min(o => o.StartDate)));
        }

        return list;
    }
}
