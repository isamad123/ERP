namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the shift and roster services (slice S1.28a, CR-085 S1.28a detail). The generated writes of <c>hrm.shifts</c>,
/// <c>hrm.rosters</c> and <c>hrm.roster_lines</c> are closed, so their actions are declared by hand in
/// <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>. Reads stay the generated <c>&lt;table&gt;.read</c>.
/// </summary>
public static class ShiftPermissions
{
    public const string ShiftsRead = "hrm.shifts.read";

    /// <summary>Create a shift (FR-HRM-ATT-06).</summary>
    public const string ShiftsCreate = "hrm.shifts.create";

    /// <summary>Change a shift from a date, which makes a new version, or switch it off (FR-HRM-ATT-06).</summary>
    public const string ShiftsUpdate = "hrm.shifts.update";

    public const string RostersRead = "hrm.rosters.read";

    /// <summary>Create a roster, assign shifts, copy it forward, edit its days and see its checks (FR-HRM-ATT-07).</summary>
    public const string RostersCreate = "hrm.rosters.create";

    /// <summary>Edit a roster that is still a draft (FR-HRM-ATT-07).</summary>
    public const string RostersUpdate = "hrm.rosters.update";

    /// <summary>Publish a roster and tell the people on it (FR-HRM-ATT-07).</summary>
    public const string RostersPublish = "hrm.rosters.publish";

    /// <summary>Change days of a published roster, with a reason (FR-HRM-ATT-07).</summary>
    public const string RostersAmend = "hrm.rosters.amend";

    /// <summary>The weekly off days an employee's shift policy gives in a period (FR-HRM-ATT-06).</summary>
    public const string WeeklyOffRead = "hrm.shift_patterns.read";

    /// <summary>Every code declared in permissions.json for the shift and roster services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [ShiftsCreate, ShiftsUpdate, RostersCreate, RostersUpdate, RostersPublish, RostersAmend, WeeklyOffRead];
}

/// <summary>HR events of rosters in <c>hrm.outbox_messages</c> (slice S1.28a).</summary>
public static class RosterEvents
{
    /// <summary>
    /// A roster was published, or a day of it was changed after publication, for one employee (FR-HRM-ATT-07): snake_case payload
    /// {roster_id, roster_name, employment_id, employee_no, employee_user_id, manager_user_id, period_start, period_end, reason, company_id, link}.
    /// One event per employee, so each person is told once per publication or change.
    /// </summary>
    public const string Published = "hrm.roster.published";

    public const string AggregateType = "hrm.roster";
}

/// <summary>The kinds of shift (<c>hrm.shifts.shift_type</c>, FR-HRM-ATT-06) and the day types of a roster line.</summary>
public static class ShiftTypes
{
    public const string Fixed = "fixed";
    public const string Flexible = "flexible";
    public const string Rotating = "rotating";
    public const string Split = "split";
    public const string Night = "night";
    public const string Continuous = "continuous";

    public static IReadOnlyList<string> All { get; } = [Fixed, Flexible, Rotating, Split, Night, Continuous];
}

/// <summary>The day types of a roster line (<c>hrm.roster_lines.day_type</c>).</summary>
public static class DayTypes
{
    public const string Working = "working";
    public const string WeeklyOff = "weekly_off";
    public const string Holiday = "holiday";

    public static IReadOnlyList<string> All { get; } = [Working, WeeklyOff, Holiday];
}
