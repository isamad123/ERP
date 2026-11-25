using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance.Reports;

/// <summary>
/// The standard attendance reports (slice S1.30b Build item 6; FR-HRM-ATT-19, FR-HRM-RPT-05, FR-HRM-GHR-06). Each report reads one read-only database view
/// (<c>hrm.rpt_att_*</c>) and applies the data scope of the caller's grant on the underlying table (days, punches, exceptions, devices) here, exactly as the
/// generated lists do; a role limited to a team sees the team. The permission <c>hrm.attendance_reports.read</c> opens the reports. No view or row type has a
/// pay or salary member. The export engine reads the same pages as the person who asked through <see cref="ReadRowsAsync"/>.
/// </summary>
internal sealed class AttendanceReportService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier) : IAttendanceReportService
{
    public const string RangeOrder = "errors.hrm.attendance.report.range_order";
    public const string RangeTooLong = "errors.hrm.attendance.report.range_too_long";
    public const string UnknownReport = "errors.hrm.attendance.report.unknown";
    public const int MostRows = 500;
    public const int DefaultRows = 100;
    private const int MostDays = 366;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static readonly IReadOnlyList<ReportDef> Reports =
    [
        new ReportDef<MusterRow>(AttendanceReportCodes.MusterDaily, "select * from hrm.rpt_att_muster", Target<MusterRow>("hrm.attendance_days", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Dated(f, r => r.WorkDate).Place(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId, r => r.LocationId)
                .Maybe(f.Status is { Length: > 0 } s ? (Expression<Func<MusterRow, bool>>)(r => r.Result == s) : null)
                .Maybe(Search<MusterRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.WorkDate).ThenBy(r => r.EmployeeNo).ThenBy(r => r.DayId),
            async (q, ct) => (await q.GroupBy(r => r.Result).Select(g => new { g.Key, N = g.LongCount() }).ToListAsync(ct).ConfigureAwait(false)).ToDictionary(x => "result." + x.Key, x => x.N)),
        new ReportDef<MonthlyRow>(AttendanceReportCodes.MusterMonthly, "select * from hrm.rpt_att_muster_monthly", Target<MonthlyRow>("hrm.attendance_days", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Months(f, r => r.Month).Place(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId, r => r.LocationId)
                .Maybe(Search<MonthlyRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.Month).ThenBy(r => r.EmployeeNo).ThenBy(r => r.EmploymentId),
            async (q, ct) => (await q.GroupBy(_ => 1).Select(g => new
            {
                Present = g.Sum(r => r.PresentDays), Absent = g.Sum(r => r.AbsentDays), Late = g.Sum(r => r.LateDays), Half = g.Sum(r => r.HalfDays), Leave = g.Sum(r => r.LeaveDays),
                Duty = g.Sum(r => r.DutyDays), Worked = g.Sum(r => r.WorkedMinutes), LateMin = g.Sum(r => r.LateMinutes), EarlyMin = g.Sum(r => r.EarlyMinutes),
            }).ToListAsync(ct).ConfigureAwait(false)).SelectMany(x => new Dictionary<string, long>
            {
                ["presentDays"] = x.Present, ["absentDays"] = x.Absent, ["lateDays"] = x.Late, ["halfDays"] = x.Half, ["leaveDays"] = x.Leave, ["dutyDays"] = x.Duty,
                ["workedMinutes"] = x.Worked, ["lateMinutes"] = x.LateMin, ["earlyMinutes"] = x.EarlyMin,
            }).ToDictionary(p => p.Key, p => p.Value)),
        new ReportDef<AbsenteeismRow>(AttendanceReportCodes.Absenteeism, "select * from hrm.rpt_att_absenteeism", Target<AbsenteeismRow>("hrm.attendance_days", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Months(f, r => r.Month).Place(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId, r => r.LocationId)
                .Where(r => r.AbsentDays > 0 || r.HalfDays > 0)
                .Maybe(Search<AbsenteeismRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.Month).ThenByDescending(r => r.AbsentDays).ThenBy(r => r.EmployeeNo).ThenBy(r => r.EmploymentId),
            async (q, ct) => (await q.GroupBy(_ => 1).Select(g => new { Scheduled = g.Sum(r => r.ScheduledDays), Absent = g.Sum(r => r.AbsentDays), Half = g.Sum(r => r.HalfDays), People = g.LongCount() })
                .ToListAsync(ct).ConfigureAwait(false)).SelectMany(x => new Dictionary<string, long> { ["scheduledDays"] = x.Scheduled, ["absentDays"] = x.Absent, ["halfDays"] = x.Half, ["rows"] = x.People })
                .ToDictionary(p => p.Key, p => p.Value)),
        new ReportDef<LateEarlyRow>(AttendanceReportCodes.LateEarly, "select * from hrm.rpt_att_late_early", Target<LateEarlyRow>("hrm.attendance_days", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Dated(f, r => r.WorkDate).Place(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId, r => r.LocationId)
                .Maybe(Search<LateEarlyRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.WorkDate).ThenBy(r => r.EmployeeNo).ThenBy(r => r.DayId),
            async (q, ct) => (await q.GroupBy(_ => 1).Select(g => new { N = g.LongCount(), Late = g.Sum(r => (long)r.LateMinutes), Early = g.Sum(r => (long)r.EarlyMinutes) })
                .ToListAsync(ct).ConfigureAwait(false)).SelectMany(x => new Dictionary<string, long> { ["days"] = x.N, ["lateMinutes"] = x.Late, ["earlyMinutes"] = x.Early })
                .ToDictionary(p => p.Key, p => p.Value)),
        new ReportDef<PunchRow>(AttendanceReportCodes.PunchLog, "select * from hrm.rpt_att_punch_log", Target<PunchRow>("hrm.attendance_events", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Dated(f, r => r.EventDate).Place(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId, r => r.LocationId)
                .Maybe(f.Kind is { Length: > 0 } k ? (Expression<Func<PunchRow, bool>>)(r => r.Source == k) : null)
                .Maybe(Search<PunchRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.EventAt).ThenBy(r => r.EventId),
            async (q, ct) => (await q.GroupBy(r => r.Source).Select(g => new { g.Key, N = g.LongCount() }).ToListAsync(ct).ConfigureAwait(false)).ToDictionary(x => "source." + x.Key, x => x.N)),
        new ReportDef<DeviceRow>(AttendanceReportCodes.DeviceSync, "select * from hrm.rpt_att_device_sync", Target<DeviceRow>("hrm.attendance_devices", r => r.CompanyId, null, null, null),
            (q, f) => q.Maybe(f.CompanyId is { } c ? (Expression<Func<DeviceRow, bool>>)(r => r.CompanyId == c) : null)
                .Maybe(f.LocationId is { } l ? (Expression<Func<DeviceRow, bool>>)(r => r.LocationId == l) : null)
                .Maybe(f.Status is { Length: > 0 } s ? (Expression<Func<DeviceRow, bool>>)(r => r.Status == s) : null)
                .Maybe(Search<DeviceRow>(f.Search, (r, t) => EF.Functions.ILike(r.SerialNo, t) || EF.Functions.ILike(r.Model ?? string.Empty, t))),
            q => q.OrderBy(r => r.SerialNo).ThenBy(r => r.DeviceId),
            async (q, ct) => (await q.GroupBy(r => r.Status).Select(g => new { g.Key, N = g.LongCount() }).ToListAsync(ct).ConfigureAwait(false)).ToDictionary(x => "status." + x.Key, x => x.N)),
        new ReportDef<ExceptionRow>(AttendanceReportCodes.ExceptionAgeing, "select * from hrm.rpt_att_exceptions", Target<ExceptionRow>("hrm.attendance_exceptions", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Dated(f, r => r.WorkDate, defaultRange: false).Place(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId, r => r.LocationId)
                .Maybe(f.Status is { Length: > 0 } s ? (Expression<Func<ExceptionRow, bool>>)(r => r.Status == s) : (r => r.Status == "open"))
                .Maybe(f.Kind is { Length: > 0 } k ? (Expression<Func<ExceptionRow, bool>>)(r => r.Kind == k) : null)
                .Maybe(f.OldestWorkDate is { } latest ? (Expression<Func<ExceptionRow, bool>>)(r => r.WorkDate <= latest) : null)
                .Maybe(Search<ExceptionRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.WorkDate).ThenBy(r => r.EmployeeNo).ThenBy(r => r.ExceptionId),
            async (q, ct) => (await q.GroupBy(r => r.Kind).Select(g => new { g.Key, N = g.LongCount() }).ToListAsync(ct).ConfigureAwait(false)).ToDictionary(x => "kind." + x.Key, x => x.N),
            (rows, today) => { foreach (var r in rows) { r.AgeDays = Math.Max(0, today.DayNumber - r.WorkDate.DayNumber); } }),
    ];

    private DbContextScopeSource Source => new(db);

    public IReadOnlyList<(string Key, string JsonName)>? ColumnsOf(string code) => Reports.FirstOrDefault(r => r.Code == code)?.Columns;

    public async Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceReportPermissions.Reports, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            reports = Reports.Select(r => new { code = r.Code, exportType = AttendanceReportCodes.ExportType(r.Code), columns = r.Columns.Select(c => c.JsonName).ToList() }).ToList(),
        });
    }

    public async Task<PeopleResult> RunAsync(string code, AttendanceReportQuery query, int? limit, int? offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await permissions.HasPermissionAsync(AttendanceReportPermissions.Reports, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var report = Reports.FirstOrDefault(r => r.Code == code);
        if (report is null)
        {
            return PeopleResult.NotFound;
        }

        var filters = await FiltersAsync(report, query, cancellationToken).ConfigureAwait(false);
        if (filters.Error is { } error)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { [error.Field] = [error.Key] });
        }

        var take = Math.Clamp(limit ?? DefaultRows, 1, MostRows);
        var skip = Math.Max(0, offset ?? 0);
        var page = await report.RunAsync(new ReportContext(db, scope, Source), filters.Value!, skip, take, summary: true, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            code,
            from = filters.Value!.From,
            to = filters.Value.To,
            items = page.Rows,
            more = page.More,
            next = page.More ? skip + take : (int?)null,
            total = page.Total,
            summary = page.Summary,
        });
    }

    public async Task<(IReadOnlyList<JsonObject> Rows, int? Next)?> ReadRowsAsync(string code, AttendanceReportQuery query, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await permissions.HasPermissionAsync(AttendanceReportPermissions.Reports, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var report = Reports.FirstOrDefault(r => r.Code == code);
        if (report is null)
        {
            return null;
        }

        var filters = await FiltersAsync(report, query, cancellationToken).ConfigureAwait(false);
        if (filters.Value is null)
        {
            return null;
        }

        var take = Math.Clamp(limit, 1, MostRows);
        var page = await report.RunAsync(new ReportContext(db, scope, Source), filters.Value, Math.Max(0, offset), take, summary: false, cancellationToken).ConfigureAwait(false);
        return (page.Rows.Select(r => (JsonObject)JsonSerializer.SerializeToNode(r, Json)!).ToList(), page.More ? offset + take : null);
    }

    /// <summary>The query with its defaults: the company's date, the date range (at most a year) and the cut-off date of an age filter.</summary>
    private async Task<(ResolvedFilters? Value, (string Field, string Key)? Error)> FiltersAsync(ReportDef report, AttendanceReportQuery q, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = q.CompanyId is { } company ? await applier.TodayAsync(company, now, ct).ConfigureAwait(false) : DateOnly.FromDateTime(now.UtcDateTime);
        var from = q.From;
        var to = q.To;
        if (from is { } f && to is { } t && t < f)
        {
            return (null, ("to", RangeOrder));
        }

        if (from is { } f2 && to is { } t2 && t2.DayNumber - f2.DayNumber + 1 > MostDays)
        {
            return (null, ("to", RangeTooLong));
        }

        if (from is { } f3 && to is null && today.DayNumber - f3.DayNumber + 1 > MostDays)
        {
            return (null, ("from", RangeTooLong));
        }

        var oldest = q.MinAgeDays is { } age and > 0 ? today.AddDays(-age) : (DateOnly?)null;
        return (new ResolvedFilters(q.CompanyId, from, to, today, q.UnitId, q.BranchId, q.LocationId, q.EmploymentId, q.Search?.Trim(), q.Status, q.Kind, oldest), null);
    }

    private static ScopeTarget<T> Target<T>(string resource, Expression<Func<T, Guid?>> company, Expression<Func<T, Guid?>>? employment, Expression<Func<T, Guid?>>? unit, Expression<Func<T, Guid?>>? branch)
        where T : class
    {
        var locators = new Dictionary<ScopeDimension, Expression<Func<T, Guid?>>> { [ScopeDimension.Company] = company };
        if (employment is not null)
        {
            locators[ScopeDimension.Employment] = employment;
        }

        if (unit is not null)
        {
            locators[ScopeDimension.OrgUnit] = unit;
        }

        if (branch is not null)
        {
            locators[ScopeDimension.Branch] = branch;
        }

        return new ScopeTarget<T>(resource, locators, new Dictionary<string, Expression<Func<T, string?>>>());
    }

    /// <summary>A case-insensitive "contains" test (the text is a pattern with % and _ escaped), or null when there is no search text.</summary>
    private static Expression<Func<T, bool>>? Search<T>(string? text, Expression<Func<T, string, bool>> test)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var body = new ReplaceParameter(test.Parameters[1], Expression.Constant(pattern)).Visit(test.Body)!;
        return Expression.Lambda<Func<T, bool>>(body, test.Parameters[0]);
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }
}

/// <summary>The query after defaults (see <c>AttendanceReportService.FiltersAsync</c>).</summary>
internal sealed record ResolvedFilters(
    Guid? CompanyId, DateOnly? From, DateOnly? To, DateOnly Today, Guid? UnitId, Guid? BranchId, Guid? LocationId, Guid? EmploymentId, string? Search, string? Status, string? Kind, DateOnly? OldestWorkDate);

internal static class ReportQueryExtensions
{
    /// <summary>The date range of a daily report: the given one, else the thirty days up to today (<paramref name="defaultRange"/> false: no range unless one is given).</summary>
    public static IQueryable<T> Dated<T>(this IQueryable<T> q, ResolvedFilters f, Expression<Func<T, DateOnly>> date, bool defaultRange = true)
    {
        var to = f.To ?? (defaultRange || f.From is not null ? f.Today : (DateOnly?)null);
        var from = f.From ?? (defaultRange && to is { } end ? end.AddDays(-30) : null);
        return q.Between(date, from, to);
    }

