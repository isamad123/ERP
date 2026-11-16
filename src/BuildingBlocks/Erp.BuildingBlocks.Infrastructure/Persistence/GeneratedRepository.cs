using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.BuildingBlocks.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Shared behaviour of the generated repositories (slice S1.02): data-scope filtering on every read and a data-scope
/// check on every write (slice S1.06: the row before and after the change must be inside the caller's scope for the
/// action, otherwise <see cref="AccessDeniedException"/>), soft-deleted rows hidden, standard columns stamped from the injected clock and actor, row_version concurrency, and PostgreSQL
/// constraint errors translated into <see cref="DataException"/> types that carry a localization key.
/// </summary>
public abstract class GeneratedRepository<TEntity> : IRepository<TEntity>
    where TEntity : class, IEntity
{
    private static readonly bool IsSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(typeof(TEntity));

    protected GeneratedRepository(DbContext context, IClock clock, ICurrentActor actor, IDataScopeFilter scope, ScopeTarget<TEntity> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Context = context;
        Clock = clock;
        Actor = actor;
        Scope = scope;
        Target = target;
        Source = new DbContextScopeSource(context);
    }

    /// <summary>Query roots of this repository's context, for parent-located scopes and per-row field access.</summary>
    protected IScopeQuerySource Source { get; }

    protected DbContext Context { get; }

    protected IClock Clock { get; }

    protected ICurrentActor Actor { get; }

    protected IDataScopeFilter Scope { get; }

    /// <summary>How rows are located for data scopes; <see cref="ScopeTarget{T}.Resource"/> names the resource.</summary>
    protected ScopeTarget<TEntity> Target { get; }

    /// <summary>Resource name used for data scopes and permissions, for example <c>hrm.leave_requests</c>.</summary>
    protected string Resource => Target.Resource;

    /// <summary>Read query: data scope applied, soft-deleted rows hidden, no tracking.</summary>
    protected ValueTask<IQueryable<TEntity>> QueryAsync(CancellationToken cancellationToken) =>
        VisibleAsync(Context.Set<TEntity>().AsNoTracking(), cancellationToken);

    private async ValueTask<IQueryable<TEntity>> VisibleAsync(IQueryable<TEntity> query, CancellationToken cancellationToken)
    {
        var scoped = await Scope.ApplyAsync(query, Target, "read", Source, cancellationToken).ConfigureAwait(false);
        return IsSoftDeletable ? scoped.Where(e => !EF.Property<bool>(e, "IsDeleted")) : scoped;
    }

    public async Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await (await QueryAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<TEntity?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await (await VisibleAsync(Context.Set<TEntity>(), cancellationToken).ConfigureAwait(false)).FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);

    /// <summary>Refuses a write when the row is outside the caller's data scope for the action.</summary>
    protected async Task EnsureInScopeAsync(TEntity entity, string action, CancellationToken cancellationToken)
    {
        if (!await Scope.AllowsAsync(entity, Target, action, Source, cancellationToken).ConfigureAwait(false))
        {
            throw new AccessDeniedException();
        }
    }

    public async Task<IReadOnlyList<FieldAccessMap>> FieldAccessAsync(IReadOnlyList<TEntity> rows, FieldCatalog catalog, IFieldAccess fields, string action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var plan = await fields.PlanAsync(catalog, Target, action, Source, cancellationToken).ConfigureAwait(false);
        return await plan.ResolveAsync(rows, Source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The row as it was read from the database (before the caller's changes), for the "before" scope check.</summary>
    protected TEntity Original(TEntity entity)
    {
        var entry = Context.Entry(entity);
        return entry.State == EntityState.Detached ? entity : (TEntity)entry.OriginalValues.ToObject();
    }

    /// <summary>Keyset page ordered by id after optional filters.</summary>
    protected static async Task<IReadOnlyList<TEntity>> PageAsync(IQueryable<TEntity> query, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.After is { } after)
        {
            query = query.Where(e => e.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(e => e.Id).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var now = Clock.UtcNow;
        if (entity.Id == Guid.Empty)
        {
            entity.Id = Guid.CreateVersion7(now);
        }

        if (entity is ICreationAudited created)
        {
            created.CreatedAt = now;
            created.CreatedBy = Actor.UserId;
        }

        if (entity is IVersioned versioned)
        {
            versioned.UpdatedAt = now;
            versioned.UpdatedBy = Actor.UserId;
            versioned.RowVersion = 1;
        }

        await EnsureInScopeAsync(entity, "create", cancellationToken).ConfigureAwait(false);
        Context.Set<TEntity>().Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves with <c>where row_version = expected</c> and increments the version.</summary>
    /// <remarks>
    /// <paramref name="scopeAction"/> (normally update) is checked on the row before and after the change; null when the
    /// caller already checked (soft delete checks <c>delete</c>).
    /// </remarks>
    protected async Task SaveVersionedAsync<TVersioned>(TVersioned entity, long expectedRowVersion, string? scopeAction, CancellationToken cancellationToken)
        where TVersioned : class, TEntity, IVersioned
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (scopeAction is not null)
        {
            await EnsureInScopeAsync(Original(entity), scopeAction, cancellationToken).ConfigureAwait(false);
            await EnsureInScopeAsync(entity, scopeAction, cancellationToken).ConfigureAwait(false);
        }

        var entry = Context.Entry(entity);
        if (entry.State == EntityState.Detached)
        {
            Context.Attach(entity);
            entry.State = EntityState.Modified;
        }

        entry.Property(nameof(IVersioned.RowVersion)).OriginalValue = expectedRowVersion;
        entity.RowVersion = expectedRowVersion + 1;
        entity.UpdatedAt = Clock.UtcNow;
        entity.UpdatedBy = Actor.UserId;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the pending changes, translating constraint errors (no scope check: callers check first).</summary>
    protected async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Context.ChangeTracker.Clear();
            throw new ConcurrencyConflictException(ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            Context.ChangeTracker.Clear();
            throw PostgresErrors.Translate(pg, ex);
        }
    }
}

/// <summary>Repository of a table with trait std: updates guarded by row_version.</summary>
public abstract class GeneratedMutableRepository<TEntity>(DbContext context, IClock clock, ICurrentActor actor, IDataScopeFilter scope, ScopeTarget<TEntity> target)
    : GeneratedRepository<TEntity>(context, clock, actor, scope, target), IMutableRepository<TEntity>
    where TEntity : class, IEntity, IVersioned
{
    public Task UpdateAsync(TEntity entity, long expectedRowVersion, CancellationToken cancellationToken) =>
        SaveVersionedAsync(entity, expectedRowVersion, "update", cancellationToken);
}

/// <summary>Repository of a table with traits std and soft.</summary>
public abstract class GeneratedSoftDeleteRepository<TEntity>(DbContext context, IClock clock, ICurrentActor actor, IDataScopeFilter scope, ScopeTarget<TEntity> target)
    : GeneratedMutableRepository<TEntity>(context, clock, actor, scope, target), ISoftDeleteRepository<TEntity>
    where TEntity : class, IEntity, IVersioned, ISoftDeletable
{
    public async Task<bool> SoftDeleteAsync(Guid id, long expectedRowVersion, CancellationToken cancellationToken)
    {
        var entity = await GetForUpdateAsync(id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        await EnsureInScopeAsync(entity, "delete", cancellationToken).ConfigureAwait(false);
        entity.IsDeleted = true;
        entity.DeletedAt = Clock.UtcNow;
        entity.DeletedBy = Actor.UserId;
        await SaveVersionedAsync(entity, expectedRowVersion, null, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Maps PostgreSQL SQLSTATE codes to repository errors (class 23: integrity constraint violation).</summary>
public static class PostgresErrors
{
    public static DataException Translate(PostgresException error, Exception wrapper)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new DataConflictException("errors.data.duplicate", error.ConstraintName, wrapper),
            PostgresErrorCodes.ExclusionViolation => new DataConflictException("errors.data.overlap", error.ConstraintName, wrapper),
            PostgresErrorCodes.ForeignKeyViolation => new DataValidationException("errors.data.reference_not_found", error.ConstraintName, wrapper),
            PostgresErrorCodes.CheckViolation => new DataValidationException("errors.data.rule_violated", error.ConstraintName, wrapper),
            PostgresErrorCodes.NotNullViolation => new DataValidationException("errors.data.required", error.ColumnName, wrapper),
            "ER001" => new DataValidationException("errors.data.append_only", error.TableName, wrapper),
            _ => new DataValidationException("errors.data.rejected", error.SqlState, wrapper),
        };
    }
}

/// <summary>Counts and lists the rows of one table a data-scope filter lets through (generated resource catalogues, S1.06).</summary>
public static class ScopePreviewQuery
{
    public static async Task<ScopePreviewResult> RunAsync<TEntity>(DbContext context, IDataScopeFilter filter, ScopeTarget<TEntity> target, int limit, CancellationToken cancellationToken)
        where TEntity : class, IEntity
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(filter);
        var query = await filter.ApplyAsync(context.Set<TEntity>().AsNoTracking(), target, "read", new DbContextScopeSource(context), cancellationToken).ConfigureAwait(false);
        if (typeof(ISoftDeletable).IsAssignableFrom(typeof(TEntity)))
        {
            query = query.Where(e => !EF.Property<bool>(e, "IsDeleted"));
        }

        var count = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var ids = await query.OrderBy(e => e.Id).Take(Math.Clamp(limit, 1, PageRequest.MaxLimit)).Select(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ScopePreviewResult(count, ids);
    }
}

/// <summary>
/// The caller's field access to one record for <c>read</c> (slice S1.07, record history): null when the record does not
/// exist or is outside the caller's data scope. Soft-deleted records are included so their history stays visible.
/// </summary>
public static class RecordReadAccess
{
    public static async Task<FieldAccessMap?> ForAsync<TEntity>(DbContext context, IDataScopeFilter filter, IFieldAccess fields, ScopeTarget<TEntity> target, FieldCatalog catalog, Guid id, CancellationToken cancellationToken)
        where TEntity : class, IEntity
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(fields);
        var source = new DbContextScopeSource(context);
        var query = await filter.ApplyAsync(context.Set<TEntity>().AsNoTracking(), target, "read", source, cancellationToken).ConfigureAwait(false);
        var row = await query.FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var plan = await fields.PlanAsync(catalog, target, "read", source, cancellationToken).ConfigureAwait(false);
        return (await plan.ResolveAsync([row], source, cancellationToken).ConfigureAwait(false))[0];
    }
}

/// <summary>The query roots of one EF Core context (parent-located scopes and per-row field access, slice S1.06).</summary>
public sealed class DbContextScopeSource(DbContext context) : IScopeQuerySource
{
    public IQueryable<T> Rows<T>()
        where T : class => context.Set<T>().AsNoTracking();

    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) => query.AnyAsync(cancellationToken);

    public Task<List<Guid>> IdsAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
        where T : class, IEntity => query.Select(e => e.Id).ToListAsync(cancellationToken);
}
