namespace Erp.Kernel.Application.Access;

/// <summary>How an access-control request ended.</summary>
public enum AccessOutcome
{
    /// <summary>The change was applied.</summary>
    Done,

    /// <summary>The change waits for a second person (maker-checker, FR-KRN-IAM-14); <see cref="AccessResult.RequestId"/> names the request.</summary>
    Pending,

    NotFound,

    /// <summary>Refused for a stated reason (<see cref="AccessResult.Error"/>, a localization key).</summary>
    Invalid,

    /// <summary>The caller lacks a permission the change needs (for example granting one they do not hold).</summary>
    Forbidden,

    /// <summary>Someone else changed the record, or the change would overlap an existing one.</summary>
    Conflict,
}

/// <summary>The answer of an access-control service. <see cref="Id"/> is the created or changed record.</summary>
public sealed record AccessResult(AccessOutcome Outcome, string? Error = null, Guid? Id = null, Guid? RequestId = null)
{
    public static AccessResult Done(Guid? id = null) => new(AccessOutcome.Done, Id: id);

    public static AccessResult Pending(Guid requestId) => new(AccessOutcome.Pending, RequestId: requestId);

    public static AccessResult NotFound { get; } = new(AccessOutcome.NotFound, "errors.data.not_found");

    public static AccessResult Invalid(string error) => new(AccessOutcome.Invalid, error);

    public static AccessResult Forbidden(string error) => new(AccessOutcome.Forbidden, error);

    public static AccessResult Conflict(string error) => new(AccessOutcome.Conflict, error);
}

/// <summary>A new role: empty, or copied from another role or from a baseline role by its code (FR-ADM-ROL-02).</summary>
public sealed record CreateRole(string? Code, IReadOnlyDictionary<string, string>? Name, IReadOnlyDictionary<string, string>? Description, Guid? CopyFromRoleId, string? CopyFromBaseline);

/// <summary>One data scope of a role: the resource (or <c>*</c>) and the iam.data_scopes row; null scope removes it.</summary>
public sealed record RoleScopeChange(string Resource, Guid? DataScopeId);

/// <summary>One field rule of a role; null access removes the rule. Field is a column name, <c>custom.&lt;key&gt;</c> or <c>class:salary</c>.</summary>
public sealed record RoleFieldChange(string Resource, string Field, string? Access);

public sealed record RoleScopeView(string Resource, Guid DataScopeId, string Code, string Kind);

public sealed record RoleFieldView(string Resource, string Field, string Access);

/// <summary>A role with what it grants and how many users hold it today (directly or through a group).</summary>
public sealed record RoleSummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Source,
    string? BaselineCode,
    bool IsActive,
    long RowVersion,
    bool IsPrivileged,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<RoleScopeView> DataScopes,
    IReadOnlyList<RoleFieldView> FieldPolicies,
    int UserCount);

/// <summary>Two roles side by side (FR-ADM-ROL-02).</summary>
public sealed record RoleComparison(Guid Left, Guid Right, IReadOnlyList<string> OnlyLeft, IReadOnlyList<string> OnlyRight, IReadOnlyList<string> Both);

/// <summary>Roles: create, copy, rename, archive; set permissions, data scopes and field rules (FR-KRN-IAM-11, FR-ADM-ROL-02).</summary>
public interface IRoleService
{
    Task<AccessResult> CreateAsync(CreateRole request, CancellationToken cancellationToken);

    Task<AccessResult> RenameAsync(Guid roleId, IReadOnlyDictionary<string, string>? name, IReadOnlyDictionary<string, string>? description, long? rowVersion, CancellationToken cancellationToken);

    Task<AccessResult> ArchiveAsync(Guid roleId, string? reason, CancellationToken cancellationToken);

    /// <summary>Replaces the role's permissions with <paramref name="permissions"/> (codes).</summary>
    Task<AccessResult> SetPermissionsAsync(Guid roleId, IReadOnlyList<string>? permissions, string? reason, CancellationToken cancellationToken);

