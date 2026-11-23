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
/// The pay set-up endpoints (slice S1.34a): create and change a pay group, generate the periods of a tax year, change a period's
/// cut-off and payment dates before it opens, the payroll calendar and the readiness checklist. Reads of the rows stay the
/// generated <c>/api/hrm/pay-groups</c> and <c>/api/hrm/pay-periods</c> (their generated writes are closed). Answers are as for
/// the person endpoints.
/// </summary>
public static class PayEndpoints
{
    private const string Groups = "hrm.pay_groups";
    private const string Periods = "hrm.pay_periods";
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
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Payroll);
}
