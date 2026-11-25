namespace Erp.Hrm.Contracts.Leave;

/// <summary>
/// Permissions of the leave engine (slice S1.32a, CR-086 S1.32a detail). The generated writes of <c>hrm.leave_types</c> and <c>hrm.leave_ledger</c> are closed, and in S1.32b those of the request, request day and year-end tables; reads stay
/// the generated <c>&lt;table&gt;.read</c>. The hand-written codes are declared in <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>.
/// </summary>
public static class LeavePermissions
{
    public const string TypesRead = "hrm.leave_types.read";

    /// <summary>Create leave types (keeps the code the generated create had).</summary>
    public const string TypesCreate = "hrm.leave_types.create";

    /// <summary>Change, retire and bring back leave types (keeps the code the generated update had).</summary>
    public const string TypesUpdate = "hrm.leave_types.update";

    /// <summary>Read ledger entries and balances (generated read; the employee's own through the scope <c>own</c>).</summary>
    public const string LedgerRead = "hrm.leave_ledger.read";

    /// <summary>Post opening balances, grants and adjustments and reverse entries. Sensitive: it changes what an employee may take and what payroll pays.</summary>
    public const string LedgerPost = "hrm.leave_ledger.post";

    /// <summary>Recompute balances from the ledger and the policies (a report; nothing is changed).</summary>
    public const string Recompute = "hrm.leave_ledger.recompute";

    /// <summary>Read leave requests and their days (generated read; the employee's own and the manager's team through the data scope).</summary>
    public const string RequestsRead = "hrm.leave_requests.read";

    /// <summary>Apply for leave (S1.32b; keeps the code the generated create had): one's own, or, with a wider scope, for an employee (GHR-09).</summary>
    public const string RequestsCreate = "hrm.leave_requests.create";

    /// <summary>Change a draft, submit it, and ask to change approved leave (S1.32b; keeps the code the generated update had).</summary>
    public const string RequestsUpdate = "hrm.leave_requests.update";

    /// <summary>Cancel a draft or approved leave (S1.32b, FR-HRM-LVE-06).</summary>
    public const string RequestsCancel = "hrm.leave_requests.cancel";

    /// <summary>Read year-end runs (generated read).</summary>
    public const string YearEndRead = "hrm.leave_year_end_runs.read";

    /// <summary>Confirm or roll back a year-end preview (S1.32b, FR-HRM-LVE-11). Sensitive: it carries forward, pays out and lapses leave.</summary>
    public const string YearEndConfirm = "hrm.leave_year_end_runs.confirm";

    /// <summary>Approve a held year-end confirmation as the second person (setting <c>hrm.leave.year_end_requires_approval</c>).</summary>
    public const string YearEndApprove = "hrm.leave_year_end_runs.approve";

    /// <summary>The hand-written codes (declared in permissions.json).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [TypesCreate, TypesUpdate, LedgerPost, Recompute, RequestsCreate, RequestsUpdate, RequestsCancel, YearEndConfirm, YearEndApprove];
}
