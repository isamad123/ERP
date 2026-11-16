using System.Linq.Expressions;
using Erp.BuildingBlocks.Domain.Entities;

namespace Erp.BuildingBlocks.Application.Security;

/// <summary>
/// Answers whether the current user holds a permission (<c>module.resource.action</c>). Implemented by the identity
/// and access slices (S1.05, S1.06). No implementation is registered before then, so generated endpoints cannot run
/// without a real permission check.
/// </summary>
public interface IPermissionChecker
{
    ValueTask<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken);
}

/// <summary>
/// Restricts reads to the rows the current user may see and checks writes against the same rules (data scopes: own,
/// direct team, subtree, org units, branches, companies, all; FR-KRN-IAM-12). Implemented by the access engine of
/// slice S1.06. Generated repositories pass every read and write through it; the action is the
/// permission action (read, create, update, delete).
/// </summary>
public interface IDataScopeFilter
{
    /// <summary>
    /// The query restricted to the rows in scope. <paramref name="source"/> gives the parent table of a target located
    /// through its parent (<see cref="ScopeTarget{T}.Via"/>); it may be null for other targets.
    /// </summary>
    ValueTask<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query, ScopeTarget<T> target, string action, IScopeQuerySource? source, CancellationToken cancellationToken)
        where T : class;

    /// <summary>True when the row (as it is in memory; its parent as stored) is inside the user's scope for the action.</summary>
    ValueTask<bool> AllowsAsync<T>(T row, ScopeTarget<T> target, string action, IScopeQuerySource? source, CancellationToken cancellationToken)
        where T : class;
}

/// <summary>
/// Query roots of the caller's database context, for targets located through a parent table (design key
/// <c>scope: {via: column}</c>, review S1.06 finding 1) and for per-row field access (finding 3).
/// </summary>
public interface IScopeQuerySource
{
    IQueryable<T> Rows<T>()
        where T : class;

    Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken);

    Task<List<Guid>> IdsAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
        where T : class, IEntity;
}

/// <summary>Builds the scope filter of a parent table for the grant or grants being evaluated (double dispatch on the parent type).</summary>
public interface IScopeParentPredicates
{
    /// <summary>The filter over the parent's rows; null means every parent row.</summary>
    Task<LambdaExpression?> BuildAsync<TParent>(ScopeTarget<TParent> parent)
        where TParent : class;
}

/// <summary>A table whose rows are located through a parent row (design key <c>scope: {via: column}</c>).</summary>
public abstract class ScopeVia<T>
    where T : class
{
    /// <summary>The parent's filter for the grant(s) the caller evaluates.</summary>
    public abstract Task<LambdaExpression?> ParentPredicateAsync(IScopeParentPredicates predicates);

    /// <summary>The child filter: rows whose parent passes <paramref name="parentPredicate"/> (null: every row).</summary>
    public abstract Expression<Func<T, bool>>? Compose(LambdaExpression? parentPredicate, IScopeQuerySource source);

    /// <summary>True when the stored parent of the row passes <paramref name="parentPredicate"/> (null: always).</summary>
    public abstract Task<bool> AllowsAsync(T row, LambdaExpression? parentPredicate, IScopeQuerySource source, CancellationToken cancellationToken);
}

