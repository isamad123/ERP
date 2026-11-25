using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Expenses;

/// <summary>An HR context on the deciding connection and transaction, so the claim rows commit (or roll back) with the decision.</summary>
internal static class ExpenseOnTransaction
{
    public static async Task<HrmDbContext> OpenAsync(IServiceProvider services, DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }
}

/// <summary>
/// The end of the approval of an expense claim (document type <c>hrm.expense_claim</c>, slice S1.42 Build items 4 to 6; FR-HRM-EXP-03, EXP-05, EXP-06,
/// FR-XM-32: the kernel never lets the requester or the employee decide), on the deciding transaction. Approved: each line is accepted as the approvers
/// decided it (a line nobody changed is accepted in full), the approved total is kept, and the claim is paid the way it chose: through payroll, one payroll
/// input of the approved total for the first open pay period of the employee's pay group (the payroll provider reads it and marks it consumed), or by a
/// payment batch (payment status pending until S1.40a returns it). The event <c>hrm.expense_claim.approved</c> goes to the outbox with the totals by
/// category and coding (D-43: no Finance tables). A claim whose lines were all rejected ends rejected. Rejected: rejected. Returned: back to draft, with
/// the decisions cleared. Withdrawn or cancelled by an administrator: cancelled. A refusal is a <see cref="WorkflowException"/>, so the decision is
/// rolled back and the approver sees why.
/// </summary>
internal sealed class ExpenseClaimHandler(IServiceProvider services, IClock clock, ICurrentActor actor) : IWorkflowOutcomeHandler
{
    public const string NoPayGroup = "errors.hrm.expense.no_pay_group";
    public const string NoOpenPeriod = "errors.hrm.expense.no_open_period";
    public const string CurrencyMismatch = "errors.hrm.expense.currency_mismatch";
    public const string ComponentGone = "errors.hrm.expense.component_gone";

    private static readonly string[] FinalRunStates = ["locked", "paid", "posted", "closed"];

    public string DocumentType => ExpenseEvents.DocumentType;

    public async Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var db = await ExpenseOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var claim = await db.Set<ExpenseClaim>().FirstOrDefaultAsync(x => x.Id == outcome.DocumentId && x.CompanyId == outcome.CompanyId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowException("errors.hrm.expense.not_found");
        if (claim.Status != "submitted")
        {
            throw new WorkflowException("errors.hrm.expense.not_waiting");
        }

        var now = clock.UtcNow;
        var lines = await db.Set<ExpenseClaimLine>().Where(l => l.ClaimId == claim.Id).OrderBy(l => l.ExpenseDate).ThenBy(l => l.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        switch (outcome.Outcome)
        {
            case WorkflowOutcomes.Approved:
                await ApproveAsync(db, claim, lines, outcome, now, transaction, cancellationToken).ConfigureAwait(false);
                break;
            case WorkflowOutcomes.Returned:
                claim.Status = "draft";
                foreach (var l in lines)
                {
                    l.ApprovedAmount = null;
                    l.DecisionReason = null;
                    l.DecidedBy = null;
                    l.DecidedAt = null;
                    EmploymentApplier.Bump(l, now, actor.UserId);
                }

                break;
            case WorkflowOutcomes.Rejected:
                claim.Status = "rejected";
                claim.ApprovedTotal = 0m;
                break;
            default:
                claim.Status = "cancelled";
                break;
        }

        EmploymentApplier.Bump(claim, now, actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApproveAsync(HrmDbContext db, ExpenseClaim claim, List<ExpenseClaimLine> lines, WorkflowOutcome outcome, DateTimeOffset now, DbTransaction transaction, CancellationToken ct)
    {
        foreach (var l in lines.Where(l => l.ApprovedAmount is null))
        {
            l.ApprovedAmount = l.Amount; // the approvers recorded only the lines they changed
            l.DecidedBy = outcome.DecidedBy;
            l.DecidedAt = now;
        }

        foreach (var l in lines)
        {
            EmploymentApplier.Bump(l, now, actor.UserId);
        }

        var total = lines.Sum(l => l.ApprovedAmount ?? 0m);
        claim.ApprovedTotal = total;
        if (total <= 0m)
        {
            claim.Status = "rejected"; // every line was rejected: nothing to pay and nothing for Finance
            return;
        }

        claim.Status = "approved";
        claim.PaymentStatus = "pending";
        if (claim.ReimburseVia == ExpenseReimbursement.Payroll)
        {
            await WritePayrollInputAsync(db, claim, lines, total, now, ct).ConfigureAwait(false);
        }

        var categories = await db.Set<ExpenseCategory>().AsNoTracking().Where(c => lines.Select(l => l.CategoryId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var byCategory = lines.GroupBy(l => l.CategoryId).Select(g => new
        {
            categoryId = g.Key,
            categoryCode = categories[g.Key].Code,
            taxTreatment = categories[g.Key].TaxTreatment,
            amount = g.Sum(l => l.ApprovedAmount ?? 0m),
        }).OrderBy(x => x.categoryCode, StringComparer.Ordinal).ToList();
        var byCoding = lines.Where(l => (l.ApprovedAmount ?? 0m) > 0m).GroupBy(l => (Project: CodingOf(l, "project"), CostCentre: CodingOf(l, "costCentre")))
            .Select(g => new { project = g.Key.Project, costCentre = g.Key.CostCentre, amount = g.Sum(l => l.ApprovedAmount ?? 0m) })
            .OrderBy(x => x.project, StringComparer.Ordinal).ThenBy(x => x.costCentre, StringComparer.Ordinal).ToList();
        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = ExpenseEvents.ClaimApproved,
            AggregateType = ExpenseEvents.AggregateType,
            AggregateId = claim.Id,
            Payload = JsonSerializer.SerializeToDocument(new Dictionary<string, object?>
            {
                ["claimId"] = claim.Id,
                ["claimNo"] = claim.ClaimNo,
                ["companyId"] = claim.CompanyId,
                ["branchId"] = claim.BranchId,
                ["employmentId"] = claim.EmploymentId,
                ["currencyCode"] = claim.CurrencyCode,
                ["claimedTotal"] = claim.TotalAmount,
                ["approvedTotal"] = total,
                ["reimburseVia"] = claim.ReimburseVia,
                ["byCategory"] = byCategory,
                ["byCoding"] = byCoding,
                ["approvedBy"] = outcome.DecidedBy,
                ["approvedAt"] = now,
            }, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor = actor.UserId }, JsonSerializerOptions.Web),
        });
    }

    private static string? CodingOf(ExpenseClaimLine l, string name) =>
        l.CostAllocation.RootElement.ValueKind == JsonValueKind.Object && l.CostAllocation.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// One payroll input of the approved total, for the first pay period of the employee's pay group that is not closed and has no locked run, whose end is not
    /// before today. The employee has to be in a pay group and the group's currency has to be the claim's; the claim's component must still be usable.
    /// </summary>
    private async Task WritePayrollInputAsync(HrmDbContext db, ExpenseClaim claim, List<ExpenseClaimLine> lines, decimal total, DateTimeOffset now, CancellationToken ct)
    {
        var applier = new EmploymentApplier(db, services.GetRequiredService<Erp.Kernel.Contracts.Settings.ISettings>(), Policies.PolicyResolver.For(db, services));
        var today = await applier.TodayAsync(claim.CompanyId, now, ct).ConfigureAwait(false);
        var groupId = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == claim.EmploymentId && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today) && a.PayGroupId != null)
            .Select(a => a.PayGroupId).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new WorkflowException(NoPayGroup);
        var group = await db.Set<PayGroup>().AsNoTracking().FirstAsync(g => g.Id == groupId, ct).ConfigureAwait(false);
        if (!string.Equals(group.CurrencyCode.Trim(), claim.CurrencyCode, StringComparison.Ordinal))
        {
            throw new WorkflowException(CurrencyMismatch);
        }

        var period = await db.Set<PayPeriod>().AsNoTracking()
            .Where(p => p.PayGroupId == groupId && p.Status != "closed" && p.EndDate >= today
                && !db.Set<PayRun>().Any(r => r.PayPeriodId == p.Id && FinalRunStates.Contains(r.Status)))
            .OrderBy(p => p.StartDate).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new WorkflowException(NoOpenPeriod);
        var componentId = await db.Set<ExpenseCategory>().AsNoTracking().Where(c => c.Id == lines[0].CategoryId).Select(c => c.ReimbursementComponentId).FirstAsync(ct).ConfigureAwait(false) ?? throw new WorkflowException(ComponentGone);
        var component = await db.Set<PayComponent>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId && !c.IsDeleted && c.IsActive, ct).ConfigureAwait(false) ?? throw new WorkflowException(ComponentGone);
        var input = new PayInput
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = claim.CompanyId,
            EmploymentId = claim.EmploymentId,
            PayPeriodId = period.Id,
            ComponentId = component.Id,
            Amount = PayRounding.Internal(total),
            CurrencyCode = claim.CurrencyCode,
            SourceType = ExpenseEvents.PayInputSource,
            SourceId = claim.Id,
            Status = "pending",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        db.Add(input);
        claim.PayInputId = input.Id;
        claim.PayPeriodId = period.Id;
    }
}