    /// <summary>The months of a monthly report: from the first day of the month of From to the one of To (default: this month).</summary>
    public static IQueryable<T> Months<T>(this IQueryable<T> q, ResolvedFilters f, Expression<Func<T, DateOnly>> month)
    {
        var from = f.From ?? f.Today;
        var to = f.To ?? f.From ?? f.Today;
        return q.Between(month, new DateOnly(from.Year, from.Month, 1), new DateOnly(to.Year, to.Month, 1));
    }

    /// <summary>The company, person, unit, branch and location filters.</summary>
    public static IQueryable<T> Place<T>(this IQueryable<T> q, ResolvedFilters f, Expression<Func<T, Guid?>> company, Expression<Func<T, Guid?>> employment, Expression<Func<T, Guid?>> unit, Expression<Func<T, Guid?>> branch, Expression<Func<T, Guid?>> location) =>
        q.Equal(company, f.CompanyId).Equal(employment, f.EmploymentId).Equal(unit, f.UnitId).Equal(branch, f.BranchId).Equal(location, f.LocationId);

    /// <summary>Keeps rows whose member equals the value; nothing happens for a null value.</summary>
    public static IQueryable<T> Equal<T>(this IQueryable<T> q, Expression<Func<T, Guid?>> member, Guid? value) =>
        value is null ? q : q.Where(Expression.Lambda<Func<T, bool>>(Expression.Equal(member.Body, Expression.Constant(value, typeof(Guid?))), member.Parameters));

    public static IQueryable<T> Between<T>(this IQueryable<T> q, Expression<Func<T, DateOnly>> date, DateOnly? from, DateOnly? to)
    {
        if (from is { } f)
        {
            q = q.Where(Expression.Lambda<Func<T, bool>>(Expression.GreaterThanOrEqual(date.Body, Expression.Constant(f)), date.Parameters));
        }

        if (to is { } t)
        {
            q = q.Where(Expression.Lambda<Func<T, bool>>(Expression.LessThanOrEqual(date.Body, Expression.Constant(t)), date.Parameters));
        }

        return q;
    }

    /// <summary>Applies a predicate only when there is one.</summary>
    public static IQueryable<T> Maybe<T>(this IQueryable<T> q, Expression<Func<T, bool>>? predicate) => predicate is null ? q : Queryable.Where(q, predicate);
}
