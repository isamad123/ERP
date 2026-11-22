using System.Data.Common;

namespace Erp.Kernel.Contracts.Identity;

/// <summary>
/// What a module may ask about the customer's users (slice S1.20b, FR-HRM-EMP-17): the kernel owns <c>iam.users</c> and modules may
/// not read it (D-10), so the HR module asks here before it links an employment to a user. Ids only.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// True when the user exists, is not deleted and is not a system user (invited, active and locked users may be linked), read
    /// on the caller's transaction when given.
    /// </summary>
    Task<bool> IsLinkableAsync(Guid userId, DbTransaction? transaction, CancellationToken cancellationToken);
}