/// <summary>
/// Receipt files of an expense claim (slice S1.42 Build item 2; FR-HRM-EXP-02, FR-KRN-DOC-01; owner type <c>hrm.expense_claims</c>, CR-114). The files
/// follow the claim: the person it is for and the person who made it, the people with an open approval task of it, and readers of claims whose data scope
/// covers it may read; the parties and the people who may make claims for this person (data scope) may add or delete while the claim is a draft; sensitive
/// files are for the same people. Nobody else, and nothing in another company's scope.
/// </summary>
internal sealed class ExpenseClaimAttachmentOwner(HrmDbContext db, ICurrentActor actor, IPermissionChecker permissions, IDataScopeFilter scope, IWorkflowQueries approvals) : IAttachmentOwner
{
    public string OwnerType => ExpenseEvents.OwnerType;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var claim = await db.Set<ExpenseClaim>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ownerId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return null;
        }

        var source = new Erp.BuildingBlocks.Infrastructure.Persistence.DbContextScopeSource(db);
        var reader = await permissions.HasPermissionAsync(ExpensePermissions.Read, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(claim, Erp.Hrm.Application.Generated.ExpenseClaimAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false);
        var maker = await permissions.HasPermissionAsync(ExpensePermissions.Create, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(claim, Erp.Hrm.Application.Generated.ExpenseClaimAccess.Scope, "create", source, cancellationToken).ConfigureAwait(false);
        var employee = await db.Set<Employment>().AsNoTracking().Where(x => x.Id == claim.EmploymentId).Select(x => x.UserRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var party = employee == actor.UserId || claim.CreatedBy == actor.UserId;
        var approver = !party && (await approvals.OpenApprovalsOfAsync(actor.UserId, null, cancellationToken).ConfigureAwait(false)).TasksToDecide
            .Any(t => t.DocumentId == claim.Id && t.DocumentType == ExpenseEvents.DocumentType);
        return new AttachmentOwnerAccess(claim.CompanyId, party || reader || approver, (party || maker) && claim.Status == "draft", party || approver || maker || reader);
    }
}
