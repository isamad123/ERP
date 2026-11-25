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
/// The attendance period lock endpoints of slice S1.31 (FR-XM-26, FR-HRM-ATT-17, ATT-21, ATT-64, GHR-04, FR-XM-32): the lock console's periods (by pay
/// period id), HR's lock request after the cut-off date, the request to cancel the locking run and reopen, the punches received after the lock, and
/// retroactive adjustments with their impact. The generated reads (<c>/api/hrm/attendance-periods</c>, <c>/api/hrm/attendance-adjustments</c>) stay;
/// their generated writes are closed. Decisions go through the shared approval endpoints (<c>/api/wf</c>); a request waiting for one is 202.
/// </summary>
public static class AttendanceLockEndpoints
{
    private const string Periods = "hrm.attendance_periods";
    private const string Adjustments = "hrm.attendance_adjustments";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmAttendanceLockEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/hrm/attendance/periods", async ([FromQuery] Guid? companyId, [FromQuery] Guid? payGroupId, IAttendanceLockService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(companyId, payGroupId, ct).ConfigureAwait(false))), "catalog", Periods, AttendanceLockPermissions.PeriodsRead);
        Tag(app.MapGet("/api/hrm/attendance/periods/{id:guid}", async (Guid id, IAttendanceLockService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, ct).ConfigureAwait(false))), "record_action", Periods, AttendanceLockPermissions.PeriodsRead);
        Tag(app.MapPost("/api/hrm/attendance/periods/{id:guid}/lock-request", async (Guid id, PeriodLockRequestInput body, IAttendanceLockService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestLockAsync(id, body, ct).ConfigureAwait(false))), "record_action", Periods, AttendanceLockPermissions.Lock);
        Tag(app.MapPost("/api/hrm/attendance/periods/{id:guid}/reopen-request", async (Guid id, PeriodReopenRequestInput body, IAttendanceLockService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestReopenAsync(id, body, ct).ConfigureAwait(false))), "record_action", Periods, AttendanceLockPermissions.Reopen);
        Tag(app.MapGet("/api/hrm/attendance/periods/{id:guid}/late-events", async (Guid id, IAttendanceLockService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.LateEventsAsync(id, ct).ConfigureAwait(false))), "record_action", Periods, AttendanceLockPermissions.PeriodsRead);

        Tag(app.MapPost("/api/hrm/attendance/adjustments/impact", async (AttendanceAdjustmentRequestInput body, IAttendanceAdjustmentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ImpactAsync(body, ct).ConfigureAwait(false))), "create", Adjustments, AttendanceLockPermissions.AdjustmentsSubmit);
        Tag(app.MapPost("/api/hrm/attendance/adjustments", async (AttendanceAdjustmentRequestInput body, IAttendanceAdjustmentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.SubmitAsync(body, ct).ConfigureAwait(false))), "create", Adjustments, AttendanceLockPermissions.AdjustmentsSubmit);
        Tag(app.MapGet("/api/hrm/attendance/adjustments", async ([FromQuery] Guid? companyId, [FromQuery] Guid? employmentId, [FromQuery] string? status, IAttendanceAdjustmentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(companyId, employmentId, status, ct).ConfigureAwait(false))), "catalog", Adjustments, AttendanceLockPermissions.AdjustmentsRead);
        Tag(app.MapGet("/api/hrm/attendance/adjustments/{id:guid}", async (Guid id, IAttendanceAdjustmentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, ct).ConfigureAwait(false))), "record_action", Adjustments, AttendanceLockPermissions.AdjustmentsRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
