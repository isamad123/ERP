namespace Erp.Kernel.Contracts.Org;

/// <summary>One holiday of a holiday calendar: its date, names in each language, kind (public, religious, optional, restricted) and whether the date is still tentative (moon sighting).</summary>
public sealed record HolidayInfo(Guid Id, Guid CalendarId, DateOnly Date, IReadOnlyDictionary<string, string> Name, string Kind, bool IsTentative);

/// <summary>
/// The holiday calendar questions of business modules (slice S1.28b, FR-HRM-ATT-15; D-10: a module never reads kernel tables). The calendar of a
/// place is the one of its location, else the one of the company. A place with no calendar has no holidays (never a guess).
/// </summary>
public interface IHolidayCalendars
{
    /// <summary>The id of the calendar in force for a company and, when given, a location; null when none is set.</summary>
    Task<Guid?> CalendarOfAsync(Guid companyId, Guid? locationId, CancellationToken cancellationToken);

    /// <summary>The holidays of one calendar between two dates (both included), by date.</summary>
    Task<IReadOnlyList<HolidayInfo>> HolidaysAsync(Guid calendarId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>The holidays of the given ids that exist.</summary>
    Task<IReadOnlyList<HolidayInfo>> FindAsync(IReadOnlyCollection<Guid> holidayIds, CancellationToken cancellationToken);
}
