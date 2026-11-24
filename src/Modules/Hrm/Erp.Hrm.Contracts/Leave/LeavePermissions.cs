namespace Erp.Hrm.Contracts.Leave;

/// <summary>
/// Permissions of the leave engine (slice S1.32a, CR-086 S1.32a detail). The generated writes of the five leave tables are closed; reads stay
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

    /// <summary>The hand-written codes (declared in permissions.json).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [TypesCreate, TypesUpdate, LedgerPost, Recompute];
}
