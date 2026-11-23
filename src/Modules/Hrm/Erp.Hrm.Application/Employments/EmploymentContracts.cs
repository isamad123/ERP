using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Employments;

/// <summary>One share of an assignment's cost allocation: exactly one of a cost centre reference or a department, and a percent.</summary>
public sealed class CostShareInput
{
    /// <summary>A cost centre reference (text until Finance exists, D-43).</summary>
    public string? CostCenterRef { get; init; }

    /// <summary>An org unit of the employment's company.</summary>
    public Guid? Department { get; init; }

    /// <summary>The share in percent (above 0, at most 100); the shares sum to exactly 100.</summary>
    public decimal? Percent { get; init; }
}

/// <summary>
/// The job details of an assignment (FR-HRM-EMP-02). On a hire every required field is given; on a change only the fields that
/// change (null leaves a field as it is; <see cref="Clear"/> empties an optional one).
/// </summary>
public sealed class AssignmentInput
{
    public Guid? OrgUnitId { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? LocationId { get; init; }

    /// <summary>A position of the company (positions arrive with S1.22; optional).</summary>
    public Guid? PositionId { get; init; }

    /// <summary>A grade of the company (grades arrive with S1.22; optional).</summary>
    public Guid? GradeId { get; init; }

    public string? Title { get; init; }

    /// <summary>The line manager: another employment of the same company.</summary>
    public Guid? ManagerEmploymentId { get; init; }

    /// <summary>Shares summing to 100 percent; on a hire without it the whole cost goes to the assignment's org unit.</summary>
    public IReadOnlyList<CostShareInput>? CostAllocation { get; init; }

    /// <summary>Full-time equivalent above 0 and at most 1 (default 1).</summary>
    public decimal? Fte { get; init; }

    public string? WorkSchedule { get; init; }

    /// <summary>An acting charge (an <c>acting</c> event sets it on unless it says otherwise).</summary>
    public bool? IsActing { get; init; }

    /// <summary>Optional fields to empty: locationId, positionId, gradeId, managerEmploymentId, workSchedule.</summary>
    public IReadOnlyList<string>? Clear { get; init; }
}

/// <summary>
/// A hire (slice S1.20 Build item 5, FR-HRM-EMP-02, -04, -07): an existing person (<see cref="PersonId"/>) or a new one
/// (<see cref="Person"/>, created in the same transaction; R-111), a company, the employment details and the first assignment.
/// </summary>
public sealed class HireRequest
{
    public Guid? PersonId { get; init; }

    public CreatePersonRequest? Person { get; init; }

    public Guid? CompanyId { get; init; }

    /// <summary>A code of the employee type list (permanent, contract, probation, intern, daily_wage, consultant, part_time).</summary>
    public string? EmployeeType { get; init; }

    public DateOnly? JoiningDate { get; init; }

    /// <summary>Defaults to the joining date.</summary>
    public DateOnly? ServiceStartDate { get; init; }

    public DateOnly? ExpectedEndDate { get; init; }

    /// <summary>A typed employee number; only with <c>hrm.employments.number_override</c> (otherwise the series gives it).</summary>
    public string? EmployeeNo { get; init; }

    /// <summary>A reason code of the hire list (default <c>new_hire</c>).</summary>
    public string? ReasonCode { get; init; }

    public string? Notes { get; init; }

    public AssignmentInput? Assignment { get; init; }

    /// <summary>Custom field values of the employment by key (S1.15b, FR-KRN-MET-02): checked against the active definitions and field security.</summary>
    public System.Text.Json.JsonElement? Custom { get; init; }
}

/// <summary>Employment fields a <c>data_correction</c> event may correct (ruling Q2). Null leaves a field; Clear empties expectedEndDate.</summary>
public sealed class EmploymentFieldsInput
{
    public string? EmployeeType { get; init; }

    public DateOnly? ServiceStartDate { get; init; }

    public DateOnly? ExpectedEndDate { get; init; }

