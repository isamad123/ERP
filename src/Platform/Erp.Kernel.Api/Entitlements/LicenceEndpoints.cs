using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Entitlements;

/// <summary>
/// Licence endpoints (slice S1.09b, ruling Q16): <c>GET /api/kernel/capabilities</c> for any signed-in user (the web shell's
/// menus, S1.12), <c>GET /api/cfg/licence</c> (the licence page, S1.16) and <c>GET /api/cfg/licence/usage</c> (monthly
/// statements). Capabilities, the licence page and its usage statements still answer when the licence is locked (ruling Q5;
/// S1.09b review finding 6). Error titles are
/// localization keys (<c>Entitlements/Localization/licence.*.json</c>).
/// </summary>
public static class LicenceEndpoints
{
    /// <summary>Every signed-in user holds it for their own account (S1.05, S1.06).</summary>
    private const string SignedIn = "iam.self.manage";

    public static IEndpointRouteBuilder MapLicenceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/kernel/capabilities", (ILicenceService s, IPermissionChecker p, CancellationToken ct) =>
                RunAsync(p, SignedIn, async () => Results.Ok(await s.CapabilitiesAsync(ct).ConfigureAwait(false)), ct))
            .WithTags("kernel.capabilities").WithMetadata(new LicenceEndpointMetadata("capabilities", SignedIn), new AllowedWhenLockedMetadata());
        app.MapGet("/api/cfg/licence", (ILicenceService s, IPermissionChecker p, CancellationToken ct) =>
                RunAsync(p, LicencePermissions.Read, async () => Results.Ok(await s.LicenceAsync(ct).ConfigureAwait(false)), ct))
            .WithTags("cfg.licence").WithMetadata(new LicenceEndpointMetadata("licence", LicencePermissions.Read), new AllowedWhenLockedMetadata());
        app.MapGet("/api/cfg/licence/usage", ([FromQuery] string? from, [FromQuery] string? to, ILicenceService s, IPermissionChecker p, CancellationToken ct) =>
                RunAsync(p, LicencePermissions.Read, async () => Results.Ok(await s.UsageAsync(from, to, ct).ConfigureAwait(false)), ct))
            .WithTags("cfg.licence").WithMetadata(new LicenceEndpointMetadata("usage", LicencePermissions.Read), new AllowedWhenLockedMetadata()); // part of the licence page (S1.09b review finding 6, CR-057)
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
        catch (ArgumentException ex) when (ex.Message.StartsWith("errors.licence.", StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
    }
}

/// <summary>
/// Limit answers (FR-KRN-ENT-05, FR-ADM-USR-08; ruling Q7) for every endpoint: a hard stop becomes 409
/// <c>errors.licence.limit_reached</c> naming the limit (<c>limit</c>, <c>used</c>, <c>max</c>); a warning taken during the
/// request is returned in the <see cref="LimitHeaders.Warning"/> header (<c>key;percent</c>, one per limit).
/// </summary>
public static class LimitResponses
{
    /// <summary>Call after <c>UseRouting</c>, before tenant resolution.</summary>
    public static IApplicationBuilder UseErpLimitResponses(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                if (context.RequestServices.GetService(typeof(ILimitWarningSource)) is ILimitWarningSource source && source.Warnings.Count > 0)
                {
                    context.Response.Headers[LimitHeaders.Warning] = source.Warnings.Select(w => $"{w.LimitKey};{w.Percent}").Distinct(StringComparer.Ordinal).ToArray();
                }

                return Task.CompletedTask;
            });

            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (LimitReachedException ex) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                var d = ex.Decision;
                await Results.Problem(statusCode: StatusCodes.Status409Conflict, title: LimitReachedException.ErrorKey, detail: "errors.licence.limit_reached." + d.LimitKey,
                    extensions: new Dictionary<string, object?> { ["limit"] = d.LimitKey, ["used"] = d.Used, ["max"] = d.Max }).ExecuteAsync(context).ConfigureAwait(false);
            }
        });
}
