namespace Erp.Hrm.Application.Policies;

/// <summary>The date rule of a new assignment set (slice S1.23a Build item 5, ruling Q4).</summary>
public static class PolicySetDates
{
    public const string SetDate = "errors.hrm.policy.assignment.set_date";

    /// <summary>
    /// A new set starts after the start of the newest set, or on that same start while it is still in the future (that set is then
    /// replaced before it ever applied), so a set that was in force is never rewritten. Null when the date is acceptable.
    /// </summary>
    public static string? Problem(DateOnly from, DateOnly? latest, DateOnly today) =>
        latest is not { } s || from > s || (from == s && s > today) ? null : SetDate;
}
