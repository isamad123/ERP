using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Application.Org;
using Erp.Kernel.Api.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Org;

/// <summary>
/// The organization routes the generated API cannot offer (slice S1.14a, CR-070): company registrations and bank accounts,
/// whose values are encrypted (the generated API never returns or writes them), and the copy of a holiday calendar year.
/// Every route is tenant-scoped like all others (the customer comes from the host and token); company and record ids that do
/// not exist in the bound customer's database answer 404, ids the caller's roles do not cover 403.
/// </summary>
public static class OrganizationEndpoints
{
    private const string Registrations = "org.company_registrations";
    private const string BankAccounts = "org.company_bank_accounts";
    private const string Calendars = "ref.calendars";
    private const string Hierarchies = "org.org_hierarchies";
    private const string Units = "org.org_units";

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/org/companies/{id:guid}/registrations", async (Guid id, IOrganizationAdministration s, CancellationToken ct) =>
            Read(await s.RegistrationsAsync(id, ct).ConfigureAwait(false))).WithTags(Registrations), "query", Registrations, "org.company_registrations.read");
        Tag(app.MapPost("/api/org/companies/{id:guid}/registrations", (Guid id, RegistrationRequest body, IOrganizationAdministration s, CancellationToken ct) =>
            Answer(s.CreateRegistrationAsync(id, body, ct), "/api/org/company-registrations/")).WithTags(Registrations), "record_action", Registrations, "org.company_registrations.create");
        Tag(app.MapPut("/api/org/company-registrations/{id:guid}", (Guid id, RegistrationRequest body, IOrganizationAdministration s, CancellationToken ct) =>
            Answer(s.UpdateRegistrationAsync(id, body, ct))).WithTags(Registrations), "record_action", Registrations, "org.company_registrations.update");

        Tag(app.MapGet("/api/org/companies/{id:guid}/bank-accounts", async (Guid id, IOrganizationAdministration s, CancellationToken ct) =>
            Read(await s.BankAccountsAsync(id, ct).ConfigureAwait(false))).WithTags(BankAccounts), "query", BankAccounts, "org.company_bank_accounts.read");
        Tag(app.MapPost("/api/org/companies/{id:guid}/bank-accounts", (Guid id, BankAccountRequest body, IOrganizationAdministration s, CancellationToken ct) =>
            Answer(s.CreateBankAccountAsync(id, body, ct), "/api/org/company-bank-accounts/")).WithTags(BankAccounts), "record_action", BankAccounts, "org.company_bank_accounts.create");
        Tag(app.MapPut("/api/org/company-bank-accounts/{id:guid}", (Guid id, BankAccountRequest body, IOrganizationAdministration s, CancellationToken ct) =>
            Answer(s.UpdateBankAccountAsync(id, body, ct))).WithTags(BankAccounts), "record_action", BankAccounts, "org.company_bank_accounts.update");
        Tag(app.MapPost("/api/org/company-bank-accounts/{id:guid}/remove", (Guid id, BankAccountRequest body, IOrganizationAdministration s, CancellationToken ct) =>
            Answer(s.DeleteBankAccountAsync(id, body, ct))).WithTags(BankAccounts), "record_action", BankAccounts, "org.company_bank_accounts.delete");

        Tag(app.MapPost("/api/ref/calendars/{id:guid}/copy-year", async (Guid id, CopyYearRequest body, IOrganizationAdministration s, CancellationToken ct) =>
        {
            var (result, copied) = await s.CopyYearAsync(id, body, ct).ConfigureAwait(false);
            return result.Outcome == AccessOutcome.Done ? Results.Ok(copied) : Problem(result);
        }).WithTags(Calendars), "record_action", Calendars, "ref.holidays.create");

        // S1.14b (CR-070 S1.14b detail): a hierarchy on a date, two dates compared, and the move of a unit.
        Tag(app.MapGet("/api/org/hierarchies/{id:guid}/tree", async (Guid id, DateOnly? asOf, IOrgUnitService s, CancellationToken ct) =>
        {
            var (result, tree) = await s.TreeAsync(id, asOf, ct).ConfigureAwait(false);
            return result.Outcome == AccessOutcome.Done ? Results.Ok(tree) : Problem(result);
        }).WithTags(Hierarchies), "query", Hierarchies, "org.org_unit_links.read");
        Tag(app.MapGet("/api/org/hierarchies/{id:guid}/compare", async (Guid id, DateOnly? from, DateOnly? to, IOrgUnitService s, CancellationToken ct) =>
        {
            var (result, compare) = await s.CompareAsync(id, from, to, ct).ConfigureAwait(false);
            return result.Outcome == AccessOutcome.Done ? Results.Ok(compare) : Problem(result);
        }).WithTags(Hierarchies), "query", Hierarchies, "org.org_unit_links.read");
        Tag(app.MapPost("/api/org/org-units/{id:guid}/move", (Guid id, MoveUnitRequest body, IOrgUnitService s, CancellationToken ct) =>
            Answer(s.MoveAsync(id, body, ct), "/api/org/org-unit-links/")).WithTags(Units), "record_action", Units, "org.org_unit_links.update");
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, resource, permission));

    private static IResult Read<T>(OrgRead<T> read) =>
        read.Outcome == AccessOutcome.Done ? Results.Ok(read.Items) : Problem(new AccessResult(read.Outcome, read.Error));

    private static async Task<IResult> Answer(Task<AccessResult> work, string? created = null)
    {
        var result = await work.ConfigureAwait(false);
        return result.Outcome switch
        {
            AccessOutcome.Done when created is not null && result.Id is { } id => Results.Created(created + id, new AccessChangeResponse("done", id, null)),
            AccessOutcome.Done => Results.Ok(new AccessChangeResponse("done", result.Id, null)),
            AccessOutcome.Pending => Results.Accepted(null, new AccessChangeResponse("pending", null, result.RequestId)),
            _ => Problem(result),
        };
    }

    private static IResult Problem(AccessResult result) => result.Outcome switch
    {
        AccessOutcome.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found"),
        AccessOutcome.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: result.Error ?? "errors.access.forbidden"),
        AccessOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Error),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error ?? "errors.data.invalid_request"),
    };
}
