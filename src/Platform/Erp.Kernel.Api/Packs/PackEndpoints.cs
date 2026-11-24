using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Packs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Packs;

/// <summary>
/// The pack manager (slice S1.23b Build items 10 and 11; contract "Pack manager API"): <c>GET /api/cfg/packs</c> (installed, available,
/// pending), <c>GET /api/cfg/packs/{packId}/items</c> (pack value against customer value), install, upgrade and "take the pack value".
/// The generated reads of <c>cfg.installed_packs</c> and <c>cfg.pack_items</c> stay; their generated writes are closed. A held action
/// answers 202 with the request id. Error titles are localization keys (<c>Packs/Localization/packs.*.json</c>).
/// </summary>
public static class PackEndpoints
{
    private const string Resource = "cfg.installed_packs";

    public static IEndpointRouteBuilder MapPackEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/cfg/packs").WithTags(Resource);
        Tag(g.MapGet("/", (IPackManager s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, PackPermissions.Read, async () => Results.Ok(await s.OverviewAsync(ct).ConfigureAwait(false)), ct)),
            "catalog", PackPermissions.Read);
        Tag(g.MapGet("/{packId}/items", (string packId, [FromQuery] bool? changed, IPackManager s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, PackPermissions.Read, async () => await s.ItemsAsync(packId, changed ?? false, ct).ConfigureAwait(false) is { } items
                ? Results.Ok(items)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.packs.not_installed"), ct)),
            "query", PackPermissions.Read);
        Tag(g.MapPost("/install", (PackInstallRequest body, IPackManager s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, PackPermissions.Install, async () => Answer(await s.InstallAsync(body, ct).ConfigureAwait(false)), ct)),
            "action", PackPermissions.Install);
        Tag(g.MapPost("/{packId}/upgrade", (string packId, PackInstallRequest body, IPackManager s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, PackPermissions.Upgrade, async () => Answer(await s.UpgradeAsync(packId, body, ct).ConfigureAwait(false)), ct)),
            "action", PackPermissions.Upgrade);
        Tag(g.MapPost("/items/{id:guid}/take-pack-value", (Guid id, PackReasonRequest body, IPackManager s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, PackPermissions.Upgrade, async () => Answer(await s.TakePackValueAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", PackPermissions.Upgrade);
        return app;
    }

    private static IResult Answer(PackActionResult result) => result.Status switch
    {
        200 => Results.Ok(result.Body),
        202 => Results.Accepted(value: result.Body),
        _ => Results.Problem(statusCode: result.Status, title: result.ErrorKey, detail: result.Detail),
    };

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, string permission, Func<Task<IResult>> action, CancellationToken cancellationToken) =>
        await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false)
            ? await action().ConfigureAwait(false)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission) =>
        endpoint.WithMetadata(new PackEndpointMetadata(kind, Resource, permission));
}