    Task<AccessResult> SetDataScopesAsync(Guid roleId, IReadOnlyList<RoleScopeChange>? changes, string? reason, CancellationToken cancellationToken);

    Task<AccessResult> SetFieldPoliciesAsync(Guid roleId, IReadOnlyList<RoleFieldChange>? changes, string? reason, CancellationToken cancellationToken);

    Task<RoleSummary?> SummaryAsync(Guid roleId, CancellationToken cancellationToken);

    Task<RoleComparison?> CompareAsync(Guid left, Guid right, CancellationToken cancellationToken);

    /// <summary>
    /// A new reusable data scope (FR-KRN-IAM-12, FR-ADM-ROL-06). Scopes are shared by every role that uses them, so they
    /// are created only, never edited in place: a role is repointed to another scope through its data scopes, where the
    /// grant rules and maker-checker apply (review S1.06 finding 2, CR-025).
    /// </summary>
    Task<AccessResult> CreateDataScopeAsync(string? code, IReadOnlyDictionary<string, string>? name, string? kind, System.Text.Json.JsonElement? definition, CancellationToken cancellationToken);
}

/// <summary>
/// The identity and access settings (keys <c>iam.*</c>): changed only here, never through the generated settings API,
/// so each change is recorded and switching off maker-checker or privileged MFA can need a second person (review S1.06
/// finding 5, CR-025).
/// </summary>
public interface IAccessSettingsService
{
    /// <summary>Sets one value; <paramref name="layer"/> defaults to tenant, <paramref name="scopeId"/> names the role or user of those layers.</summary>
    Task<AccessResult> SetAsync(string key, string? layer, Guid? scopeId, System.Text.Json.JsonElement? value, string? reason, CancellationToken cancellationToken);

    /// <summary>Sets one value as the revert of a history row (slice S1.09a, FR-ADM-SET-06).</summary>
    Task<AccessResult> SetAsync(string key, string? layer, Guid? scopeId, System.Text.Json.JsonElement? value, string? reason, Guid? revertedFrom, CancellationToken cancellationToken);

    /// <summary>Removes one layer's value, so the inherited value applies again (slice S1.09a, FR-ADM-SET-03).</summary>
    Task<AccessResult> ResetAsync(string key, string? layer, Guid? scopeId, string? reason, Guid? revertedFrom, CancellationToken cancellationToken);
}

/// <summary>Giving a role to a user or a group, per company or all companies, with dates and a reason (FR-ADM-ROL-04, FR-ADM-USR-06).</summary>
public interface IAssignmentService
{
    Task<AccessResult> AssignAsync(Guid? userId, Guid? roleId, Guid? companyId, DateOnly? validFrom, DateOnly? validTo, string? reason, CancellationToken cancellationToken);

    /// <summary>Ends the assignment at the start of <paramref name="endDate"/> (today when null).</summary>
    Task<AccessResult> EndAsync(Guid assignmentId, DateOnly? endDate, string? reason, CancellationToken cancellationToken);

    Task<AccessResult> CreateGroupAsync(string? code, IReadOnlyDictionary<string, string>? name, CancellationToken cancellationToken);

    Task<AccessResult> RenameGroupAsync(Guid groupId, IReadOnlyDictionary<string, string>? name, long? rowVersion, CancellationToken cancellationToken);

    Task<AccessResult> ArchiveGroupAsync(Guid groupId, string? reason, CancellationToken cancellationToken);

    Task<AccessResult> AddMemberAsync(Guid groupId, Guid? userId, CancellationToken cancellationToken);

    Task<AccessResult> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken);

    Task<AccessResult> AssignGroupAsync(Guid groupId, Guid? roleId, Guid? companyId, DateOnly? validFrom, DateOnly? validTo, string? reason, CancellationToken cancellationToken);

    Task<AccessResult> EndGroupRoleAsync(Guid groupRoleId, DateOnly? endDate, string? reason, CancellationToken cancellationToken);
}

