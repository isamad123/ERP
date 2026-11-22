namespace Erp.Hrm.Contracts.Employments;

/// <summary>
/// Permissions of the employment services (slice S1.20b, CR-076). The generated writes of <c>hrm.employments</c>,
/// <c>hrm.employment_assignments</c> and <c>hrm.employment_events</c> are closed (ruling Q1, R-113), so their actions are declared by
/// hand in <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>. Reads stay the generated <c>&lt;table&gt;.read</c>.
/// </summary>
public static class EmploymentPermissions
{
    public const string EmploymentsRead = "hrm.employments.read";

    /// <summary>Hire: a new employment (with its first assignment and hire event) for an existing or a new person (FR-HRM-EMP-07).</summary>
    public const string EmploymentsCreate = "hrm.employments.create";

    /// <summary>Type an employee number instead of the next one of the series (FR-HRM-EMP-04, S1.08 ruling Q9). Sensitive.</summary>
    public const string NumberOverride = "hrm.employments.number_override";

    /// <summary>Link an employment to a kernel user or unlink it (FR-HRM-EMP-17): the user then sees the employee's own records. Sensitive.</summary>
    public const string UserLink = "hrm.employments.link_user";

    public const string AssignmentsRead = "hrm.employment_assignments.read";

    public const string EventsRead = "hrm.employment_events.read";

    /// <summary>Record a change of an employment as a dated event with a reason (FR-HRM-EMP-03), corrections included.</summary>
    public const string EventsCreate = "hrm.employment_events.create";

    /// <summary>Every code declared in permissions.json for the employment services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [EmploymentsCreate, NumberOverride, UserLink, EventsCreate];
}

/// <summary>The HR events of employments in <c>hrm.outbox_messages</c> (slice S1.20 Build item 9, spec 06a section 30.1). Ids only.</summary>
public static class EmploymentEvents
{
    /// <summary>An employment was created by a hire: {employmentId, personId, companyId, eventId, joiningDate, status}.</summary>
    public const string EmployeeHired = "hrm.employee_hired";

    /// <summary>
    /// A change took effect (once per event, on its effective date; ruling Q12): {employmentId, personId, companyId, eventId, kind,
    /// effectiveDate, earliestDate}. <c>earliestDate</c> is the first day whose results may change (a correction that moves a date
    /// back starts there): attendance, leave and payroll recalculate from it (GHR-04, FR-XM-26).
    /// </summary>
    public const string EmployeeChanged = "hrm.employee_changed";

    /// <summary>An employment ended (raised by the separations of S1.25).</summary>
    public const string EmployeeSeparated = "hrm.employee_separated";

    public const string AggregateType = "hrm.employment";
}
