using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Puts employment events into force (slice S1.20 Build items 5, 6 and 9, rulings Q2 and Q12), for the employment services, the
/// approval of a held event and the daily job alike, always inside the caller's transaction: the assignment rows are brought in
/// line with the events (<see cref="EmploymentTimeline"/>), the employment fields an event corrects are set, the event's status
/// moves on (effective from its date, approved before it) and <c>hrm.employee_changed</c> is raised once, when the event takes effect.
/// </summary>
public sealed class EmploymentApplier(HrmDbContext db, ISettings settings)
{
    private static readonly TimeZoneInfo Fallback = TimeZoneInfo.Utc;

    /// <summary>The company's business date (setting <c>org.time_zone</c>, default Asia/Karachi).</summary>
    public async Task<DateOnly> TodayAsync(Guid companyId, DateTimeOffset now, CancellationToken ct)
    {
        var zone = (await settings.GetAsync("org.time_zone", new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        var tz = zone.ValueKind == JsonValueKind.String && TimeZoneInfo.TryFindSystemTimeZoneById(zone.GetString()!, out var found) ? found : Fallback;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
    }

    /// <summary>Every event of the employment, in the order recorded.</summary>
    public async Task<List<EmploymentEvent>> EventsAsync(Guid employmentId, CancellationToken ct) =>
        await db.Set<EmploymentEvent>().Where(e => e.EmploymentId == employmentId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct).ConfigureAwait(false);

    public static TimelineEvent ToTimeline(EmploymentEvent e) =>
        new(e.Id, e.Kind, e.EffectiveDate, e.Status, e.CorrectsEventId, Section(e.Changes, "assignment"));

    /// <summary>A section of an event's <c>changes</c> (<c>assignment</c>, <c>employment</c>, <c>impact</c>), or null.</summary>
    public static JsonObject? Section(JsonDocument? changes, string name) =>
        changes is not null && changes.RootElement.ValueKind == JsonValueKind.Object && changes.RootElement.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(section.GetRawText())!.AsObject()
            : null;

    /// <summary>
    /// Brings the assignment rows of the employment in line with <paramref name="events"/> (tracked, with their statuses as they are
    /// to be saved). Saves in stages so the exclusion constraint never sees two rows on one day: removed rows first, then rows
    /// that end earlier or change values, then rows that end later and new rows. Returns a localization key when the events give
    /// no starting assignment.
    /// </summary>
    public async Task<string?> SyncRowsAsync(Employment employment, IReadOnlyList<EmploymentEvent> events, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(employment);
        var planned = EmploymentTimeline.Plan([.. events.Select(ToTimeline)], out var problem);
        if (problem is not null)
        {
            return problem;
        }

        var existing = await db.Set<EmploymentAssignment>().Where(a => a.EmploymentId == employment.Id).ToListAsync(ct).ConfigureAwait(false);
        var byStart = existing.ToDictionary(a => a.ValidFrom);
        var wanted = planned.ToDictionary(p => p.ValidFrom);

        db.RemoveRange(existing.Where(a => !wanted.ContainsKey(a.ValidFrom)));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var growing = new List<(EmploymentAssignment Row, DateOnly? To)>();
        foreach (var plan in planned.Where(p => byStart.ContainsKey(p.ValidFrom)))
        {
            var row = byStart[plan.ValidFrom];
            var changed = Write(row, plan);
            if (Later(plan.ValidTo, row.ValidTo))
            {
                growing.Add((row, plan.ValidTo));
            }
            else if (row.ValidTo != plan.ValidTo)
            {
                row.ValidTo = plan.ValidTo;
                changed = true;
            }

            if (changed)
            {
                Touch(row, now, actor);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var (row, to) in growing)
        {
            row.ValidTo = to;
            Touch(row, now, actor);
        }

        foreach (var plan in planned.Where(p => !byStart.ContainsKey(p.ValidFrom)))
        {
            var row = new EmploymentAssignment
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = employment.CompanyId,
                EmploymentId = employment.Id,
                Title = string.Empty,
                CostAllocation = JsonDocument.Parse("[]"),
                ValidFrom = plan.ValidFrom,
                CreatedAt = now,
                CreatedBy = actor,
                UpdatedAt = now,
                UpdatedBy = actor,
                RowVersion = 1,
            };
            Write(row, plan);
            row.ValidTo = plan.ValidTo;
            db.Add(row);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The status an applied event takes on the day (ruling Q12): effective from its date, approved before it. A correction that
    /// replaces an approved event not yet in force cancels it (it never takes effect).
    /// </summary>
    public static void SetApplied(EmploymentEvent e, IReadOnlyList<EmploymentEvent> events, DateOnly today, DateTimeOffset now, Guid actor)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Status = e.EffectiveDate <= today ? "effective" : "approved";
        Stamp(e, now, actor);
        if (e.CorrectsEventId is { } replaced && events.FirstOrDefault(x => x.Id == replaced) is { Status: "approved" } future)
        {
            future.Status = "cancelled";
            Stamp(future, now, actor);
        }
    }

    /// <summary>
    /// The employment fields that follow from its events: the joining date is the first row's start (a correction of the hire moves
    /// it, with the service start date when that was the joining date), a confirmation sets the confirmation date, a correction
    /// sets the fields it names; status <c>pre_joining</c> becomes <c>active</c> from the joining date.
    /// </summary>
    public async Task ApplyEmploymentFieldsAsync(Employment employment, EmploymentEvent e, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(employment);
        ArgumentNullException.ThrowIfNull(e);
        var first = await db.Set<EmploymentAssignment>().Where(a => a.EmploymentId == employment.Id).OrderBy(a => a.ValidFrom).Select(a => (DateOnly?)a.ValidFrom).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (first is { } joining && joining != employment.JoiningDate)
        {
            if (employment.ServiceStartDate == employment.JoiningDate)
            {
                employment.ServiceStartDate = joining;
            }

            employment.JoiningDate = joining;
        }

        if (e.Kind == "confirmation")
        {
            employment.ConfirmationDate = e.EffectiveDate;
        }

        if (Section(e.Changes, "employment") is { } fields)
        {
            foreach (var (key, value) in fields)
            {
                switch (key)
                {
                    case "employee_type":
                        employment.EmployeeType = value!.GetValue<string>();
                        break;
                    case "service_start_date":
                        employment.ServiceStartDate = DateOnly.Parse(value!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "confirmation_date":
                        employment.ConfirmationDate = DateOnly.Parse(value!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "expected_end_date":
                        employment.ExpectedEndDate = value is null ? null : DateOnly.Parse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }
        }

        if (employment.Status == "pre_joining" && employment.JoiningDate <= today)
        {
            employment.Status = "active";
        }
    }

    /// <summary>Raises an HR event in hrm.outbox_messages (same transaction). Ids, codes and dates only (Build item 9).</summary>
    public void Raise(string type, Employment employment, DateTimeOffset now, Guid actor, object payload)
    {
        ArgumentNullException.ThrowIfNull(employment);
        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = type,
            AggregateType = EmploymentEvents.AggregateType,
            AggregateId = employment.Id,
            Payload = JsonSerializer.SerializeToDocument(payload, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor }, JsonSerializerOptions.Web),
        });
    }

    /// <summary>
    /// <c>hrm.employee_changed</c> for an event that took effect: the first day whose results may change is the earlier of the
    /// event's date and the date of the event it replaced (GHR-04: modules recalculate from there).
    /// </summary>
    public void RaiseChanged(Employment employment, EmploymentEvent e, IReadOnlyList<EmploymentEvent> events, DateTimeOffset now, Guid actor)
    {
        ArgumentNullException.ThrowIfNull(employment);
        ArgumentNullException.ThrowIfNull(e);
        var earliest = e.CorrectsEventId is { } r && events.FirstOrDefault(x => x.Id == r) is { } replaced && replaced.EffectiveDate < e.EffectiveDate
            ? replaced.EffectiveDate
            : e.EffectiveDate;
        Raise(EmploymentEvents.EmployeeChanged, employment, now, actor, new
        {
            employmentId = employment.Id,
            personId = employment.PersonId,
            companyId = employment.CompanyId,
            eventId = e.Id,
            kind = e.Kind,
            effectiveDate = e.EffectiveDate,
            earliestDate = earliest,
        });
    }

    public static void Stamp<T>(T row, DateTimeOffset now, Guid actor)
        where T : class, IVersioned
    {
        ArgumentNullException.ThrowIfNull(row);
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
    }

    private static void Touch(EmploymentAssignment row, DateTimeOffset now, Guid actor) => Stamp(row, now, actor);

    private static bool Later(DateOnly? a, DateOnly? b) => b is not null && (a is null || a > b);

    /// <summary>Writes the planned values into the row; true when something changed.</summary>
    private static bool Write(EmploymentAssignment row, PlannedRow plan)
    {
        var v = plan.Values;
        var before = (row.EventId, row.OrgUnitId, row.BranchId, row.LocationId, row.PositionId, row.GradeId, row.Title, row.ManagerEmploymentId, Cost: row.CostAllocation.RootElement.GetRawText(), row.Fte, row.WorkSchedule, row.IsActing);
        row.EventId = plan.EventId;
        row.OrgUnitId = Guid.Parse(v[AssignmentKeys.OrgUnit]!.GetValue<string>());
        row.BranchId = Guid.Parse(v[AssignmentKeys.Branch]!.GetValue<string>());
        row.LocationId = OptionalId(v, AssignmentKeys.Location);
        row.PositionId = OptionalId(v, AssignmentKeys.Position);
        row.GradeId = OptionalId(v, AssignmentKeys.Grade);
        row.Title = v[AssignmentKeys.Title]!.GetValue<string>();
        row.ManagerEmploymentId = OptionalId(v, AssignmentKeys.Manager);
        var cost = v[AssignmentKeys.CostAllocation]!;
        var costChanged = !JsonNode.DeepEquals(JsonNode.Parse(before.Cost), cost);
        if (costChanged)
        {
            row.CostAllocation = JsonDocument.Parse(cost.ToJsonString());
        }

        row.Fte = v[AssignmentKeys.Fte]!.GetValue<decimal>();
        row.WorkSchedule = v[AssignmentKeys.WorkSchedule]?.GetValue<string>();
        row.IsActing = v[AssignmentKeys.IsActing]!.GetValue<bool>();
        var after = (row.EventId, row.OrgUnitId, row.BranchId, row.LocationId, row.PositionId, row.GradeId, row.Title, row.ManagerEmploymentId, Cost: before.Cost, row.Fte, row.WorkSchedule, row.IsActing);
        return costChanged || before != after;
    }

    private static Guid? OptionalId(JsonObject v, string key) => v[key] is { } node ? Guid.Parse(node.GetValue<string>()) : null;
}
