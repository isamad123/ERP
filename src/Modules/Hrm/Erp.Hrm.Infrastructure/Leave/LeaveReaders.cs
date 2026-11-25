using System.Text.Json;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// The <c>leave</c> policy of an employment on a date (slice S1.32 Build item 2): the resolution (S1.23a, <see cref="IPolicyResolver"/>), the
/// rules of each leave type it lists and the problems of a stored policy that cannot be applied.
/// </summary>
internal sealed record LeavePolicyAt(PolicyResolution Resolution, IReadOnlyDictionary<string, LeaveTypeRules> Rules, IReadOnlyDictionary<string, string[]> Problems)
{
    /// <summary>"No leave policy applies" for a type: the resolver's reason, <c>policy_invalid</c> or <c>type_not_in_policy</c>; null when rules apply.</summary>
    public string? NoPolicyReason(string leaveTypeCode) =>
        Resolution.Policy is null ? Resolution.Reason ?? PolicyResolution.NoPolicy
        : Problems.Count > 0 ? "policy_invalid"
        : Rules.ContainsKey(leaveTypeCode) ? null : "type_not_in_policy";

    /// <summary>The rules of the type, or null when no leave policy applies to it (a stored policy with problems applies to no type).</summary>
    public LeaveTypeRules? For(string leaveTypeCode) => Problems.Count == 0 && Rules.TryGetValue(leaveTypeCode, out var r) ? r : null;
}

/// <summary>Reads the <c>leave</c> policy through <see cref="IPolicyResolver"/>; answers are cached for the life of the instance (one request or one job chunk).</summary>
internal sealed class LeavePolicyReader(IPolicyResolver resolver)
{
    private static readonly IReadOnlyDictionary<string, LeaveTypeRules> NoRules = new Dictionary<string, LeaveTypeRules>();
    private static readonly IReadOnlyDictionary<string, string[]> NoProblems = new Dictionary<string, string[]>();
    private readonly Dictionary<(Guid, DateOnly), LeavePolicyAt?> cache = [];

    /// <summary>Null when the employment does not exist.</summary>
    public async Task<LeavePolicyAt?> AtAsync(Guid employmentId, DateOnly date, CancellationToken ct)
    {
        if (cache.TryGetValue((employmentId, date), out var known))
        {
            return known;
        }

        var resolution = await resolver.ResolveAsync(employmentId, LeavePolicy.PolicyType, date, ct).ConfigureAwait(false);
        LeavePolicyAt? answer = null;
        if (resolution is not null)
        {
            if (resolution.Policy is { } match)
            {
                var (rules, problems) = LeavePolicy.Parse(match.Parameters);
                answer = new LeavePolicyAt(resolution, rules, problems);
            }
            else
            {
                answer = new LeavePolicyAt(resolution, NoRules, NoProblems);
            }
        }

        cache[(employmentId, date)] = answer;
        return answer;
    }
}

/// <summary>The company's leave-year start month (setting <c>hrm.leave.year_start_month</c>, ruling Q3); a stored value out of range reads as the setting's default.</summary>
internal sealed class LeaveYearSetting(ISettings settings)
{
    private readonly Dictionary<Guid, int> cache = [];

    public async Task<int> StartMonthAsync(Guid companyId, CancellationToken ct)
    {
        if (!cache.TryGetValue(companyId, out var month))
        {
            var value = (await settings.GetAsync(HrmSettingKeys.HrmLeaveYearStartMonth, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
            month = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var m) && m is >= 1 and <= 12 ? m : 1;
            cache[companyId] = month;
        }

        return month;
    }

    /// <summary>The leave year of a date for a leave type: the policy's month for the type when it gives one, else the company's.</summary>
    public async Task<LeaveYearSpan> YearAsync(Guid companyId, LeaveTypeRules? rules, DateOnly date, CancellationToken ct) =>
        LeaveYears.Of(date, rules?.YearStartMonth ?? await StartMonthAsync(companyId, ct).ConfigureAwait(false));
}

/// <summary>
/// The time an employment worked from its processed attendance days (S1.29 writes them): a day counts once when present, late or on duty and
/// half when the result is a half day; hours are the worked minutes. Used by the accrual models per days or hours worked (FR-HRM-LVE-02),
/// only for months whose attendance is locked, so the count no longer changes.
/// </summary>
internal sealed class WorkedTimeReader(HrmDbContext db)
{
    private static readonly string[] FullDay = ["present", "late", "on_duty"];

    public async Task<decimal> WorkedAsync(Guid employmentId, DateOnly from, DateOnly to, bool hours, CancellationToken ct)
    {
        var days = db.Set<AttendanceDay>().AsNoTracking().Where(d => d.EmploymentId == employmentId && d.WorkDate >= from && d.WorkDate <= to && d.SupersededAt == null); // the current result of each day (S1.29a row versions)
        if (hours)
        {
            var minutes = await days.SumAsync(d => (long)d.WorkedMinutes, ct).ConfigureAwait(false);
            return minutes / 60m;
        }

        var full = await days.CountAsync(d => FullDay.Contains(d.Result), ct).ConfigureAwait(false);
        var half = await days.CountAsync(d => d.Result == "half_day", ct).ConfigureAwait(false);
        return full + (half * 0.5m);
    }
}
