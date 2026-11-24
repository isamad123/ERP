using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Api.People;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.Attendance;

/// <summary>
/// The holiday and effective-shift endpoints (slice S1.28b; FR-HRM-ATT-15, ATT-16): holidays of a company's calendar with Hijri dates,
/// an employment's holidays with the optional ones it chose (and the call that changes the choice, which self-service S1.26 reuses), and
/// the shift an employment works on a day with the reason. Answers are as for the person endpoints.
/// </summary>
public static class HolidayEndpoints
{
    private const string Employments = "hrm.employments";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmHolidayEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/hrm/holidays", async ([FromQuery] Guid companyId, [FromQuery] int year, [FromQuery] Guid? locationId, [FromQuery] Guid? calendarId, IHolidayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(companyId, locationId, calendarId, year, ct).ConfigureAwait(false))), "catalog", Employments, HolidayPermissions.HolidaysRead);
        Tag(app.MapGet("/api/hrm/employments/{id:guid}/holidays", async (Guid id, [FromQuery] int year, IHolidayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ForEmploymentAsync(id, year, ct).ConfigureAwait(false))), "record_action", Employments, HolidayPermissions.HolidaysRead);
        Tag(app.MapPut("/api/hrm/employments/{id:guid}/optional-holidays", async (Guid id, OptionalHolidayRequest body, IHolidayService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ChooseAsync(id, body, ct).ConfigureAwait(false))), "record_action", Employments, HolidayPermissions.OptionalHolidaysChoose);
        Tag(app.MapGet("/api/hrm/shifts/effective", async ([FromQuery] Guid employment, [FromQuery] DateOnly date, IEffectiveShiftService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.EffectiveAsync(employment, date, ct).ConfigureAwait(false))), "catalog", Employments, HolidayPermissions.EffectiveShiftRead);
        return app;
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);
}
