using System.Security.Claims;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Domain.Entitlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Erp.Kernel.Api.ControlPlane.Tenancy;

/// <summary>Settings of tenant resolution in the API host.</summary>
public sealed class TenantResolutionOptions
{
    /// <summary>Paths served without a customer (liveness checks of the load balancer).</summary>
    public IReadOnlyList<string> PathsWithoutTenant { get; init; } = ["/health"];

    /// <summary>Name of the token claim that carries the customer code (issued by the identity slice S1.05).</summary>
    public string TenantClaim { get; init; } = "tenant";

    /// <summary>
    /// Requests of the web application's files (slice S1.12a, ruling Q2): on a known customer host, a request no endpoint
    /// matched that this returns true for goes on without a licence check, so the application loads in every access mode
    /// and shows the locked or read-only state itself. Unknown hosts still answer 404; <c>/api</c> paths never qualify.
    /// </summary>
    public Func<HttpContext, bool>? IsWebApplicationRequest { get; init; }
}

/// <summary>
/// Binds every request to exactly one customer (FR-KRN-TEN-03, FR-KRN-TEN-04). The customer comes only from the
/// request's host name looked up in cp.tenant_domains; the body, query string and other headers are never read.
/// Unknown host → 404. Slice S1.12a: the web application's files are served for a known host in every access mode. Slice S1.09b: the licence is worked out for the customer's day (FR-KRN-ENT-04); an endpoint of a
/// module or feature the customer is not licensed for, or whose module is not installed, answers exactly like a path that
/// does not exist (FR-KRN-ENT-03, ruling Q9). The access mode (FR-KRN-TEN-09) is then enforced: unavailable → 503;
/// locked licence or suspended customer → 403 except sign-in steps and endpoints marked allowed when locked (ruling Q5);
/// read-only → 403 for any method that changes data, except sign-in steps. Titles are localization keys.
/// </summary>
public static class TenantResolution
{
    public const string NotFound = "errors.tenant.not_found";
    public const string Unavailable = "errors.tenant.unavailable";
    public const string Locked = "errors.tenant.locked";
    public const string LicenceLocked = "errors.licence.locked";
    public const string ReadOnly = "errors.tenant.read_only";
    public const string WrongTenantToken = "errors.auth.token_for_another_tenant";

