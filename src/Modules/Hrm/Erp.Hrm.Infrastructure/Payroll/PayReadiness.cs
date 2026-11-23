using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>One item of the readiness registry (<c>Access/pay-readiness.json</c>).</summary>
public sealed record ReadinessItem(string Code, string LabelKey, string Slice, string Requirement);

/// <summary>
/// The readiness checklist registry (slice S1.34a, FR-HRM-PAY-38): the items of <c>pay-readiness.json</c> in order. The registry
/// lists what must be ready; <see cref="IPayReadinessCheck"/> classes registered by the slices that own the data say whether it is.
/// </summary>
public sealed class PayReadinessRegistry
{
    public PayReadinessRegistry(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var items = new List<ReadinessItem>();
        foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var item = new ReadinessItem(e.GetProperty("code").GetString()!, e.GetProperty("labelKey").GetString()!, e.GetProperty("slice").GetString()!, e.GetProperty("requirement").GetString()!);
            if (items.Exists(i => i.Code == item.Code))
            {
                throw new InvalidOperationException($"Readiness item {item.Code} is listed twice.");
            }

            items.Add(item);
        }

        Items = items;
    }

    public IReadOnlyList<ReadinessItem> Items { get; }
}

/// <summary>Shared by the checks of the pay set-up: the current employments of a pay group over a period, in the caller's data scope.</summary>
internal static class PayReadinessQueries
{
    /// <summary>How many employees a failed item names (the count is always the full number).</summary>
    public const int ListLimit = 50;

    private static readonly string[] CurrentStatuses = ["active", "on_notice"];

    /// <summary>
    /// Employments of the group during the period: an assignment in the group that overlaps the period, a current status (active or
    /// on notice) and a joining date on or before the period's last day; limited to the caller's data scope (FR-KRN-IAM).
    /// </summary>
    public static async Task<IQueryable<Employment>> InGroupAsync(HrmDbContext db, IDataScopeFilter scope, ReadinessContext c, CancellationToken ct)
    {
        var source = new DbContextScopeSource(db);
        var employments = await scope.ApplyAsync(db.Set<Employment>().AsNoTracking(), EmploymentAccess.Scope, "read", source, ct).ConfigureAwait(false);
        var assignments = db.Set<EmploymentAssignment>().AsNoTracking();
        return employments.Where(e => e.CompanyId == c.CompanyId && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= c.End
            && assignments.Any(a => a.EmploymentId == e.Id && a.PayGroupId == c.PayGroupId && a.ValidFrom <= c.End && (a.ValidTo == null || a.ValidTo > c.Start)));
    }

    public static async Task<ReadinessOutcome> OutcomeAsync(IQueryable<Employment> missing, CancellationToken ct)
    {
        var count = await missing.CountAsync(ct).ConfigureAwait(false);
        if (count == 0)
        {
            return new ReadinessOutcome(ReadinessStatus.Passed);
        }

        var first = await missing.OrderBy(e => e.EmployeeNo).Take(ListLimit).Select(e => new ReadinessEmployee(e.Id, e.EmployeeNo)).ToListAsync(ct).ConfigureAwait(false);
        return new ReadinessOutcome(ReadinessStatus.Failed, count, first);
    }
}

/// <summary>
/// <c>bank_data_complete</c> (FR-HRM-PAY-38): every current employee of the group has a payment account (bank transfer, wallet,
/// cheque or cash, S1.20d) in force on the period's last day. Lists the employees without one.
/// </summary>
internal sealed class BankDataCompleteCheck(HrmDbContext db, IDataScopeFilter scope) : IPayReadinessCheck
{
    public string Code => "bank_data_complete";

    public async Task<ReadinessOutcome> CheckAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var accounts = db.Set<EmployeeBankAccount>().AsNoTracking();
        var inGroup = await PayReadinessQueries.InGroupAsync(db, scope, context, cancellationToken).ConfigureAwait(false);
        var missing = inGroup.Where(e => !accounts.Any(b => b.EmploymentId == e.Id && b.ValidFrom <= context.End && (b.ValidTo == null || b.ValidTo > context.End)));
        return await PayReadinessQueries.OutcomeAsync(missing, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>structures_assigned</c> (FR-HRM-PAY-38, FR-HRM-PAY-04): every current employee of the group has a compensation assignment (a
/// salary structure) in force on the period's last day. Until the compensation screens of S1.34b exist nobody has one, so the item
/// fails honestly and lists them all.
/// </summary>
internal sealed class StructuresAssignedCheck(HrmDbContext db, IDataScopeFilter scope) : IPayReadinessCheck
{
    public string Code => "structures_assigned";

    public async Task<ReadinessOutcome> CheckAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var assigned = db.Set<CompensationAssignment>().AsNoTracking();
        var inGroup = await PayReadinessQueries.InGroupAsync(db, scope, context, cancellationToken).ConfigureAwait(false);
        var missing = inGroup.Where(e => !assigned.Any(a => a.EmploymentId == e.Id && a.ValidFrom <= context.End && (a.ValidTo == null || a.ValidTo > context.End)));
        return await PayReadinessQueries.OutcomeAsync(missing, cancellationToken).ConfigureAwait(false);
    }
}