/// <summary>Approve or reject a change held for a second person (FR-KRN-IAM-14).</summary>
public interface IMakerCheckerService
{
    Task<AccessResult> ApproveAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<AccessResult> RejectAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Pending requests for the inbox's "approvals of changes" (slice S1.12d Build item 22, ruling Q15): those of other
    /// people the caller may be asked to decide (holders of <c>iam.maker_checker.approve</c>), each with whether approving
    /// would pass the checks of <see cref="ApproveAsync"/> now, and the caller's own requests (never decided by them).
    /// </summary>
    Task<PendingChanges> PendingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One line of "what will change" (slice S1.12d): <see cref="Label"/> is a localization key; the value is
/// <see cref="Text"/> (plain text: a code, an e-mail address, a JSON value), <see cref="Date"/>, <see cref="Names"/> (a
/// name in each language) or <see cref="Items"/> (codes such as permission codes), as <see cref="Type"/> says
/// (<c>text</c>, <c>date</c>, <c>names</c>, <c>items</c>, <c>key</c>: <see cref="Text"/> is a localization key).
/// </summary>
public sealed record ChangeDetail(string Label, string Type, string? Text = null, DateOnly? Date = null, IReadOnlyDictionary<string, string>? Names = null, IReadOnlyList<string>? Items = null);

/// <summary>
/// A pending maker-checker request in plain words: its kind (<c>iam.role.set_permissions</c>, <c>cfg.setting.change</c>,
/// ...), who asked and when, until when it can be decided, what it changes (<see cref="Subject"/>: the role, user, group,
/// setting or route, by name in each language) and the details. <see cref="CanApprove"/> is false, with the refusal's key
/// in <see cref="ApproveProblem"/>, when approving would be refused now; rejecting needs only a reason.
/// </summary>
public sealed record ChangeRequestView(
    Guid Id,
    string Kind,
    Guid RequestedBy,
    string? RequestedByName,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? Reason,
    IReadOnlyDictionary<string, string>? Subject,
    IReadOnlyList<ChangeDetail> Details,
    bool CanApprove,
    string? ApproveProblem);

/// <summary>The pending requests the caller may decide, oldest first, and the caller's own pending requests.</summary>
public sealed record PendingChanges(IReadOnlyList<ChangeRequestView> ToDecide, IReadOnlyList<ChangeRequestView> Mine);

public sealed record CatalogEntry(string Code, string Module, string Resource, string Action, string DescriptionKey, bool IsSensitive, IReadOnlyList<string> Roles);

/// <summary>The records of a resource a user would see: how many, and the ids of the first page (FR-ADM-ROL-06).</summary>
public sealed record ScopePreview(string Resource, Guid UserId, long Count, IReadOnlyList<Guid> FirstPage);

/// <summary>One field of a resource for a role. <paramref name="Label"/> (S1.15b, additive): the name of a custom field in both languages.</summary>
public sealed record FieldPreviewItem(string Field, string? FieldClass, string Access, IReadOnlyDictionary<string, string>? Label = null);

/// <summary>The fields of a resource as one role sees them (FR-ADM-ROL-07).</summary>
public sealed record FieldPreview(string Resource, Guid RoleId, IReadOnlyList<FieldPreviewItem> Fields);

/// <summary>The permission catalogue (FR-ADM-ROL-01) and the previews of the data-scope and field-security designers.</summary>
public interface IAccessPreviewService
{
    Task<IReadOnlyList<CatalogEntry>> CatalogAsync(CancellationToken cancellationToken);

    /// <summary>Null when the user or the resource does not exist.</summary>
    Task<ScopePreview?> DataScopeAsync(Guid userId, string resource, int limit, CancellationToken cancellationToken);

    /// <summary>Null when the role or the resource does not exist.</summary>
    Task<FieldPreview?> FieldsAsync(Guid roleId, string resource, CancellationToken cancellationToken);
}
