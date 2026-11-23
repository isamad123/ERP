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
        new(e.Id, e.Kind, e.EffectiveDate, e.Status, e.CorrectsEventId, Section(e.Changes, "assignment"), e.Kind == SeparationKind);

    /// <summary>The event kind that ends an employment (S1.20c transfers; S1.25 separations). Its date is the first day without the employment.</summary>
    public const string SeparationKind = "separation";

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
            Bump(future, now, actor);
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
            await ProbationOutcomeAsync(employment, e, ct).ConfigureAwait(false);
        }

        if (e.Kind == SeparationKind && e.Status == "effective" && employment.Status != "separated")
        {
            await SeparateAsync(employment, e, ct).ConfigureAwait(false);
            return;
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

    /// <summary>
    /// A separation takes effect (S1.20c; S1.25 uses the same step): status <c>separated</c>, the separation date the day before the
    /// event's date (its last day). For a transfer between companies the linked employment becomes the person's main one, after this
    /// one is saved as separated (one current main employment per person, the database's unique key).
    /// </summary>
    private async Task SeparateAsync(Employment employment, EmploymentEvent e, CancellationToken ct)
    {
        employment.Status = "separated";
        employment.SeparationDate = e.EffectiveDate.AddDays(-1);
        if (NextEmployment(e) is not { } next)
        {
            return;
        }

        Bump(employment, e.UpdatedAt, e.UpdatedBy);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var linked = await db.Set<Employment>().FirstAsync(x => x.Id == next, ct).ConfigureAwait(false);
        if (!linked.IsPrimary)
        {
            linked.IsPrimary = true;
            Bump(linked, e.UpdatedAt, e.UpdatedBy);
        }

        await TransferProbationAsync(employment, linked, TransferApplier.CarryOver(e)?.Service, e.UpdatedAt, e.UpdatedBy, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The probation of a transfer, when it takes effect (review S1.20c finding 2: never while both employments are current, so a
    /// probation is never reminded twice). The first employment's open probation is closed as <c>transferred</c>. With service kept
    /// it continues in the linked employment with its dates (when it has not ended before the transfer date); with service
    /// restarted the linked employment gets a probation by its company's hire rule (ruling Q10). A confirmation of the first
    /// employment is not carried: BR-02 forbids a confirmation date before the joining date, and the link shows where it is.
    /// </summary>
    private async Task TransferProbationAsync(Employment from, Employment to, string? service, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var open = await db.Set<ProbationRecord>().FirstOrDefaultAsync(p => p.EmploymentId == from.Id && (p.Status == "in_progress" || p.Status == "extended"), ct).ConfigureAwait(false);
        if (open is not null)
        {
            open.Status = "transferred";
            open.DecisionDate = to.JoiningDate;
            Bump(open, now, actor);
        }

        if (await db.Set<ProbationRecord>().AnyAsync(p => p.EmploymentId == to.Id && (p.Status == "in_progress" || p.Status == "extended"), ct).ConfigureAwait(false))
        {
            return;
        }

        if (service == "keep")
        {
            if (open is not null && (open.ExtendedTo ?? open.EndDate) >= to.JoiningDate)
            {
                var record = NewProbation(to, to.JoiningDate, open.EndDate < to.JoiningDate ? to.JoiningDate : open.EndDate, now, actor);
                record.ExtendedTo = open.ExtendedTo;
                record.Status = open.ExtendedTo is null ? "in_progress" : "extended";
                db.Add(record);
            }

            return;
        }

        await OpenProbationAsync(to, now, actor, ct).ConfigureAwait(false);
    }

    /// <summary>The employment a transfer's separation event links to (<c>changes.transfer.to_employment_id</c>), or null.</summary>
    public static Guid? NextEmployment(EmploymentEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Section(e.Changes, "transfer") is { } t && t["to_employment_id"] is { } id ? Guid.Parse(id.GetValue<string>()) : null;
    }

    /// <summary>
    /// A confirmation in force closes the open probation as confirmed on its date (FR-HRM-EMP-10, S1.20c), whichever way it took
    /// effect (at once, on approval or by the daily job).
    /// </summary>
    private async Task ProbationOutcomeAsync(Employment employment, EmploymentEvent e, CancellationToken ct)
    {
        var open = await db.Set<ProbationRecord>().FirstOrDefaultAsync(p => p.EmploymentId == employment.Id && (p.Status == "in_progress" || p.Status == "extended"), ct).ConfigureAwait(false);
        if (open is null)
        {
            return;
        }

        open.Status = "confirmed";
        open.DecisionDate = e.EffectiveDate;
        Bump(open, e.UpdatedAt, e.UpdatedBy);
    }

    /// <summary>
    /// The probation of a hire or rehire (FR-HRM-EMP-10, ruling Q10): opened when the company's setting
    /// <c>hrm.probation.employee_types</c> lists the employee type, from the joining date for <c>hrm.probation.default_months</c>
    /// months (the last day is the day before the same date that many months later). Until the probation policy of S1.23 replaces
    /// the settings; no statutory meaning is claimed. Returns the record added (tracked, saved by the caller), or null.
    /// </summary>
    public async Task<ProbationRecord?> OpenProbationAsync(Employment employment, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(employment);
        var context = new SettingContext(employment.CompanyId, null);
        var types = (await settings.GetAsync("hrm.probation.employee_types", context, ct).ConfigureAwait(false)).Value;
        if (types.ValueKind != JsonValueKind.Array || !types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == employment.EmployeeType))
        {
            return null;
        }

        var months = (await settings.GetAsync("hrm.probation.default_months", context, ct).ConfigureAwait(false)).Value;
        var record = NewProbation(employment, employment.JoiningDate, employment.JoiningDate.AddMonths(months.ValueKind == JsonValueKind.Number ? months.GetInt32() : 3).AddDays(-1), now, actor);
        db.Add(record);
        return record;
    }

    /// <summary>A new open probation record of the employment (tracked by the caller).</summary>
    public static ProbationRecord NewProbation(Employment employment, DateOnly start, DateOnly end, DateTimeOffset now, Guid actor)
    {
        ArgumentNullException.ThrowIfNull(employment);
        return new ProbationRecord
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            StartDate = start,
            EndDate = end,
            Status = "in_progress",
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
            RowVersion = 1,
        };
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

    /// <summary>Raises an HR event about another aggregate (a contract, a probation record) in hrm.outbox_messages (same transaction).</summary>
    public void RaiseFor(string type, string aggregateType, Guid aggregateId, DateTimeOffset now, Guid actor, object payload) =>
        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = type,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            Payload = JsonSerializer.SerializeToDocument(payload, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor }, JsonSerializerOptions.Web),
        });

    /// <summary>
    /// <c>hrm.employee_changed</c> for an event that took effect (<c>hrm.employee_separated</c> for a separation): the first day whose results may change is the earlier of the
    /// event's date and the date of the event it replaced (GHR-04: modules recalculate from there).
    /// </summary>
    public void RaiseChanged(Employment employment, EmploymentEvent e, IReadOnlyList<EmploymentEvent> events, DateTimeOffset now, Guid actor)
    {
        ArgumentNullException.ThrowIfNull(employment);
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind == SeparationKind)
        {
            Raise(EmploymentEvents.EmployeeSeparated, employment, now, actor, new
            {
                employmentId = employment.Id,
                personId = employment.PersonId,
                companyId = employment.CompanyId,
                eventId = e.Id,
                reasonCode = e.ReasonCode,
                separationDate = e.EffectiveDate.AddDays(-1),
                nextEmploymentId = NextEmployment(e),
            });
            return;
        }

        var earliest = EarliestAffected(e.EffectiveDate, e.CorrectsEventId, events);
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

    /// <summary>
    /// Whether <c>hrm.employee_changed</c> is raised now for an event just applied (GHR-04): when it is in force, or when it waits for
    /// its date but already changes days up to today, as a correction dated in the future of an event in force does (it removes that
    /// event's rows at once; review of the S1.20b follow-up, finding 2). The daily job announces it again on its date; modules
    /// recalculate from <c>earliestDate</c>, so the second message repeats the same news.
    /// </summary>
    public static bool Announces(EmploymentEvent e, IReadOnlyList<EmploymentEvent> events, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Status == "effective" || (e.Status == "approved" && EarliestAffected(e.EffectiveDate, e.CorrectsEventId, events) <= today);
    }

    /// <summary>
    /// The first day whose results a change may alter (GHR-04): the earliest effective date of the change and of every event its
    /// correction replaces along the chain (a correction of a correction also removes what the first one replaced; S1.20b review
    /// follow-up B, CR-076 S1.20b detail r). Used for <c>hrm.employee_changed.earliestDate</c> and for the backdated change check.
    /// </summary>
    public static DateOnly EarliestAffected(DateOnly effectiveDate, Guid? correctsEventId, IReadOnlyList<EmploymentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var earliest = effectiveDate;
        var next = correctsEventId;
        for (var i = 0; next is { } id && i <= events.Count; i++)
        {
            var replaced = events.FirstOrDefault(x => x.Id == id);
            if (replaced is null)
            {
                break;
            }

            if (replaced.EffectiveDate < earliest)
            {
                earliest = replaced.EffectiveDate;
            }

            next = replaced.CorrectsEventId;
        }

        return earliest;
    }

    public static void Stamp<T>(T row, DateTimeOffset now, Guid actor)
        where T : class, IVersioned
    {
        ArgumentNullException.ThrowIfNull(row);
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
    }

    /// <summary>A stored row that changed: next row version (the column is a concurrency token; review S1.20b finding 6) and stamp.</summary>
    public static void Bump<T>(T row, DateTimeOffset now, Guid actor)
        where T : class, IVersioned
    {
        ArgumentNullException.ThrowIfNull(row);
        row.RowVersion++;
        Stamp(row, now, actor);
    }

    private static void Touch(EmploymentAssignment row, DateTimeOffset now, Guid actor) => Bump(row, now, actor);

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

    /// <summary>The assignment keys whose stored value differs from the planned one (review S1.20b finding 3).</summary>
    public static List<string> Differences(EmploymentAssignment row, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(values);
        string? Id(string key) => values[key]?.GetValue<string>();
        var keys = new List<string>();
        void Check(string key, bool same)
        {
            if (!same)
            {
                keys.Add(key);
            }
        }

        Check(AssignmentKeys.OrgUnit, Id(AssignmentKeys.OrgUnit) == row.OrgUnitId.ToString());
        Check(AssignmentKeys.Branch, Id(AssignmentKeys.Branch) == row.BranchId.ToString());
        Check(AssignmentKeys.Location, Id(AssignmentKeys.Location) == row.LocationId?.ToString());
        Check(AssignmentKeys.Position, Id(AssignmentKeys.Position) == row.PositionId?.ToString());
        Check(AssignmentKeys.Grade, Id(AssignmentKeys.Grade) == row.GradeId?.ToString());
        Check(AssignmentKeys.Title, Id(AssignmentKeys.Title) == row.Title);
        Check(AssignmentKeys.Manager, Id(AssignmentKeys.Manager) == row.ManagerEmploymentId?.ToString());
        Check(AssignmentKeys.CostAllocation, values[AssignmentKeys.CostAllocation] is { } cost && JsonNode.DeepEquals(JsonNode.Parse(row.CostAllocation.RootElement.GetRawText()), cost));
        Check(AssignmentKeys.Fte, values[AssignmentKeys.Fte]?.GetValue<decimal>() == row.Fte);
        Check(AssignmentKeys.WorkSchedule, Id(AssignmentKeys.WorkSchedule) == row.WorkSchedule);
        Check(AssignmentKeys.IsActing, values[AssignmentKeys.IsActing]?.GetValue<bool>() == row.IsActing);
        return keys;
    }

    private static Guid? OptionalId(JsonObject v, string key) => v[key] is { } node ? Guid.Parse(node.GetValue<string>()) : null;
}
