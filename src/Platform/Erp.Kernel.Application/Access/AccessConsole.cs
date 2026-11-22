namespace Erp.Kernel.Application.Access;

/// <summary>A person in a group: name and username only.</summary>
public sealed record GroupMemberLine(Guid Id, string DisplayName, string Username);

/// <summary>A user group with its members and its roles, ended ones included (slice S1.13b, FR-ADM-USR-06).</summary>
public sealed record GroupDetail(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    bool IsActive,
    long RowVersion,
    IReadOnlyList<GroupMemberLine> Members,
    IReadOnlyList<UserRoleLine> Roles);

/// <summary>One change of a role, an assignment or a group, from the audit log: field names only, never values (FR-ADM-ROL-11).</summary>
public sealed record PermissionChangeItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? ActorName,
    string EntityType,
    Guid EntityId,
    string Action,
    string? Source,
    IReadOnlyList<string> Fields,
    string? RoleCode);

public sealed record PermissionChangePage(IReadOnlyList<PermissionChangeItem> Items, string? Next);

public sealed record UnusedPermissionLine(string Code, string Module, string Resource, string Action, bool IsSensitive);

public sealed record InactiveUserLine(Guid Id, string DisplayName, string Username, string Status, DateTimeOffset? LastLoginAt);

/// <summary>Permissions no active user holds and active users without a recent sign-in (FR-ADM-ROL-11, ruling Q8).</summary>
public sealed record RoleUsageReport(int InactiveDays, IReadOnlyList<UnusedPermissionLine> UnusedPermissions, IReadOnlyList<InactiveUserLine> InactiveUsers);

/// <summary>The administrator's views over groups and the access reports (slice S1.13b, CR-069).</summary>
public interface IAccessConsoleService
{
    /// <summary>Null when the group does not exist.</summary>
    Task<GroupDetail?> GroupAsync(Guid groupId, CancellationToken cancellationToken);

    /// <summary>Newest first. Throws <see cref="ArgumentException"/> for a cursor it did not issue.</summary>
    Task<PermissionChangePage> PermissionChangesAsync(DateTimeOffset? since, DateTimeOffset? until, string? cursor, int limit, CancellationToken cancellationToken);

    Task<RoleUsageReport> RoleUsageAsync(CancellationToken cancellationToken);
}
