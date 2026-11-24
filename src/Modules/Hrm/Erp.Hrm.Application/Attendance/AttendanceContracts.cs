using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>One punch of an HR entry or import batch (slice S1.27a Build item 1). Sources other than <c>hr_entry</c> and <c>import</c> have their own paths.</summary>
public sealed class AppendEventItem
{
    public Guid? EmploymentId { get; init; }

    /// <summary>When the person punched, with its offset (stored as an instant).</summary>
    public DateTimeOffset? EventAt { get; init; }

    public string? Direction { get; init; }

    /// <summary><c>hr_entry</c> or <c>import</c>.</summary>
    public string? Source { get; init; }

    public string? IdempotencyKey { get; init; }

    /// <summary>A correction: the earlier punch of the same employment this one replaces (the earlier one stays, never counted twice by the day engine).</summary>
    public Guid? SupersedesEventId { get; init; }
}

public sealed class AppendEventsRequest
{
    public IReadOnlyList<AppendEventItem>? Events { get; init; }
}

/// <summary>A web check-in or check-out: the employee is the signed-in user's own employment, never taken from the request.</summary>
public sealed class PunchRequest
{
    /// <summary><c>in</c> or <c>out</c>.</summary>
    public string? Direction { get; init; }

    /// <summary>A client-made key, so a retried request punches once.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Only to choose among the signed-in user's own employments (one per company); it never names another person.</summary>
    public Guid? CompanyId { get; init; }
}

/// <summary>A punch by scanning the QR code shown at a gate: the code, the direction, and the signed-in session (Q5).</summary>
public sealed class QrPunchRequest
{
    public string? Code { get; init; }

    public string? Direction { get; init; }

    public string? IdempotencyKey { get; init; }
}

/// <summary>A punch at a shared kiosk station: the station's signed-in user and the badge or PIN number the employee typed.</summary>
public sealed class KioskPunchRequest
{
    public Guid? CompanyId { get; init; }

    /// <summary>The badge or PIN number as mapped in <c>hrm.device_user_maps</c> for all devices of the company.</summary>
    public string? BadgeNo { get; init; }

    public string? Direction { get; init; }

    public string? IdempotencyKey { get; init; }
}

/// <summary>Capture services of slice S1.27a: web, QR and kiosk punches, the employee's own day, and the rotating QR code of a location.</summary>
public interface IAttendanceCaptureService
{
    Task<PeopleResult> PunchAsync(PunchRequest request, string? ipAddress, CancellationToken cancellationToken);

    Task<PeopleResult> QrPunchAsync(QrPunchRequest request, string? ipAddress, CancellationToken cancellationToken);

    Task<PeopleResult> KioskPunchAsync(KioskPunchRequest request, string? ipAddress, CancellationToken cancellationToken);

    /// <summary>The signed-in user's employments and punches of today (answers 200 with no employments when the user has none).</summary>
    Task<PeopleResult> MyDayAsync(Guid? companyId, CancellationToken cancellationToken);

    /// <summary>The current QR code of a location (to show at the gate) and when it changes.</summary>
    Task<PeopleResult> QrCodeAsync(Guid companyId, Guid locationId, CancellationToken cancellationToken);

    /// <summary>HR entry and imports: appends a batch through the event store (internal ingestion contract).</summary>
    Task<PeopleResult> AppendAsync(AppendEventsRequest request, CancellationToken cancellationToken);
}
