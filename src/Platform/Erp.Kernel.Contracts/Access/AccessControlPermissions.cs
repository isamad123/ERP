using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Access;

/// <summary>
/// Permissions of the access-control services of slice S1.06 (roles, assignments, groups, maker-checker, previews),
/// registered into iam.permissions with the generated catalogue. They replace the generated write endpoints of the
/// access tables that CR-020 and CR-021 closed.
/// </summary>
public static class AccessControlPermissions
{
    /// <summary>Create, copy, rename and archive roles; set their permissions, data scopes and field rules.</summary>
    public const string ManageRoles = "iam.roles.manage";

    /// <summary>Give a role to a user, or end that assignment.</summary>
    public const string AssignRoles = "iam.roles.assign";

    /// <summary>Create, rename and archive user groups; add and remove members; give roles to a group.</summary>
    public const string ManageGroups = "iam.user_groups.manage";

    /// <summary>Approve or reject a change held by maker-checker (FR-KRN-IAM-14).</summary>
    public const string ApproveChanges = "iam.maker_checker.approve";

    /// <summary>See which records a user would see and the fields a role sees (FR-ADM-ROL-06, FR-ADM-ROL-07); sensitive because it reveals record ids.</summary>
    public const string Preview = "iam.access.preview";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(ManageRoles, "iam", "permissions.iam.roles.manage", true),
        new(AssignRoles, "iam", "permissions.iam.roles.assign", true),
        new(ManageGroups, "iam", "permissions.iam.user_groups.manage", true),
        new(ApproveChanges, "iam", "permissions.iam.maker_checker.approve", true),
        new(Preview, "iam", "permissions.iam.access.preview", true),
    ];
}

/// <summary>Event types raised by the access-control services into iam.outbox_messages (recorded in the audit log by S1.07).</summary>
public static class AccessEvents
{
    public const string RoleCreated = "iam.role_created";
    public const string RoleRenamed = "iam.role_renamed";
    public const string RoleArchived = "iam.role_archived";
    public const string RolePermissionsChanged = "iam.role_permissions_changed";
    public const string RoleDataScopesChanged = "iam.role_data_scopes_changed";
    public const string RoleFieldPoliciesChanged = "iam.role_field_policies_changed";
    public const string RoleAssigned = "iam.role_assigned";
    public const string RoleAssignmentEnded = "iam.role_assignment_ended";
    public const string GroupCreated = "iam.user_group_created";
    public const string GroupRenamed = "iam.user_group_renamed";
    public const string GroupArchived = "iam.user_group_archived";
    public const string GroupMemberAdded = "iam.user_group_member_added";
    public const string GroupMemberRemoved = "iam.user_group_member_removed";
    public const string GroupRoleAssigned = "iam.user_group_role_assigned";
    public const string GroupRoleEnded = "iam.user_group_role_ended";
    public const string ChangeRequested = "iam.maker_checker_requested";
    public const string ChangeApproved = "iam.maker_checker_approved";
    public const string ChangeRejected = "iam.maker_checker_rejected";
    public const string ChangeExpired = "iam.maker_checker_expired";
    public const string PermissionsRegistered = "iam.permissions_registered";

    // Platform Super Admin recovery and override (CR-024); payload names the platform user and the reason.
    public const string PlatformAdminRoleRepaired = "iam.platform_admin_role_repaired";
    public const string PlatformAdminGranted = "iam.platform_admin_granted";
    public const string PlatformAdminRevoked = "iam.platform_admin_revoked";
}
