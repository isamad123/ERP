using System.Security.Claims;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Contracts.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Identity;

public sealed record SignInRequest(string? Login, string? Password);

public sealed record MfaRequest(string? ChallengeToken, string? Code, string? RecoveryCode);

public sealed record RequiredPasswordRequest(string? ChallengeToken, string? NewPassword);

public sealed record ChallengeRequest(string? ChallengeToken);

public sealed record EnrolmentConfirmRequest(string? ChallengeToken, Guid DeviceId, string? Code);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record ActivateRequest(string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record StartMfaRequest(string? Label);

public sealed record ConfirmMfaRequest(string? Code);

public sealed record ReasonRequest(string? Reason);

public sealed record StatusRequest(string? Status, string? Reason);

/// <summary>The answer of every sign-in step. Only the fields of the outcome are set.</summary>
public sealed record AuthResponse(
    string Outcome,
    string? AccessToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string? RefreshToken = null,
    Guid? SessionId = null,
    string? ChallengeToken = null,
    DateTimeOffset? MfaEnrolBy = null,
    IReadOnlyList<string>? RecoveryCodes = null);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// Identity endpoints (slice S1.05). The customer comes from the host name only (S1.03 middleware); nothing in a body,
/// query or header chooses it. Error titles are localization keys (<c>Localization/identity.*.json</c>).
/// </summary>
public static class IdentityEndpoints
{
    public const string SignInFailed = "errors.auth.sign_in_failed";
    public const string InvalidRequest = "errors.auth.invalid_request";
    public const string Forbidden = "errors.access.forbidden";
    public const string NotFound = "errors.data.not_found";

    /// <summary>Maps the anonymous sign-in steps and sign-out (outside the authorization group).</summary>
    public static IEndpointRouteBuilder MapIdentityAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var auth = app.MapGroup("/api/iam/auth").WithTags("iam.auth");
        Anonymous(auth.MapPost("/sign-in", (SignInRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.SignInAsync(body.Login, body.Password, Client(http), ct))));
        Anonymous(auth.MapPost("/mfa", (MfaRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.CompleteMfaAsync(body.ChallengeToken, body.Code, body.RecoveryCode, Client(http), ct))));
        Anonymous(auth.MapPost("/password-change", (RequiredPasswordRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.ChangeRequiredPasswordAsync(body.ChallengeToken, body.NewPassword, Client(http), ct))));
        Anonymous(auth.MapPost("/mfa-enrolment", async (ChallengeRequest body, IAuthenticationService s, CancellationToken ct) =>
            await s.StartRequiredEnrolmentAsync(body.ChallengeToken, ct).ConfigureAwait(false) is { } enrolment ? Results.Ok(enrolment) : FailedResult()));
        Anonymous(auth.MapPost("/mfa-enrolment/confirm", (EnrolmentConfirmRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.ConfirmRequiredEnrolmentAsync(body.ChallengeToken, body.DeviceId, body.Code, Client(http), ct))));
        Anonymous(auth.MapPost("/refresh", (RefreshRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.RefreshAsync(body.RefreshToken, Client(http), ct))));
        Anonymous(auth.MapPost("/activate", (ActivateRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.ActivateAsync(body.Token, body.NewPassword, Client(http), ct))));

        // Ends only the caller's own session, so it needs no permission (it must work before roles exist, S1.06).
        auth.MapPost("/sign-out", async (IAuthenticationService s, HttpContext http, CancellationToken ct) =>
        {
            if (Caller(http) is not { } caller)
            {
                return Results.Unauthorized();
            }

            await s.SignOutAsync(caller.UserId, caller.SessionId, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization().WithMetadata(new IdentityEndpointMetadata("self", null, AllowedWhenReadOnly: true));
        return app;
    }

    /// <summary>Maps self-service and administrator actions; call on the group that requires authorization.</summary>
    public static IEndpointRouteBuilder MapIdentityAccountEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var me = app.MapGroup("/api/iam/me").WithTags("iam.me");
        Self(me.MapPost("/password", (ChangePasswordRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c =>
            {
                var errors = await s.ChangePasswordAsync(c.UserId, body.CurrentPassword, body.NewPassword, ct).ConfigureAwait(false);
                return errors.Count == 0 ? Results.NoContent() : Problem(StatusCodes.Status400BadRequest, InvalidRequest, errors);
            })));
        Self(me.MapGet("/sessions", (IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.ListSessionsAsync(c.UserId, c.SessionId, ct).ConfigureAwait(false)))));
        Self(me.MapDelete("/sessions/{id:guid}", (Guid id, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RevokeSessionAsync(c.UserId, id, ct).ConfigureAwait(false) ? Results.NoContent() : NotFoundResult())));
        Self(me.MapGet("/mfa-devices", (IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.ListMfaDevicesAsync(c.UserId, ct).ConfigureAwait(false)))));
        Self(me.MapPost("/mfa-devices", (StartMfaRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.StartMfaEnrolmentAsync(c.UserId, body.Label, ct).ConfigureAwait(false)))));
        Self(me.MapPost("/mfa-devices/{id:guid}/confirm", (Guid id, ConfirmMfaRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.ConfirmMfaEnrolmentAsync(c.UserId, id, body.Code, ct).ConfigureAwait(false) is { } codes
                ? Results.Ok(new RecoveryCodesResponse(codes))
                : Problem(StatusCodes.Status400BadRequest, InvalidRequest, ["errors.mfa.code_invalid"]))));
        Self(me.MapDelete("/mfa-devices/{id:guid}", (Guid id, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RemoveMfaDeviceAsync(c.UserId, id, ct).ConfigureAwait(false) ? Results.NoContent() : NotFoundResult())));
        Self(me.MapPost("/recovery-codes", (IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RegenerateRecoveryCodesAsync(c.UserId, ct).ConfigureAwait(false) is { } codes
                ? Results.Ok(new RecoveryCodesResponse(codes))
                : Problem(StatusCodes.Status400BadRequest, InvalidRequest, ["errors.mfa.no_active_device"]))));

        var users = app.MapGroup("/api/iam/users").WithTags("iam.users");
        UserAction(users.MapPost("/{id:guid}/unlock", (Guid id, ReasonRequest body, IUserAdministrationService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.Unlock, () => s.UnlockAsync(id, body.Reason, ct), ct)), IdentityPermissions.Unlock);
        UserAction(users.MapPost("/{id:guid}/force-password-change", (Guid id, ReasonRequest body, IUserAdministrationService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.ForcePasswordChange, () => s.ForcePasswordChangeAsync(id, body.Reason, ct), ct)), IdentityPermissions.ForcePasswordChange);
        UserAction(users.MapPost("/{id:guid}/reset-mfa", (Guid id, ReasonRequest body, IUserAdministrationService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.ResetMfa, () => s.ResetMfaAsync(id, body.Reason, ct), ct)), IdentityPermissions.ResetMfa);
        UserAction(users.MapPost("/{id:guid}/revoke-sessions", (Guid id, ReasonRequest body, IUserAdministrationService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.RevokeSessions, () => s.RevokeSessionsAsync(id, body.Reason, ct), ct)), IdentityPermissions.RevokeSessions);
        UserAction(users.MapPost("/{id:guid}/status", (Guid id, StatusRequest body, IUserAdministrationService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.ChangeStatus, () => s.ChangeStatusAsync(id, body.Status, body.Reason, ct), ct)), IdentityPermissions.ChangeStatus);
        UserAction(users.MapGet("/{id:guid}/sessions", async (Guid id, IUserAdministrationService s, IPermissionChecker p, CancellationToken ct) =>
        {
            if (!await p.HasPermissionAsync(IdentityPermissions.ReadSessions, ct).ConfigureAwait(false))
            {
                return ForbiddenResult();
            }

            return await s.ListSessionsAsync(id, ct).ConfigureAwait(false) is { } sessions ? Results.Ok(sessions) : NotFoundResult();
        }), IdentityPermissions.ReadSessions);
        return app;
    }

    private static void Anonymous(RouteHandlerBuilder endpoint) =>
        endpoint.AllowAnonymous().WithMetadata(new IdentityEndpointMetadata("anonymous", null, AllowedWhenReadOnly: true));

    private static void Self(RouteHandlerBuilder endpoint) =>
        endpoint.WithMetadata(new IdentityEndpointMetadata("self", IdentityPermissions.SelfManage, AllowedWhenReadOnly: false));

    private static void UserAction(RouteHandlerBuilder endpoint, string permission) =>
        endpoint.WithMetadata(new IdentityEndpointMetadata("user_action", permission, AllowedWhenReadOnly: false));

    private sealed record CallerIds(Guid UserId, Guid SessionId);

    private static CallerIds? Caller(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var user) && Guid.TryParse(http.User.FindFirstValue("sid"), out var session)
            ? new CallerIds(user, session)
            : null;

    private static async Task<IResult> AsSelfAsync(IPermissionChecker permissions, HttpContext http, Func<CallerIds, Task<IResult>> action, CancellationToken ct)
    {
        if (Caller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        return await permissions.HasPermissionAsync(IdentityPermissions.SelfManage, ct).ConfigureAwait(false)
            ? await action(caller).ConfigureAwait(false)
            : ForbiddenResult();
    }

    private static async Task<IResult> AdminAsync(IPermissionChecker permissions, string permission, Func<Task<AdminResult>> action, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false))
        {
            return ForbiddenResult();
        }

        var result = await action().ConfigureAwait(false);
        return result.Outcome switch
        {
            AdminOutcome.Done => Results.NoContent(),
            AdminOutcome.NotFound => NotFoundResult(),
            _ => Problem(result.Error == "errors.user.limit_reached" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, result.Error ?? InvalidRequest, null),
        };
    }

    private static async Task<IResult> Answer(Task<AuthResult> step)
    {
        var result = await step.ConfigureAwait(false);
        return result.Outcome switch
        {
            AuthOutcome.Failed => FailedResult(),
            AuthOutcome.Invalid => Problem(
                result.Errors is { } e && e.Contains("errors.user.limit_reached") ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                InvalidRequest,
                result.Errors),
            AuthOutcome.Tokens => Results.Ok(new AuthResponse("tokens", result.Tokens!.AccessToken, result.Tokens.AccessTokenExpiresAt, result.Tokens.RefreshToken,
                result.Tokens.SessionId, MfaEnrolBy: result.MfaEnrolBy, RecoveryCodes: result.RecoveryCodes)),
            AuthOutcome.MfaRequired => Results.Ok(new AuthResponse("mfa_required", ChallengeToken: result.ChallengeToken)),
            AuthOutcome.PasswordChangeRequired => Results.Ok(new AuthResponse("password_change_required", ChallengeToken: result.ChallengeToken)),
            AuthOutcome.MfaEnrolmentRequired => Results.Ok(new AuthResponse("mfa_enrolment_required", ChallengeToken: result.ChallengeToken)),
            _ => throw new InvalidOperationException("Unknown sign-in outcome."),
        };
    }

    /// <summary>The one answer for every failed sign-in step (no account enumeration, ruling P5).</summary>
    private static IResult FailedResult() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: SignInFailed);

    private static IResult ForbiddenResult() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: Forbidden);

    private static IResult NotFoundResult() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: NotFound);

    private static IResult Problem(int status, string title, IReadOnlyList<string>? errors) =>
        Results.Problem(statusCode: status, title: title, extensions: errors is null ? null : new Dictionary<string, object?> { ["errors"] = errors });

    /// <summary>The caller's address from the connection (a trusted proxy configuration is a hosting setting).</summary>
    private static ClientInfo Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null);
}
