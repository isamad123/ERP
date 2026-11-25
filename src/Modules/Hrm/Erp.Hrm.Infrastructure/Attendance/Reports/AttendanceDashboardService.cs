using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Policies;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance.Reports;

/// <summary>
/// The live attendance dashboard (slice S1.30b Build item 5; FR-HRM-ATT-18, SSP-02). One classification of everyone in the caller's data scope (the scope of
/// <c>hrm.attendance_days</c>: all, company, team or own) gives both the counts and the lists, so they always agree. Today is the company's date; the plan is
/// the published roster line and its shift (with the season's times through the effective-shift reader when the company has a seasonal timing policy); the
/// grace comes from the attendance policy (the shift's own, else the one the policy assignments give), read in bulk for the company. The first punch is the
/// first counted (not duplicate) punch of the day. An approved leave day or approved duty request makes a person on leave or on duty when there is no punch.
/// No push: the screen polls every <c>hrm.att.dashboard_refresh_seconds</c>.
/// </summary>
internal sealed class AttendanceDashboardService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    PolicyResolver resolver,
    PolicyTypeCatalog policyTypes,
    IEffectiveShiftReader effective,
    AttendanceSettingReader settings) : IAttendanceDashboardService
{
    private const int DefaultRefreshSeconds = 60;
    private const int DefaultRows = 100;
    private const int MostRows = 500;
    private static readonly string[] Current = ["active", "on_notice"];

    private static readonly ScopeTarget<Employment> DashboardScope = new(
        "hrm.attendance_days",
        new Dictionary<ScopeDimension, System.Linq.Expressions.Expression<Func<Employment, Guid?>>>
        {
            [ScopeDimension.Company] = e => e.CompanyId,
            [ScopeDimension.Employment] = e => e.Id,
        },
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Employment, string?>>>());

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> DashboardAsync(Guid? companyId, Guid? unitId, Guid? locationId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceReportPermissions.Dashboard, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (companies, people) = await LoadAsync(companyId, unitId, locationId, cancellationToken).ConfigureAwait(false);
        var refresh = companies.Count > 0
            ? await settings.IntAsync(HrmSettingKeys.HrmAttDashboardRefreshSeconds, companies[0].CompanyId, DefaultRefreshSeconds, cancellationToken).ConfigureAwait(false)
            : DefaultRefreshSeconds;
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            asOf = clock.UtcNow,
            refreshSeconds = Math.Max(15, refresh),
            companies = companies.Select(c => new { companyId = c.CompanyId, date = c.Today }).ToList(),
            totals = Counts(people),
            byUnit = people.Where(p => p.OrgUnitId is not null).GroupBy(p => p.OrgUnitId!.Value).OrderBy(g => g.Key).Select(g => new { unitId = g.Key, counts = Counts(g) }).ToList(),
            byLocation = people.Where(p => p.LocationId is not null).GroupBy(p => p.LocationId!.Value).OrderBy(g => g.Key).Select(g => new { locationId = g.Key, counts = Counts(g) }).ToList(),
        });
    }

    public async Task<PeopleResult> PeopleAsync(Guid? companyId, string? status, Guid? unitId, Guid? locationId, int? limit, int? offset, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceReportPermissions.Dashboard, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (status is { Length: > 0 } && !DashboardStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["status"] = ["errors.data.unknown_code"] });
        }

        var (_, people) = await LoadAsync(companyId, unitId, locationId, cancellationToken).ConfigureAwait(false);
        var wanted = status is { Length: > 0 } ? DashboardRules.Expand(status) : null;
        var matching = people.Where(p => wanted is null || wanted.Contains(p.Status, StringComparer.Ordinal)).OrderBy(p => p.Status, StringComparer.Ordinal).ThenBy(p => p.EmployeeNo, StringComparer.Ordinal).ThenBy(p => p.EmploymentId).ToList();
        var take = Math.Clamp(limit ?? DefaultRows, 1, MostRows);
        var skip = Math.Max(0, offset ?? 0);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            status,
            total = matching.Count,
            more = matching.Count > skip + take,
            items = matching.Skip(skip).Take(take).Select(p => new
            {
                employmentId = p.EmploymentId, companyId = p.CompanyId, employeeNo = p.EmployeeNo, firstName = p.FirstName, lastName = p.LastName,
                unitId = p.OrgUnitId, branchId = p.BranchId, locationId = p.LocationId, status = p.Status, shiftStart = p.ShiftStart, shiftEnd = p.ShiftEnd, firstPunch = p.FirstPunch,
            }).ToList(),
        });
    }

    private static object Counts(IEnumerable<DashPerson> people)
    {
        var list = people as IReadOnlyCollection<DashPerson> ?? [.. people];
        int N(string s) => list.Count(p => p.Status == s);
        return new
        {
            headcount = list.Count,
            scheduled = list.Count(p => p.Status != DashboardStatuses.NotScheduled),
            present = N(DashboardStatuses.Present) + N(DashboardStatuses.Late),
            late = N(DashboardStatuses.Late),
            absent = N(DashboardStatuses.Absent),
            onLeave = N(DashboardStatuses.OnLeave),
            onDuty = N(DashboardStatuses.OnDuty),
            notYetPunched = N(DashboardStatuses.NotYetPunched),
            notDue = N(DashboardStatuses.NotDue),
            notScheduled = N(DashboardStatuses.NotScheduled),
            noPolicy = N(DashboardStatuses.NoPolicy),
        };
    }

    private async Task<(List<CompanyDay> Companies, List<DashPerson> People)> LoadAsync(Guid? companyId, Guid? unitId, Guid? locationId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var scoped = await scope.ApplyAsync(db.Set<Employment>().AsNoTracking().Where(e => Current.Contains(e.Status)), DashboardScope, "read", Source, ct).ConfigureAwait(false);
        if (companyId is { } only)
        {
            scoped = scoped.Where(e => e.CompanyId == only);
        }

        var companyIds = await scoped.Select(e => e.CompanyId).Distinct().OrderBy(c => c).ToListAsync(ct).ConfigureAwait(false);
        var companies = new List<CompanyDay>();
        var all = new List<DashPerson>();
        foreach (var company in companyIds)
        {
            var zone = await applier.ZoneAsync(company, ct).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            companies.Add(new CompanyDay(company, today));
            all.AddRange(await CompanyAsync(scoped.Where(e => e.CompanyId == company), company, today, zone, now, unitId, locationId, ct).ConfigureAwait(false));
        }

        return (companies, all);
    }

    private async Task<List<DashPerson>> CompanyAsync(IQueryable<Employment> employments, Guid company, DateOnly today, TimeZoneInfo zone, DateTimeOffset now, Guid? unitId, Guid? locationId, CancellationToken ct)
    {
        var rows = await (from e in employments.Where(e => e.JoiningDate <= today && (e.SeparationDate == null || e.SeparationDate >= today))
                          join p in db.Set<Person>().AsNoTracking() on e.PersonId equals p.Id
                          join a in db.Set<EmploymentAssignment>().AsNoTracking() on e.Id equals a.EmploymentId
                          where a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today)
                             && (unitId == null || a.OrgUnitId == unitId) && (locationId == null || a.LocationId == locationId)
                          join l in db.Set<RosterLine>().AsNoTracking().Where(l => l.CompanyId == company && l.WorkDate == today && l.RosterStatus == "published" && l.DayType == "working") on e.Id equals l.EmploymentId into lines
                          from line in lines.DefaultIfEmpty()
                          join s in db.Set<Shift>().AsNoTracking() on (line == null ? null : line.ShiftId) equals s.Id into shifts
                          from shift in shifts.DefaultIfEmpty()
                          select new { e.Id, e.EmployeeNo, p.FirstName, p.LastName, a.OrgUnitId, a.BranchId, a.LocationId, ShiftId = shift == null ? (Guid?)null : shift.Id, shift!.StartTime, shift.EndTime, Crosses = shift != null && shift.CrossesMidnight, shift.PolicyId })
            .ToListAsync(ct).ConfigureAwait(false);

        var punches = (await db.Set<AttendanceEvent>().AsNoTracking().Where(v => v.CompanyId == company && v.EventDate == today && !v.IsDuplicate)
            .GroupBy(v => v.EmploymentId).Select(g => new { Id = g.Key, First = g.Min(v => v.EventAt) }).ToListAsync(ct).ConfigureAwait(false)).ToDictionary(x => x.Id, x => x.First);
        var onLeave = (await db.Set<LeaveRequestDay>().AsNoTracking().Where(d => d.CompanyId == company && d.LeaveDate == today && (d.RequestStatus == "approved" || d.RequestStatus == "cancel_requested"))
            .Select(d => d.EmploymentId).Distinct().ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var dayStart = AttendanceRequestRules.LocalInstant(today, TimeOnly.MinValue, 0, zone).ToUniversalTime();
        var dayEnd = AttendanceRequestRules.LocalInstant(today, TimeOnly.MinValue, 1, zone).ToUniversalTime();
        var onDuty = (await db.Set<DutyRequest>().AsNoTracking().Where(d => d.CompanyId == company && d.Status == "approved" && d.StartAt < dayEnd && d.EndAt > dayStart)
            .Select(d => d.EmploymentId).Distinct().ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

        var grace = await GraceAsync(company, today, rows.Where(r => r.ShiftId is not null).Select(r => (r.Id, r.PolicyId)).ToList(), ct).ConfigureAwait(false);
        var seasonal = policyTypes.Find(SeasonalTiming.PolicyType) is { } seasonalType
            && (await resolver.SetAsync(seasonalType, company, today, ct).ConfigureAwait(false)).Assignments.Count > 0;

        var result = new List<DashPerson>(rows.Count);
        foreach (var r in rows)
        {
            var hasShift = r.ShiftId is not null && r.StartTime is not null && r.EndTime is not null;
            var start = hasShift ? AttendanceRequestRules.LocalInstant(today, r.StartTime!.Value, 0, zone) : now;
            var end = hasShift ? AttendanceRequestRules.LocalInstant(today, r.EndTime!.Value, r.Crosses || r.EndTime <= r.StartTime ? 1 : 0, zone) : now;
            if (hasShift && seasonal && await effective.ReadAsync(r.Id, today, ct).ConfigureAwait(false) is { Start: { } seasonStart, End: { } seasonEnd } season)
            {
                start = AttendanceRequestRules.LocalInstant(today, seasonStart, 0, zone);
                end = AttendanceRequestRules.LocalInstant(today, seasonEnd, season.CrossesMidnight || seasonEnd <= seasonStart ? 1 : 0, zone);
            }

            var first = punches.TryGetValue(r.Id, out var at) ? at : (DateTimeOffset?)null;
            var status = DashboardRules.Classify(hasShift, start, end, grace.TryGetValue(r.Id, out var minutes) ? minutes : null, now, first, onLeave.Contains(r.Id), onDuty.Contains(r.Id));
            result.Add(new DashPerson(r.Id, company, r.EmployeeNo, r.FirstName, r.LastName, r.OrgUnitId, r.BranchId, r.LocationId, status, hasShift ? start : null, hasShift ? end : null, first));
        }

        return result;
    }

    /// <summary>The late grace in minutes of each person with a shift today (absent from the answer: no attendance policy applies). The shift's own attendance policy wins, as in the engine.</summary>
    private async Task<Dictionary<Guid, int>> GraceAsync(Guid company, DateOnly today, List<(Guid Id, Guid? PolicyId)> people, CancellationToken ct)
    {
        var answer = new Dictionary<Guid, int>();
        if (people.Count == 0 || policyTypes.Find(AttendancePolicy.PolicyType) is not { } type)
        {
            return answer;
        }

        var named = new Dictionary<Guid, int>();
        foreach (var policyId in people.Where(p => p.PolicyId is not null).Select(p => p.PolicyId!.Value).Distinct())
        {
            var version = await (from v in db.Set<PolicyVersion>().AsNoTracking()
                                 join p in db.Set<Policy>().AsNoTracking() on v.PolicyId equals p.Id
                                 where p.Id == policyId && p.PolicyType == AttendancePolicy.PolicyType && !p.IsDeleted && v.Status != "draft" && v.ValidFrom <= today && (v.ValidTo == null || v.ValidTo > today)
                                 orderby v.ValidFrom descending
                                 select v.Parameters).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (version is not null && AttendancePolicy.Read(version.RootElement) is { } read)
            {
                named[policyId] = read.LateGraceMinutes;
            }
        }

        var set = await resolver.SetAsync(type, company, today, ct).ConfigureAwait(false);
        var attributes = (await PolicyAttributeReader.CompanyAsync(db, company, Current, today, ct).ConfigureAwait(false)).ToDictionary(x => x.Employment.Id, x => x.Attributes);
        var byParameters = new Dictionary<Guid, int?>();
        foreach (var (id, policyId) in people)
        {
            if (policyId is { } shiftPolicy && named.TryGetValue(shiftPolicy, out var minutes))
            {
                answer[id] = minutes;
            }
            else if (attributes.TryGetValue(id, out var who) && PolicyResolver.Decide(type, who, today, set).Policy is { } match)
            {
                if (!byParameters.TryGetValue(match.VersionId, out var cached))
                {
                    byParameters[match.VersionId] = cached = AttendancePolicy.Read(match.Parameters)?.LateGraceMinutes;
                }

                if (cached is { } value)
                {
                    answer[id] = value;
                }
            }
        }

        return answer;
    }

    private sealed record CompanyDay(Guid CompanyId, DateOnly Today);

    private sealed record DashPerson(
        Guid EmploymentId, Guid CompanyId, string EmployeeNo, string FirstName, string? LastName, Guid? OrgUnitId, Guid? BranchId, Guid? LocationId, string Status,
        DateTimeOffset? ShiftStart, DateTimeOffset? ShiftEnd, DateTimeOffset? FirstPunch);
}
