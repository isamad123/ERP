using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Expenses;

/// <summary>
/// Reads the <c>expense</c> policy of an employment on a date through <see cref="IPolicyResolver"/> (slice S1.42, FR-HRM-EXP-01) and the code of the
/// grade the employee holds that day (a rule may be written for one grade). "No policy" is a real answer: no rule, so no limit; never a default (AS-4).
/// One answer per date is kept for the life of the instance.
/// </summary>
internal sealed class ExpensePolicyReader(IPolicyResolver resolver, HrmDbContext db)
{
    public const string PolicyType = "expense";

    private readonly Dictionary<(Guid Employment, DateOnly Date), (IReadOnlyList<ExpenseRule> Rules, string? GradeCode)> cache = [];

    public async Task<(IReadOnlyList<ExpenseRule> Rules, string? GradeCode)> RulesAsync(Guid employmentId, DateOnly date, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((employmentId, date), out var known))
        {
            return known;
        }

        var resolution = await resolver.ResolveAsync(employmentId, PolicyType, date, cancellationToken).ConfigureAwait(false);
        string? grade = null;
        if (resolution?.Attributes.GradeId is { } gradeId)
        {
            grade = await db.Set<Grade>().AsNoTracking().Where(g => g.Id == gradeId).Select(g => g.Code).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ExpenseRule> rules = resolution?.Policy is { } match ? ExpenseRules.Rules(match.Parameters) : [];
        return cache[(employmentId, date)] = (rules, grade);
    }
}
