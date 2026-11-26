using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// The loan ledger's writes, shared by disbursement, the posting of a locked run and the receipts (slice S1.41 Build items 3 to 6; FR-HRM-LON-03, LON-07). The ledger only
/// grows (the database refuses to change or delete an entry); its balance is the sum of its amounts: the disbursement adds the principal, a recovery, a receipt, a settlement
/// or a write-off takes principal away. Interest is not in the balance: it is the installment's, and Finance gets it in the event of the recovery. D-43: no ledger
/// tables of Finance, only these rows and the outbox.
/// </summary>
internal static class LoanLedger
{
    public const string SourceInstallment = "hrm.loan_installments";
    public const string SourceLoan = "hrm.loans";
    public const string SourceReceipt = "hrm.loan_receipts";

    public static async Task<decimal> BalanceAsync(HrmDbContext db, Guid loanId, CancellationToken ct) =>
        await db.Set<LoanTransaction>().AsNoTracking().Where(t => t.LoanId == loanId).SumAsync(t => (decimal?)t.Amount, ct).ConfigureAwait(false) ?? 0m;

    /// <summary>Takes the loan row for update, so two entries or two decisions on one loan follow each other.</summary>
    public static Task LockAsync(HrmDbContext db, Guid loanId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.loans where id = {loanId} for update", ct);

    public static LoanTransaction Entry(Loan loan, string kind, decimal amount, DateOnly date, string? sourceType, Guid? sourceId, string? reference, DateTimeOffset now, Guid actor) => new()
    {
        Id = Guid.CreateVersion7(now), CompanyId = loan.CompanyId, LoanId = loan.Id, Kind = kind, Amount = amount, TxnDate = date,
        SourceType = sourceType, SourceId = sourceId, Reference = reference, CreatedAt = now, CreatedBy = actor,
    };

    public static OutboxMessage Event(string eventType, Loan loan, object payload, DateTimeOffset now, Guid actor) => new()
    {
        Id = Guid.CreateVersion7(now), OccurredAt = now, EventType = eventType, AggregateType = LoanEvents.AggregateType, AggregateId = loan.Id,
        Payload = JsonSerializer.SerializeToDocument(payload, JsonSerializerOptions.Web), Headers = JsonSerializer.SerializeToDocument(new { actor }, JsonSerializerOptions.Web),
    };

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
