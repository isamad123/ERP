using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
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
    public async Task<PeopleResult> AssignAsync(Guid rosterId, AssignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.RostersUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (roster, refusal) = await LoadAsync(rosterId, "update", cancellationToken).ConfigureAwait(false);
        if (roster is null)
        {
            return refusal!;
        }

        if (roster.Status != "draft")
        {
            return PeopleResult.Conflict(roster.Status == "published" ? NotDraft : Archived);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var from = request.From ?? roster.PeriodStart;
        var to = request.To ?? roster.PeriodEnd;
        if (from < roster.PeriodStart || to > roster.PeriodEnd || to < from)
        {
            errors["from"] = [OutsidePeriod];
        }

        if (request.ShiftId is not { } shiftId)
        {
            errors["shiftId"] = [ShiftRequired];
        }

        var group = request.OrgUnitId is not null || request.GradeId is not null || request.CustomField is not null || request.Everyone == true;
        if ((request.EmploymentIds is null || request.EmploymentIds.Count == 0) && !group)
        {
            errors["employmentIds"] = [TargetRequired];
        }

        if (request.CustomField is { } custom && (string.IsNullOrWhiteSpace(custom.Key) || custom.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
        {
            errors["customField"] = [CustomFieldInvalid];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var versions = await VersionsOfAsync(shiftId: request.ShiftId!.Value, roster.CompanyId, cancellationToken).ConfigureAwait(false);
        if (versions is null)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["shiftId"] = [ShiftUnknown] });
        }

        var people = await ResolvePeopleAsync(roster, request, from, to, cancellationToken).ConfigureAwait(false);
        if (people.Error is { } peopleError)
        {
            return peopleError;
        }

        var existing = (await db.Set<RosterLine>().Where(l => l.RosterId == roster.Id && l.WorkDate >= from && l.WorkDate <= to && people.Employments.Select(e => e.Id).Contains(l.EmploymentId))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(l => (l.EmploymentId, l.WorkDate));
        var now = clock.UtcNow;
        var counts = new Counts();
        foreach (var employment in people.Employments)
        {
            var policy = request.WeeklyOff == false ? null : await policies.ReadAsync(employment.Id, from, to, cancellationToken).ConfigureAwait(false);
            if (policy is not null && policy.Whole.Error is { } patternError)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["shiftPolicy"] = [patternError] });
            }

            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (d < employment.JoiningDate || employment.SeparationDate is { } end && d > end)
                {
                    counts.OutsideEmployment++;
                    continue;
                }

                var off = policy?.For(d).IsOff(d) ?? false;
                var shift = off ? null : versions.On(d);
                if (!off && (shift is null || !shift.IsActive))
                {
                    counts.ShiftNotInForce++;
                    continue;
                }

                if (existing.TryGetValue((employment.Id, d), out var line))
                {
                    if (request.Replace != true)
                    {
                        counts.Kept++;
                        continue;
                    }

                    line.DayType = off ? DayTypes.WeeklyOff : DayTypes.Working;
                    line.ShiftId = shift?.Id;
                    line.Reason = null;
                    EmploymentApplier.Bump(line, now, actor.UserId);
                    counts.Replaced++;
                    continue;
                }

                db.Add(NewLine(roster, employment.Id, d, off ? DayTypes.WeeklyOff : DayTypes.Working, shift?.Id, null, now));
                counts.Created++;
            }
        }

        return await SaveSummaryAsync(counts, people.Employments.Count, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> CopyForwardAsync(Guid rosterId, CopyForwardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.RostersCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (source, refusal) = await LoadAsync(rosterId, "create", cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return refusal!;
        }

        var length = source.PeriodEnd.DayNumber - source.PeriodStart.DayNumber;
        var start = request.PeriodStart ?? source.PeriodEnd.AddDays(1);
        var offset = start.DayNumber - source.PeriodStart.DayNumber;
        var now = clock.UtcNow;
        var copyName = request.Name?.Trim() is { Length: > 0 } given ? given : source.Name + " (copy)";
        var copy = new Roster
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = source.CompanyId,
            OrgUnitId = source.OrgUnitId,
            Name = copyName[..Math.Min(NameLength, copyName.Length)],
            PeriodStart = start,
            PeriodEnd = start.AddDays(length),
            Status = "draft",
            CopiedFromRosterId = source.Id,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (offset <= 0 && request.PeriodStart is not null)
        {
            // Copying onto the same or an earlier period is allowed (a template), but never onto itself.
            if (offset == 0)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["periodStart"] = [PeriodOrder] });
            }
        }

        var lines = await db.Set<RosterLine>().AsNoTracking().Where(l => l.RosterId == source.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = lines.Select(l => l.EmploymentId).Distinct().ToList();
        var employments = await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        var shiftIds = lines.Where(l => l.ShiftId is not null).Select(l => l.ShiftId!.Value).Distinct().ToList();
        var versionsByShift = new Dictionary<Guid, ShiftVersions>();
        foreach (var id in shiftIds)
        {
            versionsByShift[id] = (await VersionsOfAsync(id, source.CompanyId, cancellationToken).ConfigureAwait(false))!;
        }

        var pending = new List<RosterLine>();
        var counts = new Counts();
        foreach (var person in lines.GroupBy(l => l.EmploymentId))
        {
            if (!employments.TryGetValue(person.Key, out var employment) || !CurrentStatuses.Contains(employment.Status))
            {
                counts.Skipped++;
                continue;
            }

            // The person's usual shift: the one most of their working days used, for days that were off and are now working.
            var usual = person.Where(l => l.ShiftId is not null).GroupBy(l => l.ShiftId!.Value).OrderByDescending(g => g.Count()).Select(g => (Guid?)g.Key).FirstOrDefault();
            var policy = await policies.ReadAsync(person.Key, copy.PeriodStart, copy.PeriodEnd, cancellationToken).ConfigureAwait(false);
            if (policy is not null && policy.Whole.Error is { } patternError)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["shiftPolicy"] = [patternError] });
            }

            foreach (var line in person.OrderBy(l => l.WorkDate))
            {
                var date = line.WorkDate.AddDays(offset);
                if (line.DayType == DayTypes.Holiday || date < copy.PeriodStart || date > copy.PeriodEnd
                    || date < employment.JoiningDate || employment.SeparationDate is { } end && date > end)
                {
                    counts.Skipped++;
                    continue;
                }

                // A day that follows the weekly-off pattern follows it again in the new period; a day somebody set by hand (it has a reason) is copied as it was.
                var dayType = line.DayType;
                var shiftId = line.ShiftId ?? usual;
                if (line.Reason is null && policy?.For(date).Pattern is not null)
                {
                    dayType = policy.For(date).IsOff(date) ? DayTypes.WeeklyOff : DayTypes.Working;
                }

                Guid? shiftRow = null;
                if (dayType == DayTypes.Working)
                {
                    var version = shiftId is { } sid && versionsByShift.TryGetValue(sid, out var versions) ? versions.On(date) : null;
                    if (version is null || !version.IsActive)
                    {
                        counts.ShiftNotInForce++;
                        continue;
                    }

                    shiftRow = version.Id;
                }

                pending.Add(NewLine(copy, person.Key, date, dayType, shiftRow, line.Reason, now));
                counts.Created++;
            }
        }

        try
        {
            // The roster first: the foreign key of a line is composite (company, roster), which the model does not order inserts by.
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            db.Add(copy);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.AddRange(pending);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        var body = View(copy);
        body["lines"] = counts.Created;
        body["skipped"] = counts.Skipped;
        body["shiftNotInForce"] = counts.ShiftNotInForce;
        return new PeopleResult(PeopleOutcome.Created, Body: body);
    }

    public async Task<PeopleResult> ChangeDaysAsync(Guid rosterId, ChangeDaysRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (roster, refusal) = await LoadAsync(rosterId, "update", cancellationToken).ConfigureAwait(false);
        if (roster is null)
        {
            return refusal!;
        }

        var published = roster.Status == "published";
        if (roster.Status == "archived")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(Archived);
        }

        if (!await permissions.HasPermissionAsync(published ? ShiftPermissions.RostersAmend : ShiftPermissions.RostersUpdate, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden();
        }

        var changes = request.Changes ?? [];
        if (changes.Count == 0)
        {
            return PeopleResult.Invalid(ChangesRequired);
        }

        if (changes.Count > MaxChanges)
        {
            return PeopleResult.Invalid(TooManyChanges);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var general = request.Reason?.Trim();
        if (general is { Length: > ReasonLength })
        {
            errors["reason"] = [PersonService.TooLong];
        }

        var ids = changes.Where(c => c.EmploymentId is not null).Select(c => c.EmploymentId!.Value).Distinct().ToList();
        var allowed = await ScopedEmploymentsAsync(roster.CompanyId, ids, cancellationToken).ConfigureAwait(false);
        var existing = (await db.Set<RosterLine>().Where(l => l.RosterId == roster.Id && ids.Contains(l.EmploymentId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(l => (l.EmploymentId, l.WorkDate));
        var versions = new Dictionary<Guid, ShiftVersions?>();
        var now = clock.UtcNow;
        var touched = new HashSet<Guid>();
        var counts = new Counts();
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            string Field(string name) => $"changes[{i}].{name}";
            if (c.EmploymentId is not { } employmentId || !allowed.TryGetValue(employmentId, out var employment))
            {
                errors[Field("employmentId")] = [PeopleUnknown];
                continue;
            }

            if (c.WorkDate is not { } date || date < roster.PeriodStart || date > roster.PeriodEnd)
            {
                errors[Field("workDate")] = [OutsidePeriod];
                continue;
            }

            var reason = (c.Reason ?? general)?.Trim();
            if (reason is { Length: > ReasonLength })
            {
                errors[Field("reason")] = [PersonService.TooLong];
                continue;
            }

            existing.TryGetValue((employmentId, date), out var line);
            if (c.Remove == true)
            {
                if (published)
                {
                    errors[Field("remove")] = [RemovePublished];
                }
                else if (line is not null)
                {
                    db.Remove(line);
                    existing.Remove((employmentId, date));
                    counts.Removed++;
                }

                continue;
            }

            if (c.DayType is not (DayTypes.Working or DayTypes.WeeklyOff or DayTypes.Holiday))
            {
                errors[Field("dayType")] = [DayTypeInvalid];
                continue;
            }

            if (published && string.IsNullOrEmpty(reason))
            {
                errors[Field("reason")] = [ReasonRequired];
                continue;
            }

            Guid? shiftRow = null;
            if (c.DayType == DayTypes.Working)
            {
                if (c.ShiftId is not { } shiftId)
                {
                    errors[Field("shiftId")] = [ShiftRequired];
                    continue;
                }

                if (!versions.TryGetValue(shiftId, out var found))
                {
                    versions[shiftId] = found = await VersionsOfAsync(shiftId, roster.CompanyId, cancellationToken).ConfigureAwait(false);
                }

                if (found is null)
                {
                    errors[Field("shiftId")] = [ShiftUnknown];
                    continue;
                }

                var version = found.On(date);
                if (version is null || !version.IsActive)
                {
                    errors[Field("shiftId")] = [ShiftNotInForce];
                    continue;
                }

                shiftRow = version.Id;
            }
            else if (c.ShiftId is not null)
            {
                errors[Field("shiftId")] = [DayTypeInvalid];
                continue;
            }

            if (line is null)
            {
                line = NewLine(roster, employmentId, date, c.DayType, shiftRow, string.IsNullOrEmpty(reason) ? null : reason, now);
                db.Add(line);
                existing[(employmentId, date)] = line;
                counts.Created++;
            }
            else
            {
                line.DayType = c.DayType;
                line.ShiftId = shiftRow;
                line.Reason = string.IsNullOrEmpty(reason) ? null : reason;
                EmploymentApplier.Bump(line, now, actor.UserId);
                counts.Replaced++;
            }

            touched.Add(employmentId);
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        if (published)
        {
            await RaisePublishedAsync(roster, touched, general ?? string.Join("; ", changes.Select(c => c.Reason).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct()), now, cancellationToken).ConfigureAwait(false);
        }

        return await SaveSummaryAsync(counts, ids.Count, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Counts
    {
        public int Created { get; set; }

        public int Replaced { get; set; }

        public int Kept { get; set; }

        public int Removed { get; set; }

        public int Skipped { get; set; }

        public int OutsideEmployment { get; set; }

        public int ShiftNotInForce { get; set; }
    }

    private async Task<PeopleResult> SaveSummaryAsync(Counts counts, int people, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            people,
            created = counts.Created,
            replaced = counts.Replaced,
            kept = counts.Kept,
            removed = counts.Removed,
            outsideEmployment = counts.OutsideEmployment,
            shiftNotInForce = counts.ShiftNotInForce,
        });
    }

    private RosterLine NewLine(Roster roster, Guid employmentId, DateOnly date, string dayType, Guid? shiftId, string? reason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        CompanyId = roster.CompanyId,
        RosterId = roster.Id,
        EmploymentId = employmentId,
        WorkDate = date,
        DayType = dayType,
        ShiftId = shiftId,
        RosterStatus = roster.Status,
        Reason = reason,
        CreatedAt = now,
        CreatedBy = actor.UserId,
        UpdatedAt = now,
        UpdatedBy = actor.UserId,
        RowVersion = 1,
    };

    /// <summary>The employments of the company among <paramref name="ids"/> the caller may read (data scope), by id.</summary>
    private async Task<Dictionary<Guid, Employment>> ScopedEmploymentsAsync(Guid company, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var query = await scope.ApplyAsync(db.Set<Employment>().AsNoTracking().Where(e => e.CompanyId == company && ids.Contains(e.Id)), EmploymentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        return await query.ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);
    }

    private sealed record People(IReadOnlyList<Employment> Employments, PeopleResult? Error);

    /// <summary>
    /// Resolves the people of an assignment (ruling Q1): the named employments plus, when a group is given, the current employments of the
    /// roster's company that match every group rule on the first day; only people the caller may read, and only those whose employment
    /// overlaps the dates.
    /// </summary>
    private async Task<People> ResolvePeopleAsync(Roster roster, AssignRequest request, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var company = roster.CompanyId;
        var result = new Dictionary<Guid, Employment>();
        if (request.EmploymentIds is { Count: > 0 } named)
        {
            var found = await ScopedEmploymentsAsync(company, [.. named.Distinct()], ct).ConfigureAwait(false);
            if (found.Count != named.Distinct().Count())
            {
                return new People([], PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentIds"] = [PeopleUnknown] }));
            }

            foreach (var e in found.Values)
            {
                result[e.Id] = e;
            }
        }

        var unit = request.OrgUnitId ?? (request.Everyone == true || request.GradeId is not null || request.CustomField is not null ? roster.OrgUnitId : null);
        if (request.OrgUnitId is not null || request.GradeId is not null || request.CustomField is not null || request.Everyone == true)
        {
            var assignments = db.Set<EmploymentAssignment>().AsNoTracking().Where(a => a.CompanyId == company && a.ValidFrom <= from && (a.ValidTo == null || a.ValidTo > from));
            if (unit is { } root)
            {
                var units = request.SubUnits == false ? new HashSet<Guid> { root }
                    : (await orgs.SubtreesAsync([root], from, ct).ConfigureAwait(false)).GetValueOrDefault(root) ?? new HashSet<Guid> { root };
                assignments = assignments.Where(a => units.Contains(a.OrgUnitId));
            }

            if (request.GradeId is { } grade)
            {
                assignments = assignments.Where(a => a.GradeId == grade);
            }

            var members = db.Set<Employment>().AsNoTracking().Where(e => e.CompanyId == company && CurrentStatuses.Contains(e.Status)
                && e.JoiningDate <= to && (e.SeparationDate == null || e.SeparationDate >= from)
                && assignments.Any(a => a.EmploymentId == e.Id));
            if (request.CustomField is { } custom)
            {
                var json = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [custom.Key!.Trim()] = custom.Value });
                var matching = await db.Database.SqlQuery<Guid>($"select id as \"Value\" from hrm.employments where company_id = {company} and custom @> {json}::jsonb").ToListAsync(ct).ConfigureAwait(false);
                members = members.Where(e => matching.Contains(e.Id));
            }

            members = await scope.ApplyAsync(members, EmploymentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
            foreach (var e in await members.ToListAsync(ct).ConfigureAwait(false))
            {
                result[e.Id] = e;
            }
        }

        return new People([.. result.Values.Where(e => e.JoiningDate <= to && (e.SeparationDate is null || e.SeparationDate >= from)).OrderBy(e => e.EmployeeNo, StringComparer.Ordinal)], null);
    }
}
