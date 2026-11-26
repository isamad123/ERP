using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Expenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Erp.BuildingBlocks.Contracts.Api;

namespace Erp.Hrm.Api.Expenses;

/// <summary>
/// The expense claim endpoints of slice S1.42 (FR-HRM-EXP-02, EXP-03, EXP-05, GHR-06, FR-XM-29, FR-XM-32): make and change a draft claim with its lines,
/// submit it to its route, cancel a draft, read the claim as an approver or claimant may see it, record an approver's decision on each line, and list the
/// open claims of an employment for clearance. The generated reads (<c>/api/hrm/expense-claims</c>, <c>/api/hrm/expense-claim-lines</c>) list the claims in
/// the caller's data scope with amounts for holders of salary-class access only; their generated writes are closed. A claim is decided, and a submitted claim
/// withdrawn, through the shared approval endpoints (<c>/api/wf</c>). Receipts are uploaded to <c>/api/doc/attachments</c> with owner type
/// <c>hrm.expense_claims</c>. Answers are as for the person endpoints; a claim that waits for a decision is 202.
/// </summary>
public static class ExpenseEndpoints
{
    private const string Claims = "hrm.expense_claims";
    private static readonly EntitlementMetadata Payroll = new("hrm.payroll", "hrm");

    public static IEndpointRouteBuilder MapHrmExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/expense-claims", async (ExpenseClaimInput body, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Claims, ExpensePermissions.Create);
        Tag(app.MapGet("/api/hrm/expense-claims/new-context", async (Guid? employmentId, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.NewClaimContextAsync(employmentId, ct).ConfigureAwait(false))), "catalog", Claims, ExpensePermissions.Create);
        Tag(app.MapPut("/api/hrm/expense-claims/{id:guid}", async (Guid id, ExpenseClaimInput body, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Claims, ExpensePermissions.Create);
        Tag(app.MapPost("/api/hrm/expense-claims/{id:guid}/submit", async (Guid id, ExpenseRowVersion? body, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.SubmitAsync(id, body?.RowVersion, ct).ConfigureAwait(false))), "record_action", Claims, ExpensePermissions.Create);
        Tag(app.MapPost("/api/hrm/expense-claims/{id:guid}/cancel", async (Guid id, ExpenseRowVersion? body, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CancelAsync(id, body?.RowVersion, ct).ConfigureAwait(false))), "record_action", Claims, ExpensePermissions.Create);
        Tag(app.MapGet("/api/hrm/expense-claims/{id:guid}/detail", async (Guid id, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, ct).ConfigureAwait(false))), "record_action", Claims, ExpensePermissions.Read);
        Tag(app.MapPost("/api/hrm/expense-claims/{id:guid}/line-decisions", async (Guid id, ExpenseDecisionsInput body, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DecideLinesAsync(id, body, ct).ConfigureAwait(false))), "record_action", Claims, ExpensePermissions.Decide);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/open-expense-claims", async (Guid id, IExpenseClaimService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.OpenClaimsAsync(id, ct).ConfigureAwait(false))), "record_action", "hrm.employments", ExpensePermissions.Read);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(Claims).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Payroll);
}

/// <summary>The row version a submit or cancel was made against (optional: without it the latest is used).</summary>
public sealed record ExpenseRowVersion(long? RowVersion);
