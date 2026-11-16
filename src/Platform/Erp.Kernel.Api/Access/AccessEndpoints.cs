using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Access;

public sealed record CreateRoleRequest(string? Code, Dictionary<string, string>? Name, Dictionary<string, string>? Description, Guid? CopyFromRoleId, string? CopyFromBaseline);

public sealed record RenameRequest(Dictionary<string, string>? Name, Dictionary<string, string>? Description, long? RowVersion);

public sealed record ReasonBody(string? Reason);

public sealed record SetPermissionsRequest(List<string>? Permissions, string? Reason);

public sealed record SetScopesRequest(List<RoleScopeChange>? Scopes, string? Reason);

public sealed record SetFieldsRequest(List<RoleFieldChange>? Fields, string? Reason);

public sealed record AssignRequest(Guid? UserId, Guid? RoleId, Guid? CompanyId, DateOnly? ValidFrom, DateOnly? ValidTo, string? Reason);

public sealed record GroupAssignRequest(Guid? RoleId, Guid? CompanyId, DateOnly? ValidFrom, DateOnly? ValidTo, string? Reason);

public sealed record EndRequest(DateOnly? EndDate, string? Reason);

public sealed record CreateGroupRequest(string? Code, Dictionary<string, string>? Name);

public sealed record MemberRequest(Guid? UserId);

public sealed record CreateScopeRequest(string? Code, Dictionary<string, string>? Name, string? Kind, System.Text.Json.JsonElement? Definition);

public sealed record SettingRequest(string? Layer, Guid? ScopeId, System.Text.Json.JsonElement? Value, string? Reason);

/// <summary>The answer of a change: the record it created or changed, or the maker-checker request it is waiting in.</summary>
public sealed record AccessChangeResponse(string Outcome, Guid? Id, Guid? RequestId);

