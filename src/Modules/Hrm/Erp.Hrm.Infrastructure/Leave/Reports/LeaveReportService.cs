using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Infrastructure.Attendance.Reports;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave.Reports;

/// <summary>
/// The standard leave reports (slice S1.33b Build item 9; FR-HRM-LVE-10, FR-HRM-RPT-05, FR-HRM-GHR-06). Each report reads one read-only database view
/// (<c>hrm.rpt_leave_*</c>) and applies the data scope of the caller's grant on the underlying table (ledger, request days, requests) here, exactly as the
/// generated lists do: a manager sees the team, HR the company. The permission <c>hrm.leave_reports.read</c> opens the reports. No view or row type has a pay
/// or salary member. The groups (balance by unit and leave type, leave taken by type and month, waiting requests by type) cover every row of the filter, not only
/// the page, and never add quantities of different units. The export engine reads the same pages as the person who asked through <see cref="ReadRowsAsync"/>.
/// </summary>
internal sealed class LeaveReportService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier) : ILeaveReportService
{
    public const string RangeOrder = "errors.hrm.leave.report.range_order";
    public const string RangeTooLong = "errors.hrm.leave.report.range_too_long";
    public const int MostRows = 500;
    public const int DefaultRows = 100;
    private const int MostDays = 366;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static readonly IReadOnlyList<LeaveReportDef> Reports =
    [
        new LeaveReportDef<LeaveBalanceRow>(LeaveReportCodes.BalanceSummary, "select * from hrm.rpt_leave_balances",
            Target<LeaveBalanceRow>("hrm.leave_ledger", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.PlaceLeave(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.LeaveTypeId).Where(r => r.LeaveYear == f.LeaveYear)
                .Maybe(Search<LeaveBalanceRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.OrgUnitId).ThenBy(r => r.LeaveTypeCode).ThenBy(r => r.EmployeeNo).ThenBy(r => r.EmploymentId),
            async (q, _, ct) => (await q.GroupBy(r => new { r.OrgUnitId, r.LeaveTypeCode, r.Unit })
                .Select(g => new BalanceGroup(g.Key.OrgUnitId, g.Key.LeaveTypeCode, g.Key.Unit, g.LongCount(), g.Sum(r => r.Added), g.Sum(r => r.Used), g.Sum(r => r.Adjusted), g.Sum(r => r.Removed), g.Sum(r => r.Balance)))
                .ToListAsync(ct).ConfigureAwait(false)).OrderBy(g => g.OrgUnitId).ThenBy(g => g.LeaveTypeCode, StringComparer.Ordinal).Take(LeaveReportDef<LeaveBalanceRow>.MostGroups).Cast<object>().ToList()),
        new LeaveReportDef<LeaveTakenRow>(LeaveReportCodes.LeaveTaken, "select * from hrm.rpt_leave_taken",
            Target<LeaveTakenRow>("hrm.leave_request_days", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Between(r => r.Month, MonthOf(f.From ?? new DateOnly(f.Today.Year, 1, 1)), MonthOf(f.To ?? f.Today))
                .PlaceLeave(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.LeaveTypeId)
                .Maybe(Search<LeaveTakenRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.Month).ThenBy(r => r.LeaveTypeCode).ThenBy(r => r.EmployeeNo).ThenBy(r => r.EmploymentId).ThenBy(r => r.OrgUnitId),
            async (q, _, ct) => (await q.GroupBy(r => new { r.LeaveTypeCode, r.Unit, r.Month })
                .Select(g => new TakenGroup(g.Key.LeaveTypeCode, g.Key.Unit, g.Key.Month, g.Select(r => r.EmploymentId).Distinct().Count(), g.Sum(r => r.Requests), g.Sum(r => r.Taken), g.Sum(r => r.Unpaid)))
                .ToListAsync(ct).ConfigureAwait(false)).OrderBy(g => g.LeaveTypeCode, StringComparer.Ordinal).ThenBy(g => g.Month).Take(LeaveReportDef<LeaveTakenRow>.MostGroups).Cast<object>().ToList()),
        new LeaveReportDef<LeavePendingRow>(LeaveReportCodes.PendingRequests, "select * from hrm.rpt_leave_pending",
            Target<LeavePendingRow>("hrm.leave_requests", r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.BranchId),
            (q, f) => q.Between(r => r.StartDate, f.From, f.To).PlaceLeave(f, r => r.CompanyId, r => r.EmploymentId, r => r.OrgUnitId, r => r.LeaveTypeId)
                .Maybe(f.Status is { Length: > 0 } s ? (Expression<Func<LeavePendingRow, bool>>)(r => r.Status == s) : null)
                .Maybe(Search<LeavePendingRow>(f.Search, (r, t) => EF.Functions.ILike(r.EmployeeNo, t) || EF.Functions.ILike(r.RequestNo, t) || EF.Functions.ILike(r.FirstName + " " + (r.LastName ?? string.Empty), t))),
            q => q.OrderBy(r => r.RequestedAt).ThenBy(r => r.RequestNo).ThenBy(r => r.RequestId),
            async (q, f, ct) => (await q.GroupBy(r => new { r.LeaveTypeCode, r.Status })
                .Select(g => new { g.Key.LeaveTypeCode, g.Key.Status, Requests = g.LongCount(), Oldest = g.Min(r => r.RequestedAt) })
                .ToListAsync(ct).ConfigureAwait(false))
                .OrderBy(g => g.LeaveTypeCode, StringComparer.Ordinal).ThenBy(g => g.Status, StringComparer.Ordinal).Take(LeaveReportDef<LeavePendingRow>.MostGroups)
                .Select(g => (object)new PendingGroup(g.LeaveTypeCode, g.Status, g.Requests, Math.Max(0, f.Today.DayNumber - DateOnly.FromDateTime(g.Oldest.UtcDateTime).DayNumber))).ToList(),
            (rows, today) => { foreach (var r in rows) { r.AgeDays = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(r.RequestedAt.UtcDateTime).DayNumber); } }),
    ];

    private DbContextScopeSource Source => new(db);

    public IReadOnlyList<(string Key, string JsonName)>? ColumnsOf(string code) => Reports.FirstOrDefault(r => r.Code == code)?.Columns;

    public async Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.ReportsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            reports = Reports.Select(r => new { code = r.Code, exportType = LeaveReportCodes.ExportType(r.Code), columns = r.Columns.Select(c => c.JsonName).ToList() }).ToList(),
        });
    }

    public async Task<PeopleResult> RunAsync(string code, LeaveReportQuery query, int? limit, int? offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await permissions.HasPermissionAsync(LeavePermissions.ReportsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var report = Reports.FirstOrDefault(r => r.Code == code);
        if (report is null)
        {
            return PeopleResult.NotFound;
        }

        var filters = await FiltersAsync(code, query, cancellationToken).ConfigureAwait(false);
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
            from = query.From,
            to = query.To,
            items = page.Rows,
            more = page.More,
            next = page.More ? skip + take : (int?)null,
            total = page.Total,
            summary = new { groups = page.Groups },
        });
    }

    public async Task<(IReadOnlyList<JsonObject> Rows, int? Next)?> ReadRowsAsync(string code, LeaveReportQuery query, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await permissions.HasPermissionAsync(LeavePermissions.ReportsRead, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var report = Reports.FirstOrDefault(r => r.Code == code);
        if (report is null)
        {
            return null;
        }

        var filters = await FiltersAsync(code, query, cancellationToken).ConfigureAwait(false);
        if (filters.Value is null)
        {
            return null;
        }

        var take = Math.Clamp(limit, 1, MostRows);
        var page = await report.RunAsync(new ReportContext(db, scope, Source), filters.Value, Math.Max(0, offset), take, summary: false, cancellationToken).ConfigureAwait(false);
        return (page.Rows.Select(r => (JsonObject)JsonSerializer.SerializeToNode(r, Json)!).ToList(), page.More ? offset + take : null);
    }

    /// <summary>The query with its defaults: the company's date, the leave year of the balance summary and the date range (at most a year).</summary>
    private async Task<(LeaveFilters? Value, (string Field, string Key)? Error)> FiltersAsync(string code, LeaveReportQuery q, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = q.CompanyId is { } company ? await applier.TodayAsync(company, now, ct).ConfigureAwait(false) : DateOnly.FromDateTime(now.UtcDateTime);
        if (q.From is { } from && q.To is { } to && to < from)
        {
            return (null, ("to", RangeOrder));
        }

        if (code != LeaveReportCodes.BalanceSummary)
        {
            if (q.From is { } f && q.To is { } t && t.DayNumber - f.DayNumber + 1 > MostDays)
            {
                return (null, ("to", RangeTooLong));
            }

            if (q.From is { } f2 && q.To is null && today.DayNumber - f2.DayNumber + 1 > MostDays)
            {
                return (null, ("from", RangeTooLong));
            }
        }

        return (new LeaveFilters(q.CompanyId, q.UnitId, q.EmploymentId, q.LeaveTypeId, q.LeaveYear ?? today.Year, q.From, q.To, today, q.Search?.Trim(), q.Status), null);
    }

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static ScopeTarget<T> Target<T>(string resource, Expression<Func<T, Guid?>> company, Expression<Func<T, Guid?>> employment, Expression<Func<T, Guid?>> unit, Expression<Func<T, Guid?>> branch)
        where T : class =>
        new(resource, new Dictionary<ScopeDimension, Expression<Func<T, Guid?>>>
        {
            [ScopeDimension.Company] = company, [ScopeDimension.Employment] = employment, [ScopeDimension.OrgUnit] = unit, [ScopeDimension.Branch] = branch,
        }, new Dictionary<string, Expression<Func<T, string?>>>());

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

/// <summary>Balance summary group: one unit and leave type over every row of the filter.</summary>
internal sealed record BalanceGroup(Guid? OrgUnitId, string LeaveTypeCode, string Unit, long People, decimal Added, decimal Used, decimal Adjusted, decimal Removed, decimal Balance);

/// <summary>Leave taken group: one leave type and month over every row of the filter.</summary>
internal sealed record TakenGroup(string LeaveTypeCode, string Unit, DateOnly Month, int People, long Requests, decimal Taken, decimal Unpaid);

/// <summary>Waiting requests group: one leave type and status, with the age in days of the oldest request.</summary>
internal sealed record PendingGroup(string LeaveTypeCode, string Status, long Requests, int OldestAgeDays);

internal static class LeaveReportQueryExtensions
{
    /// <summary>The company, person, unit and leave type filters.</summary>
    public static IQueryable<T> PlaceLeave<T>(this IQueryable<T> q, LeaveFilters f, Expression<Func<T, Guid?>> company, Expression<Func<T, Guid?>> employment, Expression<Func<T, Guid?>> unit, Expression<Func<T, Guid?>> leaveType) =>
        q.Equal(company, f.CompanyId).Equal(employment, f.EmploymentId).Equal(unit, f.UnitId).Equal(leaveType, f.LeaveTypeId);
}