/// <summary>Rows of <typeparamref name="T"/> located through the parent <typeparamref name="TParent"/> named by a foreign key.</summary>
public sealed class ScopeVia<T, TParent>(Expression<Func<T, Guid?>> foreignKey, ScopeTarget<TParent> parent) : ScopeVia<T>
    where T : class
    where TParent : class, IEntity
{
    private static readonly System.Reflection.MethodInfo Contains = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(typeof(Guid?));

    private readonly Func<T, Guid?> _key = foreignKey.Compile();

    public Expression<Func<T, Guid?>> ForeignKey { get; } = foreignKey;

    /// <summary>The parent's own locators (its resource name is not used: the child's resource and action decide).</summary>
    public ScopeTarget<TParent> Parent { get; } = parent;

    public override Task<LambdaExpression?> ParentPredicateAsync(IScopeParentPredicates predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        return predicates.BuildAsync(Parent);
    }

    public override Expression<Func<T, bool>>? Compose(LambdaExpression? parentPredicate, IScopeQuerySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (parentPredicate is null)
        {
            return null;
        }

        var ids = source.Rows<TParent>().Where((Expression<Func<TParent, bool>>)parentPredicate).Select(p => (Guid?)p.Id);
        var child = ForeignKey.Parameters[0];
        return Expression.Lambda<Func<T, bool>>(Expression.Call(Contains, ids.Expression, ForeignKey.Body), child);
    }

    public override async Task<bool> AllowsAsync(T row, LambdaExpression? parentPredicate, IScopeQuerySource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (parentPredicate is null)
        {
            return true;
        }

        if (_key(row) is not { } key)
        {
            return false;
        }

        var parents = source.Rows<TParent>().Where((Expression<Func<TParent, bool>>)parentPredicate).Where(p => p.Id == key);
        return await source.AnyAsync(parents, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The caller holds the permission but the row is outside their data scope (or a change would move it outside).
/// Answered 403 with <see cref="ErrorCode"/> (a localization key).
/// </summary>
public sealed class AccessDeniedException : Exception
{
    public const string OutOfScope = "errors.access.out_of_scope";

    public AccessDeniedException()
        : this(OutOfScope)
    {
    }

    public AccessDeniedException(string errorCode)
        : base(errorCode) => ErrorCode = errorCode;

    public AccessDeniedException(string errorCode, Exception innerException)
        : base(errorCode, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; } = OutOfScope;
}

/// <summary>The dimensions a data scope can locate a row by (data-design key <c>scope</c>, CR-018).</summary>
public enum ScopeDimension
{
    Company,
    Branch,
    OrgUnit,
    Employment,
    Person,
    User,
}

/// <summary>
/// How rows of one table are located for data scopes: the resource name and, per dimension, the column that holds it
/// (generated from the data design). A target without locators is not scoped: the permission alone decides.
/// </summary>
public sealed class ScopeTarget<T>
    where T : class
{
    public ScopeTarget(string resource, IReadOnlyDictionary<ScopeDimension, Expression<Func<T, Guid?>>> locators, IReadOnlyDictionary<string, Expression<Func<T, string?>>> attributes, ScopeVia<T>? via = null)
    {
        Resource = resource;
        Locators = locators;
        Attributes = attributes;
        Via = via;
    }

    /// <summary>Set when rows are located through a parent row (design key <c>scope: {via: column}</c>); then there are no locators.</summary>
    public ScopeVia<T>? Via { get; }

    /// <summary>Resource name, for example <c>hrm.leave_requests</c>; permissions are <c>resource.action</c>.</summary>
    public string Resource { get; }

    public IReadOnlyDictionary<ScopeDimension, Expression<Func<T, Guid?>>> Locators { get; }

    /// <summary>Text columns attribute conditions may test, by column name (Q5: "equals / is one of").</summary>
    public IReadOnlyDictionary<string, Expression<Func<T, string?>>> Attributes { get; }

    public bool IsScoped => Locators.Count > 0 || Via is not null;
}

/// <summary>The signed-in user (or reserved system user) whose id is written to created_by, updated_by and deleted_by.</summary>
public interface ICurrentActor
{
    Guid UserId { get; }
}

/// <summary>
/// Field-level envelope encryption for <c>enc</c> columns (README "Types"; spec 03 section 3.3). Implemented with the
/// key service in S1.06/S1.07. Generated code stores and returns ciphertext only and never pretends to decrypt.
/// </summary>
public interface IFieldEncryptor
{
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, string purpose);

    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, string purpose);

    /// <summary>Blind index (<c>hmac</c> column) for exact-match lookup of an encrypted value.</summary>
    byte[] BlindIndex(ReadOnlySpan<byte> normalizedPlaintext, string purpose);
}

/// <summary>
/// The field encryptor of the bound customer (one data key per customer, slice S1.05 ruling P7). Async because the key
/// is read from the secret store on first use.
/// </summary>
public interface IFieldEncryptorProvider
{
    ValueTask<IFieldEncryptor> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the signed-in caller holds, for rules such as "nobody can grant a permission they do not hold" (spec 02 BR) and
/// "a role given for company A grants nothing in company B" (slice S1.06 ruling A). Implemented by the access engine.
/// </summary>
public interface ICallerAccess
{
    /// <summary>True when a role the caller holds today grants the permission (in any company).</summary>
    ValueTask<bool> HoldsAsync(string permission, CancellationToken cancellationToken);

    /// <summary>
    /// True when the caller holds the permission through an assignment covering the company: one for that company or
    /// for all companies. A null company means "all companies", which only an all-companies assignment covers.
    /// </summary>
    ValueTask<bool> CoversCompanyAsync(string permission, Guid? companyId, CancellationToken cancellationToken);
}

/// <summary>
/// The access log of sensitive fields (FR-KRN-AUD-02, GHR-02, HRM-PRV-05; slice S1.07 Q6): one row per answer that showed
/// at least one salary, personal or restricted field unmasked. Written before the answer leaves the server; if it cannot
/// be written the request fails.
/// </summary>
public interface ISensitiveAccessLog
{
    /// <param name="resource">The table, for example <c>hrm.pay_results</c>.</param>
    /// <param name="entityId">The record for a single-record answer; null for a list.</param>
    /// <param name="fields">The sensitive field keys shown unmasked.</param>
    /// <param name="action"><c>view</c>, <c>export</c> or <c>print</c>.</param>
    /// <param name="rowCount">Rows in the answer.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RecordAsync(string resource, Guid? entityId, IReadOnlyList<string> fields, string action, int rowCount, CancellationToken cancellationToken);
}
