using System.Linq.Expressions;
using System.Reflection;
using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Erp.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The sort keys a generated list endpoint offers (slice S1.12c Build item 14, S1.12 ruling Q11, FR-ADM-NAV-02): columns
/// the data design indexes, so a sorted page is read from an index rather than by sorting the whole table. Paging stays
/// keyset: <c>after</c> is still the id of the last row of the previous page, and the server reads that row's sort value
/// to continue after it (sort value, then id, so equal values never repeat or skip a row).
/// </summary>
public sealed class ListSorts<TEntity>
    where TEntity : class, IEntity
{
    private readonly Dictionary<string, ISortKey<TEntity>> keys = new(StringComparer.Ordinal);

    /// <summary>A key compared as stored (numbers, dates, enum codes).</summary>
    public ListSorts<TEntity> Key<TKey>(string field, Expression<Func<TEntity, TKey>> key)
    {
        keys.Add(field, new SortKey<TEntity, TKey>(key));
        return this;
    }

    /// <summary>A text key compared without case (<c>lower(column)</c>, the form the data design indexes codes in).</summary>
    public ListSorts<TEntity> Text(string field, Expression<Func<TEntity, string>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var lowered = Expression.Lambda<Func<TEntity, string>>(Expression.Call(key.Body, ToLower), key.Parameters);
        keys.Add(field, new SortKey<TEntity, string>(lowered));
        return this;
    }

    public IReadOnlyCollection<string> Fields => keys.Keys;

    private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    /// <summary>
    /// One page sorted by <paramref name="sort"/> (<c>field</c> ascending, <c>-field</c> descending; null or empty: by id).
    /// An unknown field is refused with <c>errors.data.invalid_sort</c>; an <c>after</c> id that names no row with
    /// <c>errors.data.invalid_cursor</c>.
    /// </summary>
    public async Task<IReadOnlyList<TEntity>> PageAsync(IQueryable<TEntity> rows, IQueryable<TEntity> query, string? sort, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (string.IsNullOrEmpty(sort))
        {
            if (page.After is { } after)
            {
                query = query.Where(e => e.Id.CompareTo(after) > 0);
            }

            return await query.OrderBy(e => e.Id).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var descending = sort[0] == '-';
        var field = descending ? sort[1..] : sort;
        if (!keys.TryGetValue(field, out var key))
        {
            throw new DataValidationException("errors.data.invalid_sort", "sort");
        }

        return await key.PageAsync(rows, query, descending, page, cancellationToken).ConfigureAwait(false);
    }
}

internal interface ISortKey<TEntity>
    where TEntity : class, IEntity
{
    Task<IReadOnlyList<TEntity>> PageAsync(IQueryable<TEntity> rows, IQueryable<TEntity> query, bool descending, PageRequest page, CancellationToken cancellationToken);
}

internal sealed class SortKey<TEntity, TKey>(Expression<Func<TEntity, TKey>> key) : ISortKey<TEntity>
    where TEntity : class, IEntity
{
    private static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo GuidCompareTo = typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!;

    public async Task<IReadOnlyList<TEntity>> PageAsync(IQueryable<TEntity> rows, IQueryable<TEntity> query, bool descending, PageRequest page, CancellationToken cancellationToken)
    {
        if (page.After is { } after)
        {
            // The anchor row is read without the caller's filters (it is only a boundary, never returned): a row that has
            // since been soft-deleted or moved out of the filter still marks where the previous page ended.
            var anchor = await rows.Where(e => e.Id == after).Select(key).Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (anchor.Count == 0)
            {
                throw new DataValidationException("errors.data.invalid_cursor", "after");
            }

            query = query.Where(After(anchor[0], after, descending));
        }

        var ordered = descending ? query.OrderByDescending(key).ThenByDescending(e => e.Id) : query.OrderBy(key).ThenBy(e => e.Id);
        return await ordered.Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>(key, id) after (anchor, after) in the sort direction, as SQL comparisons the provider translates.</summary>
    private Expression<Func<TEntity, bool>> After(TKey anchor, Guid after, bool descending)
    {
        var row = key.Parameters[0];
        var value = Captured(anchor);
        var id = Expression.Property(Expression.Convert(row, typeof(IEntity)), nameof(IEntity.Id));
        var beyond = Beyond(key.Body, value, descending);
        var tie = Expression.AndAlso(Expression.Equal(key.Body, value), Beyond(id, Captured(after), descending));
        return Expression.Lambda<Func<TEntity, bool>>(Expression.OrElse(beyond, tie), row);
    }

    private static BinaryExpression Beyond(Expression left, Expression right, bool descending)
    {
        Expression compared;
        Expression zero = Expression.Constant(0);
        if (left.Type == typeof(string))
        {
            compared = Expression.Call(StringCompare, left, right);
        }
        else if (left.Type == typeof(Guid))
        {
            compared = Expression.Call(left, GuidCompareTo, right);
        }
        else
        {
            return descending ? Expression.LessThan(left, right) : Expression.GreaterThan(left, right);
        }

        return descending ? Expression.LessThan(compared, zero) : Expression.GreaterThan(compared, zero);
    }

    /// <summary>A captured value, so the provider sends it as a query parameter rather than as literal SQL.</summary>
    private static MemberExpression Captured<T>(T value) =>
        Expression.Property(Expression.Constant(new Box<T>(value)), nameof(Box<T>.Value));

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }
}

/// <summary>The case-insensitive "contains" pattern of a list search, with LIKE wildcards in the search text escaped.</summary>
public static class ListSearch
{
    public const string Escape = "\\";

    public static string Pattern(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var escaped = search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }
}
