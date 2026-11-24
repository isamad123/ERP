using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Positions;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Positions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Positions;

/// <summary>
/// The HR position endpoints (slice S1.22a, FR-HRM-POS-01, -02): create, change from a date, close, holders, the planning model, and
/// the request for a strength exception. Reads of the rows stay the generated <c>/api/hrm/positions</c> (with <c>asOf</c>),
/// <c>/api/hrm/grades</c>, <c>/api/hrm/headcount-budgets</c> and <c>/api/hrm/strength-exceptions</c>; grades and budgets keep their
/// generated writes, positions and exceptions are written here. Answers are as for the person endpoints.
/// </summary>
public static class PositionEndpoints
{
    private const string Positions = "hrm.positions";
    private const string Exceptions = "hrm.strength_exceptions";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmPositionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/positions", async (PositionCreateRequest body, IPositionService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Positions, PositionPermissions.PositionsCreate);
        Tag(app.MapPost("/api/hrm/positions/{id:guid}/changes", async (Guid id, PositionChangeRequest body, IPositionService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsUpdate);
        Tag(app.MapPost("/api/hrm/positions/{id:guid}/close", async (Guid id, PositionCloseRequest body, IPositionService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CloseAsync(id, body, ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsClose);
        Tag(app.MapGet("/api/hrm/positions/{id:guid}/holders", async (Guid id, [FromQuery] DateOnly? asOf, IPositionService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.HoldersAsync(id, asOf, ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsRead);
        Tag(app.MapGet("/api/hrm/org-model", async (IPositionService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.OrgModelsAsync(ct).ConfigureAwait(false))), "catalog", Positions, PositionPermissions.PositionsRead);
        Tag(app.MapPost("/api/hrm/strength-exceptions", async (StrengthExceptionRequest body, IStrengthExceptionService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RequestAsync(body, ct).ConfigureAwait(false))), "create", Exceptions, PositionPermissions.ExceptionsCreate);

        // Strength report and org chart (slice S1.22b, FR-HRM-POS-03, -04): reads of the caller's data scope, under hrm.positions.read.
        Tag(app.MapGet("/api/hrm/strength-report", async ([FromQuery] Guid? companyId, [FromQuery] DateOnly? asOf, [FromQuery] string? groupBy, [FromQuery] Guid? unitId, [FromQuery] Guid? gradeId, [FromQuery] Guid? locationId, IStrengthReportService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ReportAsync(Strength(companyId, asOf, groupBy, unitId, gradeId, locationId), ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsRead);
        Tag(app.MapGet("/api/hrm/strength-report/drill", async ([FromQuery] Guid? companyId, [FromQuery] DateOnly? asOf, [FromQuery] Guid? unitId, [FromQuery] Guid? gradeId, [FromQuery] Guid? locationId, IStrengthReportService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DrillAsync(Strength(companyId, asOf, null, unitId, gradeId, locationId), ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsRead);
        Tag(app.MapGet("/api/hrm/strength-report/export", async ([FromQuery] Guid? companyId, [FromQuery] DateOnly? asOf, [FromQuery] string? groupBy, [FromQuery] Guid? unitId, [FromQuery] Guid? gradeId, [FromQuery] Guid? locationId, IStrengthReportService s, CancellationToken ct) =>
            File(await s.ExportAsync(Strength(companyId, asOf, groupBy, unitId, gradeId, locationId), ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsRead);
        Tag(app.MapGet("/api/hrm/org-chart", async ([FromQuery] Guid? companyId, [FromQuery] DateOnly? asOf, [FromQuery] Guid? unitId, [FromQuery] Guid? locationId, IOrgChartService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChartAsync(new OrgChartQuery(companyId, asOf, unitId, locationId), ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsRead);
        Tag(app.MapPost("/api/hrm/org-chart/export", async (ChartExportRequest body, IOrgChartService s, CancellationToken ct) =>
            File(await s.ExportAsync(body, ct).ConfigureAwait(false))), "record_action", Positions, PositionPermissions.PositionsRead);
        return app;
    }

    private static StrengthQuery Strength(Guid? companyId, DateOnly? asOf, string? groupBy, Guid? unitId, Guid? gradeId, Guid? locationId) =>
        new(companyId, asOf, string.IsNullOrWhiteSpace(groupBy) ? null : groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), unitId, gradeId, locationId);

    /// <summary>A finished export is sent as a file; any other outcome is answered as the person endpoints do.</summary>
    private static IResult File(PeopleResult result) =>
        result is { Outcome: PeopleOutcome.Done, Body: ExportedFile file }
            ? Results.File(file.Content, file.ContentType, file.FileName)
            : PersonEndpoints.Answer(result);

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
