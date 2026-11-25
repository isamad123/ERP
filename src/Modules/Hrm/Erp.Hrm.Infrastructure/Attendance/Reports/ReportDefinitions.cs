using System.Reflection;
using System.Text.Json.Serialization;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance.Reports;

/// <summary>What a report needs to run: the database, the data scope filter and its query roots.</summary>
internal sealed record ReportContext(HrmDbContext Db, IDataScopeFilter Scope, IScopeQuerySource Source);

/// <summary>One page of a report: the rows, whether more follow, the number of rows in scope and the totals of the report.</summary>
internal sealed record ReportPage(IReadOnlyList<object> Rows, bool More, long Total, IReadOnlyDictionary<string, long>? Summary);

/// <summary>One standard attendance report: a view, how its rows are scoped, filtered and ordered, and its totals.</summary>
internal abstract class ReportDef(string code)
{
    public string Code { get; } = code;

    /// <summary>The columns of a row: the view's column name and the member name in the answer.</summary>
    public abstract IReadOnlyList<(string Key, string JsonName)> Columns { get; }

    public abstract Task<ReportPage> RunAsync(ReportContext context, ResolvedFilters filters, int skip, int take, bool summary, CancellationToken cancellationToken);
}

internal sealed class ReportDef<T>(
    string code,
    string sql,
    ScopeTarget<T> target,
    Func<IQueryable<T>, ResolvedFilters, IQueryable<T>> filter,
    Func<IQueryable<T>, IOrderedQueryable<T>> order,
    Func<IQueryable<T>, CancellationToken, Task<Dictionary<string, long>>> totals,
    Action<IReadOnlyList<T>, DateOnly>? decorate = null) : ReportDef(code)
    where T : class
{
    public override IReadOnlyList<(string Key, string JsonName)> Columns { get; } =
    [
        .. typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => (p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>()?.Name ?? ToSnake(p.Name), char.ToLowerInvariant(p.Name[0]) + p.Name[1..])),
    ];

    public override async Task<ReportPage> RunAsync(ReportContext context, ResolvedFilters filters, int skip, int take, bool summary, CancellationToken cancellationToken)
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
        Dictionary<string, long>? sums = null;
        if (summary)
        {
            sums = await totals(filtered, cancellationToken).ConfigureAwait(false);
            if (sums.Remove(RowsKey, out var counted))
            {
                total = counted; // the totals query already counted the rows: one run of an expensive view less (R-317)
            }
            else
            {
                total = more || skip > 0 ? await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false) : rows.Count;
            }
        }

        return new ReportPage([.. rows.Cast<object>()], more, total, sums);
    }

    /// <summary>A totals query may count the filtered rows under this key; it is the answer's <c>total</c>, not a summary figure.</summary>
    public const string RowsKey = "_rows";

    private static string ToSnake(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
