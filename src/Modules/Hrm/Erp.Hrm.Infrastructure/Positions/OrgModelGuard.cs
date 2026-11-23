using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// The rule for changing a company's planning model (slice S1.22 Build item 1): from position to headcount only while no assignment
/// references a position; from headcount to position only after every active assignment has one. Read by the positions screen
/// (<see cref="DescribeAsync"/>) and enforced on every change of the setting <c>hrm.org.model</c> (<see cref="OrgModelChangeGuard"/>).
/// </summary>
public static class OrgModelGuard
{
    public const string HasPositions = "errors.hrm.org_model.assignments_have_positions";
    public const string MissingPositions = "errors.hrm.org_model.assignments_lack_positions";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    /// <summary>The company's model today with the counts a change depends on and what is allowed.</summary>
    public static async Task<object> DescribeAsync(HrmDbContext db, ISettings settings, Guid companyId, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        var model = await PositionRules.ModelAsync(settings, companyId, ct).ConfigureAwait(false);
        var (with, without) = await CountsAsync(db, companyId, today, ct).ConfigureAwait(false);
        return new
        {
            companyId,
            model,
            assignmentsWithPosition = with,
            assignmentsWithoutPosition = without,
            canSwitchToPosition = model == OrgModels.Headcount && without == 0,
            canSwitchToHeadcount = model == OrgModels.Position && with == 0,
        };
    }

    /// <summary>The localization key of the reason a change to <paramref name="to"/> is refused, or null when it is allowed.</summary>
    public static async Task<string?> ProblemAsync(HrmDbContext db, ISettings settings, Guid companyId, string to, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        var current = await PositionRules.ModelAsync(settings, companyId, ct).ConfigureAwait(false);
        if (current == to)
        {
            return null;
        }

        var (with, without) = await CountsAsync(db, companyId, today, ct).ConfigureAwait(false);
        return to == OrgModels.Headcount ? with > 0 ? HasPositions : null : without > 0 ? MissingPositions : null;
    }

    private static async Task<(int With, int Without)> CountsAsync(HrmDbContext db, Guid companyId, DateOnly today, CancellationToken ct)
    {
        var rows = await (from a in db.Set<EmploymentAssignment>().AsNoTracking()
                          join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                          where a.CompanyId == companyId && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today)
                                && CurrentStatuses.Contains(e.Status) && (e.SeparationDate == null || e.SeparationDate >= today)
                          group a by a.PositionId != null into g
                          select new { HasPosition = g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        return (rows.FirstOrDefault(r => r.HasPosition)?.Count ?? 0, rows.FirstOrDefault(r => !r.HasPosition)?.Count ?? 0);
    }
}

/// <summary>
/// Refuses a change of <c>hrm.org.model</c> that <see cref="OrgModelGuard"/> forbids (slice S1.22 Build item 1). A change of the tenant
/// layer reaches every company, so each company is checked; a reset (no value) is checked as a change to the default, headcount.
/// </summary>
internal sealed class OrgModelChangeGuard(HrmDbContext db, ISettings settings, Erp.BuildingBlocks.Domain.Time.IClock clock, Employments.EmploymentApplier applier) : ISettingChangeGuard
{
    public string Key => OrgModels.SettingKey;

    public async Task<string?> ProblemAsync(Guid? companyId, System.Text.Json.JsonElement? newValue, CancellationToken cancellationToken)
    {
        var to = newValue is { } v ? PositionRules.Model(v) : OrgModels.Headcount;
        var companies = companyId is { } one
            ? [one]
            : await db.Database.SqlQuery<Guid>($"select id as \"Value\" from org.companies where not is_deleted").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var company in companies)
        {
            var today = await applier.TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (await OrgModelGuard.ProblemAsync(db, settings, company, to, today, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }
}
