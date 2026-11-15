namespace Erp.Kernel.Infrastructure.ControlPlane;

/// <summary>Server settings of the control plane and of customer-database connections (S1.03).</summary>
public sealed class ControlPlaneOptions
{
    /// <summary>Connection string of the control-plane database (from server settings, never the repository).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Statement timeout set on every customer-database connection (FR-KRN-TEN-03). Default 30 seconds.</summary>
    public int StatementTimeoutSeconds { get; init; } = 30;

    /// <summary>How long a host-name lookup is cached, so a suspension takes effect within this time. Default 30 seconds.</summary>
    public int HostCacheSeconds { get; init; } = 30;

    /// <summary>Connection pool size per customer database and process. Default 20.</summary>
    public int MaxPoolSizePerTenant { get; init; } = 20;
}

/// <summary>The database logins of one customer (CR-004): each is a member of the spec 03 section 3.7 role of the same purpose.</summary>
public static class TenantLogins
{
    public const string Owner = "owner";
    public const string ReadWrite = "rw";
    public const string ReadOnly = "ro";
    public const string Audit = "audit";
    public const string Backup = "backup";

    /// <summary>Spec 03 section 3.7 roles without login. app_owner is not a shared role: each customer's owner login owns its own database.</summary>
    public static readonly IReadOnlyList<string> GroupRoles = ["app_rw", "app_ro", "app_audit", "backup"];

    public static readonly IReadOnlyList<(string Kind, string? GroupRole)> All =
    [
        (Owner, null), (ReadWrite, "app_rw"), (ReadOnly, "app_ro"), (Audit, "app_audit"), (Backup, "backup"),
    ];

    /// <summary>Secret reference of one login: <c>{app_secret_ref}/{kind}</c>.</summary>
    public static string SecretRef(string appSecretRef, string kind) => appSecretRef + "/" + kind;

    public static string DatabaseName(string tenantCode) => "erp_t_" + tenantCode;

    public static string LoginName(string tenantCode, string kind) => "erp_t_" + tenantCode + "_" + kind;
}
