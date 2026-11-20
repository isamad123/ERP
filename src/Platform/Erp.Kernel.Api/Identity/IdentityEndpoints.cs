using System.Security.Claims;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Identity;

public sealed record SignInRequest(string? Login, string? Password);

/// <summary>The second factor; <see cref="RememberDevice"/> asks to remember this browser (slice S1.12a, ruling Q4).</summary>
public sealed record MfaRequest(string? ChallengeToken, string? Code, string? RecoveryCode, bool? RememberDevice = null);

public sealed record RequiredPasswordRequest(string? ChallengeToken, string? NewPassword);

public sealed record ChallengeRequest(string? ChallengeToken);

/// <summary>Start of the required enrolment; <see cref="Kind"/> <c>totp</c> (default) or <c>email</c> (slice S1.11b2).</summary>
public sealed record EnrolmentStartRequest(string? ChallengeToken, string? Kind);

/// <summary>Confirm of the required enrolment; an authenticator also needs <see cref="EmailCode"/>, the code e-mailed at start (R-003).</summary>
public sealed record EnrolmentConfirmRequest(string? ChallengeToken, Guid DeviceId, string? Code, string? EmailCode);

/// <summary>Forgot password (slice S1.11b2): the username or e-mail address.</summary>
public sealed record ForgotPasswordRequest(string? Login);

/// <summary>An administrator's change of a person's e-mail address, with the reason (S1.11b2 review finding 2, CR-062).</summary>
public sealed record ChangeEmailRequest(string? Email, string? Reason);

/// <summary>An e-mail change waiting for a second person: the maker-checker request to approve.</summary>
public sealed record PendingChangeResponse(string Outcome, Guid RequestId);

/// <summary>A new password with the token of an e-mailed reset link (slice S1.11b2).</summary>
public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

/// <summary>A refresh; a browser (header <c>X-Erp-Client: web</c>) sends <c>{}</c> and the token travels in its cookie (S1.12a).</summary>
public sealed record RefreshRequest(string? RefreshToken);

