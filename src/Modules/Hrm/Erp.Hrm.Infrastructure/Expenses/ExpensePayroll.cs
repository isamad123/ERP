using System.Data.Common;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Payroll.Engine;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Expenses;

/// <summary>
/// The payroll input of approved expense claims (slice S1.42 Build item 5; FR-HRM-EXP-05, FR-HRM-PAY-07): the pending input of source
/// <c>expense_claim</c> that approval wrote for the period (and the one this run already used, so a second calculation of the run reads it again), as a
/// component amount of the claim's reimbursement component. A saved result marks the input <c>consumed</c> by the run. The claim stays approved with
/// payment status <c>pending</c> until the pay run that pays it answers through <see cref="IExpensePaymentReturn"/> (S1.38). The generic provider of
/// one-time inputs leaves this source to this provider.
/// </summary>
internal sealed class ExpenseInputProvider(HrmDbContext db, IClock clock) : IPayInputProvider
{
    public const string ProviderCode = "expenses";

    public string Code => ProviderCode;

    public async Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.EmploymentIds.ToList();
        var rows = await db.Set<PayInput>().AsNoTracking()
            .Where(i => i.SourceType == ExpenseEvents.PayInputSource && ids.Contains(i.EmploymentId) && i.PayPeriodId == request.PayPeriodId
                        && (i.Status == "pending" || (i.Status == "consumed" && i.ConsumedByRunId == request.PayRunId)))
            .OrderBy(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(i => new PayInputItem(i.EmploymentId, ProviderCode, ExpenseEvents.PayInputSource, i.Id, ComponentId: i.ComponentId, Amount: i.Amount, Units: i.Units))];
    }

    public async Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        var ids = consumed.Items.Where(c => c.Item.Provider == ProviderCode && c.Item.SourceId is not null).Select(c => c.Item.SourceId!.Value).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // The rows are taken for update: a cancel that came in after the batch read them makes the batch roll back and the employee is calculated again.
        var now = clock.UtcNow;
        var rows = await db.Set<PayInput>().FromSqlInterpolated($"select * from hrm.pay_inputs where id = any({ids.ToArray()}) for update").ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count != ids.Count || rows.Any(r => !(r.Status == "pending" || (r.Status == "consumed" && r.ConsumedByRunId == consumed.PayRunId))))
        {
            throw new PayInputsChangedException();
        }

        foreach (var row in rows.Where(r => r.Status == "pending"))
        {
            row.Status = "consumed";
            row.ConsumedByRunId = consumed.PayRunId;
            EmploymentApplier.Bump(row, now, row.UpdatedBy); // the run records who calculated; the row keeps the person who made it
        }
    }
}

/// <summary>
/// The payment's answer to a claim (slice S1.42 Build item 5; FR-HRM-EXP-06): paid marks the approved claim <c>reimbursed</c> with the payment's reference;
/// failed keeps it approved with payment status <c>failed</c> so it can be paid again. A claim that is not approved, or that was already paid, is refused;
/// the same answer twice changes nothing. Called by the pay run that pays it (S1.38) and by the payment batch (S1.40a) in the transaction of the payment.
/// </summary>
internal sealed class ExpensePaymentReturnService(HrmDbContext db, IClock clock) : IExpensePaymentReturn
{
    public async Task<bool> RecordAsync(ExpensePaymentReturn answer, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Status is not ("paid" or "failed"))
        {
            return false;
        }

        if (transaction is not null && db.Database.CurrentTransaction is null)
        {
            db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
            await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        }

        var claim = await db.Set<ExpenseClaim>().FirstOrDefaultAsync(c => c.Id == answer.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null || claim.Status is not ("approved" or "reimbursed"))
        {
            return false;
        }

        if (claim.PaymentStatus == answer.Status && (answer.Status == "failed" || claim.Status == "reimbursed"))
        {
            return true; // the same answer again
        }

        if (claim.Status == "reimbursed")
        {
            return false; // paid already: a later failure answer does not undo it
        }

        claim.PaymentStatus = answer.Status;
        claim.PaymentReference = answer.Reference;
        if (answer.Status == "paid")
        {
            claim.Status = "reimbursed";
            claim.ReimbursedAt = answer.At;
        }

        EmploymentApplier.Bump(claim, clock.UtcNow, answer.By ?? claim.UpdatedBy);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Claims of an employment that are not finished (FR-XM-29): drafts, and submitted or approved claims not yet paid. Amounts are not part of the answer (GHR-06).</summary>
internal sealed class ExpenseOpenClaimsReader(HrmDbContext db) : IExpenseOpenClaims
{
    public async Task<IReadOnlyList<OpenExpenseClaim>> OpenForAsync(Guid employmentId, CancellationToken cancellationToken)
    {
        var rows = await db.Set<ExpenseClaim>().AsNoTracking()
            .Where(c => c.EmploymentId == employmentId && (c.Status == "draft" || c.Status == "submitted" || (c.Status == "approved" && c.PaymentStatus != "paid")))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.ClaimNo).Select(c => new { c.Id, c.ClaimNo, c.Status, c.PaymentStatus }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new OpenExpenseClaim(r.Id, r.ClaimNo, r.Status, r.PaymentStatus))];
    }
}
