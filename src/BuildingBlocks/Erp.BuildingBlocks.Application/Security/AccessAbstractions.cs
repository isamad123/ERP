using System.Linq.Expressions;

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
    ValueTask<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query, ScopeTarget<T> target, string action, CancellationToken cancellationToken)
        where T : class;

    /// <summary>True when the row (as it is in memory) is inside the user's scope for the action.</summary>
    ValueTask<bool> AllowsAsync<T>(T row, ScopeTarget<T> target, string action, CancellationToken cancellationToken)
        where T : class;
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
    public ScopeTarget(string resource, IReadOnlyDictionary<ScopeDimension, Expression<Func<T, Guid?>>> locators, IReadOnlyDictionary<string, Expression<Func<T, string?>>> attributes)
    {
        Resource = resource;
        Locators = locators;
        Attributes = attributes;
    }

    /// <summary>Resource name, for example <c>hrm.leave_requests</c>; permissions are <c>resource.action</c>.</summary>
    public string Resource { get; }

    public IReadOnlyDictionary<ScopeDimension, Expression<Func<T, Guid?>>> Locators { get; }

    /// <summary>Text columns attribute conditions may test, by column name (Q5: "equals / is one of").</summary>
    public IReadOnlyDictionary<string, Expression<Func<T, string?>>> Attributes { get; }

    public bool IsScoped => Locators.Count > 0;
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
