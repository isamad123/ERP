using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Leave;

/// <summary>
/// The team leave calendar's question (slice S1.33a Build item 4; FR-HRM-LVE-15): the dates (at most 62 days), the company (when the caller's scope
/// spans several), a unit, a leave type and a page of people. The people are those the caller's data scope on leave requests allows.
/// </summary>
public sealed record LeaveCalendarQuery(Guid? CompanyId, DateOnly? From, DateOnly? To, Guid? UnitId, Guid? LeaveTypeId, int? Limit, int? Offset);

/// <summary>Team leave calendar and the coverage behind an approval card (slice S1.33a; FR-HRM-LVE-05, -15; GHR-09 shown, never decided here).</summary>
public interface ILeaveCalendarService
{
    /// <summary>Leave by person and day, holidays, weekly offs, and the share of the group away on each day against the company's warning limit.</summary>
    Task<PeopleResult> CalendarAsync(LeaveCalendarQuery query, CancellationToken cancellationToken);

    /// <summary>For one request: on each of its days, the line manager's team, how many are away and the warning; names only within the caller's scope.</summary>
    Task<PeopleResult> CoverageAsync(Guid requestId, CancellationToken cancellationToken);
}
