namespace Erp.Hrm.Contracts.Positions;

/// <summary>
/// Permissions of the position services (slice S1.22a, CR-078). The generated writes of <c>hrm.positions</c> are closed (a change
/// from a date adds a row), so their actions are declared by hand in <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>; grades and
/// headcount budgets keep their generated create, update and delete. Reads stay the generated <c>&lt;table&gt;.read</c>.
/// </summary>
public static class PositionPermissions
{
    public const string PositionsRead = "hrm.positions.read";

    /// <summary>Create a position (FR-HRM-POS-02).</summary>
    public const string PositionsCreate = "hrm.positions.create";

    /// <summary>Change a position from a date, freeze or unfreeze it.</summary>
    public const string PositionsUpdate = "hrm.positions.update";

    /// <summary>Close a position from a date (it must have no holder from then on).</summary>
    public const string PositionsClose = "hrm.positions.close";

    /// <summary>Ask for a strength exception: one placement beyond a budget (BR-04, slice S1.22 ruling Q5).</summary>
    public const string ExceptionsCreate = "hrm.strength_exceptions.create";

    /// <summary>Every code declared in permissions.json for the position services (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [PositionsCreate, PositionsUpdate, PositionsClose, ExceptionsCreate];
}

/// <summary>The two ways a company plans its strength (FR-HRM-POS-01), the values of the company setting <see cref="SettingKey"/>.</summary>
public static class OrgModels
{
    public const string Position = "position";

    public const string Headcount = "headcount";

    public const string SettingKey = "hrm.org.model";

    public const string DocumentType = "hrm.strength_exception";
}
