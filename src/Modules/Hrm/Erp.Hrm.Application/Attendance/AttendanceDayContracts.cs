using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>Process an employment's days now (slice S1.29 Build item 5, on demand): the first and last day, at most 62 days, not after today.</summary>
public sealed class ProcessDaysRequest
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

/// <summary>
/// The attendance engine's API (slice S1.29a; FR-HRM-ATT-10, ATT-11, GHR-03): process an employment's days, read its day results as they
/// are or as they were known at a moment (row versions), explain one day (the stored result, its history, its exceptions and what processing now
/// would give, or why the day is not processed), and the exception queue of a company. Every call checks the permission and the data scope.
/// </summary>
public interface IAttendanceDayService
{
    Task<PeopleResult> ProcessAsync(Guid employmentId, ProcessDaysRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> DaysAsync(Guid employmentId, DateOnly? firstDay, DateOnly? lastDay, DateTimeOffset? asOf, bool history, CancellationToken cancellationToken);

    Task<PeopleResult> ExplainAsync(Guid employmentId, DateOnly workDate, CancellationToken cancellationToken);

    Task<PeopleResult> ExceptionsAsync(Guid? companyId, string? status, string? kind, DateOnly? firstDay, DateOnly? lastDay, int? limit, CancellationToken cancellationToken);
}
