using Erp.BuildingBlocks.Application.Data;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Entities;
using Erp.BuildingBlocks.Domain.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Shared behaviour of the generated repositories (slice S1.02): data-scope filtering on every read, soft-deleted
/// rows hidden, standard columns stamped from the injected clock and actor, row_version concurrency, and PostgreSQL
/// constraint errors translated into <see cref="DataException"/> types that carry a localization key.
/// </summary>
public abstract class GeneratedRepository<TEntity> : IRepository<TEntity>
    where TEntity : class, IEntity
{
    private static readonly bool IsSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(typeof(TEntity));

    protected GeneratedRepository(DbContext context, IClock clock, ICurrentActor actor, IDataScopeFilter scope, string resource)
    {
        Context = context;
        Clock = clock;
        Actor = actor;
        Scope = scope;
        Resource = resource;
    }

    protected DbContext Context { get; }

    protected IClock Clock { get; }

    protected ICurrentActor Actor { get; }

    protected IDataScopeFilter Scope { get; }

    /// <summary>Resource name used for data scopes and permissions, for example <c>hrm.leave_requests</c>.</summary>
    protected string Resource { get; }

    /// <summary>Read query: data scope applied, soft-deleted rows hidden, no tracking.</summary>
    protected IQueryable<TEntity> Query() => Visible(Context.Set<TEntity>().AsNoTracking());

    private IQueryable<TEntity> Visible(IQueryable<TEntity> query)
    {
        var scoped = Scope.Apply(query, Resource);
        return IsSoftDeletable ? scoped.Where(e => !EF.Property<bool>(e, "IsDeleted")) : scoped;
    }

    public Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Query().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<TEntity?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Visible(Context.Set<TEntity>()).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    /// <summary>Keyset page ordered by id after optional filters.</summary>
    protected async Task<IReadOnlyList<TEntity>> PageAsync(IQueryable<TEntity> query, PageRequest page, CancellationToken cancellationToken)
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

        Context.Set<TEntity>().Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves with <c>where row_version = expected</c> and increments the version.</summary>
    protected async Task SaveVersionedAsync<TVersioned>(TVersioned entity, long expectedRowVersion, CancellationToken cancellationToken)
        where TVersioned : class, TEntity, IVersioned
    {
        ArgumentNullException.ThrowIfNull(entity);
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
public abstract class GeneratedMutableRepository<TEntity>(DbContext context, IClock clock, ICurrentActor actor, IDataScopeFilter scope, string resource)
    : GeneratedRepository<TEntity>(context, clock, actor, scope, resource), IMutableRepository<TEntity>
    where TEntity : class, IEntity, IVersioned
{
    public Task UpdateAsync(TEntity entity, long expectedRowVersion, CancellationToken cancellationToken) =>
        SaveVersionedAsync(entity, expectedRowVersion, cancellationToken);
}

/// <summary>Repository of a table with traits std and soft.</summary>
public abstract class GeneratedSoftDeleteRepository<TEntity>(DbContext context, IClock clock, ICurrentActor actor, IDataScopeFilter scope, string resource)
    : GeneratedMutableRepository<TEntity>(context, clock, actor, scope, resource), ISoftDeleteRepository<TEntity>
    where TEntity : class, IEntity, IVersioned, ISoftDeletable
{
    public async Task<bool> SoftDeleteAsync(Guid id, long expectedRowVersion, CancellationToken cancellationToken)
    {
        var entity = await GetForUpdateAsync(id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        entity.IsDeleted = true;
        entity.DeletedAt = Clock.UtcNow;
        entity.DeletedBy = Actor.UserId;
        await SaveVersionedAsync(entity, expectedRowVersion, cancellationToken).ConfigureAwait(false);
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
