using System.Text.Json;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Access;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// Who an approver specification names for one request (KRN-WFL-02; slice S1.10 ruling Q4): a named user, the holders of a
/// role (a queue), a user field of the document, or the organisation directory's answer (manager, department head,
/// position holders). <see cref="RoleId"/> is set for a role queue; <see cref="People"/> are the active users found
/// (for a role: its holders covering the request's company).
/// </summary>
internal sealed record ApproverTarget(IReadOnlyList<Guid> People, Guid? RoleId)
{
    public static ApproverTarget Nobody { get; } = new([], null);

    public bool IsEmpty => People.Count == 0;
}

/// <summary>
/// Resolution of approvers and the questions the engine asks about people (rulings Q4, Q6, R1). Everything is read on
/// the work's own transaction, as of the business date of the injected clock.
/// </summary>
internal sealed class ApproverResolution(IApproverDirectory directory)
{
    public IApproverDirectory Directory => directory;

    public static DateOnly Today(WorkflowWork work) => DateOnly.FromDateTime(work.Now.UtcDateTime);

    /// <summary>The person the document is about (the first subject), else the requester: whose manager is meant.</summary>
    public static Guid AboutPerson(InstanceRow instance) => instance.SubjectUserIds.Length > 0 ? instance.SubjectUserIds[0] : instance.RequesterUserId;

