using System.Text.Json;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// Refuses a change of <c>hrm.leave.year_start_month</c> for a company whose leave ledger already has entries (review S1.32a finding 3):
/// every entry carries the leave year it was posted in, so moving the start month would put past entries in other years and change past
/// balances unseen. A change of the tenant layer reaches every company and is refused when any company with entries would see another value;
/// a reset to the inherited value counts as a change. Before the first entry the month may change freely.
/// </summary>
internal sealed class LeaveYearStartGuard(HrmDbContext db, ISettings settings) : ISettingChangeGuard
{
    public const string Fixed = "errors.hrm.leave.year_start_fixed";

    public string Key => HrmSettingKeys.HrmLeaveYearStartMonth;

    public async Task<string?> ProblemAsync(Guid? companyId, JsonElement? newValue, CancellationToken cancellationToken)
    {
        int? to = newValue is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var m) ? m : null;
        var companies = await db.Set<LeaveLedger>().AsNoTracking().Where(x => companyId == null || x.CompanyId == companyId)
            .Select(x => x.CompanyId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var company in companies)
        {
            var now = (await settings.GetAsync(Key, new SettingContext(company, null), cancellationToken).ConfigureAwait(false)).Value;
            var current = now.ValueKind == JsonValueKind.Number && now.TryGetInt32(out var c) ? c : 1;
            if (to != current)
            {
                return Fixed;
            }
        }

        return null;
    }
}
