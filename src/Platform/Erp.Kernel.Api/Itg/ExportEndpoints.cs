using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Itg;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Itg;

/// <summary>
/// The export centre API (slice S1.16b, FR-ADM-DAT-04, FR-ADM-NAV-02, FR-KRN-ENT-04), under <c>/api/itg/exports</c>: check what an export of a list would
/// be (sensitive classes, reason, approval), ask for it, list the person's exports and download one's own file. The customer comes from the host, never
/// from the request; the list's permission, data scope and field security apply in the background run. Asking and downloading are allowed in read-only and
/// locked states. Error titles are localization keys (<c>export.*.json</c>).
/// </summary>
public static class ExportEndpoints
{
    private const string Exports = "itg.export_jobs";

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/itg/exports").WithTags("itg.exports");

        Tag(g.MapPost("/check", (ExportRequest body, IExportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.CheckAsync(body, ct).ConfigureAwait(false)))), "create");
        Tag(g.MapPost("/", (ExportRequest body, IExportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var view = await s.StartAsync(body, ct).ConfigureAwait(false);
                return Results.Accepted("/api/itg/exports/" + view.Id, view);
            })), "create");
        Tag(g.MapGet("/", ([FromQuery] bool? all, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit, IExportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ListAsync(all ?? false, status, cursor, limit ?? 0, ct).ConfigureAwait(false)))), "catalog");
        Tag(g.MapGet("/{id:guid}", (Guid id, IExportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.GetAsync(id, ct).ConfigureAwait(false)))), "record_action");
        Tag(g.MapGet("/{id:guid}/download", (Guid id, IExportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var file = await s.DownloadAsync(id, ct).ConfigureAwait(false);
                return Results.File(file.Content, file.ContentType, file.FileName);
            })), "record_action");
        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ExportException e)
        {
            return Results.Problem(statusCode: e.Status, title: e.ErrorKey, detail: e.Detail);
        }
    }

    /// <summary>Marks the endpoint for the isolation suite and allows it in read-only and locked states (FR-KRN-ENT-04); the service checks the permission.</summary>
    private static void Tag(RouteHandlerBuilder endpoint, string kind) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, Exports, ExportPermissions.Run), new AllowedWhenReadOnlyMetadata(), new AllowedWhenLockedMetadata());
}
