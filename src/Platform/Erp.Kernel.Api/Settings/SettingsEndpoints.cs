using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Application.Settings;
using Erp.Kernel.Contracts.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Settings;

/// <summary>
/// Settings and feature-flag endpoints (slice S1.09a, ruling Q16). One API for every setting; <c>iam.*</c> keys keep the S1.06
/// rules through the access settings service (and <c>PUT /api/iam/settings/{key}</c> stays). A change that needs a second
/// administrator answers 202 <c>{outcome: "pending", requestId}</c>; it is decided at <c>/api/iam/maker-checker-requests</c>.
/// Error titles are localization keys (<c>Settings/Localization/settings.*.json</c>). The generated read endpoints of values,
/// history and overrides stay; their generated writes are closed (CR-036, CR-037).
/// </summary>
public static class SettingsEndpoints
{
    private const string Values = "cfg.setting_values";
    private const string Overrides = "cfg.feature_flag_overrides";

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/cfg/settings").WithTags("cfg.settings");
        Tag(app.MapGet("/api/cfg/settings", ([FromQuery] string? search, [FromQuery] string? module, [FromQuery] string? area, [FromQuery] Guid? companyId, ISettingsAdministration s, CancellationToken ct) =>
            Read(async () => Results.Ok(await s.ListAsync(search, module, area, companyId, ct).ConfigureAwait(false)))).WithTags("cfg.settings"), "read", Values, SettingsPermissions.Read);
        Tag(g.MapGet("/{key}", (string key, [FromQuery] Guid? companyId, [FromQuery] Guid? roleId, [FromQuery] Guid? userId, ISettingsAdministration s, CancellationToken ct) =>
            Read(async () => await s.DetailAsync(key, companyId, roleId, userId, ct).ConfigureAwait(false) is { } d ? Results.Ok(d) : NotFound())), "read", Values, SettingsPermissions.Read);
        Tag(g.MapPut("/{key}", (string key, SetSettingRequest body, ISettingsAdministration s, CancellationToken ct) =>
            Answer(s.SetAsync(key, body, ct))), "write", Values, SettingsPermissions.Update);
        Tag(g.MapPost("/{key}/reset", (string key, ResetSettingRequest body, ISettingsAdministration s, CancellationToken ct) =>
            Answer(s.ResetAsync(key, body, ct))), "write", Values, SettingsPermissions.Update);
        Tag(g.MapGet("/{key}/history", (string key, [FromQuery] string? layer, [FromQuery] Guid? scopeId, ISettingsAdministration s, CancellationToken ct) =>
            Read(async () => await s.HistoryAsync(key, layer, scopeId, ct).ConfigureAwait(false) is { } h ? Results.Ok(h) : NotFound())), "read", Values, SettingsPermissions.Read);
        Tag(g.MapPost("/history/{id:guid}/revert", (Guid id, ReasonRequest body, ISettingsAdministration s, CancellationToken ct) =>
            Answer(s.RevertAsync(id, body.Reason, ct))), "write", Values, SettingsPermissions.Update);

        // Self-service (FR-ADM-USR-10): the caller's own user-layer value of a setting marked self-service.
        var me = app.MapGroup("/api/cfg/me/settings").WithTags("cfg.settings");
        Tag(me.MapGet("/{key}", (string key, ISettingsAdministration s, CancellationToken ct) =>
            Read(async () => await s.MineAsync(key, ct).ConfigureAwait(false) is { } d ? Results.Ok(d) : NotFound())), "read", Values, SettingsPermissions.SelfService);
        Tag(me.MapPut("/{key}", (string key, SelfSettingRequest body, ISettingsAdministration s, CancellationToken ct) =>
            Answer(s.SetMineAsync(key, body, ct))), "self", Values, SettingsPermissions.SelfService);

        // Feature flags (FR-KRN-SET-05).
        var flags = app.MapGroup("/api/cfg/feature-flags").WithTags("cfg.feature_flags");
        Tag(app.MapGet("/api/cfg/feature-flags", ([FromQuery] Guid? companyId, ISettingsAdministration s, CancellationToken ct) =>
            Read(async () => Results.Ok(await s.FlagsAsync(companyId, ct).ConfigureAwait(false)))).WithTags("cfg.feature_flags"), "read", Overrides, SettingsPermissions.FlagsRead);
        Tag(flags.MapPut("/{key}", (string key, SetFlagRequest body, ISettingsAdministration s, CancellationToken ct) =>
            Answer(s.SetFlagAsync(key, body, ct))), "write", Overrides, SettingsPermissions.FlagsUpdate);
        Tag(flags.MapPost("/{key}/reset", (string key, ResetFlagRequest body, ISettingsAdministration s, CancellationToken ct) =>
            Answer(s.ResetFlagAsync(key, body, ct))), "write", Overrides, SettingsPermissions.FlagsUpdate);
        return app;
    }

    private static async Task<IResult> Read(Func<Task<IResult>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (AccessDeniedException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.ErrorCode);
        }
    }

    private static async Task<IResult> Answer(Task<AccessResult> work)
    {
        var result = await work.ConfigureAwait(false);
        return result.Outcome switch
        {
            AccessOutcome.Done => Results.Ok(new SettingsChangeResponse("done", result.Id, null)),
            AccessOutcome.Pending => Results.Accepted(null, new SettingsChangeResponse("pending", null, result.RequestId)),
            AccessOutcome.NotFound => NotFound(),
            AccessOutcome.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: result.Error ?? "errors.access.forbidden"),
            AccessOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Error),
            _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error ?? "errors.data.invalid_request"),
        };
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found");

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new SettingsEndpointMetadata(kind, resource, permission));
}

/// <summary>The answer of a change: the value row it wrote, or the maker-checker request it waits in.</summary>
public sealed record SettingsChangeResponse(string Outcome, Guid? Id, Guid? RequestId);
