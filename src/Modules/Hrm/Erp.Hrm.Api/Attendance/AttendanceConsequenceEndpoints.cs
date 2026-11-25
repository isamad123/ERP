using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Attendance;

/// <summary>
/// The overtime and lateness endpoints of slice S1.29b (FR-HRM-ATT-12, ATT-13, FR-XM-26, FR-XM-32): claim worked overtime, ask for overtime before the
/// day, and ask for a lateness penalty to be waived. The generated reads (<c>/api/hrm/overtime-entries</c>, <c>/api/hrm/attendance-penalties</c>) list
/// them in the caller's data scope; their generated writes are closed. Decisions go through the shared approval endpoints (<c>/api/wf</c>). Answers are
/// as for the person endpoints; a request that waits for a decision is 202.
/// </summary>
public static class AttendanceConsequenceEndpoints
{
    private const string Overtime = "hrm.overtime_entries";
    private const string Penalties = "hrm.attendance_penalties";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmAttendanceConsequenceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/attendance/overtime-entries/{id:guid}/claim", async (Guid id, OvertimeClaimInput body, IAttendanceConsequenceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ClaimOvertimeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Overtime, AttendanceConsequencePermissions.OvertimeSubmit);
        Tag(app.MapPost("/api/hrm/attendance/overtime-requests", async (OvertimeRequestInput body, IAttendanceConsequenceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestOvertimeAsync(body, ct).ConfigureAwait(false))), "create", Overtime, AttendanceConsequencePermissions.OvertimeSubmit);
        Tag(app.MapPost("/api/hrm/attendance/penalties/{id:guid}/waiver", async (Guid id, PenaltyWaiverInput body, IAttendanceConsequenceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestWaiverAsync(id, body, ct).ConfigureAwait(false))), "record_action", Penalties, AttendanceConsequencePermissions.PenaltyWaive);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
