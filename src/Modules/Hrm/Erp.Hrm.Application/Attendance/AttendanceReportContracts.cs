using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// The live attendance dashboard (slice S1.30b Build item 5, FR-HRM-ATT-18, SSP-02): counts of present, late, absent, on leave, on duty, not yet punched
/// and not due for today of the caller's company or companies, in total and by unit and location, and the people behind each count (drill-down). Both
/// answers come from one classification, so a count always equals its list. The data scope of the caller applies; the answers are as of the moment asked.
/// </summary>
public interface IAttendanceDashboardService
{
    Task<PeopleResult> DashboardAsync(Guid? companyId, Guid? unitId, Guid? locationId, CancellationToken cancellationToken);

    Task<PeopleResult> PeopleAsync(Guid? companyId, string? status, Guid? unitId, Guid? locationId, int? limit, int? offset, CancellationToken cancellationToken);
}

/// <summary>The filters of a standard attendance report. A filter a report has no use for is ignored.</summary>
public sealed class AttendanceReportQuery
{
    public Guid? CompanyId { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public Guid? UnitId { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? LocationId { get; init; }

    public Guid? EmploymentId { get; init; }

    /// <summary>Employee number or name contains.</summary>
    public string? Search { get; init; }

    /// <summary>Day result of the daily muster, device state or exception status.</summary>
    public string? Status { get; init; }

    /// <summary>Exception kind or punch source.</summary>
    public string? Kind { get; init; }

    /// <summary>Exception ageing: only exceptions at least this many days old.</summary>
    public int? MinAgeDays { get; init; }
}

/// <summary>
/// The standard attendance reports (slice S1.30b Build item 6, FR-HRM-ATT-19, RPT-05): muster (daily and monthly), absenteeism, late and early,
/// punch log, device sync and exception ageing, each over a read-only database view with the data scope applied here. No report has a pay or salary
/// field. The export centre reads the same rows through <see cref="ReadRowsAsync"/> as the person who asked.
/// </summary>
public interface IAttendanceReportService
{
    /// <summary>The reports the caller may run, with their columns.</summary>
    Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken);

    Task<PeopleResult> RunAsync(string code, AttendanceReportQuery query, int? limit, int? offset, CancellationToken cancellationToken);

    /// <summary>One page of a report for the export engine: the rows as JSON objects, in the report's fixed order, and the offset of the next page.</summary>
    Task<(IReadOnlyList<System.Text.Json.Nodes.JsonObject> Rows, int? Next)?> ReadRowsAsync(string code, AttendanceReportQuery query, int offset, int limit, CancellationToken cancellationToken);

    /// <summary>The column keys and JSON names of a report (for the export engine); null for an unknown code.</summary>
    IReadOnlyList<(string Key, string JsonName)>? ColumnsOf(string code);
}
