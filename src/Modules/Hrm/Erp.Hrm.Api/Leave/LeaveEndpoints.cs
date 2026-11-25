using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Leave;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Leave;

/// <summary>
/// The leave engine endpoints of slice S1.32a: create and change leave types, post and reverse ledger entries, an employment's balances as of a
/// date (HR, within the data scope), the signed-in employee's own balances (the employment comes from the token), and the recompute report.
/// Reads of the rows stay the generated <c>/api/hrm/leave-types</c> and <c>/api/hrm/leave-ledger</c> (their generated writes are closed). S1.32b adds
/// leave requests (preview, apply, draft change, submit, cancel, change of approved leave, details), the year-end's confirm and roll back, and the
/// leave side of a transfer; reads of requests, their days and year-end runs stay generated.
/// Answers are as for the person endpoints.
/// </summary>
public static class LeaveEndpoints
{
    private const string Types = "hrm.leave_types";
    private const string Ledger = "hrm.leave_ledger";
    private const string Employments = "hrm.employments";
    private const string Requests = "hrm.leave_requests";
    private const string YearEnds = "hrm.leave_year_end_runs";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmLeaveEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/leave-types", async (LeaveTypeRequest body, ILeaveTypeService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Types, LeavePermissions.TypesCreate);
        Tag(app.MapPut("/api/hrm/leave-types/{id:guid}", async (Guid id, LeaveTypeRequest body, ILeaveTypeService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Types, LeavePermissions.TypesUpdate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/leave-ledger", async (Guid id, LeavePostRequest body, ILeaveLedgerService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PostAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, LeavePermissions.LedgerPost);
        Tag(app.MapPost("/api/hrm/leave-ledger/{id:guid}/reverse", async (Guid id, LeaveReverseRequest body, ILeaveLedgerService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ReverseAsync(id, body, ct).ConfigureAwait(false))), "record_action", Ledger, LeavePermissions.LedgerPost);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/leave-balances", async (Guid id, [FromQuery] DateOnly? asOf, [FromQuery] Guid? leaveTypeId, ILeaveLedgerService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.BalancesAsync(id, asOf, leaveTypeId, ct).ConfigureAwait(false))), "record_action", Employments, LeavePermissions.LedgerRead);
        Tag(app.MapGet("/api/hrm/my/leave-balances", async ([FromQuery] Guid? companyId, [FromQuery] DateOnly? asOf, [FromQuery] Guid? leaveTypeId, ILeaveLedgerService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.MyBalancesAsync(companyId, asOf, leaveTypeId, ct).ConfigureAwait(false))), "catalog", Ledger, LeavePermissions.LedgerRead);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/leave-recompute", async (Guid id, [FromQuery] Guid? leaveTypeId, ILeaveRecomputeService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RecomputeAsync(id, leaveTypeId, ct).ConfigureAwait(false))), "record_action", Employments, LeavePermissions.Recompute);

        // S1.32b: requests (FR-HRM-LVE-04 to -06, -16), year-end (LVE-11) and the leave side of transfers (FR-XM-30).
        Tag(app.MapPost("/api/hrm/leave-requests/preview", async (LeaveRequestInput body, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PreviewAsync(body, ct).ConfigureAwait(false))), "record_action", Requests, LeavePermissions.RequestsCreate);
        Tag(app.MapPost("/api/hrm/leave-requests", async (LeaveRequestInput body, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Requests, LeavePermissions.RequestsCreate);
        Tag(app.MapPut("/api/hrm/leave-requests/{id:guid}/draft", async (Guid id, LeaveRequestInput body, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Requests, LeavePermissions.RequestsUpdate);
        Tag(app.MapPost("/api/hrm/leave-requests/{id:guid}/submit", async (Guid id, LeaveSubmitInput body, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.SubmitAsync(id, body, ct).ConfigureAwait(false))), "record_action", Requests, LeavePermissions.RequestsUpdate);
        Tag(app.MapPost("/api/hrm/leave-requests/{id:guid}/cancel", async (Guid id, LeaveCancelInput body, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CancelAsync(id, body, ct).ConfigureAwait(false))), "record_action", Requests, LeavePermissions.RequestsCancel);
        Tag(app.MapPost("/api/hrm/leave-requests/{id:guid}/change", async (Guid id, LeaveRequestInput body, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Requests, LeavePermissions.RequestsUpdate);
        Tag(app.MapGet("/api/hrm/leave-requests/{id:guid}/details", async (Guid id, ILeaveRequestService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DetailsAsync(id, ct).ConfigureAwait(false))), "record_action", Requests, LeavePermissions.RequestsRead);
        Tag(app.MapPost("/api/hrm/leave-year-end-runs/{id:guid}/confirm", async (Guid id, LeaveYearEndDecision body, ILeaveYearEndService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ConfirmAsync(id, body, ct).ConfigureAwait(false))), "record_action", YearEnds, LeavePermissions.YearEndConfirm);
        Tag(app.MapPost("/api/hrm/leave-year-end-runs/{id:guid}/rollback", async (Guid id, LeaveYearEndDecision body, ILeaveYearEndService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RollbackAsync(id, body, ct).ConfigureAwait(false))), "record_action", YearEnds, LeavePermissions.YearEndConfirm);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/leave-transfer", async (Guid id, ILeaveTransferService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, ct).ConfigureAwait(false))), "record_action", Employments, LeavePermissions.LedgerRead);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/leave-transfer", async (Guid id, LeaveTransferMapInput body, ILeaveTransferService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.MapAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, LeavePermissions.LedgerPost);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
