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
/// The attendance capture endpoints (slice S1.27a; FR-HRM-ATT-01, ATT-03, DAT-01, DAT-02). Reads of the stored punches stay the generated
/// <c>/api/hrm/attendance-events</c>; its generated write is closed, every punch is appended by the event store. The person of a web or QR
/// punch comes from the token, never from the request; the caller's IP is the connection's address.
/// </summary>
public static class AttendanceEndpoints
{
    private const string Events = "hrm.attendance_events";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/attendance/events", async (AppendEventsRequest body, IAttendanceCaptureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AppendAsync(body, ct).ConfigureAwait(false))), "create", AttendancePermissions.EventsAppend);
        Tag(app.MapPost("/api/hrm/attendance/punch", async (PunchRequest body, HttpContext http, IAttendanceCaptureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PunchAsync(body, Address(http), ct).ConfigureAwait(false))), "create", AttendancePermissions.Punch);
        Tag(app.MapPost("/api/hrm/attendance/qr-punch", async (QrPunchRequest body, HttpContext http, IAttendanceCaptureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.QrPunchAsync(body, Address(http), ct).ConfigureAwait(false))), "create", AttendancePermissions.Punch);
        Tag(app.MapPost("/api/hrm/attendance/kiosk/punch", async (KioskPunchRequest body, HttpContext http, IAttendanceCaptureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.KioskPunchAsync(body, Address(http), ct).ConfigureAwait(false))), "create", AttendancePermissions.Kiosk);
        Tag(app.MapGet("/api/hrm/attendance/my-day", async ([FromQuery] Guid? companyId, IAttendanceCaptureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.MyDayAsync(companyId, ct).ConfigureAwait(false))), "catalog", AttendancePermissions.Punch);
        Tag(app.MapGet("/api/hrm/attendance/qr-code", async ([FromQuery] Guid companyId, [FromQuery] Guid locationId, IAttendanceCaptureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.QrCodeAsync(companyId, locationId, ct).ConfigureAwait(false))), "record_action", AttendancePermissions.QrDisplay, "org.locations");
        return app;
    }

    private static string? Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission, string resource = Events) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
