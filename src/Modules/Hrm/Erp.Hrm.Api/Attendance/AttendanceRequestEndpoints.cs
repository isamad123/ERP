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
/// The attendance request endpoints of slice S1.30a (FR-HRM-ATT-11, ATT-14, GHR-09): ask for a correction of a day's punches and for on duty,
/// official visit, work from home or field duty. The generated reads (<c>/api/hrm/regularization-requests</c>, <c>/api/hrm/duty-requests</c>) list
/// the requests in the caller's data scope; their generated writes are closed. A request is decided, and cancelled by its requester, through the
/// shared approval endpoints (<c>/api/wf</c>). Answers are as for the person endpoints; a request that waits for a decision is 202.
/// </summary>
public static class AttendanceRequestEndpoints
{
    private const string Regularizations = "hrm.regularization_requests";
    private const string Duties = "hrm.duty_requests";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmAttendanceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/attendance/regularization-requests", async (RegularizationRequestInput body, IAttendanceRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestRegularizationAsync(body, ct).ConfigureAwait(false))), "create", Regularizations, AttendanceRequestPermissions.RegularizationCreate);
        Tag(app.MapPost("/api/hrm/attendance/duty-requests", async (DutyRequestInput body, IAttendanceRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestDutyAsync(body, ct).ConfigureAwait(false))), "create", Duties, AttendanceRequestPermissions.DutyCreate);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
