using Erp.BuildingBlocks.Domain.Entities;

namespace Erp.BuildingBlocks.Application.Data;

/// <summary>Keyset paging (spec 01: lists use keyset pagination and bounded page sizes). Ids are UUIDv7, so time-ordered.</summary>
public sealed record PageRequest(Guid? After, int Limit)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static PageRequest Create(Guid? after, int? limit) =>
        new(after, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));
}

/// <summary>Reads and inserts for every generated table with an <c>id</c> key.</summary>
public interface IRepository<TEntity>
    where TEntity : class, IEntity
{
    /// <summary>The row, or null when it does not exist, is soft-deleted, or is outside the user's data scope.</summary>
    Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Tracked copy for an update; same visibility rules as <see cref="GetByIdAsync"/>.</summary>
    Task<TEntity?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stamps id (UUIDv7), created_at/by (and updated_at/by, row_version 1) and inserts.</summary>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's field access to each stored row for the action (slice S1.06, review finding 3): only the roles whose
    /// data scope covers a row decide its fields.
    /// </summary>
    Task<IReadOnlyList<Erp.BuildingBlocks.Application.Security.FieldAccessMap>> FieldAccessAsync(IReadOnlyList<TEntity> rows, Erp.BuildingBlocks.Application.Security.FieldCatalog catalog, Erp.BuildingBlocks.Application.Security.IFieldAccess fields, string action, CancellationToken cancellationToken);
}

/// <summary>Updates guarded by row_version (spec 03 R-13): a lost update raises <see cref="ConcurrencyConflictException"/>.</summary>
public interface IMutableRepository<TEntity> : IRepository<TEntity>
    where TEntity : class, IEntity, IVersioned
{
    Task UpdateAsync(TEntity entity, long expectedRowVersion, CancellationToken cancellationToken);
}

/// <summary>Soft delete for master data (trait soft).</summary>
public interface ISoftDeleteRepository<TEntity> : IMutableRepository<TEntity>
    where TEntity : class, IEntity, IVersioned, ISoftDeletable
{
    /// <summary>Marks the row deleted. Returns false when it does not exist or is not visible.</summary>
    Task<bool> SoftDeleteAsync(Guid id, long expectedRowVersion, CancellationToken cancellationToken);
}

/// <summary>Base for data errors raised by repositories. <see cref="ErrorCode"/> is a localization key, never display text.</summary>
public abstract class DataException : Exception
{
    protected DataException(string errorCode, string? constraint, Exception? inner)
        : base(errorCode + (constraint is null ? string.Empty : " (" + constraint + ")"), inner)
    {
        ErrorCode = errorCode;
        Constraint = constraint;
    }

    public string ErrorCode { get; }

    public string? Constraint { get; }
}

/// <summary>Someone else changed the row since it was read (row_version mismatch).</summary>
public sealed class ConcurrencyConflictException(Exception? inner = null)
    : DataException("errors.data.concurrency_conflict", null, inner);

/// <summary>A unique or exclusion constraint refused the change (duplicate or overlapping period).</summary>
public sealed class DataConflictException(string errorCode, string? constraint, Exception? inner = null)
    : DataException(errorCode, constraint, inner);

/// <summary>A check, not-null or foreign-key constraint refused the change.</summary>
public sealed class DataValidationException(string errorCode, string? constraint, Exception? inner = null)
    : DataException(errorCode, constraint, inner);
