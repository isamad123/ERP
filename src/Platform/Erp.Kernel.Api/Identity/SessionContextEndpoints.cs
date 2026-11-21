using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Identity;

/// <summary>
/// <c>GET /api/kernel/me</c> (slice S1.12b, S1.12 ruling Q9; contract "Session context API"): who is signed in, for the web
/// shell. Any signed-in person (permission <c>iam.self.manage</c>, held by every baseline role), also while the licence is
/// locked or the customer suspended, so the shell can show the locked page. <c>companyId</c> only chooses the company layer
/// of the number settings and is ignored unless it is one of the person's own companies.
/// </summary>
public static class SessionContextEndpoints
{
    public static IEndpointRouteBuilder MapSessionContextEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/kernel/me", async ([FromQuery] Guid? companyId, ISessionContextService service, IPermissionChecker permissions, CancellationToken ct) =>
                await permissions.HasPermissionAsync(IdentityPermissions.SelfManage, ct).ConfigureAwait(false)
                    && await service.GetAsync(companyId, ct).ConfigureAwait(false) is { } me
                    ? Results.Ok(me)
                    : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden"))
            .WithTags("kernel.me")
            .WithMetadata(new IdentityEndpointMetadata("self", IdentityPermissions.SelfManage, AllowedWhenReadOnly: false), new AllowedWhenLockedMetadata());
        return app;
    }
}
