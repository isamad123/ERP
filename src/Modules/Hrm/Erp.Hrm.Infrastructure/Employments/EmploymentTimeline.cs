using System.Text.Json.Nodes;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>The assignment columns an event may set (snake_case keys of <c>employment_events.changes.assignment</c>).</summary>
public static class AssignmentKeys
{
    public const string OrgUnit = "org_unit_id";
    public const string Branch = "branch_id";
    public const string Location = "location_id";
    public const string Position = "position_id";
    public const string Grade = "grade_id";
    public const string Title = "title";
    public const string Manager = "manager_employment_id";
    public const string PayGroup = "pay_group_id";
    public const string CostAllocation = "cost_allocation";
    public const string Fte = "fte";
    public const string WorkSchedule = "work_schedule";
    public const string IsActing = "is_acting";

    /// <summary>Every key, in the table's order.</summary>
    public static IReadOnlyList<string> All { get; } = [OrgUnit, Branch, Location, Position, Grade, Title, Manager, PayGroup, CostAllocation, Fte, WorkSchedule, IsActing];

    /// <summary>Keys every assignment row needs a value for (the table's required columns).</summary>
    public static IReadOnlyList<string> Required { get; } = [OrgUnit, Branch, Title, CostAllocation, Fte, IsActing];

    /// <summary>Keys a change may empty (the table's optional columns).</summary>
    public static IReadOnlyList<string> Clearable { get; } = [Location, Position, Grade, Manager, WorkSchedule];
}

/// <summary>
/// An employment event as the timeline reads it: its id, kind, date, status, the event it replaces (a correction) and the
/// assignment fields it sets (a full set for a hire), or null when it changes none. <see cref="Ends"/> marks an end of the employment
/// (a <c>separation</c>, S1.20c): from its date there are no job details.
/// </summary>
public sealed record TimelineEvent(Guid Id, string Kind, DateOnly EffectiveDate, string Status, Guid? CorrectsEventId, JsonObject? Assignment, bool Ends = false);

/// <summary>One assignment row the events call for: from <see cref="ValidFrom"/> to <see cref="ValidTo"/> (exclusive, open when null).</summary>
public sealed record PlannedRow(DateOnly ValidFrom, DateOnly? ValidTo, Guid EventId, JsonObject Values);

/// <summary>
/// The assignment history of an employment as its events call for it (slice S1.20 Build item 6, FR-HRM-EMP-03, FR-XM-05). The
/// events are the record; the effective-dated rows of <c>hrm.employment_assignments</c> follow from them:
/// <list type="bullet">
/// <item>Only applied events count: approved (dated in the future, ruling Q12) or effective; drafts, pending and cancelled ones do not.</item>
/// <item>A <c>data_correction</c> that names an event replaces it: the named event is left out (it is never edited) and the correction,
/// which carries the corrected changes, counts from its own date.</item>
/// <item>Events count in date order (on one date, in the order they were recorded); each sets only the fields it names, so a
/// backdated change carries forward until a later event sets the same field ("a change between two rows splits correctly").</item>
/// <item>One row per date on which an event sets assignment fields; the last event of that date owns the row.</item>
/// <item>An event that ends the employment (a separation, S1.20c) closes the row in force on its date: its date is the first day
/// without job details (the employment's separation date is the day before). Nothing after it counts.</item>
/// </list>
/// Pure: no database, so the rules are unit-tested.
/// </summary>
public static class EmploymentTimeline
{
    public const string NoStartingAssignment = "errors.hrm.employment.no_starting_assignment";

    public static bool IsApplied(string status) => status is "approved" or "effective";

    /// <summary>The events replaced by an applied correction (the keys of <see cref="ReplacedBy"/>).</summary>
    public static IReadOnlySet<Guid> Replaced(IEnumerable<TimelineEvent> events) => ReplacedBy(events).Keys.ToHashSet();