    public IReadOnlyList<string>? Clear { get; init; }
}

/// <summary>
/// A change of an employment (slice S1.20 Build item 6, FR-HRM-EMP-03, BR-02): its kind, reason, effective date and what changes.
/// A <c>data_correction</c> with <see cref="CorrectsEventId"/> replaces that earlier event: it takes the earlier event's changes,
/// overridden by <see cref="Assignment"/>, from its own <see cref="EffectiveDate"/> (FR-XM-05: nothing is edited in place).
/// </summary>
public sealed class EmploymentChangeRequest
{
    /// <summary>promotion, demotion, transfer, grade_change, acting, secondment, confirmation or data_correction.</summary>
    public string? Kind { get; init; }

    public string? ReasonCode { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public string? Notes { get; init; }

    public Guid? CorrectsEventId { get; init; }

    public AssignmentInput? Assignment { get; init; }

    public EmploymentFieldsInput? Employment { get; init; }

    /// <summary>The employment's row version the caller read (required to save; optimistic concurrency).</summary>
    public long? RowVersion { get; init; }
}

/// <summary>Link an employment to a kernel user (FR-HRM-EMP-17, ruling Q8).</summary>
public sealed class UserLinkRequest
{
    public Guid? UserId { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>
/// The employment services (slice S1.20b Build items 5 to 10). Answers are <see cref="PeopleResult"/>s like the person services.
/// Every write checks the permission, the data scope before and after, field security, and saves in one transaction with the
/// change log under the acting user (GHR-09).
/// </summary>
/// <summary>
/// The employee list (slice S1.21a Build item 1, FR-HRM-EMP-01). Every filter is optional. <paramref name="Search"/> matches the number, the job title
/// and, for a caller who may read persons, the names (including the local-script name); <paramref name="AsOf"/> (a date) lists the employments
/// that had started and not ended on that date with their job details of that date (the caller passes its own "today": the server uses the UTC date
/// when it is missing). <paramref name="Sort"/> is <c>employeeNo</c> (default) or <c>joiningDate</c> (newest first); <paramref name="After"/> is the
/// <c>next</c> value of the previous page.
/// </summary>
public sealed record EmployeeListQuery(
    string? Search, string? EmployeeNo, Guid? CompanyId, Guid? OrgUnitId, string? Title, Guid? ManagerEmploymentId, string? Status, string? EmployeeType,
    DateOnly? JoinedFrom, DateOnly? JoinedTo, DateOnly? AsOf, string? Sort, string? After, int? Limit);

public interface IEmploymentService
{
    /// <summary>The employees the caller may read, with name and job details, by the filters of <paramref name="query"/>; no pay data (GHR-06).</summary>
    Task<PeopleResult> EmployeeListAsync(EmployeeListQuery query, CancellationToken cancellationToken);

    Task<PeopleResult> HireAsync(HireRequest request, CancellationToken cancellationToken);

    /// <summary>The backdated change check and the validation of a change, without saving it (GHR-04: shown before saving).</summary>
    Task<PeopleResult> CheckChangeAsync(Guid employmentId, EmploymentChangeRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> RecordChangeAsync(Guid employmentId, EmploymentChangeRequest request, CancellationToken cancellationToken);

    /// <summary>The employment and its assignment as of a date (GHR-03; today when null).</summary>
    Task<PeopleResult> AsOfAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken);

    /// <summary>The events of the employment with what they changed, who and when, newest effective date first (FR-HRM-EMP-11).</summary>
    Task<PeopleResult> TimelineAsync(Guid employmentId, string? kind, CancellationToken cancellationToken);

    Task<PeopleResult> LinkUserAsync(Guid employmentId, UserLinkRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UnlinkUserAsync(Guid employmentId, long? rowVersion, CancellationToken cancellationToken);

    /// <summary>Employee types, event kinds and their reason codes (provisional until the option lists of S1.15b).</summary>
    Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken);
}
