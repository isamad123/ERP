using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Settings;

/// <summary>
/// The public branding of the sign-in page (slice S1.15a, FR-ADM-SEC-08, ruling Q3; contract "Branding API"). No login: the
/// sign-in page needs it before anyone is known. The customer comes from the host name only. The answer holds the product name,
/// the logo address, the login banner and the terms notice and nothing else; it may be cached for a minute. The logo is served
/// only when it is a clean PNG or JPEG of the branding owner. Changes are made with the settings centre and the attachment API.
/// </summary>
public static class BrandingEndpoints
{
    private const string CacheShort = "public, max-age=60";
    private const string CacheLogo = "public, max-age=3600";

    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Anonymous(app.MapGet("/api/kernel/branding", async (IBrandingService s, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = CacheShort;
            return Results.Ok(await s.GetAsync(ct).ConfigureAwait(false));
        }).WithTags("kernel.branding"));
        Anonymous(app.MapGet("/api/kernel/branding/logo", async (IBrandingService s, HttpContext http, CancellationToken ct) =>
        {
            if (await s.LogoAsync(ct).ConfigureAwait(false) is not { } logo)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found");
            }

            http.Response.Headers.CacheControl = CacheLogo;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(logo.Content, logo.ContentType);
        }).WithTags("kernel.branding"));
        return app;
    }

    private static void Anonymous(RouteHandlerBuilder endpoint) =>
        endpoint.AllowAnonymous().WithMetadata(new IdentityEndpointMetadata("anonymous", null, AllowedWhenReadOnly: true));
}
