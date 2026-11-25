using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Leave;

/// <summary>The filters of a leave report. A filter a report has no use for is ignored (S1.33b, FR-HRM-LVE-10).</summary>
public sealed class LeaveReportQuery
{
    public Guid? CompanyId { get; init; }

    public Guid? UnitId { get; init; }

    public Guid? EmploymentId { get; init; }

    public Guid? LeaveTypeId { get; init; }

    /// <summary>Balance summary: the leave year (default: the year of the company's today).</summary>
    public int? LeaveYear { get; init; }

    /// <summary>Leave taken: the first month; pending requests: the first start date.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Leave taken: the last month; pending requests: the last start date.</summary>
    public DateOnly? To { get; init; }

    /// <summary>Employee number or name contains.</summary>
    public string? Search { get; init; }

    /// <summary>Pending requests: <c>submitted</c> or <c>cancel_requested</c>.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// The standard leave reports (slice S1.33 Build item 9, FR-HRM-LVE-10, FR-HRM-RPT-05): balance summary by unit and leave type, leave taken by type and month,
/// and requests waiting for a decision, each over a read-only database view with the data scope of the caller applied here. No report has a pay or salary
/// field (FR-HRM-GHR-06). The export centre reads the same rows through <see cref="ReadRowsAsync"/> as the person who asked.
/// </summary>
public interface ILeaveReportService
{
    /// <summary>The reports the caller may run, with their columns.</summary>
    Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken);

    Task<PeopleResult> RunAsync(string code, LeaveReportQuery query, int? limit, int? offset, CancellationToken cancellationToken);

    /// <summary>One page of a report for the export engine: the rows as JSON objects, in the report's fixed order, and the offset of the next page.</summary>
    Task<(IReadOnlyList<System.Text.Json.Nodes.JsonObject> Rows, int? Next)?> ReadRowsAsync(string code, LeaveReportQuery query, int offset, int limit, CancellationToken cancellationToken);

    /// <summary>The column keys and JSON names of a report (for the export engine); null for an unknown code.</summary>
    IReadOnlyList<(string Key, string JsonName)>? ColumnsOf(string code);
}
