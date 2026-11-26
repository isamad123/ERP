using System.Text.Json;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>What an application needs to know about the employee (slice S1.41 Build item 2): service, grade, pay group, the pay the compensation in force gives, loans already open.</summary>
internal sealed record LoanContext(
    Employment Employment,
    string? GradeCode,
    int ServiceMonths,
    Guid? PayGroupId,
    string? Frequency,
    string? CurrencyCode,
    decimal? GrossPay,
    int OpenLoans,
    int Decimals,
    MidpointRounding Mode);

/// <summary>The installment periods of a loan: pay periods of the employee's pay group, in order, still open to a run.</summary>
internal sealed record LoanPeriods(IReadOnlyList<PayPeriod> Periods, bool Enough);

/// <summary>Reads the facts the loan checks and the schedule use. Reads only; nothing here changes data.</summary>
internal sealed class LoanFacts(HrmDbContext db, ISettings settings)
{
    public static readonly string[] OpenStatuses = ["applied", "approved", "active"];

    public async Task<LoanContext> ReadAsync(Employment e, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        var assignment = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == e.Id && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today))
            .OrderByDescending(a => a.ValidFrom).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var grade = assignment?.GradeId is { } gradeId ? await db.Set<Grade>().AsNoTracking().Where(g => g.Id == gradeId).Select(g => g.Code).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var group = assignment?.PayGroupId is { } groupId ? await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct).ConfigureAwait(false) : null;
        var gross = await db.Set<CompensationAssignment>().AsNoTracking()
            .Where(c => c.EmploymentId == e.Id && c.ValidFrom <= today && (c.ValidTo == null || c.ValidTo > today))
            .OrderByDescending(c => c.ValidFrom).Select(c => c.GrossAmount).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var open = await db.Set<Loan>().AsNoTracking().CountAsync(l => l.EmploymentId == e.Id && OpenStatuses.Contains(l.Status), ct).ConfigureAwait(false);
        var decimals = group is null ? 2 : Math.Clamp((await db.Database.SqlQuery<int>($"select decimals as \"Value\" from ref.currencies where code = {group.CurrencyCode}").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault(2), 0, 4);
        var mode = (await settings.GetAsync(HrmSettingKeys.HrmPayRoundingMode, new SettingContext(e.CompanyId, null), ct).ConfigureAwait(false)).Value;
        var months = ((today.Year - e.ServiceStartDate.Year) * 12) + today.Month - e.ServiceStartDate.Month - (today.Day < e.ServiceStartDate.Day ? 1 : 0);
        return new LoanContext(e, grade, Math.Max(0, months), group?.Id, group?.Frequency, group?.CurrencyCode, gross, open, decimals,
            PayRounding.Mode(mode.ValueKind == JsonValueKind.String ? mode.GetString() : null));
    }

    /// <summary>The rules of the type's policy in force today: the active version of the policy the type names, or null when it has none.</summary>
    public async Task<LoanPolicyRules?> RulesAsync(LoanType type, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.PolicyId is not { } policyId)
        {
            return null;
        }

        var parameters = await db.Set<PolicyVersion>().AsNoTracking()
            .Where(v => v.PolicyId == policyId && v.Status == "active" && v.ValidFrom <= today && (v.ValidTo == null || v.ValidTo > today))
            .OrderByDescending(v => v.Version).Select(v => v.Parameters).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return parameters is null ? null : LoanPolicy.Read(parameters.RootElement);
    }

    /// <summary>
    /// The periods the installments fall in: the pay group's periods that are future or open and that no run holds yet, from <paramref name="from"/> (a period of the
    /// group) or, without it, the first that starts after today. <see cref="LoanPeriods.Enough"/> says the calendar reaches <paramref name="count"/> installments.
    /// </summary>
    public async Task<LoanPeriods> PeriodsAsync(Guid payGroupId, Guid? from, DateOnly today, int count, CancellationToken ct)
    {
        var free = db.Set<PayPeriod>().AsNoTracking()
            .Where(p => p.PayGroupId == payGroupId && (p.Status == "future" || p.Status == "open")
                        && !db.Set<PayRun>().Any(r => r.PayPeriodId == p.Id && r.Status != "cancelled"));
        var start = from is { } f ? await db.Set<PayPeriod>().AsNoTracking().Where(p => p.Id == f && p.PayGroupId == payGroupId).Select(p => (DateOnly?)p.StartDate).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var periods = start is { } s
            ? await free.Where(p => p.StartDate >= s).OrderBy(p => p.StartDate).Take(count).ToListAsync(ct).ConfigureAwait(false)
            : await free.Where(p => p.StartDate > today).OrderBy(p => p.StartDate).Take(count).ToListAsync(ct).ConfigureAwait(false);
        if (start is null && periods.Count == 0)
        {
            periods = await free.OrderBy(p => p.StartDate).Take(count).ToListAsync(ct).ConfigureAwait(false);
        }

        return new LoanPeriods(periods, periods.Count == count);
    }
}
