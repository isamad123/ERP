using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

internal sealed partial class RosterService
{
    public async Task<PeopleResult> ChecksAsync(Guid rosterId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(ShiftPermissions.RostersRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (roster, refusal) = await LoadAsync(rosterId, "read", cancellationToken).ConfigureAwait(false);
        if (roster is null)
        {
            return refusal!;
        }

        var checks = await RunChecksAsync(roster, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            rosterId = roster.Id,
            status = roster.Status,
            errors = checks.Count(c => c.Severity == RosterChecks.Error),
            warnings = checks.Count(c => c.Severity == RosterChecks.Warning),
            messages = checks.Select(c => new
            {
                code = c.Code,
                severity = c.Severity,
                key = "hrm.roster.check." + c.Code,
                employmentId = c.EmploymentId,
                date = c.Date,
                detail = c.Detail,
            }),
        });
    }

    public async Task<PeopleResult> PublishAsync(Guid rosterId, PublishRosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.RostersPublish, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var (roster, refusal) = await LoadAsync(rosterId, "update", cancellationToken).ConfigureAwait(false);
        if (roster is null)
        {
            return refusal!;
        }

        if (roster.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (roster.Status != "draft")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(NotDraft);
        }

        var lines = await db.Set<RosterLine>().Where(l => l.RosterId == roster.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Invalid(NothingToPublish);
        }

        var checks = await RunChecksAsync(roster, cancellationToken).ConfigureAwait(false);
        if (checks.Any(c => c.Severity == RosterChecks.Error))
        {
            db.ChangeTracker.Clear();
            return new PeopleResult(PeopleOutcome.Conflict, ChecksFailed, Extensions: new Dictionary<string, object?>
            {
                ["messages"] = checks.Select(c => new { code = c.Code, severity = c.Severity, employmentId = c.EmploymentId, date = c.Date, detail = c.Detail }).ToList(),
            });
        }

        var now = clock.UtcNow;
        roster.Status = "published";
        roster.PublishedAt = now;
        roster.PublishedBy = actor.UserId;
        EmploymentApplier.Bump(roster, now, actor.UserId);
        foreach (var line in lines)
        {
            line.RosterStatus = "published";
            EmploymentApplier.Bump(line, now, actor.UserId);
        }

        await RaisePublishedAsync(roster, lines.Select(l => l.EmploymentId).Distinct().ToList(), null, now, cancellationToken).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        var body = View(roster);
        body["people"] = lines.Select(l => l.EmploymentId).Distinct().Count();
        body["warnings"] = checks.Count;
        return new PeopleResult(PeopleOutcome.Done, Body: body);
    }

    /// <summary>
    /// One <c>hrm.roster.published</c> per person, in the same transaction as the change: the person's user and the user of the line manager
    /// on the first day of the period are the audience (a person without a user link is not notified; R-141).
    /// </summary>
    private async Task RaisePublishedAsync(Roster roster, IReadOnlyCollection<Guid> employmentIds, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        var ids = employmentIds.ToList();
        var employments = await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct).ConfigureAwait(false);
        var managers = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => ids.Contains(a.EmploymentId) && a.ValidFrom <= roster.PeriodStart && (a.ValidTo == null || a.ValidTo > roster.PeriodStart))
            .Select(a => new { a.EmploymentId, a.ManagerEmploymentId }).ToListAsync(ct).ConfigureAwait(false);
        var managerIds = managers.Where(m => m.ManagerEmploymentId is not null).Select(m => m.ManagerEmploymentId!.Value).Distinct().ToList();
        var managerUsers = await db.Set<Employment>().AsNoTracking().Where(e => managerIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.UserRef, ct).ConfigureAwait(false);
        var managerOf = managers.GroupBy(m => m.EmploymentId).ToDictionary(g => g.Key, g => g.First().ManagerEmploymentId);
        foreach (var e in employments.OrderBy(e => e.EmployeeNo, StringComparer.Ordinal))
        {
            Guid? managerUser = managerOf.GetValueOrDefault(e.Id) is { } m ? managerUsers.GetValueOrDefault(m) : null;
            applier.RaiseFor(RosterEvents.Published, RosterEvents.AggregateType, roster.Id, now, actor.UserId, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["roster_id"] = roster.Id,
                ["roster_name"] = roster.Name,
                ["employment_id"] = e.Id,
                ["employee_no"] = e.EmployeeNo,
                ["employee_user_id"] = e.UserRef,
                ["manager_user_id"] = managerUser,
                ["period_start"] = Iso(roster.PeriodStart),
                ["period_end"] = Iso(roster.PeriodEnd),
                ["reason"] = reason ?? string.Empty,
                ["company_id"] = roster.CompanyId,
                ["link"] = $"/hrm/rosters/{roster.Id}",
            });
        }
    }

    /// <summary>Gathers what <see cref="RosterChecks"/> needs for a roster and runs it.</summary>
    private async Task<IReadOnlyList<RosterCheckMessage>> RunChecksAsync(Roster roster, CancellationToken ct)
    {
        var lines = await db.Set<RosterLine>().AsNoTracking().Where(l => l.RosterId == roster.Id).ToListAsync(ct).ConfigureAwait(false);
        var ids = lines.Select(l => l.EmploymentId).Distinct().ToList();
        var from = roster.PeriodStart.AddDays(-1);
        var to = roster.PeriodEnd.AddDays(1);
        var other = await db.Set<RosterLine>().AsNoTracking()
            .Where(l => l.RosterId != roster.Id && ids.Contains(l.EmploymentId) && l.WorkDate >= from && l.WorkDate <= to && l.RosterStatus != "archived")
            .ToListAsync(ct).ConfigureAwait(false);
        var shiftIds = lines.Concat(other).Where(l => l.ShiftId is not null).Select(l => l.ShiftId!.Value).Distinct().ToList();
        var shifts = (await db.Set<Shift>().AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
            .Where(s => s.StartTime is not null && s.EndTime is not null)
            .ToDictionary(s => s.Id, s => new ShiftTimes(s.StartTime!.Value, s.EndTime!.Value, s.CrossesMidnight));
        var spans = (await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id, e => new EmploymentSpan(e.JoiningDate, e.SeparationDate));
        var rest = new Dictionary<Guid, PeriodPolicy>();
        foreach (var id in ids)
        {
            var policy = await policies.ReadAsync(id, roster.PeriodStart, roster.PeriodEnd, ct).ConfigureAwait(false);
            if (policy is null)
            {
                continue;
            }

            rest[id] = policy;
        }

        return RosterChecks.Run(
            [.. lines.Select(l => new RosterDay(l.EmploymentId, l.WorkDate, l.DayType, l.ShiftId, l.Reason))],
            [.. other.Select(l => new OtherRosterDay(l.RosterId, l.EmploymentId, l.WorkDate, l.DayType, l.ShiftId))],
            shifts,
            (employment, date) => rest.TryGetValue(employment, out var p) ? p.For(date).MinimumRestMinutes : null,
            spans,
            (employment, date) => rest.TryGetValue(employment, out var p) && p.For(date).IsOff(date));
    }
}
