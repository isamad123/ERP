using Erp.Hrm.Contracts.Attendance;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// What each person on the live attendance dashboard is doing now (slice S1.30b Build item 5, FR-HRM-ATT-18, rulings Q2 and CR-090 S1.30b detail c). Pure: the
/// service gathers the plan, the punches and the approved leave and duty; this decides. A person has exactly one status, so a count is always the length of its list.
/// <list type="bullet">
/// <item>No working shift today: <c>not_scheduled</c>.</item>
/// <item>A punch today: <c>present</c>, or <c>late</c> when the first punch is after the shift start plus the grace of the attendance policy (late people are present too).</item>
/// <item>No punch, approved leave: <c>on_leave</c>; approved duty: <c>on_duty</c>.</item>
/// <item>No punch and no attendance policy: <c>no_policy</c> (no number has a default; lateness cannot be judged).</item>
/// <item>No punch: <c>absent</c> once the shift has ended; <c>not_yet_punched</c> once start plus grace has passed; before that <c>not_due</c>.</item>
/// </list>
/// </summary>
public static class DashboardRules
{
    public static string Classify(bool hasShift, DateTimeOffset start, DateTimeOffset end, int? graceMinutes, DateTimeOffset now, DateTimeOffset? firstPunch, bool onLeave, bool onDuty)
    {
        if (!hasShift)
        {
            return DashboardStatuses.NotScheduled;
        }

        if (firstPunch is { } punch)
        {
            return graceMinutes is { } grace && punch > start.AddMinutes(grace) ? DashboardStatuses.Late : DashboardStatuses.Present;
        }

        if (onLeave)
        {
            return DashboardStatuses.OnLeave;
        }

        if (onDuty)
        {
            return DashboardStatuses.OnDuty;
        }

        if (graceMinutes is not { } g)
        {
            return DashboardStatuses.NoPolicy;
        }

        if (now >= end)
        {
            return DashboardStatuses.Absent;
        }

        return now >= start.AddMinutes(g) ? DashboardStatuses.NotYetPunched : DashboardStatuses.NotDue;
    }

    /// <summary>The statuses a drill-down status stands for: <c>present</c> includes the late people (they are present), the others are themselves.</summary>
    public static IReadOnlyList<string> Expand(string status) =>
        status == DashboardStatuses.Present ? [DashboardStatuses.Present, DashboardStatuses.Late] : [status];
}
