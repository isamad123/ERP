using Erp.Hrm.Contracts.Employments;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The result of the backdated change check (GHR-04): whether the change reaches back before today, what each module answered,
/// the periods flagged for recalculation (a change inside a locked period becomes a retroactive adjustment in the next open
/// payroll, FR-XM-26; never refused, CR-096), and whether a termination must be refused without an approved override (BR-02).
/// <see cref="Summary"/> is a localization key: none of the modules registered (or none of their answers) found a locked or
/// calculated period, or some periods are flagged.
/// </summary>
public sealed record ImpactReport(
    bool Backdated,
    DateOnly EarliestDate,
    IReadOnlyList<EmploymentImpactAnswer> Answers,
    IReadOnlyList<AffectedPeriod> Flagged,
    bool TerminationNeedsOverride,
    string Summary)
{
    public const string NotBackdated = "hrm.impact.not_backdated";
    public const string NothingAffected = "hrm.impact.nothing_affected";
    public const string PeriodsFlagged = "hrm.impact.periods_flagged";
    public const string TerminationLocked = "errors.hrm.employment.termination_in_locked_period";
}

/// <summary>
/// The backdated change check of slice S1.20 Build item 8 (ruling Q6 as corrected by CR-096): a change whose earliest affected day
/// is before today asks every registered <see cref="IEmploymentChangeImpact"/>. Attendance, leave and payroll register theirs in
/// their slices; with none registered the answer says that no locked period or calculated result is affected, which is then true
/// (nothing locks or calculates without those modules). Only a termination dated before the last attendance or inside a locked
/// period is to be refused without an approved override (BR-02); every other change is saved and its periods flagged.
/// </summary>
public sealed class EmploymentChangeCheck(IEnumerable<IEmploymentChangeImpact> modules)
{
    public async Task<ImpactReport> RunAsync(EmploymentChange change, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.EarliestDate >= today)
        {
            return new ImpactReport(false, change.EarliestDate, [], [], false, ImpactReport.NotBackdated);
        }

        var answers = new List<EmploymentImpactAnswer>();
        foreach (var module in modules.OrderBy(m => m.ModuleCode, StringComparer.Ordinal))
        {
            answers.Add(await module.AnalyseAsync(change, cancellationToken).ConfigureAwait(false));
        }

        // Every period a module named from the earliest affected day on is recalculated (locked ones as an adjustment).
        var flagged = answers.SelectMany(a => a.Periods).Where(p => p.To >= change.EarliestDate)
            .OrderBy(p => p.From).ThenBy(p => p.Kind, StringComparer.Ordinal).ToList();
        var terminationBlocked = change.IsTermination
            && (flagged.Any(p => p.Locked) || answers.Any(a => a.LastAttendanceDate is { } last && last > change.EffectiveDate));
        return new ImpactReport(true, change.EarliestDate, answers, flagged, terminationBlocked,
            flagged.Count == 0 ? ImpactReport.NothingAffected : ImpactReport.PeriodsFlagged);
    }
}
