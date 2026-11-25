using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Payroll;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Payroll;

/// <summary>
/// The loan endpoints of slice S1.41 (FR-HRM-LON-01 to -03, -05, -07, GHR-06): create, change and switch off loan types; preview and apply for a loan (the application goes to the
/// approval route <c>hrm.loan_application</c>, so the answer is 202); a loan's detail with its schedule, ledger and balance; cancel an approved loan; record the payout; hold a repayment
/// received outside payroll for a second person (<c>/api/iam/maker-checker-requests</c> decides it); and the outstanding and reconciliation reports. Reads of the rows stay the generated
/// <c>/api/hrm/loan-types</c>, <c>/loans</c>, <c>/loan-installments</c> and <c>/loan-transactions</c> (their generated writes are closed). Answers are as for the person endpoints.
/// </summary>
public static class LoanEndpoints
{
    private const string Types = "hrm.loan_types";
    private const string Loans = "hrm.loans";
    private static readonly EntitlementMetadata Payroll = new("hrm.payroll", "hrm");

    public static IEndpointRouteBuilder MapHrmLoanEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/loan-types", async (LoanTypeRequest body, ILoanTypeService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Types, LoanPermissions.TypesCreate);
        Tag(app.MapPut("/api/hrm/loan-types/{id:guid}", async (Guid id, LoanTypeRequest body, ILoanTypeService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Types, LoanPermissions.TypesUpdate);

        Tag(app.MapPost("/api/hrm/loans/preview", async (LoanApplicationRequest body, ILoanService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PreviewAsync(body, ct).ConfigureAwait(false))), "catalog", Loans, LoanPermissions.Apply);
        Tag(app.MapPost("/api/hrm/loans", async (LoanApplicationRequest body, ILoanService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ApplyAsync(body, ct).ConfigureAwait(false))), "create", Loans, LoanPermissions.Apply);
        Tag(app.MapGet("/api/hrm/loans/{id:guid}/detail", async (Guid id, ILoanService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DetailAsync(id, ct).ConfigureAwait(false))), "record_action", Loans, LoanPermissions.LoansRead);
        Tag(app.MapPost("/api/hrm/loans/{id:guid}/cancel", async (Guid id, ILoanService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CancelAsync(id, ct).ConfigureAwait(false))), "record_action", Loans, LoanPermissions.Cancel);
        Tag(app.MapPost("/api/hrm/loans/{id:guid}/disburse", async (Guid id, LoanDisbursementRequest body, ILoanService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DisburseAsync(id, body, ct).ConfigureAwait(false))), "record_action", Loans, LoanPermissions.Disburse);
        Tag(app.MapPost("/api/hrm/loans/{id:guid}/receipts", async (Guid id, LoanReceiptRequest body, ILoanService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ReceiptAsync(id, body, ct).ConfigureAwait(false))), "record_action", Loans, LoanPermissions.Receipt);

        Tag(app.MapGet("/api/hrm/loan-reports/outstanding", async ([FromQuery] Guid? companyId, [FromQuery] int? limit, [FromQuery] int? offset, ILoanReportService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.OutstandingAsync(companyId, limit ?? 100, offset ?? 0, ct).ConfigureAwait(false))), "catalog", Loans, LoanPermissions.ReportsRead);
        Tag(app.MapGet("/api/hrm/loan-reports/reconciliation", async ([FromQuery] Guid? companyId, ILoanReportService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ReconciliationAsync(companyId, ct).ConfigureAwait(false))), "catalog", Loans, LoanPermissions.ReportsRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Payroll);
}
