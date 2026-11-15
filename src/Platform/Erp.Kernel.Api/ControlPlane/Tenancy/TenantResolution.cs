using System.Security.Claims;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Erp.Kernel.Api.ControlPlane.Tenancy;

/// <summary>Settings of tenant resolution in the API host.</summary>
public sealed class TenantResolutionOptions
{
    /// <summary>Paths served without a customer (liveness checks of the load balancer).</summary>
    public IReadOnlyList<string> PathsWithoutTenant { get; init; } = ["/health"];

    /// <summary>Database schema → module, for endpoints of business modules (a module that is not installed has no API, FR-KRN-ENT-03).</summary>
    public IReadOnlyDictionary<string, string> SchemaModules { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["hrm"] = "hrm" };

    /// <summary>Name of the token claim that carries the customer code (issued by the identity slice S1.05).</summary>
    public string TenantClaim { get; init; } = "tenant";
}

/// <summary>
/// Binds every request to exactly one customer (FR-KRN-TEN-03, FR-KRN-TEN-04). The customer comes only from the
/// request's host name looked up in cp.tenant_domains; the body, query string and other headers are never read.
/// Unknown host → 404. The access mode (FR-KRN-TEN-09) is enforced here: unavailable → 503, locked → 403,
/// read-only → 403 for any method that changes data. Titles are localization keys.
/// </summary>
public static class TenantResolution
{
    public const string NotFound = "errors.tenant.not_found";
    public const string Unavailable = "errors.tenant.unavailable";
    public const string Locked = "errors.tenant.locked";
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

            var mode = TenantAccessPolicy.Decide(tenant.Status, tenant.DatabaseStatus, tenant.EntitlementStatus);
            var refusal = mode switch
            {
                TenantAccessMode.NotFound => (StatusCodes.Status404NotFound, NotFound),
                TenantAccessMode.Unavailable => (StatusCodes.Status503ServiceUnavailable, Unavailable),
                TenantAccessMode.Locked => (StatusCodes.Status403Forbidden, Locked),
                TenantAccessMode.ReadOnly when !IsReadOnlyMethod(context.Request.Method) => (StatusCodes.Status403Forbidden, ReadOnly),
                _ => ((int Status, string Title)?)null,
            };
            if (refusal is { } r)
            {
                await ProblemAsync(context, r.Item1, r.Item2).ConfigureAwait(false);
                return;
            }

            var resource = context.GetEndpoint()?.Metadata.GetMetadata<GeneratedEndpointMetadata>()?.Resource;
            if (resource is not null && settings.SchemaModules.TryGetValue(resource[..resource.IndexOf('.', StringComparison.Ordinal)], out var module) && !tenant.InstalledModules.Contains(module))
            {
                await ProblemAsync(context, StatusCodes.Status404NotFound, NotFound).ConfigureAwait(false);
                return;
            }

            var factory = (ITenantDataSourceFactory)services.GetService(typeof(ITenantDataSourceFactory))!;
            var dataSource = await factory.GetAsync(tenant, context.RequestAborted).ConfigureAwait(false);
            ((TenantContext)services.GetService(typeof(TenantContext))!).Bind(tenant, mode, dataSource);
            await next(context).ConfigureAwait(false);
        });
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