    /// <summary>
    /// Each replaced event and the correction that replaces it. Replacement follows the chain (review S1.20b finding 2): an applied
    /// correction replaces its target, and when that target is itself a correction (cancelled because it was replaced before its
    /// date, or still in force) it goes on replacing its own target, so a correction of a correction never brings back the event
    /// the first one removed. A correction that never counted (pending, draft, rejected) replaces nothing.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> ReplacedBy(IEnumerable<TimelineEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.ToList();
        var byId = list.ToDictionary(e => e.Id);
        var replaced = new Dictionary<Guid, Guid>();
        foreach (var start in list.Where(e => IsApplied(e.Status) && e.CorrectsEventId is not null))
        {
            var by = start.Id;
            var target = start.CorrectsEventId;
            while (target is { } id && replaced.TryAdd(id, by))
            {
                by = id;
                target = byId.TryGetValue(id, out var next) ? next.CorrectsEventId : null;
            }
        }

        return replaced;
    }

    /// <summary>The applied, not replaced events in the order they count.</summary>
    public static IReadOnlyList<TimelineEvent> Counted(IReadOnlyList<TimelineEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var replaced = Replaced(events);
        var order = events.Select((e, i) => (e, i)).ToDictionary(x => x.e.Id, x => x.i);
        return events.Where(e => IsApplied(e.Status) && !replaced.Contains(e.Id))
            .OrderBy(e => e.EffectiveDate).ThenBy(e => order[e.Id]).ToList();
    }

    /// <summary>
    /// The rows the events call for, oldest first. <paramref name="problem"/> is set (and no rows are returned) when the first
    /// counted event with assignment fields does not give a full assignment (nothing to start from).
    /// </summary>
    /// <param name="events">Every event of the employment in the order it was recorded.</param>
    /// <param name="problem">Set when the rows cannot be planned.</param>
    public static IReadOnlyList<PlannedRow> Plan(IReadOnlyList<TimelineEvent> events, out string? problem)
    {
        problem = null;
        var rows = new List<PlannedRow>();
        JsonObject? state = null;
        foreach (var e in Counted(events).Where(e => e.Ends || e.Assignment is { Count: > 0 }))
        {
            if (e.Ends)
            {
                rows.RemoveAll(r => r.ValidFrom >= e.EffectiveDate);
                if (rows.Count > 0 && (rows[^1].ValidTo is null || rows[^1].ValidTo > e.EffectiveDate))
                {
                    rows[^1] = rows[^1] with { ValidTo = e.EffectiveDate };
                }

                break;
            }

            if (state is null)
            {
                if (AssignmentKeys.Required.Any(k => e.Assignment![k] is null))
                {
                    problem = NoStartingAssignment;
                    return [];
                }

                state = new JsonObject();
            }

            state = Apply(state, e.Assignment!);
            if (rows.Count > 0 && rows[^1].ValidFrom == e.EffectiveDate)
            {
                rows[^1] = rows[^1] with { EventId = e.Id, Values = state };
            }
            else
            {
                if (rows.Count > 0)
                {
                    rows[^1] = rows[^1] with { ValidTo = e.EffectiveDate };
                }

                rows.Add(new PlannedRow(e.EffectiveDate, null, e.Id, state));
            }
        }

        return rows;
    }

    /// <summary>The row valid on a date, or null.</summary>
    public static PlannedRow? On(IReadOnlyList<PlannedRow> rows, DateOnly date) =>
        rows.LastOrDefault(r => r.ValidFrom <= date && (r.ValidTo is null || r.ValidTo > date));

    /// <summary>A copy of <paramref name="state"/> with the fields of <paramref name="delta"/> set (a null value empties a field).</summary>
    public static JsonObject Apply(JsonObject state, JsonObject delta)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(delta);
        var next = (JsonObject)state.DeepClone();
        foreach (var (key, value) in delta)
        {
            next[key] = value?.DeepClone();
        }

        return next;
    }
}
