namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permission codes of the attendance device services (slice S1.27b, CR-088 S1.27b detail). The generated writes of
/// <c>hrm.attendance_devices</c>, <c>hrm.device_user_maps</c> and <c>hrm.unmapped_punches</c> are closed, so these actions are declared by hand in
/// <c>Erp.Hrm.Infrastructure/Access/permissions.json</c>. Reads stay the generated <c>&lt;table&gt;.read</c>.
/// </summary>
public static class DevicePermissions
{
    public const string DevicesRead = "hrm.attendance_devices.read";

    /// <summary>Register a device at a location (FR-HRM-ATT-02).</summary>
    public const string DevicesCreate = "hrm.attendance_devices.create";

    /// <summary>Change a device's connection or credentials, or retire it (FR-HRM-ATT-02).</summary>
    public const string DevicesUpdate = "hrm.attendance_devices.update";

    /// <summary>Ask for a device to be read now (FR-HRM-ATT-02, ATT-03).</summary>
    public const string DevicesSync = "hrm.attendance_devices.sync";

    /// <summary>Send a device's punches to the push endpoint: held by the integration user a push gateway signs in as (S1.27c replaces it with an API key scope).</summary>
    public const string DevicesPush = "hrm.attendance_devices.push";

    public const string MapsRead = "hrm.device_user_maps.read";

    /// <summary>Map a device user number to an employment, with consent where biometrics are switched on (FR-HRM-ATT-03, ATT-20).</summary>
    public const string MapsCreate = "hrm.device_user_maps.create";

    /// <summary>End a mapping by hand (FR-HRM-ATT-03).</summary>
    public const string MapsEnd = "hrm.device_user_maps.end";

    /// <summary>Confirm that a former employee's user was removed from the device (FR-HRM-ATT-20).</summary>
    public const string MapsRemoval = "hrm.device_user_maps.removal";

    public const string UnmappedRead = "hrm.unmapped_punches.read";

    /// <summary>Discard a punch that belongs to nobody (FR-HRM-ATT-03).</summary>
    public const string UnmappedDiscard = "hrm.unmapped_punches.discard";

    /// <summary>The codes declared by hand in <c>permissions.json</c> (the generated reads are not listed).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [DevicesCreate, DevicesUpdate, DevicesSync, DevicesPush, MapsCreate, MapsEnd, MapsRemoval, UnmappedDiscard];
}

/// <summary>Driver codes, as kept in <c>itg.connectors.provider</c> (kind <c>biometric</c>).</summary>
public static class DeviceProviders
{
    /// <summary>Any device or gateway that pushes signed batches to the push endpoint.</summary>
    public const string GenericPush = "generic_push";

    /// <summary>Pull driver for the ZKTeco-family protocol (CR-088 slice S1.27 Q4).</summary>
    public const string ZkTeco = "zkteco";

    public static IReadOnlyList<string> All { get; } = [GenericPush, ZkTeco];
}

/// <summary>
/// What a driver is told about a device. The secret (device password, communication key or push secret) is read from the secret store for
/// the call and lives only in memory: drivers never log it or put it in an error text.
/// </summary>
public sealed record DeviceConnection(Guid DeviceId, string SerialNo, string Host, int Port, string Protocol, string? Secret, TimeZoneInfo Zone);

/// <summary>
/// One punch read from a device (FR-HRM-ATT-03). Only the device user number, the time and the direction exist here: there is no field
/// for a template, a photo or a name, so none can be carried (FR-HRM-ATT-20).
/// </summary>
public sealed record DeviceLogRecord(string DeviceUserNo, DateTimeOffset At, string Direction, string? SourceKey = null);

/// <summary>The outcome of a health check: reachable or not, with a localization key and a short reason when not (never the secret).</summary>
public sealed record DeviceHealth(bool Reachable, string? ErrorKey = null, string? Detail = null);

/// <summary>The punches a driver read, or why it could not.</summary>
public sealed record DevicePullResult(bool Succeeded, IReadOnlyList<DeviceLogRecord> Records, string? ErrorKey = null, string? Detail = null);

/// <summary>
/// The extension point for device models (FR-HRM-ATT-03): one implementation per <see cref="Provider"/>. A driver reads punches
/// (<see cref="PullAsync"/>), accepts a pushed batch (<see cref="ParsePush"/>) and checks health. It never reads, stores or returns a
/// biometric template; the types it can return have no place for one.
/// </summary>
public interface IAttendanceDeviceDriver
{
    /// <summary>The <c>itg.connectors.provider</c> code this driver serves.</summary>
    string Provider { get; }

    /// <summary>Whether the platform dials the device (true) or the device or its gateway sends (false).</summary>
    bool CanPull { get; }

    Task<DeviceHealth> CheckHealthAsync(DeviceConnection device, CancellationToken cancellationToken);

    /// <summary>Reads the punches the device holds, from <paramref name="since"/> when the device can filter (the caller removes repeats by key).</summary>
    Task<DevicePullResult> PullAsync(DeviceConnection device, DateTimeOffset? since, CancellationToken cancellationToken);

    /// <summary>Turns a pushed request body into punches; null when the body is not what the driver accepts.</summary>
    IReadOnlyList<DeviceLogRecord>? ParsePush(string body, DeviceConnection device);
}

/// <summary>Events of the device services, written to the HR outbox.</summary>
public static class DeviceEvents
{
    public const string AggregateType = "hrm.device_user_map";

    /// <summary>A mapping ended because the employment ended: the user is to be removed from the device by hand (FR-HRM-ATT-20).</summary>
    public const string RemovalDue = "hrm.device_user.removal_due";
}
