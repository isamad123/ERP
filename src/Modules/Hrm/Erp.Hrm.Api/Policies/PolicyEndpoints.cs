using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Policies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Policies;

/// <summary>
/// The HR policy endpoints (slice S1.23a Build items 1 to 6): the type catalogue, policies and their versions (create with a first draft,
/// rename, new draft, edit and delete a draft, activate from a date, retire), assignment sets per type and company (read, check, activate)
/// and the resolution view "which policy applies and why". Reads of the rows stay the generated <c>/api/hrm/policies</c>,
/// <c>/api/hrm/policy-versions</c> and <c>/api/hrm/policy-assignments</c>; their generated writes are closed. Answers are as for the
/// person endpoints.
/// </summary>
public static class PolicyEndpoints
{
    private const string Policies = "hrm.policies";
    private const string Versions = "hrm.policy_versions";
    private const string Assignments = "hrm.policy_assignments";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/hrm/policy-types", async (IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.TypesAsync(ct).ConfigureAwait(false))), "catalog", Policies, PolicyPermissions.Read);
        Tag(app.MapPost("/api/hrm/policies", async (PolicyCreateRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Policies, PolicyPermissions.Manage);
        Tag(app.MapGet("/api/hrm/policies/{id:guid}/detail", async (Guid id, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DetailAsync(id, ct).ConfigureAwait(false))), "record_action", Policies, PolicyPermissions.Read);
        Tag(app.MapPut("/api/hrm/policies/{id:guid}/name", async (Guid id, PolicyEditRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.EditAsync(id, body, ct).ConfigureAwait(false))), "record_action", Policies, PolicyPermissions.Manage);
        Tag(app.MapPost("/api/hrm/policies/{id:guid}/versions", async (Guid id, PolicyDraftRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.NewDraftAsync(id, body, ct).ConfigureAwait(false))), "record_action", Policies, PolicyPermissions.Manage);
        Tag(app.MapPost("/api/hrm/policies/{id:guid}/retire", async (Guid id, PolicyDateRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RetireAsync(id, body, ct).ConfigureAwait(false))), "record_action", Policies, PolicyPermissions.Manage);
        Tag(app.MapPut("/api/hrm/policy-versions/{id:guid}/parameters", async (Guid id, PolicyDraftRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.EditDraftAsync(id, body, ct).ConfigureAwait(false))), "record_action", Versions, PolicyPermissions.Manage);
        Tag(app.MapDelete("/api/hrm/policy-versions/{id:guid}/draft", async (Guid id, [FromQuery] long? rowVersion, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DeleteDraftAsync(id, rowVersion, ct).ConfigureAwait(false))), "record_action", Versions, PolicyPermissions.Manage);
        Tag(app.MapPost("/api/hrm/policy-versions/{id:guid}/activate", async (Guid id, PolicyDateRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ActivateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Versions, PolicyPermissions.Manage);
        Tag(app.MapGet("/api/hrm/policy-assignment-sets", async ([FromQuery] Guid? companyId, [FromQuery] string? policyType, [FromQuery] DateOnly? asOf, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AssignmentsAsync(companyId, policyType, asOf, ct).ConfigureAwait(false))), "record_action", Assignments, PolicyPermissions.Read);
        Tag(app.MapPost("/api/hrm/policy-assignment-sets/check", async (PolicyAssignmentSetRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CheckAssignmentsAsync(body, ct).ConfigureAwait(false))), "record_action", Assignments, PolicyPermissions.Manage);
        Tag(app.MapPost("/api/hrm/policy-assignment-sets/activate", async (PolicyAssignmentSetRequest body, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ActivateAssignmentsAsync(body, ct).ConfigureAwait(false))), "record_action", Assignments, PolicyPermissions.Manage);
        Tag(app.MapGet("/api/hrm/policy-resolution", async ([FromQuery] Guid? employmentId, [FromQuery] string? policyType, [FromQuery] DateOnly? date, IPolicyService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ExplainAsync(employmentId, policyType, date, ct).ConfigureAwait(false))), "record_action", Policies, PolicyPermissions.Read);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
