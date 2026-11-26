using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// The loan reports (slice S1.41 Build items 5 and 6; FR-HRM-LON-05, LON-07, GHR-06). <b>Outstanding</b>: the open loans in the caller's data scope with what is lent, what is still
/// owed in principal and the next installment. <b>Reconciliation</b>: per company and currency the ledger against disbursed less recovered, and, loan by loan, the ledger balance
/// against the principal of the installments not yet recovered; any difference is listed (there must be none). D-43: with no Finance module this is the report and the outbox
/// events are the feed; the control account of Finance is compared with it once Finance exists. Amounts are salary-class data: they are left out for a caller without the field class.
/// </summary>
internal sealed class LoanReportService(HrmDbContext db, IPermissionChecker permissions, IDataScopeFilter scope, IFieldAccess fields) : ILoanReportService
{
    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> OutstandingAsync(Guid? companyId, int limit, int offset, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LoanPermissions.ReportsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var query = db.Set<Loan>().AsNoTracking().Where(l => l.Status == "active" || l.Status == "approved");
        if (companyId is { } company)
        {
            query = query.Where(l => l.CompanyId == company);
        }

        query = await scope.ApplyAsync(query, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await (from l in query
                          join t in db.Set<LoanType>().AsNoTracking() on l.LoanTypeId equals t.Id
                          join e in db.Set<Employment>().AsNoTracking() on l.EmploymentId equals e.Id
                          orderby l.LoanNo
                          select new
                          {
                              Loan = l, TypeCode = t.Code, t.Kind, e.EmployeeNo,
                              Balance = db.Set<LoanTransaction>().Where(x => x.LoanId == l.Id).Sum(x => (decimal?)x.Amount) ?? 0m,
                              Repayable = db.Set<LoanInstallment>().Where(i => i.LoanId == l.Id && (i.Status == "scheduled" || i.Status == "skipped")).Sum(i => (decimal?)(i.PrincipalAmount + i.InterestAmount)) ?? 0m,
                              Left = db.Set<LoanInstallment>().Count(i => i.LoanId == l.Id && (i.Status == "scheduled" || i.Status == "skipped")),
                              Next = db.Set<LoanInstallment>().Where(i => i.LoanId == l.Id && (i.Status == "scheduled" || i.Status == "skipped"))
                                  .Join(db.Set<PayPeriod>(), i => i.PayPeriodId, p => p.Id, (i, p) => (DateOnly?)p.StartDate).Min(),
                          }).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = await fields.PlanAsync(LoanAccess.Fields, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = await plan.ResolveAsync([.. page.Select(p => p.Loan)], Source, cancellationToken).ConfigureAwait(false);
        var items = page.Select((p, i) =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["loanId"] = p.Loan.Id, ["loanNo"] = p.Loan.LoanNo, ["status"] = p.Loan.Status, ["employmentId"] = p.Loan.EmploymentId, ["employeeNo"] = p.EmployeeNo,
                ["companyId"] = p.Loan.CompanyId, ["typeCode"] = p.TypeCode, ["kind"] = p.Kind, ["currencyCode"] = p.Loan.CurrencyCode, ["installmentsLeft"] = p.Left, ["nextDue"] = p.Next,
                ["disbursedOn"] = p.Loan.DisbursedOn,
            };
            if (access[i].Of("principal") >= FieldAccessLevel.ReadOnly)
            {
                row["principal"] = p.Loan.Principal;
                row["balance"] = p.Balance;
                row["repayable"] = p.Repayable;
            }

            return row;
        }).ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new { total, items });
    }

    public async Task<PeopleResult> ReconciliationAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LoanPermissions.ReportsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var loans = db.Set<Loan>().AsNoTracking().Where(l => l.Status != "applied" && l.Status != "rejected" && l.Status != "cancelled");
        if (companyId is { } company)
        {
            loans = loans.Where(l => l.CompanyId == company);
        }

        loans = await scope.ApplyAsync(loans, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var map = await fields.ForAsync(LoanAccess.Fields, "read", companyId, cancellationToken).ConfigureAwait(false);
        var perLoan = await (from l in loans
                             select new
                             {
                                 l.Id, l.LoanNo, l.CompanyId, l.CurrencyCode, l.Status,
                                 Disbursed = db.Set<LoanTransaction>().Where(t => t.LoanId == l.Id && t.Kind == "disbursement").Sum(t => (decimal?)t.Amount) ?? 0m,
                                 Recovered = -(db.Set<LoanTransaction>().Where(t => t.LoanId == l.Id && t.Kind != "disbursement" && t.Amount < 0m).Sum(t => (decimal?)t.Amount) ?? 0m),
                                 Balance = db.Set<LoanTransaction>().Where(t => t.LoanId == l.Id).Sum(t => (decimal?)t.Amount) ?? 0m,
                                 Open = db.Set<LoanInstallment>().Where(i => i.LoanId == l.Id && (i.Status == "scheduled" || i.Status == "skipped")).Sum(i => (decimal?)i.PrincipalAmount) ?? 0m,
                             }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var showAmounts = map.Of("principal") >= FieldAccessLevel.ReadOnly;
        var companies = perLoan.GroupBy(x => (x.CompanyId, x.CurrencyCode)).OrderBy(g => g.Key.CompanyId).Select(g =>
        {
            // A loan not paid out yet has no ledger and no receivable; the schedule is compared with the ledger only for loans that were.
            var paid = g.Where(x => x.Disbursed > 0m).ToList();
            var differences = paid.Where(x => x.Balance != (x.Status == "active" ? x.Open : 0m)).ToList();
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["companyId"] = g.Key.CompanyId, ["currencyCode"] = g.Key.CurrencyCode, ["loans"] = paid.Count, ["differenceCount"] = differences.Count, ["reconciles"] = differences.Count == 0,
            };
            if (showAmounts)
            {
                var disbursed = paid.Sum(x => x.Disbursed);
                var recovered = paid.Sum(x => x.Recovered);
                row["disbursed"] = disbursed;
                row["recovered"] = recovered;
                row["outstanding"] = paid.Sum(x => x.Balance);
                row["expectedOutstanding"] = disbursed - recovered;
                row["scheduledPrincipalOutstanding"] = paid.Where(x => x.Status == "active").Sum(x => x.Open);
                row["differences"] = differences.Select(x => new { loanId = x.Id, x.LoanNo, ledgerBalance = x.Balance, scheduledPrincipal = x.Open, difference = x.Balance - x.Open }).ToList();
            }

            return row;
        }).ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new { companies });
    }
}
