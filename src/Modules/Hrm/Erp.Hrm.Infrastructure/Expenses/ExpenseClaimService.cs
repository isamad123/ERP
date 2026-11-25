using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Expenses;

/// <summary>
/// Expense claims (slice S1.42 Build items 2 to 4; FR-HRM-EXP-02, EXP-03, EXP-05, GHR-06, FR-XM-32). A claim is made for the caller's own employment or,
/// when the data scope of their role allows, for another person's (the row's <c>created_by</c> is then the acting user). Lines are converted into the
/// claim currency (the pay group's) at the customer's own rate, which is kept on the line; policy flags are worked out at every save and again at
/// submit; a claim is decided by the workflow route (manager, then finance when the total reaches the threshold) and never by the people it concerns.
/// Amounts are restricted data (GHR-06): the claimant, the approvers and holders of salary-class access see them, nobody else.
/// </summary>
internal sealed partial class ExpenseClaimService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    EmploymentApplier applier,
    ExpensePolicyReader policy,
    INumberAllocator numbers,
    IRecordFiles files,
    IWorkflowService workflow,
    IWorkflowQueries approvals,
    ISettings settings) : IExpenseClaimService
{
    public const string NotDraft = "errors.hrm.expense.not_draft";
    public const string NoLines = "errors.hrm.expense.no_lines";
    public const string NoRate = "errors.hrm.expense.no_rate";
    public const string NoCurrency = "errors.hrm.expense.no_currency";
    public const string CategoryUnknown = "errors.hrm.expense.category_unknown";
    public const string ReceiptUnknown = "errors.hrm.expense.receipt_unknown";
    public const string BlockedByFlags = "errors.hrm.expense.blocked_by_flags";
    public const string NoComponent = "errors.hrm.expense.no_component";
    public const string MixedComponents = "errors.hrm.expense.mixed_components";
    public const string ComponentNotAllowed = "errors.hrm.expense.component_not_allowed";
    public const string EmploymentClosed = "errors.hrm.expense.employment_closed";
    public const string UseWithdraw = "errors.hrm.expense.use_withdraw";
    public const string NotWaiting = "errors.hrm.expense.not_waiting";
    public const string NotApprover = "errors.hrm.expense.not_approver";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];
    private static readonly string[] Ways = [ExpenseReimbursement.Payroll, ExpenseReimbursement.PaymentBatch];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CreateAsync(ExpenseClaimInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(ExpensePermissions.Create, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, failure) = await EmploymentAsync(input.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return failure!;
        }

        var header = CheckHeader(input);
        if (header.Count > 0)
        {
            return PeopleResult.Fields(header);
        }

        return await InTransactionAsync(employment.Id, async () =>
        {
            var now = clock.UtcNow;
            var today = await applier.TodayAsync(employment.CompanyId, now, cancellationToken).ConfigureAwait(false);
            var currency = await ClaimCurrencyAsync(employment, today, cancellationToken).ConfigureAwait(false);
            if (currency is null)
            {
                return PeopleResult.Conflict(NoCurrency);
            }

            var branch = await db.Set<EmploymentAssignment>().AsNoTracking()
                .Where(a => a.EmploymentId == employment.Id && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today))
                .Select(a => (Guid?)a.BranchId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var claim = new ExpenseClaim
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = employment.CompanyId,
                BranchId = branch,
                EmploymentId = employment.Id,
                CurrencyCode = currency,
                ReimburseVia = input.ReimburseVia!,
                Title = Trimmed(input.Title),
                Status = "draft",
                PaymentStatus = "none",
                ClaimNo = string.Empty,
                CreatedAt = now,
                CreatedBy = actor.UserId,
                UpdatedAt = now,
                UpdatedBy = actor.UserId,
                RowVersion = 1,
            };
            if (!await scope.AllowsAsync(claim, ExpenseClaimAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            // Numbered when first saved (FR-KRN-NUM, FR-XM-06): gap-free in this transaction; a rollback gives the number back.
            claim.ClaimNo = await numbers.AssignAsync(new NumberRequest(ExpenseEvents.NumberType, claim.CompanyId, claim.BranchId, today), db.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            db.Add(claim);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var saved = await ReplaceLinesAsync(claim, employment, [], input.Lines ?? [], today, cancellationToken).ConfigureAwait(false);
            if (saved is not null)
            {
                return saved;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return input.Submit
                ? await SendAsync(claim, employment, today, cancellationToken).ConfigureAwait(false)
                : new PeopleResult(PeopleOutcome.Created, Body: await ViewAsync(claim, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid claimId, ExpenseClaimInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(ExpensePermissions.Create, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var header = CheckHeader(input);
        var peek = await db.Set<ExpenseClaim>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
        if (peek is null || !await scope.AllowsAsync(peek, ExpenseClaimAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (header.Count > 0)
        {
            return PeopleResult.Fields(header);
        }

        return await InTransactionAsync(peek.EmploymentId, async () =>
        {
            var claim = await db.Set<ExpenseClaim>().FirstAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
            if (claim.Status != "draft")
            {
                return PeopleResult.Conflict(NotDraft);
            }

            if (input.RowVersion is { } v && v != claim.RowVersion)
            {
                return PeopleResult.Conflict(PersonService.Concurrency);
            }

            var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == claim.EmploymentId, cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNow;
            var today = await applier.TodayAsync(claim.CompanyId, now, cancellationToken).ConfigureAwait(false);
            var existing = await db.Set<ExpenseClaimLine>().Where(l => l.ClaimId == claim.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            claim.ReimburseVia = input.ReimburseVia!;
            claim.Title = Trimmed(input.Title);
            var saved = await ReplaceLinesAsync(claim, employment, existing, input.Lines ?? [], today, cancellationToken).ConfigureAwait(false);
            if (saved is not null)
            {
                return saved;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return input.Submit
                ? await SendAsync(claim, employment, today, cancellationToken).ConfigureAwait(false)
                : new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(claim, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> SubmitAsync(Guid claimId, long? rowVersion, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(ExpensePermissions.Create, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var peek = await db.Set<ExpenseClaim>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
        if (peek is null || !await scope.AllowsAsync(peek, ExpenseClaimAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        return await InTransactionAsync(peek.EmploymentId, async () =>
        {
            var claim = await db.Set<ExpenseClaim>().FirstAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
            if (claim.Status != "draft")
            {
                return PeopleResult.Conflict(NotDraft);
            }

            if (rowVersion is { } v && v != claim.RowVersion)
            {
                return PeopleResult.Conflict(PersonService.Concurrency);
            }

            var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == claim.EmploymentId, cancellationToken).ConfigureAwait(false);
            var today = await applier.TodayAsync(claim.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return await SendAsync(claim, employment, today, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> CancelAsync(Guid claimId, long? rowVersion, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(ExpensePermissions.Create, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var claim = await db.Set<ExpenseClaim>().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
        if (claim is null || !await scope.AllowsAsync(claim, ExpenseClaimAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (claim.Status != "draft")
        {
            return PeopleResult.Conflict(claim.Status == "submitted" ? UseWithdraw : NotDraft);
        }

        if (claim.WorkflowInstanceId is not null)
        {
            return PeopleResult.Conflict(UseWithdraw); // a returned claim still has its request: it is withdrawn there
        }

        if (rowVersion is { } v && v != claim.RowVersion)
        {
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        claim.Status = "cancelled";
        EmploymentApplier.Bump(claim, clock.UtcNow, actor.UserId);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { id = claim.Id, status = claim.Status, rowVersion = claim.RowVersion });
    }

    /// <summary>The person the claim is for: the employment named, else the caller's own (exactly one current employment), inside the caller's data scope.</summary>
    private async Task<(Employment? Employment, PeopleResult? Failure)> EmploymentAsync(Guid? employmentId, CancellationToken ct)
    {
        Employment? employment;
        if (employmentId is { } id && id != Guid.Empty)
        {
            employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        }
        else
        {
            var own = await db.Set<Employment>().AsNoTracking().Where(e => e.UserRef == actor.UserId && CurrentStatuses.Contains(e.Status)).OrderBy(e => e.EmployeeNo).Take(2).ToListAsync(ct).ConfigureAwait(false);
            if (own.Count != 1)
            {
                return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentId"] = [ExpenseRules.Required] }));
            }

            employment = own[0];
        }

        if (employment is null || !await scope.AllowsAsync(new ExpenseClaim { CompanyId = employment.CompanyId, EmploymentId = employment.Id }, ExpenseClaimAccess.Scope, "create", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return CurrentStatuses.Contains(employment.Status) ? (employment, null) : (null, PeopleResult.Conflict(EmploymentClosed));
    }

    private static Dictionary<string, string[]> CheckHeader(ExpenseClaimInput input)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (input.ReimburseVia is null || !Ways.Contains(input.ReimburseVia, StringComparer.Ordinal))
        {
            errors["reimburseVia"] = [input.ReimburseVia is null ? ExpenseRules.Required : ExpenseRules.ReimburseViaInvalid];
        }

        if ((input.Title?.Length ?? 0) > 200)
        {
            errors["title"] = [ExpenseRules.TextTooLong];
        }

        if ((input.Lines?.Count ?? 0) > ExpenseRules.MostLines)
        {
            errors["lines"] = [ExpenseRules.TooManyLines];
        }

        return errors;
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>One transaction under the employment's claim lock (two saves of one person's claims do not interleave); lock and deadlock refusals become a conflict.</summary>
    private async Task<PeopleResult> InTransactionAsync(Guid employmentId, Func<Task<PeopleResult>> work, CancellationToken ct)
    {
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            PeopleResult result = PeopleResult.NotFound;
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.expense_claim:" + employmentId.ToString("D")}, 0))", ct).ConfigureAwait(false);
                result = await work().ConfigureAwait(false);
                if (result.Outcome is PeopleOutcome.Created or PeopleOutcome.Done or PeopleOutcome.Pending)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            return result;
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }
}
