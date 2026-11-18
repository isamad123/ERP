using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Identity;

/// <summary>
/// Permissions of the identity endpoints (slice S1.05 build items 6 and 7), registered into iam.permissions with the
/// generated catalogue by S1.06. Until then the permission checker grants nothing and these endpoints answer 403.
/// </summary>
public static class IdentityPermissions
{
    public const string SelfManage = "iam.self.manage";
    public const string Unlock = "iam.users.unlock";
    public const string ForcePasswordChange = "iam.users.force_password_change";
    public const string ResetMfa = "iam.users.reset_mfa";
    public const string RevokeSessions = "iam.users.revoke_sessions";
    public const string ChangeStatus = "iam.users.change_status";
    public const string ReadSessions = "iam.users.read_sessions";

    /// <summary>Send or send again an invited person's activation e-mail (slice S1.11b2, ADM-USR-02).</summary>
    public const string SendInvitation = "iam.users.send_invitation";

    /// <summary>Send a person a password reset link (slice S1.11b2, ruling Q16).</summary>
    public const string SendPasswordReset = "iam.users.send_password_reset";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(SelfManage, "iam", "permissions.iam.self.manage", false),
        new(Unlock, "iam", "permissions.iam.users.unlock", true),
        new(ForcePasswordChange, "iam", "permissions.iam.users.force_password_change", true),
        new(ResetMfa, "iam", "permissions.iam.users.reset_mfa", true),
        new(RevokeSessions, "iam", "permissions.iam.users.revoke_sessions", true),
        new(ChangeStatus, "iam", "permissions.iam.users.change_status", true),
        new(ReadSessions, "iam", "permissions.iam.users.read_sessions", true),
        new(SendInvitation, "iam", "permissions.iam.users.send_invitation", true),
        new(SendPasswordReset, "iam", "permissions.iam.users.send_password_reset", true),
    ];
}
