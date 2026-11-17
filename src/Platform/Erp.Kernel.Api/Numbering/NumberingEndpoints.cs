using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Numbering;
using Erp.Kernel.Contracts.Numbering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Numbering;

/// <summary>
/// Numbering endpoints (slice S1.08 ruling Q11): scheme create, edit (before first use only) and retire, the registered
/// document types and the void report. The generated read endpoints of schemes and voids stay; their generated writes are
/// closed. No number is ever handed out through the API. Error titles are localization keys
/// (<c>Numbering/Localization/numbering.*.json</c>).
/// </summary>
public static class NumberingEndpoints
{
    private const string Schemes = "cfg.numbering_schemes";

    public static IEndpointRouteBuilder MapNumberingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/cfg/numbering").WithTags("cfg.numbering");

        Tag(g.MapPost("/schemes", (CreateSchemeRequest body, INumberingAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, NumberingPermissions.Create, async () =>
            {
                var view = await s.CreateAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/cfg/numbering-schemes/" + view.Id, view);
            }, ct)), "create", NumberingPermissions.Create);

        Tag(g.MapPut("/schemes/{id:guid}", (Guid id, EditSchemeRequest body, INumberingAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, NumberingPermissions.Update, async () => Results.Ok(await s.EditAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", NumberingPermissions.Update);

        Tag(g.MapPost("/schemes/{id:guid}/retire", (Guid id, RetireSchemeRequest body, INumberingAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, NumberingPermissions.Retire, async () => Results.Ok(await s.RetireAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", NumberingPermissions.Retire);

        Tag(g.MapGet("/document-types", ([FromQuery] Guid? companyId, INumberingAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, NumberingPermissions.Read, async () => Results.Ok(await s.DocumentTypesAsync(companyId, ct).ConfigureAwait(false)), ct)),
            "catalog", NumberingPermissions.Read);

        Tag(g.MapGet("/void-report", ([FromQuery] Guid? schemeId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, INumberingAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, NumberingPermissions.VoidReport, async () =>
                schemeId is null ? Invalid("errors.numbering.scheme_required") : Results.Ok(await s.VoidReportAsync(schemeId.Value, from, to, ct).ConfigureAwait(false)), ct)),
            "query", NumberingPermissions.VoidReport);
        return app;
    }

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, string permission, Func<Task<IResult>> action, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex) when (ex.Message.StartsWith("errors.", StringComparison.Ordinal))
        {
            return Invalid(ex.Message);
        }
        catch (NumberingException ex)
        {
            return Invalid(ex.ErrorKey);
        }
        catch (KeyNotFoundException)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.numbering.not_found");
        }
        catch (AccessDeniedException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.ErrorCode);
        }
        catch (DataException ex) when (ex is ConcurrencyConflictException or DataConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex is DataConflictException ? "errors.numbering.active_scheme_exists" : ex.ErrorCode);
        }
        catch (DataValidationException ex)
        {
            return Invalid(ex.ErrorCode);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission) =>
        endpoint.WithMetadata(new NumberingEndpointMetadata(kind, Schemes, permission));

    private static IResult Invalid(string error) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
}
