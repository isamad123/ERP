namespace Erp.Hrm.Application.Leave;

/// <summary>
/// Team coverage rules of slice S1.33a (slice ruling Q1; FR-HRM-LVE-05, -15), pure: the share of a team away on a day against the company's
/// setting <c>hrm.leave.coverage_warning_percent</c>, and who a manager's team is. A warning only, never a block.
/// </summary>
public static class LeaveCoverageRules
{
    /// <summary>The share away in percent with one decimal (0 for an empty team).</summary>
    public static decimal Percent(int away, int teamSize) => teamSize <= 0 ? 0m : Math.Round(away * 100m / teamSize, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// True when the share away reaches the limit. A limit of 0 means the warning is off; an empty team never warns. Integer arithmetic, so 3 of 10
    /// against 30 percent warns exactly, with no rounding.
    /// </summary>
    public static bool Warns(int away, int teamSize, int limitPercent) =>
        limitPercent > 0 && teamSize > 0 && away > 0 && (long)away * 100 >= (long)limitPercent * teamSize;

    /// <summary>
    /// The direct and indirect reports of a manager (not the manager), from each person's own manager. Safe against a cycle in the data (the database
    /// refuses one, but this answer must still end).
    /// </summary>
    public static HashSet<Guid> Subtree(Guid manager, IReadOnlyDictionary<Guid, Guid?> managerOf)
    {
        ArgumentNullException.ThrowIfNull(managerOf);
        var reports = managerOf.Where(p => p.Value is not null).ToLookup(p => p.Value!.Value, p => p.Key);
        var team = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(manager);
        while (queue.Count > 0)
        {
            foreach (var report in reports[queue.Dequeue()])
            {
                if (report != manager && team.Add(report))
                {
                    queue.Enqueue(report);
                }
            }
        }

        return team;
    }
}
