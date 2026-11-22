using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Access;

/// <summary>
/// The administrator's views over users (slice S1.13a, CR-069): the user list, the access data of one user, the people
/// lookup for pickers (ruling Q7) and the single revocations of the user record. Writes of the console use the existing
/// endpoints; these only read, or end one session or one remembered browser.
/// </summary>
public static class UserConsoleEndpoints
{
    private const string Users = "iam.users";
    private const string ReadUsers = "iam.users.read";

    /// <summary>Permissions whose screens need a person picker (ruling Q7): the administrator's and the approver's.</summary>
    private static readonly string[] LookupPermissions =
    [
        ReadUsers, IdentityPermissions.ChangeStatus, WorkflowPermissions.TasksReassign, WorkflowPermissions.DelegationsCreate, AccessControlPermissions.AssignRoles,
    ];

    public static IEndpointRouteBuilder MapUserConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/iam/user-overview", async (
                [FromQuery] string? search, [FromQuery] string? status, [FromQuery] Guid? roleId, [FromQuery] Guid? companyId, [FromQuery] bool? mfa,
                [FromQuery] string? cursor, [FromQuery] int? limit, IUserConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync(ReadUsers, ct).ConfigureAwait(false) ? Forbidden()
                : status is not null && !Statuses.Contains(status, StringComparer.Ordinal) ? Invalid()
                : Results.Ok(await s.OverviewAsync(new UserOverviewQuery(search, status, roleId, companyId, mfa, cursor, limit ?? 0), ct).ConfigureAwait(false))).WithTags(Users),
            "query", Users, ReadUsers);

        Tag(app.MapGet("/api/iam/users/{id:guid}/access", async (Guid id, IUserConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync(ReadUsers, ct).ConfigureAwait(false) ? Forbidden()
                : await s.AccessAsync(id, ct).ConfigureAwait(false) is { } view ? Results.Ok(view) : NotFound()).WithTags(Users),
            "query", Users, ReadUsers);

        Tag(app.MapGet("/api/iam/people-lookup", async ([FromQuery] string? search, IUserConsoleService s, IPermissionChecker p, CancellationToken ct) =>
        {
            foreach (var permission in LookupPermissions)
            {
                if (await p.HasPermissionAsync(permission, ct).ConfigureAwait(false))
                {
                    return Results.Ok(await s.PeopleAsync(search, ct).ConfigureAwait(false));
                }
            }

            return Forbidden();
        }).WithTags(Users), "query", Users, ReadUsers);

        Tag(app.MapPost("/api/iam/users/{id:guid}/sessions/{sessionId:guid}/revoke", async (Guid id, Guid sessionId, ConsoleReason body, IUserConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync(IdentityPermissions.RevokeSessions, ct).ConfigureAwait(false) ? Forbidden()
                : await s.RevokeSessionAsync(id, sessionId, body.Reason, ct).ConfigureAwait(false) ? Results.NoContent() : NotFound()).WithTags(Users),
            "record_action", Users, IdentityPermissions.RevokeSessions);

        Tag(app.MapPost("/api/iam/users/{id:guid}/trusted-devices/{deviceId:guid}/remove", async (Guid id, Guid deviceId, ConsoleReason body, IUserConsoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync(IdentityPermissions.RevokeSessions, ct).ConfigureAwait(false) ? Forbidden()
                : await s.RemoveTrustedDeviceAsync(id, deviceId, body.Reason, ct).ConfigureAwait(false) ? Results.NoContent() : NotFound()).WithTags(Users),
            "record_action", Users, IdentityPermissions.RevokeSessions);
        return app;
    }

    private static readonly string[] Statuses = ["invited", "active", "locked", "disabled", "archived"];

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, resource, permission));

    private static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found");

    private static IResult Invalid() => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "errors.data.invalid_request");
}

/// <summary>Body of a single revocation: the administrator's note, kept in the audit entry.</summary>
public sealed record ConsoleReason(string? Reason);
