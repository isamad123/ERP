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
/// Restricts a query to the rows the current user may see (data scopes: own, team, subtree, company; KRN-IAM-12).
/// Implemented in S1.06. Generated repositories pass every read through it.
/// </summary>
public interface IDataScopeFilter
{
    IQueryable<T> Apply<T>(IQueryable<T> query, string resource)
        where T : class;
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
