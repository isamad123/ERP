using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Attendance;

/// <summary>
/// Create or change a shift (slice S1.28a Build item 1, FR-HRM-ATT-06). The property names are those of the generated shift API. A change
/// names <see cref="EffectiveFrom"/>: the shift in force ends the day before and a new version starts that day (the old row is kept).
/// Sending <c>null</c> for an optional field empties it (see <see cref="Parse"/>).
/// </summary>
public sealed class ShiftRequest
{
    public Guid? CompanyId { get; init; }

    public string? Code { get; init; }

    public IReadOnlyDictionary<string, string>? NameI18n { get; init; }

    public string? ShiftType { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public TimeOnly? CoreStart { get; init; }

    public TimeOnly? CoreEnd { get; init; }

    public bool? CrossesMidnight { get; init; }

    /// <summary>Segments of the day, <c>[{"start":"09:00","end":"13:00","kind":"work"}]</c>; kind is <c>work</c> (default) or <c>break</c>.</summary>
    public JsonElement? Segments { get; init; }

    public int? MinFullDayMinutes { get; init; }

    public Guid? PolicyId { get; init; }

    public bool? IsActive { get; init; }

    /// <summary>On a change: the first day of the new version. Required when anything but the name or the active flag changes and the shift has been in use.</summary>
    public DateOnly? EffectiveFrom { get; init; }

    /// <summary>Names of optional fields to empty: coreStart, coreEnd, policyId.</summary>
    public IReadOnlyList<string>? Clear { get; init; }

    public long? RowVersion { get; init; }

    public static readonly string[] Clearable = ["coreStart", "coreEnd", "policyId"];

    public static ShiftRequest Parse(JsonElement body)
    {
        var request = body.ValueKind == JsonValueKind.Object ? body.Deserialize<ShiftRequest>(JsonSerializerOptions.Web) ?? new ShiftRequest() : new ShiftRequest();
        if (body.ValueKind != JsonValueKind.Object)
        {
            return request;
        }

        var nulled = body.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Null && Clearable.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Select(p => Clearable.First(c => string.Equals(c, p.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return nulled.Count == 0 ? request : new ShiftRequest
        {
            CompanyId = request.CompanyId, Code = request.Code, NameI18n = request.NameI18n, ShiftType = request.ShiftType, StartTime = request.StartTime, EndTime = request.EndTime,
            CoreStart = request.CoreStart, CoreEnd = request.CoreEnd, CrossesMidnight = request.CrossesMidnight, Segments = request.Segments, MinFullDayMinutes = request.MinFullDayMinutes,
            PolicyId = request.PolicyId, IsActive = request.IsActive, EffectiveFrom = request.EffectiveFrom, RowVersion = request.RowVersion,
            Clear = [.. (request.Clear ?? []).Concat(nulled).Distinct(StringComparer.Ordinal)],
        };
    }
}

/// <summary>Create a draft roster for a unit (or the whole company) and a period (slice S1.28a Build item 4).</summary>
public sealed class RosterRequest
{
    public Guid? CompanyId { get; init; }

    public string? Name { get; init; }

    public Guid? OrgUnitId { get; init; }

    public DateOnly? PeriodStart { get; init; }

    public DateOnly? PeriodEnd { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>A custom field of the employment equal to a value, as a group rule of an assignment.</summary>
public sealed record CustomFieldMatch(string? Key, JsonElement Value);

/// <summary>
/// Assign a shift to people for the days of a roster (slice S1.28a Build item 4, ruling Q1: a group is resolved to individual lines at save
/// time and the lines stay editable). The people are named employments, or a group: the unit of the roster (or <see cref="OrgUnitId"/>,
/// with its sub-units when <see cref="SubUnits"/>), a grade, a custom field value, or <see cref="Everyone"/> in the roster's unit or company.
/// <see cref="WeeklyOff"/> (default true) makes the days the person's shift policy names as weekly off into weekly-off lines instead.
/// A day that already has a line is kept unless <see cref="Replace"/> is true.
/// </summary>
public sealed class AssignRequest
{
    public Guid? ShiftId { get; init; }

    public IReadOnlyList<Guid>? EmploymentIds { get; init; }

    public Guid? OrgUnitId { get; init; }

    public bool? SubUnits { get; init; }

    public Guid? GradeId { get; init; }

    public CustomFieldMatch? CustomField { get; init; }

    public bool? Everyone { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public bool? WeeklyOff { get; init; }

    public bool? Replace { get; init; }
}

/// <summary>Copy a roster forward: a new draft with the same days moved by the length of the period (or to <see cref="PeriodStart"/>).</summary>
public sealed class CopyForwardRequest
{
    public string? Name { get; init; }

    public DateOnly? PeriodStart { get; init; }
}

/// <summary>One day of one person to set. <see cref="DayType"/> working needs a shift; weekly_off and holiday take none; <see cref="Remove"/> deletes a draft's day.</summary>
public sealed class DayChange
{
    public Guid? EmploymentId { get; init; }

    public DateOnly? WorkDate { get; init; }

    public string? DayType { get; init; }

    public Guid? ShiftId { get; init; }

    public string? Reason { get; init; }

    public bool? Remove { get; init; }
}

/// <summary>Edit single days. After publication <see cref="Reason"/> (or each change's own reason) is required and the people are told again.</summary>
public sealed class ChangeDaysRequest
{
    public IReadOnlyList<DayChange>? Changes { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Publish a roster; <see cref="RowVersion"/> is the one the caller read.</summary>
public sealed class PublishRosterRequest
{
    public long? RowVersion { get; init; }
}

/// <summary>The shift services (slice S1.28a Build items 1 to 3).</summary>
public interface IShiftService
{
    Task<PeopleResult> CreateAsync(ShiftRequest request, CancellationToken cancellationToken);

    /// <summary>Changes a shift; times and type change as a new version from <see cref="ShiftRequest.EffectiveFrom"/>.</summary>
    Task<PeopleResult> UpdateAsync(Guid shiftId, ShiftRequest request, CancellationToken cancellationToken);

    /// <summary>The weekly off dates of an employment's <c>shift</c> policy in a period (FR-HRM-ATT-06); empty with the reason when no policy applies.</summary>
    Task<PeopleResult> WeeklyOffAsync(Guid employmentId, DateOnly? firstDay, DateOnly? lastDay, CancellationToken cancellationToken);
}

/// <summary>The roster services (slice S1.28a Build items 4 and 5).</summary>
public interface IRosterService
{
    Task<PeopleResult> CreateAsync(RosterRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid rosterId, RosterRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> AssignAsync(Guid rosterId, AssignRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> CopyForwardAsync(Guid rosterId, CopyForwardRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ChangeDaysAsync(Guid rosterId, ChangeDaysRequest request, CancellationToken cancellationToken);

    /// <summary>Every finding of the roster checks, together.</summary>
    Task<PeopleResult> ChecksAsync(Guid rosterId, CancellationToken cancellationToken);

    Task<PeopleResult> PublishAsync(Guid rosterId, PublishRosterRequest request, CancellationToken cancellationToken);
}
