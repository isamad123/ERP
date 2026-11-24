using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// The signed push endpoint's service (slice S1.27b Build item 7; FR-HRM-ATT-03): a device, or the gateway in front of it, sends a batch of punches
/// with its original times. The caller holds <c>hrm.attendance_devices.push</c> (an integration user, replaced by an API-client scope in S1.27c) and signs the
/// body with the device's secret (<c>X-Erp-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>); a stale or wrong signature is refused before anything is parsed.
/// The device's driver turns the body into punches; they go through <see cref="DeviceIngestion"/>, so a resend, or hours of offline buffer, causes no duplicate and no loss.
/// </summary>
internal sealed class DevicePushService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    ICallerAccess caller,
    DeviceRuntime runtime,
    DeviceIngestion ingestion) : IDevicePushService
{
    public const string SignatureInvalid = "errors.hrm.device.signature_invalid";
    public const string NoSecret = "errors.hrm.device.no_secret";
    public const string PushInvalid = "errors.hrm.device.push_invalid";
    public const string NotPushable = "errors.hrm.device.not_pushable";
    public const int MaxBodyBytes = 1024 * 1024;

    public async Task<PeopleResult> PushAsync(Guid deviceId, string body, string? signature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!await permissions.HasPermissionAsync(DevicePermissions.DevicesPush, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var device = await db.Set<AttendanceDevice>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsDeleted, cancellationToken).ConfigureAwait(false);
        // A pushing user holds only the push permission for the company: company coverage is the scope (no read permission is needed or implied).
        if (device is null || !await caller.CoversCompanyAsync(DevicePermissions.DevicesPush, device.CompanyId, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (device.Status == "retired")
        {
            return PeopleResult.Invalid(DeviceService.Retired);
        }

        var provider = (await runtime.ProvidersAsync([device.ConnectorId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(device.ConnectorId);
        if (runtime.Driver(provider) is not { CanPull: false } driver)
        {
            return PeopleResult.Invalid(NotPushable);
        }

        if (!await runtime.BiometricEnabledAsync(device.CompanyId, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Invalid(DeviceService.BiometricDisabled);
        }

        var connection = await runtime.ConnectionAsync(device, true, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connection.Secret))
        {
            return PeopleResult.Invalid(NoSecret);
        }

        if (!DeviceRules.VerifySignature(connection.Secret, signature, body, clock.UtcNow))
        {
            return PeopleResult.Forbidden(SignatureInvalid);
        }

        var records = driver.ParsePush(body, connection);
        if (records is null)
        {
            return PeopleResult.Invalid(PushInvalid);
        }

        var result = await ingestion.IngestAsync(device, records, cancellationToken).ConfigureAwait(false);
        await ingestion.TouchAsync(device.Id, false, records.Count == 0 ? null : records.Max(r => r.At), cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            received = records.Count,
            stored = result.Stored,
            alreadyReceived = result.AlreadyReceived,
            duplicates = result.Duplicates,
            unmapped = result.Unmapped,
            refused = result.Refused,
            errors = result.Errors,
        });
    }
}
