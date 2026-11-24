namespace Erp.Hrm.Contracts.Payroll;

/// <summary>
/// Permissions of the pay set-up services (slice S1.34a, CR-087). The generated writes of <c>hrm.pay_groups</c> and
/// <c>hrm.pay_periods</c> are closed, so their actions are declared by hand in <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>
/// (the codes <c>hrm.pay_groups.create</c> and <c>.update</c> keep the names the generated ones had). Reads stay the generated
/// <c>&lt;table&gt;.read</c>.
/// </summary>
public static class PayPermissions
{
    public const string GroupsRead = "hrm.pay_groups.read";

    /// <summary>Create a pay group (FR-HRM-PAY-01).</summary>
    public const string GroupsCreate = "hrm.pay_groups.create";

    /// <summary>Change a pay group, or switch it off (FR-HRM-PAY-01).</summary>
    public const string GroupsUpdate = "hrm.pay_groups.update";

    public const string PeriodsRead = "hrm.pay_periods.read";

    /// <summary>Generate the periods of a tax year for a pay group (FR-HRM-PAY-01, -38).</summary>
    public const string PeriodsGenerate = "hrm.pay_periods.generate";

    /// <summary>Change the cut-off or payment date of a period before it opens (FR-HRM-PAY-38).</summary>
    public const string PeriodsUpdate = "hrm.pay_periods.update";

    /// <summary>See the readiness checklist of a period, including who is missing (FR-HRM-PAY-38).</summary>
    public const string ReadinessRead = "hrm.pay_readiness.read";

    public const string ComponentsRead = "hrm.pay_components.read";

    /// <summary>Create a pay component, with or without its first version (FR-HRM-PAY-02; S1.34b, CR-087 detail; the code the generated one had).</summary>
    public const string ComponentsCreate = "hrm.pay_components.create";

    /// <summary>Change a component or add a version of it (FR-HRM-PAY-02, -03, -05).</summary>
    public const string ComponentsUpdate = "hrm.pay_components.update";

    public const string StructuresRead = "hrm.salary_structures.read";

    /// <summary>Create a salary structure (FR-HRM-PAY-04).</summary>
    public const string StructuresCreate = "hrm.salary_structures.create";

    /// <summary>Change a salary structure: name, switch off, assign rule (held for a second person while the setting is on).</summary>
    public const string StructuresUpdate = "hrm.salary_structures.update";

    /// <summary>Set the component list of a structure from a date; also the edit right on the default amounts (salary-class data, GHR-06).</summary>
    public const string StructureComponentsUpdate = "hrm.salary_structure_components.update";

    /// <summary>Assign a structure with amounts to an employee or revise it (FR-HRM-PAY-04, -06).</summary>
    public const string CompensationCreate = "hrm.compensation_assignments.create";

    /// <summary>Also the edit right on the amounts of the lines (salary-class data, GHR-06).</summary>
    public const string CompensationLinesCreate = "hrm.compensation_lines.create";

    /// <summary>See an employee's salary arrangement and its history (CR-087; the amounts also need the salary field class, GHR-06).</summary>
    public const string CompensationRead = "hrm.pay.compensation.read";

    /// <summary>Approve or reject a held change of a structure or of an employee's compensation (CR-087 S1.34b detail; FR-XM-32).</summary>
    public const string ChangeApprove = "hrm.pay.change.approve";

    /// <summary>Every code declared in permissions.json for the pay set-up services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } =
    [
        GroupsCreate, GroupsUpdate, PeriodsGenerate, PeriodsUpdate, ReadinessRead,
        ComponentsCreate, ComponentsUpdate, StructuresCreate, StructuresUpdate, StructureComponentsUpdate,
        CompensationCreate, CompensationLinesCreate, CompensationRead, ChangeApprove,
    ];
}

/// <summary>HR events of the pay set-up in <c>hrm.outbox_messages</c> (slice S1.34a).</summary>
public static class PayEvents
{
    /// <summary>
    /// A period's cut-off is near (FR-HRM-PAY-38): snake_case payload for the notification catalogue
    /// {pay_period_id, pay_group_id, pay_group_code, period_no, cut_off_date, company_id, link}.
    /// </summary>
    public const string CutoffDue = "hrm.pay.cutoff_due";

    public const string AggregateType = "hrm.pay_period";

    /// <summary>
    /// A salary arrangement or revision took effect on the record (slice S1.34b; FR-HRM-PAY-06, GHR-04): snake_case-free ids and dates only,
    /// never an amount: {employmentId, companyId, eventId, assignmentId, previousAssignmentId, structureId, previousStructureId, validFrom,
    /// reasonCode, backdated, earliestDate, affectedPeriodIds}. Payroll recalculates from <c>earliestDate</c>; S1.39 turns the affected
    /// periods into arrears.
    /// </summary>
    public const string CompensationChanged = "hrm.compensation.changed";
}