public sealed record ActivateRequest(string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Self-service enrolment; <see cref="Kind"/> <c>totp</c> (default) or <c>email</c> (slice S1.11b2, CR-009).</summary>
public sealed record StartMfaRequest(string? Label, string? CurrentPassword, string? Code, string? Kind = null);

/// <summary>Proof of presence for sensitive self-service actions: the current password or a current authenticator code.</summary>
public sealed record ReauthRequest(string? CurrentPassword, string? Code);

public sealed record ConfirmMfaRequest(string? Code);

public sealed record ReasonRequest(string? Reason);

public sealed record StatusRequest(string? Status, string? Reason);

/// <summary>
/// The answer of every sign-in step. Only the fields of the outcome are set. Version 3 (slice S1.12a): a browser's answer
/// carries no <see cref="RefreshToken"/> (it is in the HttpOnly cookie); <see cref="IdleTimeoutMinutes"/> comes with
/// <c>tokens</c>, <see cref="RememberDeviceDays"/> with <c>mfa_required</c> (0 = not offered).
/// </summary>
public sealed record AuthResponse(
    string Outcome,
    string? AccessToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string? RefreshToken = null,
    Guid? SessionId = null,
    string? ChallengeToken = null,
    DateTimeOffset? MfaEnrolBy = null,
    IReadOnlyList<string>? RecoveryCodes = null,
    IReadOnlyList<string>? MfaMethods = null,
    int? IdleTimeoutMinutes = null,
    int? RememberDeviceDays = null);

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
    public const string Busy = "errors.auth.busy";
    public const string ReauthenticationFailed = "errors.auth.reauthentication_failed";

    /// <summary>Maps the anonymous sign-in steps and sign-out (outside the authorization group).</summary>
    public static IEndpointRouteBuilder MapIdentityAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var auth = app.MapGroup("/api/iam/auth").WithTags("iam.auth");
        Anonymous(auth.MapPost("/sign-in", (SignInRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.SignInAsync(body.Login, body.Password, Client(http), ct), http)));
        Anonymous(auth.MapPost("/mfa", (MfaRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.CompleteMfaAsync(body.ChallengeToken, body.Code, body.RecoveryCode, body.RememberDevice == true && BrowserCookies.IsBrowser(http), Client(http), ct), http)));
        Anonymous(auth.MapPost("/password-change", (RequiredPasswordRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.ChangeRequiredPasswordAsync(body.ChallengeToken, body.NewPassword, Client(http), ct), http)));
        Anonymous(auth.MapPost("/mfa-enrolment", async (EnrolmentStartRequest body, IAuthenticationService s, CancellationToken ct) =>
            await s.StartRequiredEnrolmentAsync(body.ChallengeToken, body.Kind, ct).ConfigureAwait(false) is { } enrolment ? Results.Ok(enrolment) : FailedResult()));
        Anonymous(auth.MapPost("/mfa-enrolment/confirm", (EnrolmentConfirmRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.ConfirmRequiredEnrolmentAsync(body.ChallengeToken, body.DeviceId, body.Code, body.EmailCode, Client(http), ct), http)));

        // Identity e-mails (slice S1.11b2; KRN-IAM-01, KRN-IAM-02, ruling Q16). Links and codes go out by e-mail only.
        Anonymous(auth.MapPost("/password/forgot", async (ForgotPasswordRequest body, IIdentityEmailService s, CancellationToken ct) =>
        {
            await s.RequestPasswordResetAsync(body.Login, ct).ConfigureAwait(false);
            return Results.Accepted(); // the same answer whether or not the account exists
        }));
        Anonymous(auth.MapPost("/password/reset", async (ResetPasswordRequest body, IIdentityEmailService s, CancellationToken ct) =>
        {
            try
            {
                return await s.ResetPasswordAsync(body.Token, body.NewPassword, ct).ConfigureAwait(false) switch
                {
                    null => FailedResult(),
                    { Count: 0 } => Results.NoContent(),
                    var errors => Problem(StatusCodes.Status400BadRequest, InvalidRequest, errors),
                };
            }
            catch (PasswordHasherBusyException)
            {
                return BusyResult();
            }
        }));
        Anonymous(auth.MapPost("/mfa/email-code", async (ChallengeRequest body, IIdentityEmailService s, CancellationToken ct) =>
            await s.SendSignInCodeAsync(body.ChallengeToken, ct).ConfigureAwait(false) ? Results.Accepted() : FailedResult()));
        // Version 3 (slice S1.12a, ruling Q3): a browser's refresh token is read from its cookie only, never from the body.
        Anonymous(auth.MapPost("/refresh", (RefreshRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.RefreshAsync(BrowserCookies.IsBrowser(http) ? BrowserCookies.RefreshToken(http) : body.RefreshToken, Client(http), ct), http)));
        Anonymous(auth.MapPost("/activate", (ActivateRequest body, IAuthenticationService s, HttpContext http, CancellationToken ct) =>
            Answer(s.ActivateAsync(body.Token, body.NewPassword, Client(http), ct), http)));

        // Ends only the caller's own session, so it needs no permission (it must work before roles exist, S1.06).
        auth.MapPost("/sign-out", async (IAuthenticationService s, HttpContext http, CancellationToken ct) =>
        {
            if (Caller(http) is not { } caller)
            {
                return Results.Unauthorized();
            }

            await s.SignOutAsync(caller.UserId, caller.SessionId, ct).ConfigureAwait(false);
            if (BrowserCookies.IsBrowser(http))
            {
                BrowserCookies.ClearRefresh(http); // the device trust stays: it is ended by its own rules (ruling Q4)
            }

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
                var errors = await s.ChangePasswordAsync(c.UserId, c.SessionId, body.CurrentPassword, body.NewPassword, ct).ConfigureAwait(false);
                return errors.Count == 0 ? Results.NoContent() : Problem(StatusCodes.Status400BadRequest, InvalidRequest, errors);
            })));
        Self(me.MapGet("/sessions", (IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.ListSessionsAsync(c.UserId, c.SessionId, ct).ConfigureAwait(false)))));
        Self(me.MapDelete("/sessions/{id:guid}", (Guid id, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RevokeSessionAsync(c.UserId, id, ct).ConfigureAwait(false) ? Results.NoContent() : NotFoundResult())));
        Self(me.MapGet("/mfa-devices", (IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.ListMfaDevicesAsync(c.UserId, ct).ConfigureAwait(false)))));
        Self(me.MapPost("/mfa-devices", (StartMfaRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.StartMfaEnrolmentAsync(c.UserId, body.Label, body.Kind, new Reauthentication(body.CurrentPassword, body.Code), ct).ConfigureAwait(false)))));
        Self(me.MapPost("/mfa-devices/{id:guid}/confirm", (Guid id, ConfirmMfaRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.ConfirmMfaEnrolmentAsync(c.UserId, id, body.Code, ct).ConfigureAwait(false) is { } codes
                ? Results.Ok(new RecoveryCodesResponse(codes))
                : Problem(StatusCodes.Status400BadRequest, InvalidRequest, ["errors.mfa.code_invalid"]))));
        Self(me.MapPost("/mfa-devices/{id:guid}/remove", (Guid id, ReauthRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RemoveMfaDeviceAsync(c.UserId, id, new Reauthentication(body.CurrentPassword, body.Code), ct).ConfigureAwait(false) ? Results.NoContent() : NotFoundResult())));
        // Remembered browsers (slice S1.12a, ruling Q4): the person's own list and removal; the screen comes with S1.26.
        Self(me.MapGet("/trusted-devices", (IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => Results.Ok(await s.ListTrustedDevicesAsync(c.UserId, ct).ConfigureAwait(false)))));
        Self(me.MapDelete("/trusted-devices/{id:guid}", (Guid id, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RemoveTrustedDeviceAsync(c.UserId, id, ct).ConfigureAwait(false) ? Results.NoContent() : NotFoundResult())));
        Self(me.MapPost("/recovery-codes", (ReauthRequest body, IAccountService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            AsSelfAsync(p, http, ct: ct, action: async c => await s.RegenerateRecoveryCodesAsync(c.UserId, new Reauthentication(body.CurrentPassword, body.Code), ct).ConfigureAwait(false) is { } codes
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
        UserAction(users.MapPost("/{id:guid}/status", (Guid id, StatusRequest body, IUserAdministrationService s, IWorkflowMonitor w, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.ChangeStatus, async () => await WithOpenTasksAsync(await s.ChangeStatusAsync(id, body.Status, body.Reason, ct).ConfigureAwait(false), id, w, ct).ConfigureAwait(false), ct)),
            IdentityPermissions.ChangeStatus);
        UserAction(users.MapPost("/{id:guid}/invitation", (Guid id, IIdentityEmailService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.SendInvitation, () => s.SendInvitationAsync(id, ct), ct)), IdentityPermissions.SendInvitation);
        UserAction(users.MapPost("/{id:guid}/password-reset-link", (Guid id, ReasonRequest body, IIdentityEmailService s, IPermissionChecker p, CancellationToken ct) =>
            AdminAsync(p, IdentityPermissions.SendPasswordReset, () => s.SendPasswordResetLinkAsync(id, body.Reason, ct), ct)), IdentityPermissions.SendPasswordReset);
        UserAction(users.MapPost("/{id:guid}/email", async (Guid id, ChangeEmailRequest body, IIdentityEmailService s, IPermissionChecker p, CancellationToken ct) =>
        {
            if (!await p.HasPermissionAsync(IdentityPermissions.ChangeEmail, ct).ConfigureAwait(false))
            {
                return ForbiddenResult();
            }

            var result = await s.ChangeEmailAsync(id, body.Email, body.Reason, ct).ConfigureAwait(false);
            return result switch
            {
                { Outcome: AdminOutcome.NotFound } => NotFoundResult(),
                { Outcome: AdminOutcome.Invalid } => Problem(StatusCodes.Status400BadRequest, result.Error ?? InvalidRequest, null),
                { PendingRequestId: { } request } => Results.Accepted(value: new PendingChangeResponse("pending", request)),
                _ => Results.NoContent(),
            };
        }), IdentityPermissions.ChangeEmail);
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

        if (!await permissions.HasPermissionAsync(IdentityPermissions.SelfManage, ct).ConfigureAwait(false))
        {
            return ForbiddenResult();
        }

        try
        {
            return await action(caller).ConfigureAwait(false);
        }
        catch (ReauthenticationFailedException)
        {
            return Problem(StatusCodes.Status400BadRequest, InvalidRequest, [ReauthenticationFailed]);
        }
        catch (MfaRequestRefusedException e)
        {
            return Problem(StatusCodes.Status400BadRequest, InvalidRequest, [e.Message]);
        }
        catch (PasswordHasherBusyException)
        {
            return BusyResult();
        }
    }

    /// <summary>
    /// The open approval tasks of a user just disabled or archived (slice S1.10b ruling P7): the caller's own list of them
    /// (<c>GET /api/wf/users/{id}/open-tasks</c>: their companies, licensed document types), 0 when the caller reassigns no
    /// tasks (S1.10b review finding 7).
    /// </summary>
    private static async Task<AdminResult> WithOpenTasksAsync(AdminResult result, Guid userId, IWorkflowMonitor monitor, CancellationToken ct)
    {
        if (result.OpenWorkflowTasks is null)
        {
            return result;
        }

        try
        {
            return result with { OpenWorkflowTasks = (await monitor.OpenTasksOfAsync(userId, ct).ConfigureAwait(false)).Count };
        }
        catch (KeyNotFoundException)
        {
            return result with { OpenWorkflowTasks = 0 };
        }
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
            AdminOutcome.Done when result.OpenWorkflowTasks is { } open => Results.Ok(new { openWorkflowTasks = open }), // S1.10b ruling P7
            AdminOutcome.Done => Results.NoContent(),
            AdminOutcome.NotFound => NotFoundResult(),
            _ => Problem(StatusCodes.Status400BadRequest, result.Error ?? InvalidRequest, null),
        };
    }

    private static async Task<IResult> Answer(Task<AuthResult> step, HttpContext http)
    {
        AuthResult result;
        try
        {
            result = await step.ConfigureAwait(false);
        }
        catch (PasswordHasherBusyException)
        {
            return BusyResult();
        }

        // Version 3 (slice S1.12a, ruling Q3): a browser gets its refresh token and device trust as HttpOnly cookies only.
        var browser = BrowserCookies.IsBrowser(http);
        if (browser && result.Outcome == AuthOutcome.Tokens)
        {
            BrowserCookies.SetRefresh(http, result.Tokens!.RefreshToken);
            if (result.TrustedDeviceToken is { } device && result.TrustedDeviceExpiresAt is { } until)
            {
                BrowserCookies.SetDevice(http, device, until);
            }
        }
        else if (browser && result.Outcome == AuthOutcome.Failed && http.Request.Path.Value?.EndsWith("/refresh", StringComparison.Ordinal) == true)
        {
            BrowserCookies.ClearRefresh(http); // the session has ended: forget its token
        }

        return result.Outcome switch
        {
            AuthOutcome.Failed => FailedResult(),
            AuthOutcome.Invalid => Problem(
                StatusCodes.Status400BadRequest,
                InvalidRequest,
                result.Errors),
            AuthOutcome.Tokens => Results.Ok(new AuthResponse("tokens", result.Tokens!.AccessToken, result.Tokens.AccessTokenExpiresAt, browser ? null : result.Tokens.RefreshToken,
                result.Tokens.SessionId, MfaEnrolBy: result.MfaEnrolBy, RecoveryCodes: result.RecoveryCodes, IdleTimeoutMinutes: result.IdleTimeoutMinutes)),
            AuthOutcome.MfaRequired => Results.Ok(new AuthResponse("mfa_required", ChallengeToken: result.ChallengeToken, MfaMethods: result.MfaMethods,
                RememberDeviceDays: browser ? result.RememberDeviceDays : 0)),
            AuthOutcome.PasswordChangeRequired => Results.Ok(new AuthResponse("password_change_required", ChallengeToken: result.ChallengeToken)),
            AuthOutcome.MfaEnrolmentRequired => Results.Ok(new AuthResponse("mfa_enrolment_required", ChallengeToken: result.ChallengeToken)),
            _ => throw new InvalidOperationException("Unknown sign-in outcome."),
        };
    }

    /// <summary>The one answer for every failed sign-in step (no account enumeration, ruling P5).</summary>
    private static IResult FailedResult() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: SignInFailed);

    /// <summary>The password checks of this server are all in use (review finding 5); nothing was counted, try again.</summary>
    private static IResult BusyResult() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: Busy, extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = 5 });

    private static IResult ForbiddenResult() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: Forbidden);

    private static IResult NotFoundResult() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: NotFound);

    private static IResult Problem(int status, string title, IReadOnlyList<string>? errors) =>
        Results.Problem(statusCode: status, title: title, extensions: errors is null ? null : new Dictionary<string, object?> { ["errors"] = errors });

    /// <summary>The caller's address from the connection (a trusted proxy configuration is a hosting setting).</summary>
    private static ClientInfo Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null)
        {
            TrustedDeviceToken = BrowserCookies.DeviceToken(http), // a remembered browser (slice S1.12a, ruling Q4)
        };
}