/// <summary>
/// Access-control endpoints (slice S1.06): roles, assignments, groups, maker-checker decisions, the permission
/// catalogue and the previews. They replace the generated writes closed by CR-020 and CR-021; every rule is applied by
/// the services. A change that needs a second person answers 202 with the request id. Error titles are localization
/// keys (<c>Localization/access.*.json</c>).
/// </summary>
public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        const string roles = "iam.roles";
        const string manage = AccessControlPermissions.ManageRoles;

        Tag(app.MapGet("/api/iam/permission-catalog", async (IAccessPreviewService s, IPermissionChecker p, CancellationToken ct) =>
            await p.HasPermissionAsync("iam.permissions.read", ct).ConfigureAwait(false) ? Results.Ok(await s.CatalogAsync(ct).ConfigureAwait(false)) : ForbiddenResult()),
            "catalog", "iam.permissions", "iam.permissions.read");

        var role = app.MapGroup("/api/iam/roles").WithTags(roles);
        Tag(role.MapPost("/", (CreateRoleRequest body, IRoleService s, CancellationToken ct) =>
            Answer(s.CreateAsync(new CreateRole(body.Code, body.Name, body.Description, body.CopyFromRoleId, body.CopyFromBaseline), ct), created: "/api/iam/roles/")), "create", roles, manage);
        Tag(role.MapPut("/{id:guid}", (Guid id, RenameRequest body, IRoleService s, CancellationToken ct) =>
            Answer(s.RenameAsync(id, body.Name, body.Description, body.RowVersion, ct))), "record_action", roles, manage);
        Tag(role.MapPost("/{id:guid}/archive", (Guid id, ReasonBody body, IRoleService s, CancellationToken ct) =>
            Answer(s.ArchiveAsync(id, body.Reason, ct))), "record_action", roles, manage);
        Tag(role.MapPut("/{id:guid}/permissions", (Guid id, SetPermissionsRequest body, IRoleService s, CancellationToken ct) =>
            Answer(s.SetPermissionsAsync(id, body.Permissions, body.Reason, ct))), "record_action", roles, manage);
        Tag(role.MapPut("/{id:guid}/data-scopes", (Guid id, SetScopesRequest body, IRoleService s, CancellationToken ct) =>
            Answer(s.SetDataScopesAsync(id, body.Scopes, body.Reason, ct))), "record_action", roles, manage);
        Tag(role.MapPut("/{id:guid}/field-policies", (Guid id, SetFieldsRequest body, IRoleService s, CancellationToken ct) =>
            Answer(s.SetFieldPoliciesAsync(id, body.Fields, body.Reason, ct))), "record_action", roles, manage);
        Tag(role.MapGet("/{id:guid}/summary", async (Guid id, IRoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync("iam.roles.read", ct).ConfigureAwait(false) ? ForbiddenResult()
                : await s.SummaryAsync(id, ct).ConfigureAwait(false) is { } summary ? Results.Ok(summary) : NotFoundResult()), "record_action", roles, "iam.roles.read");
        Tag(role.MapGet("/compare", async ([FromQuery] Guid? left, [FromQuery] Guid? right, IRoleService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync("iam.roles.read", ct).ConfigureAwait(false) ? ForbiddenResult()
                : left is null || right is null ? Invalid("errors.data.invalid_request")
                : await s.CompareAsync(left.Value, right.Value, ct).ConfigureAwait(false) is { } comparison ? Results.Ok(comparison) : NotFoundResult()), "query", roles, "iam.roles.read");
        Tag(role.MapGet("/{id:guid}/field-preview", async (Guid id, [FromQuery] string? resource, IAccessPreviewService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync(AccessControlPermissions.Preview, ct).ConfigureAwait(false) ? ForbiddenResult()
                : resource is null ? Invalid("errors.role.unknown_resource")
                : await s.FieldsAsync(id, resource, ct).ConfigureAwait(false) is { } preview ? Results.Ok(preview) : NotFoundResult()), "record_action", roles, AccessControlPermissions.Preview);

        var assignments = app.MapGroup("/api/iam/user-roles").WithTags("iam.user_roles");
        Tag(assignments.MapPost("/", (AssignRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.AssignAsync(body.UserId, body.RoleId, body.CompanyId, body.ValidFrom, body.ValidTo, body.Reason, ct), created: "/api/iam/user-roles/")), "create", "iam.user_roles", AccessControlPermissions.AssignRoles);
        Tag(assignments.MapPost("/{id:guid}/end", (Guid id, EndRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.EndAsync(id, body.EndDate, body.Reason, ct))), "record_action", "iam.user_roles", AccessControlPermissions.AssignRoles);

        const string groups = "iam.user_groups";
        const string manageGroups = AccessControlPermissions.ManageGroups;
        var group = app.MapGroup("/api/iam/user-groups").WithTags(groups);
        Tag(group.MapPost("/", (CreateGroupRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.CreateGroupAsync(body.Code, body.Name, ct), created: "/api/iam/user-groups/")), "create", groups, manageGroups);
        Tag(group.MapPut("/{id:guid}", (Guid id, RenameRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.RenameGroupAsync(id, body.Name, body.RowVersion, ct))), "record_action", groups, manageGroups);
        Tag(group.MapPost("/{id:guid}/archive", (Guid id, ReasonBody body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.ArchiveGroupAsync(id, body.Reason, ct))), "record_action", groups, manageGroups);
        Tag(group.MapPost("/{id:guid}/members", (Guid id, MemberRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.AddMemberAsync(id, body.UserId, ct))), "record_action", groups, manageGroups);
        Tag(group.MapPost("/{id:guid}/members/{userId:guid}/remove", (Guid id, Guid userId, IAssignmentService s, CancellationToken ct) =>
            Answer(s.RemoveMemberAsync(id, userId, ct))), "record_action", groups, manageGroups);
        Tag(group.MapPost("/{id:guid}/roles", (Guid id, GroupAssignRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.AssignGroupAsync(id, body.RoleId, body.CompanyId, body.ValidFrom, body.ValidTo, body.Reason, ct))), "record_action", groups, manageGroups);
        Tag(app.MapPost("/api/iam/user-group-roles/{id:guid}/end", (Guid id, EndRequest body, IAssignmentService s, CancellationToken ct) =>
            Answer(s.EndGroupRoleAsync(id, body.EndDate, body.Reason, ct))).WithTags("iam.user_group_roles"), "record_action", "iam.user_group_roles", manageGroups);

        const string requests = "iam.maker_checker_requests";
        var decisions = app.MapGroup("/api/iam/maker-checker-requests").WithTags(requests);
        Tag(decisions.MapPost("/{id:guid}/approve", (Guid id, ReasonBody body, IMakerCheckerService s, CancellationToken ct) =>
            Answer(s.ApproveAsync(id, body.Reason, ct))), "record_action", requests, AccessControlPermissions.ApproveChanges);
        Tag(decisions.MapPost("/{id:guid}/reject", (Guid id, ReasonBody body, IMakerCheckerService s, CancellationToken ct) =>
            Answer(s.RejectAsync(id, body.Reason, ct))), "record_action", requests, AccessControlPermissions.ApproveChanges);

        // Data scopes are created here only; the generated create and update are closed (review S1.06 finding 2, CR-025).
        Tag(app.MapPost("/api/iam/data-scopes", (CreateScopeRequest body, IRoleService s, CancellationToken ct) =>
            Answer(s.CreateDataScopeAsync(body.Code, body.Name, body.Kind, body.Definition, ct), created: "/api/iam/data-scopes/")).WithTags("iam.data_scopes"), "create", "iam.data_scopes", manage);

        // Identity and access settings (iam.*): recorded, and switching off maker-checker or privileged MFA can need a
        // second administrator (review S1.06 finding 5, CR-025).
        Tag(app.MapPut("/api/iam/settings/{key}", (string key, SettingRequest body, IAccessSettingsService s, CancellationToken ct) =>
            Answer(s.SetAsync(key, body.Layer, body.ScopeId, body.Value, body.Reason, ct))).WithTags("iam.settings"), "create", "cfg.setting_values", "cfg.setting_values.update");

        Tag(app.MapGet("/api/iam/data-scope-preview", async ([FromQuery] Guid? userId, [FromQuery] string? resource, [FromQuery] int? limit, IAccessPreviewService s, IPermissionChecker p, CancellationToken ct) =>
            !await p.HasPermissionAsync(AccessControlPermissions.Preview, ct).ConfigureAwait(false) ? ForbiddenResult()
                : userId is null || resource is null ? Invalid("errors.data.invalid_request")
                : await s.DataScopeAsync(userId.Value, resource, limit ?? 50, ct).ConfigureAwait(false) is { } preview ? Results.Ok(preview) : NotFoundResult())
            .WithTags("iam.access"), "query", "iam.users", AccessControlPermissions.Preview);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, resource, permission));

    private static async Task<IResult> Answer(Task<AccessResult> work, string? created = null)
    {
        var result = await work.ConfigureAwait(false);
        return result.Outcome switch
        {
            AccessOutcome.Done when created is not null && result.Id is { } id => Results.Created(created + id, new AccessChangeResponse("done", id, null)),
            AccessOutcome.Done => Results.Ok(new AccessChangeResponse("done", result.Id, null)),
            AccessOutcome.Pending => Results.Accepted(null, new AccessChangeResponse("pending", null, result.RequestId)),
            AccessOutcome.NotFound => NotFoundResult(),
            AccessOutcome.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: result.Error ?? "errors.access.forbidden"),
            AccessOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Error),
            _ => Invalid(result.Error ?? "errors.data.invalid_request"),
        };
    }

    private static IResult Invalid(string error) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);

    private static IResult ForbiddenResult() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");

    private static IResult NotFoundResult() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found");
}
