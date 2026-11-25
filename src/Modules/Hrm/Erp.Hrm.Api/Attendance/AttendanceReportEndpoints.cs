using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Attendance;

/// <summary>
/// The live attendance dashboard and the standard attendance reports (slice S1.30b; FR-HRM-ATT-18, ATT-19, RPT-05, SSP-02): <c>/api/hrm/attendance/dashboard</c>
/// (counts of today by unit and location) and <c>/dashboard/people</c> (the people behind a count), <c>/api/hrm/attendance/reports</c> (the reports the caller
/// may run) and <c>/reports/{code}</c> (one report with its filters, totals and a page of rows). The permission opens the endpoint; the data scope of the
/// caller's grant on the underlying table decides which people appear. Answers are as for the person endpoints.
/// </summary>
public static class AttendanceReportEndpoints
{
    private const string Days = "hrm.attendance_days";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmAttendanceReportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/hrm/attendance/dashboard", async ([FromQuery] Guid? companyId, [FromQuery] Guid? unitId, [FromQuery] Guid? locationId, IAttendanceDashboardService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DashboardAsync(companyId, unitId, locationId, ct).ConfigureAwait(false))), AttendanceReportPermissions.Dashboard);
        Tag(app.MapGet("/api/hrm/attendance/dashboard/people", async ([FromQuery] Guid? companyId, [FromQuery] string? status, [FromQuery] Guid? unitId, [FromQuery] Guid? locationId, [FromQuery] int? limit, [FromQuery] int? offset, IAttendanceDashboardService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PeopleAsync(companyId, status, unitId, locationId, limit, offset, ct).ConfigureAwait(false))), AttendanceReportPermissions.Dashboard);
        Tag(app.MapGet("/api/hrm/attendance/reports", async (IAttendanceReportService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CatalogueAsync(ct).ConfigureAwait(false))), AttendanceReportPermissions.Reports);
        Tag(app.MapGet("/api/hrm/attendance/reports/{code}", async (string code, [AsParameters] ReportParameters p, IAttendanceReportService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RunAsync(code, p.ToQuery(), p.Limit, p.Offset, ct).ConfigureAwait(false))), AttendanceReportPermissions.Reports);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string permission) =>
        endpoint.WithTags(Days).WithMetadata(new HrmEndpointMetadata("catalog", Days, permission), Core);

    /// <summary>The query string of a report: the filters of <see cref="AttendanceReportQuery"/> plus the page.</summary>
    internal sealed class ReportParameters
    {
        public Guid? CompanyId { get; init; }

        public DateOnly? From { get; init; }

        public DateOnly? To { get; init; }

        public Guid? UnitId { get; init; }

        public Guid? BranchId { get; init; }

        public Guid? LocationId { get; init; }

        public Guid? EmploymentId { get; init; }

        public string? Search { get; init; }

        public string? Status { get; init; }

        public string? Kind { get; init; }

        public int? MinAgeDays { get; init; }

        public int? Limit { get; init; }

        public int? Offset { get; init; }

        public AttendanceReportQuery ToQuery() => new()
        {
            CompanyId = CompanyId, From = From, To = To, UnitId = UnitId, BranchId = BranchId, LocationId = LocationId, EmploymentId = EmploymentId, Search = Search, Status = Status, Kind = Kind, MinAgeDays = MinAgeDays,
        };
    }
}
