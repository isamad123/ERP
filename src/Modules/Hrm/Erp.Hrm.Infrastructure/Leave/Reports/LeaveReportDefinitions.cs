using System.Reflection;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Infrastructure.Attendance.Reports;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave.Reports;

/// <summary>The query after defaults (see <c>LeaveReportService.FiltersAsync</c>).</summary>
internal sealed record LeaveFilters(
    Guid? CompanyId, Guid? UnitId, Guid? EmploymentId, Guid? LeaveTypeId, int LeaveYear, DateOnly? From, DateOnly? To, DateOnly Today, string? Search, string? Status);

/// <summary>One page of a leave report: the rows, whether more follow, the number of rows in scope and the groups of the report (over every row of the filter).</summary>
internal sealed record LeaveReportPage(IReadOnlyList<object> Rows, bool More, long Total, IReadOnlyList<object>? Groups);

/// <summary>One standard leave report: a view, how its rows are scoped, filtered and ordered, and its groups.</summary>
internal abstract class LeaveReportDef(string code)
{
    public string Code { get; } = code;

    /// <summary>The columns of a row: the view's column name and the member name in the answer.</summary>
    public abstract IReadOnlyList<(string Key, string JsonName)> Columns { get; }

    public abstract Task<LeaveReportPage> RunAsync(ReportContext context, LeaveFilters filters, int skip, int take, bool summary, CancellationToken cancellationToken);
}

internal sealed class LeaveReportDef<T>(
    string code,
    string sql,
    ScopeTarget<T> target,
    Func<IQueryable<T>, LeaveFilters, IQueryable<T>> filter,
    Func<IQueryable<T>, IOrderedQueryable<T>> order,
    Func<IQueryable<T>, LeaveFilters, CancellationToken, Task<IReadOnlyList<object>>> groups,
    Action<IReadOnlyList<T>, DateOnly>? decorate = null) : LeaveReportDef(code)
    where T : class
{
    /// <summary>The most groups a report answers (units times leave types times months): more is cut, never silently wrong because every group is complete.</summary>
    public const int MostGroups = 1000;

    public override IReadOnlyList<(string Key, string JsonName)> Columns { get; } =
    [
        .. typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>()?.Name ?? ToSnake(p.Name), char.ToLowerInvariant(p.Name[0]) + p.Name[1..])),
    ];

    public override async Task<LeaveReportPage> RunAsync(ReportContext context, LeaveFilters filters, int skip, int take, bool summary, CancellationToken cancellationToken)
    {
        // The view's text is a constant of this class (never built from input); the filters, scope and order are composed on top of it.
        var all = context.Db.Database.SqlQueryRaw<T>(sql).AsNoTracking();
        var scoped = await context.Scope.ApplyAsync(all, target, "read", context.Source, cancellationToken).ConfigureAwait(false);
        var filtered = filter(scoped, filters);
        var rows = await order(filtered).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = rows.Count > take;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        decorate?.Invoke(rows, filters.Today);
        long total = 0;
        IReadOnlyList<object>? summed = null;
        if (summary)
        {
            summed = await groups(filtered, filters, cancellationToken).ConfigureAwait(false);
            total = more || skip > 0 ? await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false) : rows.Count;
        }

        return new LeaveReportPage([.. rows.Cast<object>()], more, total, summed);
    }

    private static string ToSnake(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