    /// <summary>Call after <c>UseRouting</c> and before <c>UseAuthentication</c>.</summary>
    public static IApplicationBuilder UseErpTenantResolution(this IApplicationBuilder app, TenantResolutionOptions? options = null)
    {
        var settings = options ?? new TenantResolutionOptions();
        return app.Use(async (context, next) =>
        {
            if (settings.PathsWithoutTenant.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var host = HostNames.Normalize(context.Request.Host.Value);
            var services = context.RequestServices;
            var directory = (ITenantDirectory)services.GetService(typeof(ITenantDirectory))!;
            var tenant = host is null ? null : await directory.FindByHostAsync(host, context.RequestAborted).ConfigureAwait(false);
            if (tenant is null)
            {
                await ProblemAsync(context, StatusCodes.Status404NotFound, NotFound).ConfigureAwait(false);
                return;
            }

            // The registry and the database decide first; their database is not touched when they already refuse.
            switch (TenantAccessPolicy.Decide(tenant.Status, tenant.DatabaseStatus, LicenceStates.Active))
            {
                case TenantAccessMode.NotFound:
                    await ProblemAsync(context, StatusCodes.Status404NotFound, NotFound).ConfigureAwait(false);
                    return;
                case TenantAccessMode.Unavailable:
                    await ProblemAsync(context, StatusCodes.Status503ServiceUnavailable, Unavailable).ConfigureAwait(false);
                    return;
            }

            // The web application's files (slice S1.12a): no endpoint, a known customer, nothing of the customer's data is read.
            if (context.GetEndpoint() is null && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && settings.IsWebApplicationRequest?.Invoke(context) == true)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var factory = (ITenantDataSourceFactory)services.GetService(typeof(ITenantDataSourceFactory))!;
            var dataSource = await factory.GetAsync(tenant, context.RequestAborted).ConfigureAwait(false);
            var licence = await ((ILicenceResolver)services.GetService(typeof(ILicenceResolver))!).ResolveAsync(tenant, dataSource, context.RequestAborted).ConfigureAwait(false);
            var mode = TenantAccessPolicy.Decide(tenant.Status, tenant.DatabaseStatus, licence.State);

            var endpoint = context.GetEndpoint();
            if (endpoint is null || !Serves(endpoint, tenant, licence))
            {
                // Exactly what routing answers for a path that does not exist: 404, no body (ruling Q9).
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var refusal = mode switch
            {
                TenantAccessMode.Locked when !AllowedWhenLocked(endpoint) =>
                    (StatusCodes.Status403Forbidden, tenant.Status == "suspended" ? Locked : LicenceLocked),
                TenantAccessMode.ReadOnly when !IsReadOnlyMethod(context.Request.Method) && !AllowedWhenReadOnly(endpoint) => (StatusCodes.Status403Forbidden, ReadOnly),
                _ => ((int Status, string Title)?)null,
            };
            if (refusal is { } r)
            {
                await ProblemAsync(context, r.Item1, r.Item2).ConfigureAwait(false);
                return;
            }

            ((TenantContext)services.GetService(typeof(TenantContext))!).Bind(tenant, mode, dataSource, licence);
            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The endpoint exists for the customer (FR-KRN-ENT-03): its module is installed and licensed, and its feature key is
    /// licensed. A customer with no readable entitlement is Locked; its endpoints are left to the access mode.
    /// </summary>
    public static bool Serves(Endpoint endpoint, ResolvedTenant tenant, LicenceSnapshot licence)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(licence);
        var key = endpoint.Metadata.GetMetadata<EntitlementMetadata>() ?? EntitlementMetadata.Kernel;
        if (key.Module != EntitlementMetadata.KernelModule && !tenant.InstalledModules.Contains(key.Module))
        {
            return false;
        }

        return licence.Claims is null || (licence.HasModule(key.Module) && licence.IsLicensed(key.FeatureKey));
    }

    /// <summary>
    /// Call after <c>UseAuthentication</c>: a signed token is accepted only on the host of the customer it was issued
    /// for. A valid token presented on another customer's host is refused with 401.
    /// </summary>
    public static IApplicationBuilder UseErpTokenTenantCheck(this IApplicationBuilder app, TenantResolutionOptions? options = null)
    {
        var settings = options ?? new TenantResolutionOptions();
        return app.Use(async (context, next) =>
        {
            var tenant = (ITenantContext)context.RequestServices.GetService(typeof(ITenantContext))!;
            if (tenant.IsResolved && context.User.Identity?.IsAuthenticated == true
                && !string.Equals(context.User.FindFirstValue(settings.TenantClaim), tenant.TenantCode, StringComparison.Ordinal))
            {
                await ProblemAsync(context, StatusCodes.Status401Unauthorized, WrongTenantToken).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Sign-in steps (S1.05) still work in read-only mode, so users can reach the data the mode keeps visible.</summary>
    private static bool AllowedWhenReadOnly(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IdentityEndpointMetadata>() is { AllowedWhenReadOnly: true } || endpoint.Metadata.GetMetadata<AllowedWhenReadOnlyMetadata>() is not null;

    /// <summary>Locked (ruling Q5): sign-in steps, and the licence page, capabilities and exports marked allowed when locked.</summary>
    private static bool AllowedWhenLocked(Endpoint endpoint) =>
        AllowedWhenReadOnly(endpoint) || endpoint.Metadata.GetMetadata<AllowedWhenLockedMetadata>() is not null;

    private static bool IsReadOnlyMethod(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static Task ProblemAsync(HttpContext context, int status, string title) =>
        Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
}

/// <summary>The signed-in user from the validated token's <c>sub</c> claim (a UUID), for created_by and updated_by.</summary>
public sealed class ClaimsCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public Guid UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("sub") ?? accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The request has no signed-in user.");
}
