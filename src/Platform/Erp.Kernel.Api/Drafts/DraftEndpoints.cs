using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Drafts;
using Erp.Kernel.Contracts.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Drafts;

/// <summary>
/// Form drafts (slice S1.21a, CR-077; FR-ADM-UXA-05). Under <c>/api/cfg/drafts</c>: the signed-in person's own drafts only (list, one,
/// save, delete); nothing here names another person. Like saved views, every endpoint needs only the self-service permission
/// (<c>iam.self.manage</c>, every baseline role) and the tenant-isolation suite probes them as self-service endpoints. The generated
/// <c>cfg.form_drafts</c> endpoints only read.
/// </summary>
public static class DraftEndpoints
{
    public static IEndpointRouteBuilder MapDraftEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/cfg/drafts").WithTags("cfg.form_drafts");
        Self(g.MapGet("/", ([FromQuery] string? form, IFormDraftService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Results.Ok(await s.ListAsync(form, ct).ConfigureAwait(false)), ct)));
        Self(g.MapGet("/{id:guid}", (Guid id, IFormDraftService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Answer(await s.GetAsync(id, ct).ConfigureAwait(false)), ct)));
        Self(g.MapPut("/", (DraftWrite body, IFormDraftService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Answer(await s.SaveAsync(body, ct).ConfigureAwait(false)), ct)));
        Self(g.MapDelete("/{id:guid}", (Guid id, IFormDraftService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, async () => Answer(await s.DeleteAsync(id, ct).ConfigureAwait(false)), ct)));
        return app;
    }

    private static RouteHandlerBuilder Self(RouteHandlerBuilder builder) =>
        builder.WithMetadata(new IdentityEndpointMetadata("self", IdentityPermissions.SelfManage, AllowedWhenReadOnly: false));

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, Func<Task<IResult>> action, CancellationToken cancellationToken) =>
        await permissions.HasPermissionAsync(IdentityPermissions.SelfManage, cancellationToken).ConfigureAwait(false)
            ? await action().ConfigureAwait(false)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");

    private static IResult Answer(DraftOutcome outcome) => outcome switch
    {
        { Status: 204 } => Results.NoContent(),
        { Draft: { } draft, Status: 201 } => Results.Created("/api/cfg/drafts/" + draft.Id, draft),
        { Draft: { } draft } => Results.Ok(draft),
        { Status: 400, Field: { } field } => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [outcome.ErrorKey!] }, title: outcome.ErrorKey),
        _ => Results.Problem(statusCode: outcome.Status, title: outcome.ErrorKey),
    };
}