    private static DateOnly DocumentDate(WorkflowDocumentType type, IReadOnlyDictionary<string, JsonElement> values, DateOnly today) =>
        type.SubjectDateField is { } field && values.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.String && DateOnly.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var date)
            ? date : today;

    public async Task<ApproverTarget> ResolveAsync(WorkflowWork work, ApproverSpec spec, InstanceRow instance, WorkflowDocumentType type, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct)
    {
        var today = Today(work);
        switch (spec.Kind)
        {
            case ApproverKinds.User:
                return await ActiveUserAsync(work, spec.UserId!.Value, ct).ConfigureAwait(false) ? new([spec.UserId.Value], null) : ApproverTarget.Nobody;
            case ApproverKinds.Role:
                if (await RoleIdAsync(work, spec.Role!, ct).ConfigureAwait(false) is not { } roleId)
                {
                    return ApproverTarget.Nobody;
                }

                return new(await RoleHoldersAsync(work, roleId, instance.CompanyId, today, ct).ConfigureAwait(false), roleId);
            case ApproverKinds.DocumentUser:
                return values.TryGetValue(spec.Field!, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id)
                    && await ActiveUserAsync(work, id, ct).ConfigureAwait(false) ? new([id], null) : ApproverTarget.Nobody;
            case ApproverKinds.Manager when directory.Supports(DirectoryKinds.Manager) && type.SubjectEmploymentField is { } employmentField:
                // R-305: the manager of the document's subject employment as of the document date; a subject we cannot name has no
                // manager (the route's fallback applies), never the requester's manager.
                return values.TryGetValue(employmentField, out var subject) && subject.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(subject.GetString())
                    ? new(await ActiveAsync(work, await directory.ManagersOfSubjectAsync(subject.GetString()!, spec.Levels, DocumentDate(type, values, today), today, ct).ConfigureAwait(false), ct).ConfigureAwait(false), null)
                    : ApproverTarget.Nobody;
            case ApproverKinds.Manager when directory.Supports(DirectoryKinds.Manager):
                return new(await ActiveAsync(work, await directory.ManagersAsync(AboutPerson(instance), spec.Levels, today, ct).ConfigureAwait(false), ct).ConfigureAwait(false), null);
            case ApproverKinds.DepartmentHead when directory.Supports(DirectoryKinds.DepartmentHead):
                return new(await ActiveAsync(work, await directory.DepartmentHeadsAsync(AboutPerson(instance), instance.CompanyId, today, ct).ConfigureAwait(false), ct).ConfigureAwait(false), null);
            case ApproverKinds.Position when directory.Supports(DirectoryKinds.Position):
                return new(await ActiveAsync(work, await directory.PositionHoldersAsync(spec.Position!, instance.CompanyId, today, ct).ConfigureAwait(false), ct).ConfigureAwait(false), null);
            default:
                return ApproverTarget.Nobody;
        }
    }

    /// <summary>The line managers of the given people who are not themselves excluded (ruling Q6: one level up).</summary>
    public async Task<List<Guid>> ManagersOfAsync(WorkflowWork work, IEnumerable<Guid> people, CancellationToken ct)
    {
        var result = new List<Guid>();
        if (!directory.Supports(DirectoryKinds.Manager))
        {
            return result;
        }

        foreach (var person in people)
        {
            result.AddRange(await directory.ManagersAsync(person, 1, Today(work), ct).ConfigureAwait(false));
        }

        return await ActiveAsync(work, result.Distinct(), ct).ConfigureAwait(false);
    }

    public static Task<Guid?> RoleIdAsync(WorkflowWork work, string code, CancellationToken ct) =>
        work.ScalarAsync<Guid?>("select id from iam.roles where lower(code) = lower($1) and not is_deleted and is_active", ct, code);

    public static Task<bool> ActiveUserAsync(WorkflowWork work, Guid userId, CancellationToken ct) =>
        work.ScalarAsync<bool>("select exists (select 1 from iam.users where id = $1 and status = 'active' and not is_deleted and not is_system)", ct, userId);

    private static async Task<List<Guid>> ActiveAsync(WorkflowWork work, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        await using var command = work.Command("select id from iam.users where id = any($1) and status = 'active' and not is_deleted and not is_system order by id", (object)wanted);
        return await AccessDb.IdsAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Active users holding the role today through their own or their groups' assignments, for the company or for all
    /// companies (ruling Q4); <paramref name="companyId"/> null means any company (the activation check).
    /// </summary>
    public static async Task<List<Guid>> RoleHoldersAsync(WorkflowWork work, Guid roleId, Guid? companyId, DateOnly today, CancellationToken ct)
    {
        await using var command = work.Command("""
            select u.id from iam.users u
            where u.status = 'active' and not u.is_deleted and not u.is_system and (
                exists (select 1 from iam.user_roles ur
                        where ur.user_id = u.id and ur.role_id = $1 and ur.valid_from <= $3 and (ur.valid_to is null or ur.valid_to > $3)
                          and ($2::uuid is null or ur.company_id is null or ur.company_id = $2))
                or exists (select 1 from iam.user_group_roles gr
                           join iam.user_group_members m on m.group_id = gr.group_id and m.user_id = u.id
                           join iam.user_groups g on g.id = gr.group_id and not g.is_deleted and g.is_active
                           where gr.role_id = $1 and gr.valid_from <= $3 and (gr.valid_to is null or gr.valid_to > $3)
                             and ($2::uuid is null or gr.company_id is null or gr.company_id = $2)))
            order by u.id
            """, roleId, companyId, today);
        return await AccessDb.IdsAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the user may decide requests of a document type in at least one company today: they hold
    /// <c>wf.workflow_tasks.update</c> and, when the type has one, its own decide permission, for a common company (an
    /// all-companies grant covers every company). The activation check of review S1.10a finding 2 (CR-059).
    /// </summary>
    public static async Task<bool> CanDecideSomewhereAsync(WorkflowWork work, Guid userId, string? decidePermission, CancellationToken ct)
    {
        var snapshot = await AccessSnapshot.LoadAsync(work.Connection, work.Transaction, userId, Today(work), ct).ConfigureAwait(false);
        var decide = snapshot.Granting(Contracts.Workflow.WorkflowPermissions.TasksDecide).Select(g => g.Assignment.CompanyId).ToHashSet();
        if (decide.Count == 0 || decidePermission is null)
        {
            return decide.Count > 0;
        }

        var own = snapshot.Granting(decidePermission).Select(g => g.Assignment.CompanyId).ToHashSet();
        return own.Count > 0 && (decide.Contains(null) || own.Contains(null) || decide.Overlaps(own));
    }

    /// <summary>Whether the role's own rules grant every one of the permissions (not retired).</summary>
    public static async Task<bool> RoleGrantsAsync(WorkflowWork work, Guid roleId, string[] permissions, CancellationToken ct) =>
        await work.ScalarAsync<long>("""
            select count(distinct p.code) from iam.role_permissions rp
            join iam.permissions p on p.id = rp.permission_id and p.retired_at is null
            where rp.role_id = $1 and p.code = any($2)
            """, ct, roleId, permissions.Distinct(StringComparer.Ordinal).ToArray()).ConfigureAwait(false) == permissions.Distinct(StringComparer.Ordinal).Count();

    /// <summary>Whether a user holds a permission today for any company.</summary>
    public static async Task<bool> HoldsAnyAsync(WorkflowWork work, Guid userId, string permission, CancellationToken ct) =>
        (await AccessSnapshot.LoadAsync(work.Connection, work.Transaction, userId, Today(work), ct).ConfigureAwait(false)).Holds(permission);

    /// <summary>Whether a user (not necessarily the caller) holds a permission for the company today, by the access engine's rules.</summary>
    public static async Task<bool> HoldsAsync(WorkflowWork work, Guid userId, string permission, Guid companyId, CancellationToken ct)
    {
        var snapshot = await AccessSnapshot.LoadAsync(work.Connection, work.Transaction, userId, Today(work), ct).ConfigureAwait(false);
        return snapshot.Granting(permission).Any(g => g.Assignment.CompanyId is null || g.Assignment.CompanyId == companyId);
    }
}
