using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Expenses;

internal sealed partial class ExpenseClaimService
{
    public async Task<PeopleResult> GetAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var claim = await db.Set<ExpenseClaim>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return PeopleResult.NotFound;
        }

        var seat = await SeatAsync(claim, cancellationToken).ConfigureAwait(false);
        if (!seat.Party && !seat.Approver && !seat.Reader)
        {
            return PeopleResult.NotFound; // a claim outside the caller's reach is never confirmed to exist
        }

        return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(claim, cancellationToken, seat).ConfigureAwait(false));
    }

    /// <summary>How the caller stands to a claim: the person it is for or who made it, an approver with an open task, a reader inside the data scope (and whether salary-class amounts are open to them).</summary>
    private sealed record Seat(bool Party, bool Approver, bool Reader, bool AmountsOpen);

    private async Task<Seat> SeatAsync(ExpenseClaim claim, CancellationToken ct)
    {
        var employee = await db.Set<Employment>().AsNoTracking().Where(e => e.Id == claim.EmploymentId).Select(e => e.UserRef).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var party = employee == actor.UserId || claim.CreatedBy == actor.UserId;
        var approver = !party && await ExpenseApprovers.IsApproverAsync(claim, actor.UserId, permissions, scope, approvals, Source, ct).ConfigureAwait(false);
        var reader = await permissions.HasPermissionAsync(ExpensePermissions.Read, ct).ConfigureAwait(false)
            && await scope.AllowsAsync(claim, ExpenseClaimAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var open = party || approver;
        if (!open && reader)
        {
            var plan = await fields.PlanAsync(ExpenseClaimAccess.Fields, ExpenseClaimAccess.Scope, "read", Source, ct).ConfigureAwait(false);
            open = (await plan.ResolveAsync([claim], Source, ct).ConfigureAwait(false))[0].Of("total_amount") >= FieldAccessLevel.ReadOnly;
        }

        return new Seat(party, approver, reader, open);
    }

    /// <summary>The claim as the caller may see it: header, lines with their flags and decisions, and what the caller may do next. Amounts only when open to the caller (GHR-06).</summary>
    private async Task<object> ViewAsync(ExpenseClaim claim, CancellationToken ct, Seat? known = null)
    {
        var seat = known ?? await SeatAsync(claim, ct).ConfigureAwait(false);
        var lines = await db.Set<ExpenseClaimLine>().AsNoTracking().Where(l => l.ClaimId == claim.Id).OrderBy(l => l.ExpenseDate).ThenBy(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
        var categoryIds = lines.Select(l => l.CategoryId).Distinct().ToList();
        var categories = await db.Set<ExpenseCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var employee = await db.Set<Employment>().AsNoTracking().Where(e => e.Id == claim.EmploymentId).Select(e => new { e.EmployeeNo, e.UserRef }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var input = claim.PayInputId is { } pid ? await db.Set<PayInput>().AsNoTracking().Where(i => i.Id == pid).Select(i => i.Status).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var amounts = seat.AmountsOpen;
        var maker = claim.Status == "draft" && (seat.Party || await permissions.HasPermissionAsync(ExpensePermissions.Create, ct).ConfigureAwait(false) && await scope.AllowsAsync(claim, ExpenseClaimAccess.Scope, "create", Source, ct).ConfigureAwait(false));
        var canDecide = claim.Status == "submitted" && seat.Approver && await permissions.HasPermissionAsync(ExpensePermissions.Decide, ct).ConfigureAwait(false);
        return new
        {
            id = claim.Id,
            claimNo = claim.ClaimNo,
            status = claim.Status,
            title = claim.Title,
            employmentId = claim.EmploymentId,
            employeeNo = employee?.EmployeeNo,
            currencyCode = claim.CurrencyCode,
            reimburseVia = claim.ReimburseVia,
            paymentStatus = claim.PaymentStatus,
            paymentReference = claim.PaymentReference,
            payrollInput = input,
            needsFinance = claim.NeedsFinance,
            submittedAt = claim.SubmittedAt,
            reimbursedAt = claim.ReimbursedAt,
            instanceId = claim.WorkflowInstanceId,
            returned = claim.Status == "draft" && claim.WorkflowInstanceId is not null,
            totalAmount = amounts ? claim.TotalAmount : (decimal?)null,
            approvedTotal = amounts ? claim.ApprovedTotal : null,
            amountsHidden = !amounts,
            canEdit = maker,
            canDecide,
            rowVersion = claim.RowVersion,
            lines = lines.Select(l => new
            {
                id = l.Id,
                categoryId = l.CategoryId,
                categoryCode = categories.GetValueOrDefault(l.CategoryId)?.Code,
                categoryName = categories.TryGetValue(l.CategoryId, out var c) ? Names(c.NameI18n) : null,
                taxTreatment = categories.GetValueOrDefault(l.CategoryId)?.TaxTreatment,
                expenseDate = l.ExpenseDate,
                originalCurrencyCode = l.OriginalCurrencyCode,
                originalAmount = amounts ? l.OriginalAmount : null,
                exchangeRate = l.ExchangeRate,
                rateDate = l.RateDate,
                amount = amounts ? l.Amount : (decimal?)null,
                quantity = l.Quantity,
                description = l.Description,
                receiptFileId = l.ReceiptFileId,
                project = Coding(l.CostAllocation, "project"),
                costCentre = Coding(l.CostAllocation, "costCentre"),
                flags = l.PolicyFlags,
                blocksSubmit = l.PolicyFlags.Length > 0 && categories.GetValueOrDefault(l.CategoryId)?.FlagsBlockSubmit == true,
                approvedAmount = amounts ? l.ApprovedAmount : null,
                decisionReason = l.DecisionReason,
                decidedAt = l.DecidedAt,
            }).ToList(),
        };
    }

    private static string? Coding(JsonDocument coding, string name) =>
        coding.RootElement.ValueKind == JsonValueKind.Object && coding.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, string> Names(JsonDocument names) =>
        names.RootElement.ValueKind == JsonValueKind.Object
            ? names.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];

    public async Task<PeopleResult> DecideLinesAsync(Guid claimId, ExpenseDecisionsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await permissions.HasPermissionAsync(ExpensePermissions.Decide, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var peek = await db.Set<ExpenseClaim>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
        if (peek is null)
        {
            return PeopleResult.NotFound;
        }

        var seat = await SeatAsync(peek, cancellationToken).ConfigureAwait(false);
        if (seat.Party)
        {
            return PeopleResult.Forbidden(NotApprover); // nobody decides their own claim, or the claim of the person they made it for (FR-XM-32)
        }

        if (!seat.Approver)
        {
            return PeopleResult.NotFound;
        }

        if (input.Decisions is not { Count: > 0 } decisions)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["decisions"] = [ExpenseRules.Required] });
        }

        return await InTransactionAsync(peek.EmploymentId, async () =>
        {
            var claim = await db.Set<ExpenseClaim>().FirstAsync(c => c.Id == claimId, cancellationToken).ConfigureAwait(false);
            if (claim.Status != "submitted")
            {
                return PeopleResult.Conflict(NotWaiting);
            }

            var lines = await db.Set<ExpenseClaimLine>().Where(l => l.ClaimId == claim.Id).ToDictionaryAsync(l => l.Id, cancellationToken).ConfigureAwait(false);
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var now = clock.UtcNow;
            for (var n = 0; n < decisions.Count; n++)
            {
                var d = decisions[n];
                if (d.LineId is not { } id || !lines.TryGetValue(id, out var line))
                {
                    errors[$"decisions[{n}].lineId"] = [d.LineId is null ? ExpenseRules.Required : "errors.data.not_found"];
                    continue;
                }

                if (ExpenseRules.CheckDecision(line.Amount, d.ApprovedAmount, d.Reason) is { } problem)
                {
                    errors[$"decisions[{n}].approvedAmount"] = [problem];
                    continue;
                }

                line.ApprovedAmount = d.ApprovedAmount;
                line.DecisionReason = d.ApprovedAmount < line.Amount ? d.Reason!.Trim() : null;
                line.DecidedBy = actor.UserId;
                line.DecidedAt = now;
                EmploymentApplier.Bump(line, now, actor.UserId);
            }

            if (errors.Count > 0)
            {
                return PeopleResult.Fields(errors);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // A decision that changes nothing for the claim as a whole is still shown: the view carries each line's decision.
            return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(claim, cancellationToken, seat).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> OpenClaimsAsync(Guid employmentId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(ExpensePermissions.Read, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(new ExpenseClaim { CompanyId = employment.CompanyId, EmploymentId = employment.Id }, ExpenseClaimAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var open = await new ExpenseOpenClaimsReader(db).OpenForAsync(employmentId, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { employmentId, items = open });
    }
}
