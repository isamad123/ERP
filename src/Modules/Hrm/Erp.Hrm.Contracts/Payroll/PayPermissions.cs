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

    /// <summary>Every code declared in permissions.json for the pay set-up services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [GroupsCreate, GroupsUpdate, PeriodsGenerate, PeriodsUpdate, ReadinessRead];
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
}
