using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Setup;

/// <summary>
/// The readiness checklist and go-live (slice S1.18a, contract "Readiness API"; ruling Q4): <c>GET /api/cfg/readiness</c>, tick, skip and
/// re-open of a task, and <c>POST /api/cfg/readiness/{module}/go-live</c>. The generated reads of <c>cfg.setup_tasks</c> stay; its
/// generated writes are closed. Error titles are localization keys (<c>Setup/Localization/setup.*.json</c>). The tenant-isolation suite
/// probes these as access-control endpoints.
/// </summary>
public static class SetupEndpoints
{
    private const string Resource = "cfg.setup_tasks";

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/cfg/readiness").WithTags(Resource);
        Tag(g.MapGet("/", ([FromQuery] Guid? companyId, IReadinessService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, SetupPermissions.Read, async () => Results.Ok(await s.OverviewAsync(companyId, ct).ConfigureAwait(false)), ct)),
            "catalog", SetupPermissions.Read);
        Tag(g.MapPost("/{module}/tasks/{key}/complete", (string module, string key, SetupTaskChange body, IReadinessService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, SetupPermissions.Update, async () => Results.Ok(await s.CompleteAsync(module, key, body, ct).ConfigureAwait(false)), ct)),
            "record_action", SetupPermissions.Update);
        Tag(g.MapPost("/{module}/tasks/{key}/skip", (string module, string key, SetupTaskChange body, IReadinessService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, SetupPermissions.Update, async () => Results.Ok(await s.SkipAsync(module, key, body, ct).ConfigureAwait(false)), ct)),
            "record_action", SetupPermissions.Update);
        Tag(g.MapPost("/{module}/tasks/{key}/reopen", (string module, string key, SetupTaskChange body, IReadinessService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, SetupPermissions.Update, async () => Results.Ok(await s.ReopenAsync(module, key, body, ct).ConfigureAwait(false)), ct)),
            "record_action", SetupPermissions.Update);
        Tag(g.MapPost("/{module}/go-live", (string module, GoLiveRequest body, IReadinessService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, SetupPermissions.GoLive, async () => Results.Ok(await s.GoLiveAsync(module, body, ct).ConfigureAwait(false)), ct)),
            "record_action", SetupPermissions.GoLive);
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
        catch (SetupRefusedException refusal)
        {
            return refusal.Field is { } field
                ? Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [refusal.ErrorKey] }, statusCode: refusal.Status, title: refusal.ErrorKey)
                : Results.Problem(statusCode: refusal.Status, title: refusal.ErrorKey);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, Resource, permission));
}
