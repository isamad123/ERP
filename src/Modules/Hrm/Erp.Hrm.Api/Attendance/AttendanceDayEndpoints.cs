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
/// The attendance engine endpoints of slice S1.29a: process an employment's days now, its day results (now, as known at a moment, or with
/// their history), the explanation of one day, and the exception queue. The generated <c>/api/hrm/attendance-days</c>,
/// <c>/api/hrm/attendance-exceptions</c> and <c>/api/hrm/overtime-entries</c> stay for reading rows (their generated writes are closed).
/// A company's or unit's days are processed by the job <c>hrm.attendance_process</c> through the job centre. Answers are as for the person endpoints.
/// </summary>
public static class AttendanceDayEndpoints
{
    private const string Employments = "hrm.employments";
    private const string Exceptions = "hrm.attendance_exceptions";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmAttendanceDayEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/employments/{id:guid}/attendance-days/process", async (Guid id, ProcessDaysRequest body, IAttendanceDayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ProcessAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, AttendanceEnginePermissions.Process);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/attendance-days", async (Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] DateTimeOffset? asOf, [FromQuery] bool? history, IAttendanceDayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DaysAsync(id, from, to, asOf, history ?? false, ct).ConfigureAwait(false))), "record_action", Employments, AttendanceEnginePermissions.DaysRead);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/attendance-days/{date}/explain", async (Guid id, DateOnly date, IAttendanceDayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ExplainAsync(id, date, ct).ConfigureAwait(false))), "record_action", Employments, AttendanceEnginePermissions.DaysRead);
        Tag(app.MapGet("/api/hrm/attendance/exceptions", async ([FromQuery] Guid? companyId, [FromQuery] string? status, [FromQuery] string? kind, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? limit, [FromQuery] Guid? unitId, [FromQuery] int? minAgeDays, IAttendanceDayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ExceptionsAsync(companyId, status, kind, from, to, limit, ct, unitId, minAgeDays).ConfigureAwait(false))), "catalog", Exceptions, AttendanceEnginePermissions.ExceptionsRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
