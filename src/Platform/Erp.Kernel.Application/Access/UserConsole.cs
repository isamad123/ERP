namespace Erp.Kernel.Application.Access;

/// <summary>A role as a chip in the user list: id, code and the names by language.</summary>
public sealed record RoleChip(Guid RoleId, string Code, IReadOnlyDictionary<string, string> Name);

/// <summary>One row of the administrator's user list (slice S1.13a, FR-ADM-USR-01).</summary>
public sealed record UserOverviewItem(
    Guid Id,
    string Username,
    string DisplayName,
    string Email,
    string Status,
    DateTimeOffset? LastLoginAt,
    Guid? DefaultCompanyId,
    DateOnly? AccessValidTo,
    bool HasMfa,
    IReadOnlyList<RoleChip> Roles,
    IReadOnlyList<Guid> CompanyIds,
    bool AllCompanies);

/// <summary>A page of the user list; <see cref="Next"/> is the cursor of the next page (null on the last).</summary>
public sealed record UserOverviewPage(IReadOnlyList<UserOverviewItem> Items, string? Next);

/// <summary>Filters of the user list. Every value is optional; <see cref="Status"/> is one of the five account statuses.</summary>
public sealed record UserOverviewQuery(string? Search, string? Status, Guid? RoleId, Guid? CompanyId, bool? HasMfa, string? Cursor, int Limit);

public sealed record UserRoleLine(
    Guid AssignmentId, Guid RoleId, string RoleCode, IReadOnlyDictionary<string, string> RoleName, Guid? CompanyId,
    DateOnly ValidFrom, DateOnly? ValidTo, string? Reason, bool InForce);

public sealed record UserGroupLine(Guid GroupId, string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyList<UserRoleLine> Roles);

public sealed record UserScopeLine(string Resource, Guid DataScopeId, string ScopeCode, string ScopeKind, IReadOnlyDictionary<string, string> ScopeName, string RoleCode, string Source);

public sealed record UserDelegationLine(Guid Id, Guid FromUserId, string FromName, Guid ToUserId, string ToName, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, string Status, string? Reason);

/// <summary>A second factor: method and dates only. The secret never leaves the server.</summary>
public sealed record UserMfaLine(Guid Id, string Kind, string? Label, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record UserDeviceLine(Guid Id, string? Label, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>Everything the user record shows beside the profile (FR-ADM-USR-04).</summary>
public sealed record UserAccessView(
    Guid UserId,
    IReadOnlyList<UserRoleLine> Roles,
    IReadOnlyList<UserGroupLine> Groups,
    IReadOnlyList<UserScopeLine> Scopes,
    IReadOnlyList<UserDelegationLine> Delegations,
    IReadOnlyList<UserMfaLine> MfaDevices,
    IReadOnlyList<UserDeviceLine> TrustedDevices);

/// <summary>A person in a picker: name, username and id only (slice S1.13 ruling Q7).</summary>
public sealed record PersonLookupItem(Guid Id, string DisplayName, string Username);

/// <summary>The administrator's views over users and the small people lookup (slice S1.13a).</summary>
public interface IUserConsoleService
{
    Task<UserOverviewPage> OverviewAsync(UserOverviewQuery query, CancellationToken cancellationToken);

    /// <summary>Null when the user does not exist.</summary>
    Task<UserAccessView?> AccessAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Active users of the caller's companies (all of them for a caller whose assignments cover every company), 20 at a time.</summary>
    Task<IReadOnlyList<PersonLookupItem>> PeopleAsync(string? search, CancellationToken cancellationToken);

    /// <summary>Ends one session of a user (FR-KRN-IAM-04). False when the user does not exist; an unknown session counts as already ended.</summary>
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, string? reason, CancellationToken cancellationToken);

    /// <summary>Removes one remembered browser of a user (FR-ADM-USR-24). False when the user does not exist.</summary>
    Task<bool> RemoveTrustedDeviceAsync(Guid userId, Guid deviceId, string? reason, CancellationToken cancellationToken);
}
