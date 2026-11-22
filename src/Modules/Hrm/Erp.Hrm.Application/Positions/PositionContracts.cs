using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Positions;

/// <summary>A new position (slice S1.22 Build item 3, FR-HRM-POS-02). The first row of its code, valid from <see cref="ValidFrom"/>.</summary>
public sealed class PositionCreateRequest
{
    public Guid? CompanyId { get; init; }

    /// <summary>The position's code, unique in the company (any case).</summary>
    public string? PositionCode { get; init; }

    public string? Title { get; init; }

    public Guid? OrgUnitId { get; init; }

    public Guid? GradeId { get; init; }

    /// <summary>The position this one reports to (any row of it; a position is its code). No cycles.</summary>
    public Guid? ReportsToPositionId { get; init; }

    public Guid? LocationId { get; init; }

    public Guid? CostCenterRef { get; init; }

    /// <summary>Budgeted full-time equivalent (0 or more; default 1).</summary>
    public decimal? BudgetedFte { get; init; }

    /// <summary>The first day of the position (default: today). A date in the future makes it a planned position.</summary>
    public DateOnly? ValidFrom { get; init; }
}

/// <summary>
/// A change of a position from a date: a new row starts that day with the values given, the rest carried over, the old row ends the
/// day before. <see cref="Status"/> freezes (<c>frozen</c>) or reopens (<c>open</c>) the position; null leaves it.
/// </summary>
public sealed class PositionChangeRequest
{
    public DateOnly? EffectiveDate { get; init; }

    public string? Title { get; init; }

    public Guid? OrgUnitId { get; init; }

    public Guid? GradeId { get; init; }

    public Guid? ReportsToPositionId { get; init; }

    public Guid? LocationId { get; init; }

    public Guid? CostCenterRef { get; init; }

    public decimal? BudgetedFte { get; init; }

    /// <summary><c>open</c> or <c>frozen</c>; the other statuses follow from holders, dates and closing.</summary>
    public string? Status { get; init; }

    /// <summary>Optional fields to empty: gradeId, reportsToPositionId, locationId, costCenterRef.</summary>
    public IReadOnlyList<string>? Clear { get; init; }

    public long? RowVersion { get; init; }
}

public sealed class PositionCloseRequest
{
    public DateOnly? EffectiveDate { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>A request to place one person beyond a budget (BR-04; slice S1.22 ruling Q5).</summary>
public sealed class StrengthExceptionRequest
{
    public Guid? EmploymentId { get; init; }

    public Guid? OrgUnitId { get; init; }

    public Guid? GradeId { get; init; }

    /// <summary>Set in a position-model company: the position the person is to fill.</summary>
    public Guid? PositionId { get; init; }

    /// <summary>The day the placement starts.</summary>
    public DateOnly? EffectiveDate { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The position services (slice S1.22a Build items 1 and 3).</summary>
public interface IPositionService
{
    Task<PeopleResult> CreateAsync(PositionCreateRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ChangeAsync(Guid positionId, PositionChangeRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> CloseAsync(Guid positionId, PositionCloseRequest request, CancellationToken cancellationToken);

    /// <summary>Who holds the position on the date, its filled and vacant FTE and its status then.</summary>
    Task<PeopleResult> HoldersAsync(Guid positionId, DateOnly? asOf, CancellationToken cancellationToken);

    /// <summary>The planning model of every company the caller reads, with what a switch would need (Build item 1).</summary>
    Task<PeopleResult> OrgModelsAsync(CancellationToken cancellationToken);
}

/// <summary>The strength exception services (slice S1.22a Build item 5).</summary>
public interface IStrengthExceptionService
{
    Task<PeopleResult> RequestAsync(StrengthExceptionRequest request, CancellationToken cancellationToken);
}
