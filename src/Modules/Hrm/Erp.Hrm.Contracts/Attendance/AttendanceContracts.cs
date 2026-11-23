namespace Erp.Hrm.Contracts.Attendance;

/// <summary>Permission codes of attendance capture (slice S1.27a; hand-written ones are declared in <c>Access/permissions.json</c>).</summary>
public static class AttendancePermissions
{
    public const string EventsRead = "hrm.attendance_events.read";

    /// <summary>Append punches for other people (HR entry, imports): the internal ingestion contract (FR-HRM-DAT-01, DAT-02).</summary>
    public const string EventsAppend = "hrm.attendance_events.create";

    /// <summary>Check in and out of the signed-in employee's own employment, from the web or by scanning a gate's QR code (FR-HRM-ATT-01). Self-service.</summary>
    public const string Punch = "hrm.attendance.punch";

    /// <summary>Use the kiosk screen of a shared station: punch for an employee who types a badge or PIN number (FR-HRM-ATT-01).</summary>
    public const string Kiosk = "hrm.attendance.kiosk";

    /// <summary>Show the rotating QR code of a location on a screen at the gate (FR-HRM-ATT-01).</summary>
    public const string QrDisplay = "hrm.attendance.qr_display";
}

/// <summary>The sources of a punch (<c>hrm.attendance_events.source</c>).</summary>
public static class AttendanceSources
{
    public const string Web = "web";
    public const string Qr = "qr";
    public const string Kiosk = "kiosk";
    public const string Biometric = "biometric";
    public const string HrEntry = "hr_entry";
    public const string Import = "import";

    public static IReadOnlyList<string> All { get; } = [Web, Qr, Kiosk, Biometric, HrEntry, Import];
}

/// <summary>The directions of a punch (<c>hrm.attendance_events.direction</c>).</summary>
public static class AttendanceDirections
{
    public const string In = "in";
    public const string Out = "out";
    public const string BreakOut = "break_out";
    public const string BreakIn = "break_in";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [In, Out, BreakOut, BreakIn, Unknown];
}

/// <summary>
/// One punch offered to the event store (slice S1.27a Build item 1; FR-HRM-DAT-01, DAT-02). <see cref="EventAt"/> is when the person
/// punched (the device's time, or the server's for the web); the server's receipt time is always its own clock. The key is unique per source
/// and employment: sending the same punch again gives the same event, never a second one.
/// </summary>
public sealed record AttendanceEventInput(
    Guid EmploymentId,
    DateTimeOffset EventAt,
    string Direction,
    string Source,
    string IdempotencyKey,
    Guid? DeviceId = null,
    string? IpAddress = null,
    Guid? SupersedesEventId = null);

/// <summary>What became of one offered punch: <c>stored</c>, <c>already_received</c> (same key before) or <c>refused</c> with a localization key.</summary>
public sealed record AttendanceEventOutcome(string IdempotencyKey, string Status, Guid? EventId, bool IsDuplicate, string? Error)
{
    public const string Stored = "stored";
    public const string AlreadyReceived = "already_received";
    public const string Refused = "refused";
}

/// <summary>
/// The attendance event store: the one writer of <c>hrm.attendance_events</c> (append only). Web, kiosk, QR, HR entry and (S1.27b) device
/// connectors all append through it, so duplicates, the future tolerance, the local date and the receipt time are decided in one place.
/// </summary>
public interface IAttendanceEventStore
{
    /// <summary>Appends the punches in the order given, each on its own merits; a refused punch does not stop the others.</summary>
    Task<IReadOnlyList<AttendanceEventOutcome>> AppendAsync(IReadOnlyList<AttendanceEventInput> events, CancellationToken cancellationToken);
}
