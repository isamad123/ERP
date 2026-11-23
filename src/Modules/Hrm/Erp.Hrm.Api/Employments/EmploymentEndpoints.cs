using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Employments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Employments;

/// <summary>
/// The HR employment endpoints (slice S1.20b, ruling Q1): hire, record and check a change, the as-of record and the timeline, the
/// user link, the catalogue, and (S1.20c) transfers between companies, rehire, contracts and probation. Reads of the rows stay the generated <c>/api/hrm/employments</c>, <c>/api/hrm/employment-assignments</c>
/// (with <c>asOf</c>) and <c>/api/hrm/employment-events</c>; their generated writes are closed. Answers are as for the person endpoints.
/// </summary>
public static class EmploymentEndpoints
{
    private const string Employments = "hrm.employments";
    private const string Contracts = "hrm.contracts";
    private const string BankAccounts = "hrm.employee_bank_accounts";
    private const string Registrations = "hrm.employment_statutory_registrations";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmEmploymentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/employments", async (HireRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.HireAsync(body, ct).ConfigureAwait(false))), "create", Employments, EmploymentPermissions.EmploymentsCreate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/events", async (Guid id, EmploymentChangeRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RecordChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EventsCreate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/events/check", async (Guid id, EmploymentChangeRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CheckChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EventsCreate);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/as-of", async (Guid id, [FromQuery] DateOnly? date, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AsOfAsync(id, date, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EmploymentsRead);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/timeline", async (Guid id, [FromQuery] string? kind, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.TimelineAsync(id, kind, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EventsRead);
        Tag(app.MapPut("/api/hrm/employments/{id:guid}/user", async (Guid id, UserLinkRequest body, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.LinkUserAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.UserLink);
        Tag(app.MapDelete("/api/hrm/employments/{id:guid}/user", async (Guid id, [FromQuery] long? rowVersion, IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UnlinkUserAsync(id, rowVersion, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.UserLink);
        // S1.20c: transfers between companies, rehire, contracts and probation (Build items 11 to 13).
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/transfer", async (Guid id, TransferRequest body, IEmploymentMoves s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.TransferAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.Transfer);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/transfer/check", async (Guid id, TransferRequest body, IEmploymentMoves s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CheckTransferAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.Transfer);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/rehire", async (Guid id, RehireRequest body, IEmploymentMoves s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RehireAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.EmploymentsCreate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/contracts", async (Guid id, ContractRequest body, IEmploymentTermsService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateContractAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.ContractsCreate);
        Tag(app.MapPost("/api/hrm/contracts/{id:guid}/renew", async (Guid id, ContractRequest body, IEmploymentTermsService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RenewContractAsync(id, body, ct).ConfigureAwait(false))), "record_action", Contracts, EmploymentPermissions.ContractsCreate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/probation/extend", async (Guid id, ProbationDecisionRequest body, IEmploymentTermsService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ExtendProbationAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.ProbationUpdate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/probation/confirm", async (Guid id, ProbationDecisionRequest body, IEmploymentTermsService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ConfirmProbationAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.ProbationUpdate);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/probation/not-confirm", async (Guid id, ProbationDecisionRequest body, IEmploymentTermsService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.NotConfirmProbationAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.ProbationUpdate);
        // S1.20d: payment accounts (held for a second person while hrm.bank_accounts.requires_approval is on) and statutory registrations.
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/bank-accounts", async (Guid id, [FromQuery] DateOnly? asOf, IEmployeeBankAccountService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(id, asOf, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.BankAccountsRead);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/bank-accounts", async (Guid id, BankAccountsRequest body, IEmployeeBankAccountService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.BankAccountsUpdate);
        Tag(app.MapGet("/api/hrm/bank-accounts/{id:guid}/reveal", async (Guid id, IEmployeeBankAccountService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RevealAsync(id, ct).ConfigureAwait(false))), "record_action", BankAccounts, EmploymentPermissions.BankAccountsReveal);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/statutory-registrations", async (Guid id, [FromQuery] DateOnly? asOf, IStatutoryRegistrationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(id, asOf, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.RegistrationsRead);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/statutory-registrations", async (Guid id, StatutoryRegistrationRequest body, IStatutoryRegistrationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, EmploymentPermissions.RegistrationsCreate);
        Tag(app.MapPut("/api/hrm/statutory-registrations/{id:guid}", async (Guid id, StatutoryRegistrationRequest body, IStatutoryRegistrationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Registrations, EmploymentPermissions.RegistrationsUpdate);
        Tag(app.MapDelete("/api/hrm/statutory-registrations/{id:guid}", async (Guid id, [FromQuery] long? rowVersion, IStatutoryRegistrationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DeleteAsync(id, rowVersion, ct).ConfigureAwait(false))), "record_action", Registrations, EmploymentPermissions.RegistrationsDelete);
        Tag(app.MapGet("/api/hrm/statutory-registrations/{id:guid}/reveal", async (Guid id, IStatutoryRegistrationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.RevealAsync(id, ct).ConfigureAwait(false))), "record_action", Registrations, EmploymentPermissions.RegistrationsReveal);
        Tag(app.MapGet("/api/hrm/employment-catalogue", async (IEmploymentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CatalogueAsync(ct).ConfigureAwait(false))), "catalog", Employments, EmploymentPermissions.EmploymentsRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
