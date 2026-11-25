using System.Data.Common;
using System.Globalization;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Access;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>A repayment received outside payroll, held for a second person (sealed with the customer's data key: it holds an amount).</summary>
internal sealed record LoanReceiptPlan(Guid LoanId, Guid CompanyId, decimal Amount, DateOnly Date, string Reference, bool Full, string? Reason);

/// <summary>
/// The rules of a repayment received outside payroll (slice S1.41 Build item 5; FR-HRM-LON-03). It takes principal off the loan: the ledger gets an
/// <c>early_settlement</c> entry, and the installments not yet recovered are settled from the last backwards (one fully covered becomes settled early and its interest is not
/// charged; the last one partly covered gets a smaller principal). Nothing is allowed while an installment is held by a calculated run that is not locked yet: the run would
/// recover it again (the receipt waits for the lock). A full settlement is the whole outstanding principal. Ruling for the product owner: R-182.
/// </summary>
internal static class LoanReceipts
{
    /// <summary>The amount to take (the whole balance for a full settlement) or the reason it cannot be taken now.</summary>
    public static async Task<(decimal Amount, string? Problem)> CheckAsync(HrmDbContext db, Loan loan, decimal? amount, bool full, CancellationToken ct)
    {
        if (loan.Status != "active")
        {
            return (0m, LoanService.NotActive);
        }

        var balance = await LoanLedger.BalanceAsync(db, loan.Id, ct).ConfigureAwait(false);
        if (balance <= 0m)
        {
            return (0m, LoanService.ReceiptNothing);
        }

        var open = await db.Set<LoanInstallment>().AsNoTracking().Where(i => i.LoanId == loan.Id && (i.Status == "scheduled" || i.Status == "skipped")).ToListAsync(ct).ConfigureAwait(false);
        if (open.Any(i => i.PayResultLineId is not null))
        {
            return (0m, LoanService.ReceiptRunOpen);
        }

        var take = full ? balance : amount ?? 0m;
        if (take <= 0m)
        {
            return (0m, LoanService.ReceiptAmount);
        }

        if (open.Sum(i => i.PrincipalAmount) != balance)
        {
            return (0m, LoanService.LedgerMismatch); // the schedule and the ledger disagree: the reconciliation report lists it; nothing is booked on a loan that does not add up
        }

        return take > balance ? (0m, LoanService.ReceiptTooMuch) : (take, null);
    }

    /// <summary>Writes the ledger entry, settles the installments and, when nothing is outstanding, the loan; in the caller's transaction, after <see cref="CheckAsync"/>.</summary>
    public static async Task ApplyAsync(HrmDbContext db, Loan loan, LoanReceiptPlan plan, Guid requestId, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        db.Add(LoanLedger.Entry(loan, "early_settlement", -plan.Amount, plan.Date, LoanLedger.SourceReceipt, requestId, plan.Reference, now, actor));
        var open = await db.Set<LoanInstallment>().Where(i => i.LoanId == loan.Id && (i.Status == "scheduled" || i.Status == "skipped")).OrderByDescending(i => i.Seq).ToListAsync(ct).ConfigureAwait(false);
        var left = plan.Amount;
        foreach (var installment in open)
        {
            if (left <= 0m)
            {
                break;
            }

            if (left >= installment.PrincipalAmount)
            {
                left -= installment.PrincipalAmount;
                installment.InterestAmount = 0m;
                installment.Status = "settled_early";
            }
            else
            {
                installment.PrincipalAmount -= left;
                left = 0m;
            }

            EmploymentApplier.Bump(installment, now, actor);
        }

        var balance = await LoanLedger.BalanceAsync(db, loan.Id, ct).ConfigureAwait(false) - plan.Amount;
        if (balance == 0m)
        {
            loan.Status = "settled";
            EmploymentApplier.Bump(loan, now, actor);
        }

        db.Add(LoanLedger.Event(LoanEvents.InstallmentRecovered, loan, new
        {
            loanId = loan.Id, loanNo = loan.LoanNo, companyId = loan.CompanyId, employmentId = loan.EmploymentId, kind = "receipt", principalAmount = plan.Amount, interestAmount = 0m,
            currencyCode = loan.CurrencyCode, payRunId = (Guid?)null, installmentId = (Guid?)null, txnDate = LoanLedger.Iso(plan.Date), reference = plan.Reference, settled = balance == 0m,
        }, now, actor));
    }
}

/// <summary>
/// Applies a held repayment when a second person approves it (slice S1.41 Build item 5, FR-HRM-LON-03, FR-XM-32: the kernel refuses the requester; the borrower never approves a
/// repayment of their own loan). The approver needs <c>hrm.loans.receipt_approve</c>, the loan inside their data scope and the salary field class (they decide on an amount).
/// Everything is checked again on the approval's transaction, so a receipt that went stale (the loan moved, a run holds an installment) is void and changes nothing.
/// </summary>
internal sealed class LoanReceiptHandler(IServiceProvider services, IDataScopeFilter scope, IClock clock, ICurrentActor actor, IFieldAccess fields, IFieldEncryptorProvider keys) : IHeldChangeHandler
{
    public string Kind => LoanEvents.ReceiptKind;

    public string ApprovePermission => LoanPermissions.ReceiptApprove;

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await ReadAsync(change, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return false;
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var loan = await db.Set<Loan>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == plan.LoanId, cancellationToken).ConfigureAwait(false);
        return loan is not null && await scope.AllowsAsync(loan, LoanAccess.Scope, "read", new DbContextScopeSource(db), cancellationToken).ConfigureAwait(false);
    }

    public Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) => RunAsync(change, transaction, apply: false, cancellationToken);

    public Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) => RunAsync(change, transaction, apply: true, cancellationToken);

    private async Task<HeldChangeProblem?> RunAsync(HeldChange change, DbTransaction transaction, bool apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(transaction);
        if (await ReadAsync(change, ct).ConfigureAwait(false) is not { } plan)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, ct).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);
        if (apply)
        {
            await LoanLedger.LockAsync(db, plan.LoanId, ct).ConfigureAwait(false);
        }

        var loan = await db.Set<Loan>().FirstOrDefaultAsync(l => l.Id == plan.LoanId && l.CompanyId == plan.CompanyId, ct).ConfigureAwait(false);
        if (loan is null || !await scope.AllowsAsync(loan, LoanAccess.Scope, "receipt_approve", source, ct).ConfigureAwait(false))
        {
            return new HeldChangeProblem("errors.data.not_found");
        }

        // The approver decides on an amount, so they must be able to read it (GHR-06).
        var access = (await (await fields.PlanAsync(LoanAccess.Fields, LoanAccess.Scope, "read", source, ct).ConfigureAwait(false)).ResolveAsync([loan], source, ct).ConfigureAwait(false))[0];
        if (access.Of("principal") < FieldAccessLevel.ReadOnly)
        {
            return new HeldChangeProblem("errors.hrm.loan.approver_cannot_see_amounts", Forbidden: true);
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == loan.EmploymentId, ct).ConfigureAwait(false);
        if (employment.UserRef is { } user && user == actor.UserId)
        {
            return new HeldChangeProblem(LoanService.OwnLoan, Forbidden: true);
        }

        var (amount, problem) = await LoanReceipts.CheckAsync(db, loan, plan.Full ? null : plan.Amount, plan.Full, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return new HeldChangeProblem(problem);
        }

        if (plan.Full && amount != plan.Amount)
        {
            return new HeldChangeProblem(LoanService.ReceiptTooMuch); // the balance moved since the settlement was asked for: ask again
        }

        if (!apply)
        {
            return null;
        }

        await LoanReceipts.ApplyAsync(db, loan, plan with { Amount = amount }, change.RequestId, clock.UtcNow, actor.UserId, ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            return new HeldChangeProblem(Erp.Hrm.Infrastructure.People.PersonService.Refusal(pg).Error ?? "errors.data.invalid_request");
        }

        return null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await ReadAsync(change, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var who = await (from l in db.Set<Loan>().AsNoTracking()
                         join e in db.Set<Employment>().AsNoTracking() on l.EmploymentId equals e.Id
                         join p in db.Set<Person>().AsNoTracking() on e.PersonId equals p.Id
                         where l.Id == plan.LoanId
                         select new { l.LoanNo, l.CurrencyCode, l.Principal, e.EmployeeNo, Person = p }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var subject = who is null ? null : new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = $"{who.EmployeeNo} {Erp.Hrm.Infrastructure.People.PersonService.DisplayName(who.Person)}",
            ["ur"] = $"{who.EmployeeNo} {who.Person.NameLocal ?? Erp.Hrm.Infrastructure.People.PersonService.DisplayName(who.Person)}",
        };
        var details = new List<HeldChangeDetail>
        {
            new("hrm.loan.receipt.detail.loan", "text", Text: who?.LoanNo ?? "?"),
            new("hrm.loan.receipt.detail.amount", "text", Text: plan.Amount.ToString("0.####", CultureInfo.InvariantCulture) + " " + who?.CurrencyCode),
            new("hrm.loan.receipt.detail.kind", "key", Text: plan.Full ? "hrm.loan.receipt.full" : "hrm.loan.receipt.extra"),
            new("hrm.loan.receipt.detail.date", "date", Date: plan.Date),
            new("hrm.loan.receipt.detail.reference", "text", Text: plan.Reference),
        };
        return new HeldChangeDescription(subject, details);
    }

    private async Task<LoanReceiptPlan?> ReadAsync(HeldChange change, CancellationToken ct) =>
        PayHeldChanges.Open<LoanReceiptPlan>(await keys.GetAsync(ct).ConfigureAwait(false), change.Payload);
}
