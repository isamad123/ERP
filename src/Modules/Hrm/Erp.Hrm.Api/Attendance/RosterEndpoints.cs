using System.Text.Json;
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
/// The shift and roster endpoints (slice S1.28a; FR-HRM-ATT-06, ATT-07): create and change a shift (a change from a date is a new version),
/// the weekly-off days of an employee's shift policy, and a roster's life: create, assign shifts to people or a group, copy forward, edit
/// single days, check and publish. Reads of the rows stay the generated <c>/api/hrm/shifts</c>, <c>/rosters</c> and <c>/roster-lines</c>
/// (their generated writes are closed). Answers are as for the person endpoints.
/// </summary>
public static class RosterEndpoints
{
    private const string Shifts = "hrm.shifts";
    private const string Rosters = "hrm.rosters";
    private const string Employments = "hrm.employments";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmRosterEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/shifts", async (JsonElement body, IShiftService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(ShiftRequest.Parse(body), ct).ConfigureAwait(false))), "create", Shifts, ShiftPermissions.ShiftsCreate);
        Tag(app.MapPut("/api/hrm/shifts/{id:guid}", async (Guid id, JsonElement body, IShiftService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, ShiftRequest.Parse(body), ct).ConfigureAwait(false))), "record_action", Shifts, ShiftPermissions.ShiftsUpdate);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/weekly-off", async (Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, IShiftService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.WeeklyOffAsync(id, from, to, ct).ConfigureAwait(false))), "record_action", Employments, ShiftPermissions.WeeklyOffRead);

        Tag(app.MapPost("/api/hrm/rosters", async (RosterRequest body, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Rosters, ShiftPermissions.RostersCreate);
        Tag(app.MapPut("/api/hrm/rosters/{id:guid}", async (Guid id, RosterRequest body, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Rosters, ShiftPermissions.RostersUpdate);
        Tag(app.MapPost("/api/hrm/rosters/{id:guid}/assign", async (Guid id, AssignRequest body, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AssignAsync(id, body, ct).ConfigureAwait(false))), "record_action", Rosters, ShiftPermissions.RostersUpdate);
        Tag(app.MapPost("/api/hrm/rosters/{id:guid}/copy-forward", async (Guid id, CopyForwardRequest body, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CopyForwardAsync(id, body, ct).ConfigureAwait(false))), "record_action", Rosters, ShiftPermissions.RostersCreate);
        Tag(app.MapPut("/api/hrm/rosters/{id:guid}/days", async (Guid id, ChangeDaysRequest body, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChangeDaysAsync(id, body, ct).ConfigureAwait(false))), "record_action", Rosters, ShiftPermissions.RostersUpdate);
        Tag(app.MapGet("/api/hrm/rosters/{id:guid}/checks", async (Guid id, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChecksAsync(id, ct).ConfigureAwait(false))), "record_action", Rosters, ShiftPermissions.RostersRead);
        Tag(app.MapPost("/api/hrm/rosters/{id:guid}/publish", async (Guid id, PublishRosterRequest body, IRosterService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PublishAsync(id, body, ct).ConfigureAwait(false))), "record_action", Rosters, ShiftPermissions.RostersPublish);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
