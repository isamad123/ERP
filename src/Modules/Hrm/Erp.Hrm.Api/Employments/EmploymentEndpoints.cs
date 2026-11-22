using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Employments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Employments;

/// <summary>
/// The HR employment endpoints (slice S1.20b, ruling Q1): hire, record and check a change, the as-of record and the timeline, the
/// user link, and the catalogue. Reads of the rows stay the generated <c>/api/hrm/employments</c>, <c>/api/hrm/employment-assignments</c>
/// (with <c>asOf</c>) and <c>/api/hrm/employment-events</c>; their generated writes are closed. Answers are as for the person endpoints.
/// </summary>
public static class EmploymentEndpoints
{
    private const string Employments = "hrm.employments";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmEmploymentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/employments", async (HireRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.HireAsync(body, ct).ConfigureAwait(false))), "create", Employments, EmploymentPermissions.EmploymentsCreate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/events", async (Guid id, EmploymentChangeRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RecordChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EventsCreate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/events/check", async (Guid id, EmploymentChangeRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CheckChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EventsCreate);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/as-of", async (Guid id, [FromQuery] DateOnly? date, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AsOfAsync(id, date, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EmploymentsRead);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/timeline", async (Guid id, [FromQuery] string? kind, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.TimelineAsync(id, kind, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EventsRead);
        Tag(app.MapPut("/api/hrm/employments/{id:guid}/user", async (Guid id, UserLinkRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.LinkUserAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.UserLink);
        Tag(app.MapDelete("/api/hrm/employments/{id:guid}/user", async (Guid id, [FromQuery] long? rowVersion, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UnlinkUserAsync(id, rowVersion, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.UserLink);
        Tag(app.MapGet("/api/hrm/employment-catalogue", async (IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CatalogueAsync(ct).ConfigureAwait(false))), "catalog", Employments, EmploymentPermissions.EmploymentsRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
