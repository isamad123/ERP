using System.Text.Json;
using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Hrm.Infrastructure.Expenses;

internal sealed partial class ExpenseClaimService
{
    /// <summary>The claim currency: that of the pay group the employee is in on the day (reimbursement is paid in it), else the company's base currency.</summary>
    private async Task<string?> ClaimCurrencyAsync(Employment employment, DateOnly today, CancellationToken ct)
    {
        var group = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employment.Id && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today) && a.PayGroupId != null)
            .Select(a => a.PayGroupId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (group is { } id)
        {
            var code = await db.Set<PayGroup>().AsNoTracking().Where(g => g.Id == id).Select(g => g.CurrencyCode).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(code))
            {
                return code.Trim();
            }
        }

        var company = employment.CompanyId;
        var baseCode = (await db.Database.SqlQuery<string>($"select base_currency_code::text as \"Value\" from org.companies where id = {company}").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault();
        return string.IsNullOrWhiteSpace(baseCode) ? null : baseCode.Trim();
    }

    /// <summary>
    /// Brings the lines of a draft to the lines sent (new ones added, named ones changed, the rest removed), converts them into the claim currency at the
    /// customer's rate, links their receipts, works out the policy flags and sets the claim's total. Null when it went through; else the refusal.
    /// </summary>
    private async Task<PeopleResult?> ReplaceLinesAsync(ExpenseClaim claim, Employment employment, List<ExpenseClaimLine> existing, IReadOnlyList<ExpenseLineInput> inputs, DateOnly today, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var user = actor.UserId;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var categoryIds = inputs.Where(i => i.CategoryId is not null).Select(i => i.CategoryId!.Value).Distinct().ToList();
        var categories = await db.Set<ExpenseCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id) && c.CompanyId == claim.CompanyId && !c.IsDeleted && c.IsActive)
            .ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var tx = db.Database.CurrentTransaction!.GetDbTransaction();
        var current = (await files.CurrentAsync(ExpenseEvents.OwnerType, claim.Id, tx, ct).ConfigureAwait(false)).ToHashSet();
        var hashes = await files.ChecksumsAsync([.. inputs.Where(i => i.ReceiptFileId is not null).Select(i => i.ReceiptFileId!.Value).Where(current.Contains)], tx, ct).ConfigureAwait(false);
        var decimals = (await db.Database.SqlQuery<int>($"select decimals as \"Value\" from ref.currencies where code = {claim.CurrencyCode}").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault(2);
        var rates = new Dictionary<(string Currency, DateOnly Date), ExchangeRate?>();
        var byId = existing.ToDictionary(l => l.Id);
        var keep = new HashSet<Guid>();
        var built = new List<(ExpenseClaimLine Line, ExpenseCategory Category)>();
        for (var n = 0; n < inputs.Count; n++)
        {
            var input = inputs[n];
            var at = $"lines[{n}].";
            ExpenseCategory? category = input.CategoryId is { } cid && categories.TryGetValue(cid, out var c) ? c : null;
            ExpenseRules.CheckLine(n, input, category?.Kind, errors);
            if (input.CategoryId is not null && category is null)
            {
                errors[at + "categoryId"] = [CategoryUnknown];
            }

            if (input.ReceiptFileId is { } receipt && !current.Contains(receipt))
            {
                errors[at + "receiptFileId"] = [ReceiptUnknown];
            }

            ExpenseClaimLine? line = null;
            if (input.Id is { } lineId)
            {
                if (!byId.TryGetValue(lineId, out line))
                {
                    errors[at + "id"] = ["errors.data.not_found"];
                }
            }

            if (category is null || errors.Keys.Any(k => k.StartsWith(at, StringComparison.Ordinal)))
            {
                continue;
            }

            var date = input.ExpenseDate!.Value;
            var original = PayRoundingInternal(input.Amount!.Value);
            var currency = input.CurrencyCode!;
            decimal rate = 1m;
            DateOnly? rateDate = null;
            if (!string.Equals(currency, claim.CurrencyCode, StringComparison.Ordinal))
            {
                if (!rates.TryGetValue((currency, date), out var found))
                {
                    found = await db.Set<ExchangeRate>().AsNoTracking()
                        .Where(r => r.CompanyId == claim.CompanyId && r.FromCurrency == currency && r.ToCurrency == claim.CurrencyCode && r.RateDate <= date)
                        .OrderByDescending(r => r.RateDate).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                    rates[(currency, date)] = found;
                }

                if (found is null)
                {
                    errors[at + "currencyCode"] = [NoRate];
                    continue;
                }

                rate = found.Rate;
                rateDate = found.RateDate;
            }

            var isNew = line is null;
            line ??= new ExpenseClaimLine { Id = Guid.CreateVersion7(now), CompanyId = claim.CompanyId, ClaimId = claim.Id, CreatedAt = now, CreatedBy = user, RowVersion = 0 };
            line.CategoryId = category.Id;
            line.ExpenseDate = date;
            line.OriginalCurrencyCode = currency;
            line.OriginalAmount = original;
            line.ExchangeRate = rate;
            line.RateDate = rateDate;
            line.Amount = ExpenseRules.Convert(original, rate, decimals);
            line.Quantity = input.Quantity;
            line.Description = Trimmed(input.Description);
            line.ReceiptFileId = input.ReceiptFileId;
            line.ReceiptHash = input.ReceiptFileId is { } rid && hashes.TryGetValue(rid, out var hash) ? hash : null;
            line.CostAllocation = JsonSerializer.SerializeToDocument(Coding(input), JsonSerializerOptions.Web);
            line.ApprovedAmount = null;
            line.DecisionReason = null;
            line.DecidedBy = null;
            line.DecidedAt = null;
            line.PolicyFlags = [];
            EmploymentApplier.Bump(line, now, user);
            if (isNew)
            {
                db.Add(line);
            }

            keep.Add(line.Id);
            built.Add((line, category));
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        db.RemoveRange(existing.Where(l => !keep.Contains(l.Id)));
        await ApplyFlagsAsync(claim, employment, built, today, ct).ConfigureAwait(false);
        claim.TotalAmount = built.Sum(b => b.Line.Amount);
        claim.NeedsFinance = ExpenseRules.NeedsFinance(claim.TotalAmount, await FinanceThresholdAsync(claim.CompanyId, ct).ConfigureAwait(false));
        EmploymentApplier.Bump(claim, now, user);
        return null;
    }

    private static decimal PayRoundingInternal(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static Dictionary<string, string> Coding(ExpenseLineInput input)
    {
        var coding = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Trimmed(input.Project) is { } project)
        {
            coding["project"] = project;
        }

        if (Trimmed(input.CostCentre) is { } centre)
        {
            coding["costCentre"] = centre;
        }

        return coding;
    }

    private async Task<decimal> FinanceThresholdAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(Contracts.Generated.HrmSettingKeys.HrmExpFinanceThreshold, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) ? d : 0m;
    }

    private async Task<int> WindowDaysAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(Contracts.Generated.HrmSettingKeys.HrmExpClaimWindowDays, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>
    /// The policy flags of every line (FR-HRM-EXP-03): over the limit of the policy rule for the line's category and the employee's grade on the expense
    /// date, a receipt used before (the same file with the same amount and date in another claim not cancelled or rejected, or earlier in this one),
    /// a missing receipt, and a date outside the claim window.
    /// </summary>
    private async Task ApplyFlagsAsync(ExpenseClaim claim, Employment employment, List<(ExpenseClaimLine Line, ExpenseCategory Category)> lines, DateOnly today, CancellationToken ct)
    {
        var window = await WindowDaysAsync(claim.CompanyId, ct).ConfigureAwait(false);
        var seen = new HashSet<(string Hash, decimal Amount, DateOnly Date)>();
        foreach (var (line, category) in lines)
        {
            var (rules, grade) = await policy.RulesAsync(employment.Id, line.ExpenseDate, ct).ConfigureAwait(false);
            var duplicate = false;
            if (line.ReceiptHash is { } hash)
            {
                duplicate = !seen.Add((hash, line.Amount, line.ExpenseDate))
                    || await db.Set<ExpenseClaimLine>().AsNoTracking().AnyAsync(l => l.ReceiptHash == hash && l.Amount == line.Amount && l.ExpenseDate == line.ExpenseDate && l.ClaimId != claim.Id
                        && db.Set<ExpenseClaim>().Any(c => c.Id == l.ClaimId && c.Status != "cancelled" && c.Status != "rejected"), ct).ConfigureAwait(false);
            }

            var facts = new ExpenseLineFacts(category.RequiresReceipt, line.ReceiptFileId is not null, duplicate, line.ExpenseDate, line.Amount, line.Quantity, ExpenseRules.RuleFor(rules, category.Code, grade));
            line.PolicyFlags = [.. ExpenseRules.Flags(facts, today, window)];
        }
    }

    /// <summary>Sends a draft to its route (or back to it after a return): flags again, checks of the category rules, the finance step and the request.</summary>
    private async Task<PeopleResult> SendAsync(ExpenseClaim claim, Employment employment, DateOnly today, CancellationToken ct)
    {
        var lines = await db.Set<ExpenseClaimLine>().Where(l => l.ClaimId == claim.Id).OrderBy(l => l.ExpenseDate).ThenBy(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            return PeopleResult.Conflict(NoLines);
        }

        var categoryIds = lines.Select(l => l.CategoryId).Distinct().ToList();
        var categories = await db.Set<ExpenseCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var pairs = lines.Select(l => (l, categories[l.CategoryId])).ToList();
        await ApplyFlagsAsync(claim, employment, pairs, today, ct).ConfigureAwait(false);
        var blocked = pairs.Where(p => p.Item1.PolicyFlags.Length > 0 && p.Item2.FlagsBlockSubmit).ToList();
        if (blocked.Count > 0)
        {
            return PeopleResult.Conflict(BlockedByFlags, new Dictionary<string, object?> { ["lines"] = blocked.Select(p => new { id = p.Item1.Id, flags = p.Item1.PolicyFlags }).ToList() });
        }

        if (claim.ReimburseVia == ExpenseReimbursement.Payroll)
        {
            var refusal = await CheckPayrollComponentAsync(categories.Values, claim, today, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                return PeopleResult.Conflict(refusal);
            }
        }

        var now = clock.UtcNow;
        claim.TotalAmount = lines.Sum(l => l.Amount);
        claim.NeedsFinance = ExpenseRules.NeedsFinance(claim.TotalAmount, await FinanceThresholdAsync(claim.CompanyId, ct).ConfigureAwait(false));
        claim.Status = "submitted";
        claim.SubmittedAt = now;
        EmploymentApplier.Bump(claim, now, actor.UserId);
        foreach (var l in lines)
        {
            EmploymentApplier.Bump(l, now, actor.UserId);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = JsonSerializer.SerializeToElement(claim.CompanyId.ToString()),
            ["employment"] = JsonSerializer.SerializeToElement(employment.Id.ToString()), // the manager step follows the subject's employment (R-305)
            ["employee_no"] = JsonSerializer.SerializeToElement(employment.EmployeeNo),
            ["claim_no"] = JsonSerializer.SerializeToElement(claim.ClaimNo),
            ["reimburse_via"] = JsonSerializer.SerializeToElement(claim.ReimburseVia),
            ["needs_finance"] = JsonSerializer.SerializeToElement(claim.NeedsFinance),
        };
        if (employment.UserRef is { } employee)
        {
            values["employee"] = JsonSerializer.SerializeToElement(employee.ToString());
        }

        var transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var started = claim.WorkflowInstanceId is null
            ? await workflow.StartAsync(new WorkflowStartRequest(ExpenseEvents.DocumentType, claim.Id, claim.CompanyId, employment.UserRef is { } subject ? [subject] : [], values), transaction, ct).ConfigureAwait(false)
            : await workflow.ResubmitAsync(ExpenseEvents.DocumentType, claim.Id, values, transaction, ct).ConfigureAwait(false);
        claim.WorkflowInstanceId = started.InstanceId;
        claim.RowVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Pending, Body: new
        {
            id = claim.Id,
            claimNo = claim.ClaimNo,
            employmentId = employment.Id,
            status = claim.Status,
            instanceId = started.InstanceId,
            needsFinance = claim.NeedsFinance,
            onBehalf = employment.UserRef is { } u && u != actor.UserId,
            flagged = lines.Count(l => l.PolicyFlags.Length > 0),
            rowVersion = claim.RowVersion,
        });
    }

    /// <summary>The payroll route needs one component for the whole claim, active and open to one-time amounts and never a statutory one; a different component goes on another claim.</summary>
    private async Task<string?> CheckPayrollComponentAsync(IEnumerable<ExpenseCategory> categories, ExpenseClaim claim, DateOnly today, CancellationToken ct)
    {
        var components = categories.Select(c => c.ReimbursementComponentId).Distinct().ToList();
        if (components.Any(c => c is null))
        {
            return NoComponent;
        }

        if (components.Count > 1)
        {
            return MixedComponents;
        }

        var id = components[0]!.Value;
        var component = await db.Set<PayComponent>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == claim.CompanyId && !c.IsDeleted && c.IsActive, ct).ConfigureAwait(false);
        if (component is null)
        {
            return NoComponent;
        }

        var version = await db.Set<PayComponentVersion>().AsNoTracking().Where(v => v.ComponentId == id && v.ValidFrom <= today && (v.ValidTo == null || v.ValidTo > today)).OrderByDescending(v => v.ValidFrom).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var statutory = version?.StatutoryFlags.RootElement.TryGetProperty(SnapshotVersion.StatutoryFlag, out var flag) == true && flag.ValueKind == JsonValueKind.True;
        return version is null || statutory || (!component.IsOneTimeAllowed && version.Method != "manual") ? ComponentNotAllowed : null;
    }
}
