using System.Text.Json;
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
/// The pay set-up endpoints (slice S1.34a; S1.34b adds components with their versions, salary structures, compensation and revisions): create and change a pay group, generate the periods of a tax year, change a period's
/// cut-off and payment dates before it opens, the payroll calendar and the readiness checklist. Reads of the rows stay the
/// generated <c>/api/hrm/pay-groups</c> and <c>/api/hrm/pay-periods</c> (their generated writes are closed). Answers are as for
/// the person endpoints.
/// </summary>
public static class PayEndpoints
{
    private const string Groups = "hrm.pay_groups";
    private const string Periods = "hrm.pay_periods";
    private const string Components = "hrm.pay_components";
    private const string Structures = "hrm.salary_structures";
    private const string Compensation = "hrm.compensation_assignments";
    private static readonly EntitlementMetadata Payroll = new("hrm.payroll", "hrm");

    public static IEndpointRouteBuilder MapHrmPayEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/pay-groups", async (JsonElement body, IPayGroupService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(PayGroupRequest.Parse(body), ct).ConfigureAwait(false))), "create", Groups, PayPermissions.GroupsCreate);
        Tag(app.MapPut("/api/hrm/pay-groups/{id:guid}", async (Guid id, JsonElement body, IPayGroupService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, PayGroupRequest.Parse(body), ct).ConfigureAwait(false))), "record_action", Groups, PayPermissions.GroupsUpdate);
        Tag(app.MapPost("/api/hrm/pay-groups/{id:guid}/periods/generate", async (Guid id, GeneratePeriodsRequest body, IPayCalendarService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GenerateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Groups, PayPermissions.PeriodsGenerate);
        Tag(app.MapPut("/api/hrm/pay-periods/{id:guid}", async (Guid id, ChangePeriodRequest body, IPayCalendarService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChangeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Periods, PayPermissions.PeriodsUpdate);
        Tag(app.MapGet("/api/hrm/pay-calendar", async ([FromQuery] Guid? payGroupId, [FromQuery] int? taxYear, IPayCalendarService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CalendarAsync(payGroupId, taxYear, ct).ConfigureAwait(false))), "catalog", Periods, PayPermissions.PeriodsRead);
        Tag(app.MapGet("/api/hrm/pay-periods/{id:guid}/readiness", async (Guid id, IPayCalendarService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ReadinessAsync(id, ct).ConfigureAwait(false))), "record_action", Periods, PayPermissions.ReadinessRead);

        // S1.34b: components and versions, structures, compensation and revisions.
        Tag(app.MapPost("/api/hrm/pay-components", async (JsonElement body, IPayComponentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(PayComponentRequest.Parse(body), ct).ConfigureAwait(false))), "create", Components, PayPermissions.ComponentsCreate);
        Tag(app.MapPut("/api/hrm/pay-components/{id:guid}", async (Guid id, JsonElement body, IPayComponentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, PayComponentRequest.Parse(body), ct).ConfigureAwait(false))), "record_action", Components, PayPermissions.ComponentsUpdate);
        Tag(app.MapPost("/api/hrm/pay-components/{id:guid}/versions", async (Guid id, PayComponentVersionRequest body, IPayComponentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AddVersionAsync(id, body, ct).ConfigureAwait(false))), "record_action", Components, PayPermissions.ComponentsUpdate);
        Tag(app.MapGet("/api/hrm/pay-components/{id:guid}/detail", async (Guid id, [FromQuery] DateOnly? asOf, IPayComponentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, asOf, ct).ConfigureAwait(false))), "record_action", Components, PayPermissions.ComponentsRead);
        Tag(app.MapPost("/api/hrm/pay-formulas/check", async (FormulaCheckRequest body, IPayComponentService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CheckFormulaAsync(body.CompanyId, body.Formula, ct).ConfigureAwait(false))), "catalog", Components, PayPermissions.ComponentsRead);
        Tag(app.MapPost("/api/hrm/salary-structures", async (JsonElement body, ISalaryStructureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(SalaryStructureRequest.Parse(body), ct).ConfigureAwait(false))), "create", Structures, PayPermissions.StructuresCreate);
        Tag(app.MapPut("/api/hrm/salary-structures/{id:guid}", async (Guid id, JsonElement body, ISalaryStructureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, SalaryStructureRequest.Parse(body), ct).ConfigureAwait(false))), "record_action", Structures, PayPermissions.StructuresUpdate);
        Tag(app.MapGet("/api/hrm/salary-structures/{id:guid}/detail", async (Guid id, [FromQuery] DateOnly? asOf, ISalaryStructureService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, asOf, ct).ConfigureAwait(false))), "record_action", Structures, PayPermissions.StructuresRead);
        Tag(app.MapPost("/api/hrm/employments/{id:guid}/compensation", async (Guid id, JsonElement body, ICompensationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.AssignAsync(id, CompensationRequest.Parse(body), ct).ConfigureAwait(false))), "record_action", Compensation, PayPermissions.CompensationCreate);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/compensation", async (Guid id, [FromQuery] DateOnly? asOf, ICompensationService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.GetAsync(id, asOf, ct).ConfigureAwait(false))), "record_action", Compensation, PayPermissions.CompensationRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Payroll);
}

/// <summary>Body of <c>POST /api/hrm/pay-formulas/check</c>: the company whose component codes the formula may name, and the formula.</summary>
public sealed record FormulaCheckRequest(Guid CompanyId, string? Formula);
