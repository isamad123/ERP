using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Access;

/// <summary>
/// Read views of the access console (slice S1.13b, CR-069): one group with its members and roles, the permission change
/// report and the role usage report. They only read; every change of the console uses the S1.06 endpoints.
/// </summary>
public static class AccessConsoleEndpoints
{
    public static IEndpointRouteBuilder MapAccessConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/iam/user-groups/{id:guid}/detail", async (Guid id, IAccessConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync("iam.user_groups.read", ct).ConfigureAwait(false) ? Forbidden()
                : await s.GroupAsync(id, ct).ConfigureAwait(false) is { } group ? Results.Ok(group) : NotFound()).WithTags("iam.user_groups"),
            "record_action", "iam.user_groups", "iam.user_groups.read");

        Tag(app.MapGet("/api/iam/reports/permission-changes", async (
                [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int? limit,
                IAccessConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            {
                if (!await p.HasPermissionAsync(AuditPermissions.ViewChanges, ct).ConfigureAwait(false))
                {
                    return Forbidden();
                }

                try
                {
                    return Results.Ok(await s.PermissionChangesAsync(from, to, cursor, limit ?? 50, ct).ConfigureAwait(false));
                }
                catch (ArgumentException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "errors.audit.filter_invalid");
                }
            }).WithTags("iam.reports"),
            "query", "aud.change_log", AuditPermissions.ViewChanges);

        Tag(app.MapGet("/api/iam/reports/role-usage", async (IAccessConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync("iam.roles.read", ct).ConfigureAwait(false) ? Forbidden()
                : Results.Ok(await s.RoleUsageAsync(ct).ConfigureAwait(false))).WithTags("iam.reports"),
            "query", "iam.roles", "iam.roles.read");
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, resource, permission));

    private static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found");
}
