using System.Text;
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
/// The attendance device endpoints (slice S1.27b; FR-HRM-ATT-02, ATT-03, ATT-20): the registry and console, the signed push, device-user mapping with
/// consent and removal tasks, unmapped punches and the punch log. Reads of the stored rows stay the generated <c>/api/hrm/attendance-devices</c>,
/// <c>/device-user-maps</c> and <c>/unmapped-punches</c> (their generated writes are closed). The push is signed with the device's secret
/// (<c>X-Erp-Signature</c>) on top of the caller's permission; the secret never appears in any answer.
/// </summary>
public static class DeviceEndpoints
{
    public const string SignatureHeader = "X-Erp-Signature";

    private const string Devices = "hrm.attendance_devices";
    private const string Maps = "hrm.device_user_maps";
    private const string Unmapped = "hrm.unmapped_punches";
    private const string Events = "hrm.attendance_events";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapGet("/api/hrm/attendance/devices", async ([FromQuery] Guid? companyId, IDeviceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(companyId, ct).ConfigureAwait(false))), "catalog", Devices, DevicePermissions.DevicesRead);
        Tag(app.MapPost("/api/hrm/attendance/devices", async (DeviceRequest body, IDeviceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.CreateAsync(body, ct).ConfigureAwait(false))), "create", Devices, DevicePermissions.DevicesCreate);
        Tag(app.MapPut("/api/hrm/attendance/devices/{id:guid}", async (Guid id, DeviceRequest body, IDeviceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Devices, DevicePermissions.DevicesUpdate);
        Tag(app.MapPost("/api/hrm/attendance/devices/{id:guid}/sync", async (Guid id, IDeviceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.SyncNowAsync(id, ct).ConfigureAwait(false))), "record_action", Devices, DevicePermissions.DevicesSync);
        Tag(app.MapPost("/api/hrm/attendance/devices/{id:guid}/push", async (Guid id, HttpContext http, IDevicePushService s, CancellationToken ct) =>
        {
            // The signature covers the exact bytes sent, so the body is read as text and never re-serialized.
            if (http.Request.ContentLength is > Limits.DevicePushLimit)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, false, 8192, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            return PersonEndpoints.Answer(await s.PushAsync(id, body, http.Request.Headers[SignatureHeader].ToString(), ct).ConfigureAwait(false));
        }), "record_action", Devices, DevicePermissions.DevicesPush).WithMetadata(new ApiClientEndpointMetadata(DevicePermissions.DevicesPush)); // S1.27c: an API client with this scope may push

        Tag(app.MapGet("/api/hrm/attendance/device-maps", async ([FromQuery] Guid? companyId, [FromQuery] Guid? deviceId, [FromQuery] string? state, IDeviceMappingService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ListAsync(companyId, deviceId, state, ct).ConfigureAwait(false))), "catalog", Maps, DevicePermissions.MapsRead);
        Tag(app.MapPost("/api/hrm/attendance/device-maps", async (MapRequest body, IDeviceMappingService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.MapAsync(body, ct).ConfigureAwait(false))), "create", Maps, DevicePermissions.MapsCreate);
        Tag(app.MapPost("/api/hrm/attendance/device-maps/{id:guid}/end", async (Guid id, EndMapRequest body, IDeviceMappingService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.EndAsync(id, body, ct).ConfigureAwait(false))), "record_action", Maps, DevicePermissions.MapsEnd);
        Tag(app.MapPost("/api/hrm/attendance/device-maps/{id:guid}/removal-done", async (Guid id, RemovalDoneRequest body, IDeviceMappingService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.ConfirmRemovalAsync(id, body, ct).ConfigureAwait(false))), "record_action", Maps, DevicePermissions.MapsRemoval);

        Tag(app.MapGet("/api/hrm/attendance/unmapped", async ([FromQuery] Guid? companyId, [FromQuery] Guid? deviceId, [FromQuery] string? status, IDeviceMappingService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.UnmappedAsync(companyId, deviceId, status, ct).ConfigureAwait(false))), "catalog", Unmapped, DevicePermissions.UnmappedRead);
        Tag(app.MapPost("/api/hrm/attendance/unmapped/{id:guid}/discard", async (Guid id, DiscardRequest body, IDeviceMappingService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.DiscardAsync(id, body, ct).ConfigureAwait(false))), "record_action", Unmapped, DevicePermissions.UnmappedDiscard);

        Tag(app.MapGet("/api/hrm/attendance/punch-log", async ([AsParameters] PunchLogQuery query, IDeviceService s, CancellationToken ct) =>
            PersonEndpoints.Answer(await s.PunchLogAsync(query, ct).ConfigureAwait(false))), "catalog", Events, AttendancePermissions.EventsRead);
        return app;
    }

    private static RouteHandlerBuilder Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);

    private static class Limits
    {
        public const long DevicePushLimit = 1024 * 1024;
    }
}
