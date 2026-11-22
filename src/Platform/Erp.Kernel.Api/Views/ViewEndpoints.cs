using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Contracts.Views;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Views;

/// <summary>
/// Saved views of lists and label overrides (slice S1.12c Build items 15 and 17, S1.12 rulings Q11 and Q12; contracts
/// "Saved views API" and "Label overrides API"). Every endpoint acts for the signed-in person only (permission
/// <c>iam.self.manage</c>, held by every baseline role); sharing a view with a role also needs
/// <see cref="ViewPermissions.Share"/>. The tenant-isolation suite probes them as self-service endpoints.
/// </summary>
public static class ViewEndpoints
{
    public static IEndpointRouteBuilder MapViewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var views = app.MapGroup("/api/cfg/saved-views").WithTags("cfg.saved_views");
        Self(views.MapGet("/", ([FromQuery] string? list, ISavedViewService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => string.IsNullOrWhiteSpace(list)
                ? Invalid("errors.views.invalid_list", "list")
                : Results.Ok(await s.ListAsync(list, ct).ConfigureAwait(false)), ct)));
        Self(views.MapGet("/roles", (ISavedViewService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Results.Ok(await s.ShareableRolesAsync(ct).ConfigureAwait(false)), ct)));
        Self(views.MapPost("/", (SavedViewWrite body, ISavedViewService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Answer(await s.CreateAsync(body, ct).ConfigureAwait(false)), ct)));
        Self(views.MapPut("/{id:guid}", (Guid id, SavedViewWrite body, ISavedViewService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false)), ct)));
        Self(views.MapDelete("/{id:guid}", (Guid id, [FromQuery] long? rowVersion, ISavedViewService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => rowVersion is { } version
                ? Answer(await s.DeleteAsync(id, version, ct).ConfigureAwait(false))
                : Invalid("errors.data.row_version_required", "rowVersion"), ct)));

        // Labels apply on every screen, also the locked page and the licence page.
        Self(app.MapGet("/api/kernel/labels", ([FromQuery] Guid? companyId, ILabelOverrideService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Results.Ok(await s.OverridesAsync(companyId, ct).ConfigureAwait(false)), ct)).WithTags("kernel.labels"))
            .WithMetadata(new AllowedWhenLockedMetadata());
        return app;
    }

    private static RouteHandlerBuilder Self(RouteHandlerBuilder builder) =>
        builder.WithMetadata(new IdentityEndpointMetadata("self", IdentityPermissions.SelfManage, AllowedWhenReadOnly: false));

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, Func<Task<IResult>> action, CancellationToken cancellationToken) =>
        await permissions.HasPermissionAsync(IdentityPermissions.SelfManage, cancellationToken).ConfigureAwait(false)
            ? await action().ConfigureAwait(false)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");

    private static IResult Answer(ViewOutcome outcome) => outcome switch
    {
        { Status: 204 } => Results.NoContent(),
        { View: { } view, Status: 201 } => Results.Created("/api/cfg/saved-views/" + view.Id, view),
        { View: { } view } => Results.Ok(view),
        { Status: 400, Field: { } field } => Invalid(outcome.ErrorKey!, field),
        _ => Results.Problem(statusCode: outcome.Status, title: outcome.ErrorKey),
    };

    private static IResult Invalid(string key, string field) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [key] }, title: key);
}
