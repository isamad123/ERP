using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Attendance;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// The team leave calendar and the coverage warning of approval cards (slice S1.33a Build items 3 and 4; FR-HRM-LVE-05, -15). Reads only: no
/// ledger or request is written. Who is shown is the data scope of the caller's <c>hrm.leave_requests.read</c> grant (own, team, company), the
/// same scope the list of requests has. A day's leave is a request day of a request that holds the calendar (waiting, approved, or approved with a
/// cancellation waiting: <see cref="LeaveRequestPlanner.Holding"/>), so the calendar equals the S1.32b day lists. A sandwich day (a holiday or weekly
/// off counted as leave) is shown but is not "away" for coverage, because nobody is expected at work. The limit is the company's
/// <c>hrm.leave.coverage_warning_percent</c>; the warning never blocks anything.
/// </summary>
internal sealed class LeaveCalendarService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    IPolicyResolver resolver,
    IHolidayCalendars calendars,
    ISettings settings) : ILeaveCalendarService
{
    public const string RangeInvalid = "errors.hrm.leave.calendar.range";
    public const int MostDays = 62;
    private const int DefaultPeople = 50;
    private const int MostPeople = 200;
    private const int DefaultLimit = 30;
    private static readonly string[] Current = ["active", "on_notice"];

    private static readonly ScopeTarget<Employment> CalendarScope = new(
        "hrm.leave_requests",
        new Dictionary<ScopeDimension, System.Linq.Expressions.Expression<Func<Employment, Guid?>>>
        {
            [ScopeDimension.Company] = e => e.CompanyId,
            [ScopeDimension.Employment] = e => e.Id,
        },
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Employment, string?>>>());

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CalendarAsync(LeaveCalendarQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (query.From is not { } from || query.To is not { } to || to < from || to.DayNumber - from.DayNumber >= MostDays)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["from"] = [RangeInvalid] });
        }

        var scoped = await scope.ApplyAsync(
            db.Set<Employment>().AsNoTracking().Where(e => Current.Contains(e.Status) && e.JoiningDate <= to && (e.SeparationDate == null || e.SeparationDate >= from)),
            CalendarScope, "read", Source, cancellationToken).ConfigureAwait(false);
        if (query.CompanyId is { } only)
        {
            scoped = scoped.Where(e => e.CompanyId == only);
        }

        var employments = await scoped.OrderBy(e => e.EmployeeNo).ThenBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var companyIds = employments.Select(e => e.CompanyId).Distinct().OrderBy(c => c).ToList();
        var days = new Dictionary<Guid, DateOnly>();
        var limits = new Dictionary<Guid, int>();
        foreach (var company in companyIds)
        {
            days[company] = await applier.TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            limits[company] = await LimitAsync(company, cancellationToken).ConfigureAwait(false);
        }

        // The assignment in force on the day the screen is about (today when inside the range, else its first or last day): unit, location.
        var ids = employments.Select(e => e.Id).ToList();
        var placed = await PlacesAsync(employments, from, to, days, cancellationToken).ConfigureAwait(false);
        if (query.UnitId is { } unit)
        {
            employments = [.. employments.Where(e => placed.TryGetValue(e.Id, out var p) && p.UnitId == unit)];
            ids = [.. employments.Select(e => e.Id)];
        }

        var leave = await LeaveDaysAsync(ids, from, to, query.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => leave.Select(l => l.TypeId).Distinct().Contains(t.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var coverage = Coverage(employments, leave, from, to, limits);

        var skip = Math.Max(0, query.Offset ?? 0);
        var take = Math.Clamp(query.Limit ?? DefaultPeople, 1, MostPeople);
        var page = employments.Skip(skip).Take(take).ToList();
        var names = await NamesAsync(page, cancellationToken).ConfigureAwait(false);
        var offs = await OffDaysAsync(page, from, to, cancellationToken).ConfigureAwait(false);
        var holidayCalendars = await HolidaysAsync(page, placed, from, to, cancellationToken).ConfigureAwait(false);
        var leaveOf = leave.ToLookup(l => l.EmploymentId);

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            from,
            to,
            companies = companyIds.Select(c => new { companyId = c, today = days[c], limitPercent = limits[c] }).ToList(),
            types = types.Select(t => new { id = t.Id, code = t.Code, nameI18n = t.NameI18n.RootElement.Clone(), colorToken = t.ColorToken, unit = t.Unit }).ToList(),
            total = employments.Count,
            more = employments.Count > skip + take,
            people = page.Select(e => new
            {
                employmentId = e.Id,
                companyId = e.CompanyId,
                employeeNo = e.EmployeeNo,
                firstName = names.TryGetValue(e.PersonId, out var n) ? n.First : null,
                lastName = names.TryGetValue(e.PersonId, out var n2) ? n2.Last : null,
                unitId = placed.TryGetValue(e.Id, out var p) ? p.UnitId : null,
                calendarId = holidayCalendars.PersonCalendar.TryGetValue(e.Id, out var cal) ? cal : (Guid?)null,
                leave = leaveOf[e.Id].Select(l => new { date = l.Date, leaveTypeId = l.TypeId, requestId = l.RequestId, quantity = l.Quantity, status = l.Status, kind = l.Kind }).OrderBy(l => l.date).ToList(),
                offDays = offs.TryGetValue(e.Id, out var o) ? o.OrderBy(x => x.Date).Select(x => new { date = x.Date, kind = x.Kind }).ToList<object>() : [],
            }).ToList(),
            holidays = holidayCalendars.Holidays.Select(h => new { calendarId = h.Key, items = h.Value.Select(x => new { date = x.Date, name = x.Name, kind = x.Kind }).ToList() }).ToList(),
            coverage = coverage.Select(c => new { companyId = c.CompanyId, date = c.Date, teamSize = c.Size, away = c.Away, percent = LeaveCoverageRules.Percent(c.Away, c.Size), warn = c.Warn }).ToList(),
        });
    }

    public async Task<PeopleResult> CoverageAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.RequestsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var request = await db.Set<LeaveRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null || !await scope.AllowsAsync(request, LeaveRequestAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var today = await applier.TodayAsync(request.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var limit = await LimitAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
        var asOf = request.StartDate > today ? request.StartDate : today;
        var managerOf = await ManagersAsync(request.CompanyId, asOf, cancellationToken).ConfigureAwait(false);
        if (!managerOf.TryGetValue(request.EmploymentId, out var manager) || manager is not { } managerId)
        {
            // No line manager: there is no team to measure; say so rather than invent one (no rule is made up).
            return new PeopleResult(PeopleOutcome.Done, Body: new { requestId, limitPercent = limit, teamKnown = false, managerEmploymentId = (Guid?)null, teamSize = 0, days = Array.Empty<object>(), warn = false });
        }

        var team = LeaveCoverageRules.Subtree(managerId, managerOf);
        team.Add(request.EmploymentId);
        var members = await db.Set<Employment>().AsNoTracking()
            .Where(e => team.Contains(e.Id) && Current.Contains(e.Status) && e.JoiningDate <= request.EndDate && (e.SeparationDate == null || e.SeparationDate >= request.StartDate))
            .Select(e => new { e.Id, e.EmployeeNo, e.PersonId, e.JoiningDate, e.SeparationDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var memberIds = members.Select(m => m.Id).ToList();
        var rows = await LeaveDaysAsync(memberIds, request.StartDate, request.EndDate, null, cancellationToken).ConfigureAwait(false);
        var away = rows.Where(r => r.Kind != "sandwich" && r.Quantity > 0).ToLookup(r => r.Date);

        // The people shown by name are those the caller may read; the counts are of the whole team (a manager's own approver sees the full picture).
        var visible = (await scope.ApplyAsync(db.Set<Employment>().AsNoTracking().Where(e => memberIds.Contains(e.Id)), CalendarScope, "read", Source, cancellationToken).ConfigureAwait(false))
            .Select(e => e.Id).ToHashSet();
        var visiblePeople = members.Where(m => visible.Contains(m.Id)).ToList();
        var names = await NamesAsync(visiblePeople.Select(m => new Employment { Id = m.Id, PersonId = m.PersonId }).ToList(), cancellationToken).ConfigureAwait(false);
        var list = new List<object>();
        var anyWarn = false;
        for (var d = request.StartDate; d <= request.EndDate; d = d.AddDays(1))
        {
            var size = members.Count(m => m.JoiningDate <= d && (m.SeparationDate == null || m.SeparationDate >= d));
            var awayIds = away[d].Select(r => r.EmploymentId).Distinct().ToList();
            var warn = LeaveCoverageRules.Warns(awayIds.Count, size, limit);
            anyWarn |= warn;
            // A day the request itself does not hold (a holiday, a weekly off) is not a day of leave; only days with a row of this request are listed.
            if (!rows.Any(r => r.RequestId == requestId && r.Date == d))
            {
                continue;
            }

            list.Add(new
            {
                date = d,
                teamSize = size,
                away = awayIds.Count,
                percent = LeaveCoverageRules.Percent(awayIds.Count, size),
                warn,
                people = visiblePeople.Where(m => awayIds.Contains(m.Id) && m.Id != request.EmploymentId).OrderBy(m => m.EmployeeNo, StringComparer.Ordinal)
                    .Select(m => new { employmentId = m.Id, employeeNo = m.EmployeeNo, firstName = names.TryGetValue(m.PersonId, out var n) ? n.First : null, lastName = names.TryGetValue(m.PersonId, out var n2) ? n2.Last : null }).ToList(),
            });
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { requestId, limitPercent = limit, teamKnown = true, managerEmploymentId = managerId, teamSize = members.Count, days = list, warn = anyWarn });
    }

    private async Task<int> LimitAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(HrmSettingKeys.HrmLeaveCoverageWarningPercent, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var n) ? Math.Clamp(n, 0, 100) : DefaultLimit;
    }

    /// <summary>Each employment's manager employment in force on a day (null when none), for the whole company.</summary>
    private async Task<Dictionary<Guid, Guid?>> ManagersAsync(Guid company, DateOnly day, CancellationToken ct) =>
        (await db.Set<EmploymentAssignment>().AsNoTracking().Where(a => a.CompanyId == company && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day))
            .Select(a => new { a.EmploymentId, a.ManagerEmploymentId }).ToListAsync(ct).ConfigureAwait(false))
        .GroupBy(a => a.EmploymentId).ToDictionary(g => g.Key, g => g.First().ManagerEmploymentId);

    private async Task<Dictionary<Guid, (Guid? UnitId, Guid? LocationId)>> PlacesAsync(List<Employment> employments, DateOnly from, DateOnly to, Dictionary<Guid, DateOnly> today, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (Guid?, Guid?)>();
        foreach (var group in employments.GroupBy(e => e.CompanyId))
        {
            var day = today[group.Key] < from ? from : today[group.Key] > to ? to : today[group.Key];
            var ids = group.Select(e => e.Id).ToList();
            var rows = await db.Set<EmploymentAssignment>().AsNoTracking().Where(a => ids.Contains(a.EmploymentId) && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day))
                .Select(a => new { a.EmploymentId, a.OrgUnitId, a.LocationId }).ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.EmploymentId] = (row.OrgUnitId, row.LocationId);
            }
        }

        return result;
    }

    private async Task<List<Day>> LeaveDaysAsync(List<Guid> employmentIds, DateOnly first, DateOnly last, Guid? typeId, CancellationToken ct)
    {
        if (employmentIds.Count == 0)
        {
            return [];
        }

        var holding = LeaveRequestPlanner.Holding;
        return await (from d in db.Set<LeaveRequestDay>().AsNoTracking()
                      join r in db.Set<LeaveRequest>().AsNoTracking() on d.LeaveRequestId equals r.Id
                      where employmentIds.Contains(d.EmploymentId) && d.LeaveDate >= first && d.LeaveDate <= last && holding.Contains(d.RequestStatus) && (typeId == null || r.LeaveTypeId == typeId)
                      select new Day(d.EmploymentId, d.LeaveDate, r.LeaveTypeId, r.Id, d.Quantity, d.RequestStatus, d.DayKind)).ToListAsync(ct).ConfigureAwait(false);
    }

    private static List<CoverageRow> Coverage(List<Employment> people, List<Day> leave, DateOnly from, DateOnly to, Dictionary<Guid, int> limits)
    {
        var rows = new List<CoverageRow>();
        var away = leave.Where(l => l.Kind != "sandwich" && l.Quantity > 0).ToLookup(l => l.Date);
        foreach (var company in people.GroupBy(p => p.CompanyId))
        {
            var ids = company.Select(p => p.Id).ToHashSet();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var size = company.Count(p => p.JoiningDate <= d && (p.SeparationDate == null || p.SeparationDate >= d));
                var count = away[d].Where(l => ids.Contains(l.EmploymentId)).Select(l => l.EmploymentId).Distinct().Count();
                rows.Add(new CoverageRow(company.Key, d, size, count, LeaveCoverageRules.Warns(count, size, limits[company.Key])));
            }
        }

        return rows;
    }

    private async Task<Dictionary<Guid, (string First, string? Last)>> NamesAsync(List<Employment> people, CancellationToken ct)
    {
        var ids = people.Select(p => p.PersonId).Distinct().ToList();
        return (await db.Set<Person>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.FirstName, p.LastName }).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(p => p.Id, p => (p.FirstName, p.LastName));
    }

    /// <summary>Weekly-off and roster off days of a page of people: a published roster day wins, else the shift policy's weekly off (as the effective shift does).</summary>
    private async Task<Dictionary<Guid, List<(DateOnly Date, string Kind)>>> OffDaysAsync(List<Employment> page, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ids = page.Select(p => p.Id).ToList();
        var lines = await db.Set<RosterLine>().AsNoTracking().Where(l => ids.Contains(l.EmploymentId) && l.WorkDate >= from && l.WorkDate <= to && l.RosterStatus == "published")
            .Select(l => new { l.EmploymentId, l.WorkDate, l.DayType }).ToListAsync(ct).ConfigureAwait(false);
        var byPerson = lines.ToLookup(l => l.EmploymentId);
        var shiftPolicies = new ShiftPolicyReader(resolver);
        var result = new Dictionary<Guid, List<(DateOnly, string)>>();
        foreach (var e in page)
        {
            var list = new List<(DateOnly, string)>();
            var rostered = byPerson[e.Id].ToDictionary(l => l.WorkDate, l => l.DayType);
            var policy = await shiftPolicies.ReadAsync(e.Id, from, to, ct).ConfigureAwait(false);
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (d < e.JoiningDate || (e.SeparationDate is { } end && d > end))
                {
                    continue;
                }

                if (rostered.TryGetValue(d, out var type))
                {
                    if (type != DayTypes.Working)
                    {
                        list.Add((d, type));
                    }
                }
                else if (policy?.For(d).IsOff(d) == true)
                {
                    list.Add((d, DayTypes.WeeklyOff));
                }
            }

            result[e.Id] = list;
        }

        return result;
    }

    private async Task<HolidaySet> HolidaysAsync(List<Employment> page, Dictionary<Guid, (Guid? UnitId, Guid? LocationId)> placed, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var personCalendar = new Dictionary<Guid, Guid>();
        var holidays = new Dictionary<Guid, List<HolidayInfo>>();
        var byPlace = new Dictionary<(Guid, Guid?), Guid?>();
        foreach (var e in page)
        {
            var location = placed.TryGetValue(e.Id, out var p) ? p.LocationId : null;
            if (!byPlace.TryGetValue((e.CompanyId, location), out var calendar))
            {
                byPlace[(e.CompanyId, location)] = calendar = await calendars.CalendarOfAsync(e.CompanyId, location, ct).ConfigureAwait(false);
            }

            if (calendar is { } id)
            {
                personCalendar[e.Id] = id;
                if (!holidays.ContainsKey(id))
                {
                    holidays[id] = [.. await calendars.HolidaysAsync(id, from, to, ct).ConfigureAwait(false)];
                }
            }
        }

        return new HolidaySet(personCalendar, holidays);
    }

    private sealed record Day(Guid EmploymentId, DateOnly Date, Guid TypeId, Guid RequestId, decimal Quantity, string Status, string Kind);

    private sealed record CoverageRow(Guid CompanyId, DateOnly Date, int Size, int Away, bool Warn);

    private sealed record HolidaySet(Dictionary<Guid, Guid> PersonCalendar, Dictionary<Guid, List<HolidayInfo>> Holidays);
}
