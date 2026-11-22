using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>Applying changes, assignments, groups and maker-checker (slice S1.06 build items 4, 8 and 9).</summary>
public sealed partial class AccessAdministrationService
{
    // ================================================================== maker-checker (FR-KRN-IAM-14)

    /// <summary>Stores the change as a pending request instead of applying it (ruling Q6).</summary>
    private static async Task<AccessResult> SubmitAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var (type, subject) = change.Subject();
        var id = Guid.CreateVersion7(now);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.maker_checker_requests (id, action_type, subject_type, subject_id, payload, requested_by, status, reason, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, 'pending', $7, $8, $6, $8, $6)
            """, ct, id, change.Kind, type, subject, new JsonPayload(change.ToJson()), by, change.Reason, now).ConfigureAwait(false);
        await AccessDb.RaiseAsync(c, tx, AccessEvents.ChangeRequested, "iam.maker_checker_request", id, by, now, new { action = change.Kind, subject_type = type, subject_id = subject }, ct).ConfigureAwait(false);
        return AccessResult.Pending(id);
    }

    /// <summary>Holds a change built by another kernel service for a second person (slice S1.10: route activation).</summary>
    internal static Task<AccessResult> SubmitChangeAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct) =>
        SubmitAsync(c, tx, change, now, by, ct);

    public async Task<AccessResult> ApproveAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        await ExpireOldRequestsAsync(cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(AccessControlPermissions.ApproveChanges, async (c, tx, now, by) =>
        {
            if (await LockRequestAsync(c, tx, requestId, cancellationToken).ConfigureAwait(false) is not { } request)
            {
                return AccessResult.NotFound;
            }

            if (request.Status != "pending")
            {
                return AccessResult.Invalid(request.Status == "expired" ? Expired : NotPending);
            }

            if (request.RequestedBy == by)
            {
                return AccessResult.Forbidden(OwnRequest); // nobody approves their own request (FR-XM-32; also a database check)
            }

            AccessChange change;
            try
            {
                change = AccessChange.FromJson(request.Payload);
            }
            catch (System.Text.Json.JsonException)
            {
                return AccessResult.Invalid("errors.data.invalid_request"); // a request not written by these services
            }

            if (await ValidateAsync(c, tx, change, cancellationToken).ConfigureAwait(false) is { } invalid)
            {
                return invalid;
            }

            // The approver needs every permission the change grants, like the requester (ruling Q6).
            if (await CheckGrantsAsync(c, tx, change, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            if (await ApproverProblemAsync(c, tx, change, by, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }

            var applied = change.Kind == AccessChange.ChangeUserEmail
                ? await IdentityEmailService.ApplyHeldEmailChangeAsync(c, tx, services.GetRequiredService<Notifications.NotificationCatalog>(), change.UserId!.Value, change.Email!,
                    change.PreviousEmail!, change.Reason, now, by, cancellationToken).ConfigureAwait(false)
                : await ApplyAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
            if (applied.Outcome != AccessOutcome.Done)
            {
                return applied;
            }

            await IdentityDb.ExecuteAsync(c, tx, """
                update iam.maker_checker_requests set status = 'approved', decided_by = $2, decided_at = $3, reason = $4, updated_at = $3, updated_by = $2,
                    row_version = row_version + 1 where id = $1
                """, cancellationToken, requestId, by, now, reason).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.ChangeApproved, "iam.maker_checker_request", requestId, by, now, new { action = change.Kind, requested_by = request.RequestedBy, reason }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(applied.Id ?? requestId);
        }, cancellationToken).ConfigureAwait(false);
        SettingsChanged(result);
        return result;
    }

    public async Task<AccessResult> RejectAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        await ExpireOldRequestsAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(AccessControlPermissions.ApproveChanges, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            if (await LockRequestAsync(c, tx, requestId, cancellationToken).ConfigureAwait(false) is not { } request)
            {
                return AccessResult.NotFound;
            }

            if (request.Status != "pending")
            {
                return AccessResult.Invalid(request.Status == "expired" ? Expired : NotPending);
            }

            if (request.RequestedBy == by)
            {
                return AccessResult.Forbidden(OwnRequest);
            }

            await IdentityDb.ExecuteAsync(c, tx, """
                update iam.maker_checker_requests set status = 'rejected', decided_by = $2, decided_at = $3, reason = $4, updated_at = $3, updated_by = $2,
                    row_version = row_version + 1 where id = $1
                """, cancellationToken, requestId, by, now, reason).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.ChangeRejected, "iam.maker_checker_request", requestId, by, now, new { action = request.ActionType, requested_by = request.RequestedBy, reason }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(requestId);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Review S1.06 finding 6 (CR-025): an approver of an assignment change must cover its company, like the requester
    /// (ruling A), and nobody approves a change that gives themselves a role (FR-XM-32: no approval of one's own gain).
    /// </summary>
    private async Task<AccessResult?> ApproverProblemAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, Guid approver, CancellationToken ct)
    {
        if (change.Kind is AccessChange.Assign or AccessChange.AssignGroup or AccessChange.EndAssignment or AccessChange.EndGroupRole
            && !(await caller.CoversCompanyAsync(AccessControlPermissions.ApproveChanges, change.CompanyId, ct).ConfigureAwait(false)
                 && await caller.CoversCompanyAsync(AccessControlPermissions.AssignRoles, change.CompanyId, ct).ConfigureAwait(false)))
        {
            return AccessResult.Forbidden(CompanyNotCovered);
        }

        // A held setting change (slice S1.09a): the approver must be allowed to make the change themselves, for the company
        // of a company-layer value or for all companies otherwise (ruling Q16).
        if (change.Kind is AccessChange.ChangeSetting
            && !await caller.CoversCompanyAsync(Contracts.Settings.SettingsPermissions.Update, change.SettingLayer == "company" ? change.SettingScopeId : null, ct).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(CompanyNotCovered);
        }

        // A held route activation (slice S1.10 ruling Q10): the approver must be allowed to activate routes themselves.
        if (change.Kind is AccessChange.ActivateWorkflowVersion
            && !await caller.CoversCompanyAsync(Contracts.Workflow.WorkflowPermissions.RoutesUpdate, null, ct).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(Forbidden);
        }

        // A held bank account change (slice S1.14a): the approver may make the change themselves for that company.
        if (change.Kind is AccessChange.SaveBankAccount or AccessChange.DeleteBankAccount && change.BankAccount is { } bank)
        {
            var permission = change.Kind == AccessChange.DeleteBankAccount ? "org.company_bank_accounts.delete" : bank.Operation == "create" ? "org.company_bank_accounts.create" : "org.company_bank_accounts.update";
            if (!await caller.CoversCompanyAsync(permission, bank.CompanyId, ct).ConfigureAwait(false))
            {
                return AccessResult.Forbidden(CompanyNotCovered);
            }
        }

        // A held e-mail change (S1.11b2 review finding 2, CR-062): the approver may change addresses themselves.
        if (change.Kind is AccessChange.ChangeUserEmail && !await permissions.HasPermissionAsync(Contracts.Identity.IdentityPermissions.ChangeEmail, ct).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(Forbidden);
        }

        var ownGain = change.Kind switch
        {
            AccessChange.Assign or AccessChange.AddMember or AccessChange.ChangeUserEmail => change.UserId == approver,
            AccessChange.AssignGroup => await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.user_group_members where group_id = $1 and user_id = $2)", ct, change.GroupId, approver).ConfigureAwait(false),
            _ => false,
        };
        return ownGain ? AccessResult.Forbidden(OwnGain) : null;
    }

    private sealed record PendingRequest(string ActionType, string Status, Guid RequestedBy, string Payload);

    private static async Task<PendingRequest?> LockRequestAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, "select action_type, status, requested_by, payload::text from iam.maker_checker_requests where id = $1 for update", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new PendingRequest(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3))
            : null;
    }

    /// <summary>
    /// Pending requests older than <c>iam.maker_checker.expiry_days</c> (default 7) become expired and apply nothing. Run
    /// before every decision in its own transaction, so an expiry is recorded even when the decision is refused.
    /// </summary>
    private Task<int> ExpireOldRequestsAsync(CancellationToken ct) => ExpirePendingAsync(ct);

    /// <summary>
    /// The expiry above as its own step (slice S1.11 Build item 5): the daily job <c>kernel.maker_checker_expire</c> calls it,
    /// so requests nobody touches also expire on time. Returns the number of requests that expired.
    /// </summary>
    public async Task<int> ExpirePendingAsync(CancellationToken ct)
    {
        var expired = 0;
        var now = clock.UtcNow;
        var by = actor.UserId;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var days = await AccessDb.SettingIntAsync(c, tx, IdentitySettings.MakerCheckerExpiryDays, 7, ct).ConfigureAwait(false);
        await using (var command = IdentityDb.Command(c, tx, """
            update iam.maker_checker_requests set status = 'expired', updated_at = $1, updated_by = $2, row_version = row_version + 1
            where status = 'pending' and created_at <= $3
            returning id
            """, now, by, now.AddDays(-days)))
        {
            foreach (var id in await AccessDb.IdsAsync(command, ct).ConfigureAwait(false))
            {
                await AccessDb.RaiseAsync(c, tx, AccessEvents.ChangeExpired, "iam.maker_checker_request", id, by, now, new { expiry_days = days }, ct).ConfigureAwait(false);
                expired++;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return expired;
    }

    // ================================================================== applying a change

    private static async Task<AccessResult> ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct) =>
        change.Kind switch
        {
            AccessChange.SetPermissions => await ApplyPermissionsAsync(c, tx, change, now, by, ct).ConfigureAwait(false),
            AccessChange.SetDataScopes => await ApplyScopesAsync(c, tx, change, now, by, ct).ConfigureAwait(false),
            AccessChange.SetFieldPolicies => await ApplyFieldsAsync(c, tx, change, now, by, ct).ConfigureAwait(false),
            AccessChange.Assign => await ApplyAssignAsync(c, tx, change, now, by, group: false, ct).ConfigureAwait(false),
            AccessChange.AssignGroup => await ApplyAssignAsync(c, tx, change, now, by, group: true, ct).ConfigureAwait(false),
            AccessChange.EndAssignment => await ApplyEndAsync(c, tx, change, now, by, group: false, ct).ConfigureAwait(false),
            AccessChange.EndGroupRole => await ApplyEndAsync(c, tx, change, now, by, group: true, ct).ConfigureAwait(false),
            AccessChange.AddMember => await ApplyAddMemberAsync(c, tx, change, now, by, ct).ConfigureAwait(false),
            AccessChange.RemoveMember => await ApplyRemoveMemberAsync(c, tx, change, now, by, ct).ConfigureAwait(false),
            AccessChange.SetSetting or AccessChange.ChangeSetting => await ApplySettingAsync(c, tx, change, now, by, ct).ConfigureAwait(false),
            AccessChange.SaveBankAccount or AccessChange.DeleteBankAccount when change.BankAccount is { } bank =>
                await Org.OrganizationAdministrationService.ApplyAsync(c, tx, bank, now, by, ct).ConfigureAwait(false),
            AccessChange.ActivateWorkflowVersion => AccessResult.Done(await Workflow.WorkflowAdministrationService.ApplyHeldActivationAsync(c, tx, change.WorkflowVersionId!.Value, now, by, ct).ConfigureAwait(false)),
            _ => AccessResult.Invalid("errors.data.invalid_request"),
        };

    private static async Task<AccessResult> ApplyPermissionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var roleId = change.RoleId!.Value;
        if (await AccessDb.LockRoleAsync(c, tx, roleId, ct).ConfigureAwait(false) is not { } role)
        {
            return AccessResult.NotFound;
        }

        var current = await AccessDb.RolePermissionsAsync(c, tx, roleId, ct).ConfigureAwait(false);
        var (addedList, removedList) = change.PermissionDelta(current);
        var added = addedList.ToArray();
        var removed = removedList.ToArray();
        await IdentityDb.ExecuteAsync(c, tx, """
            delete from iam.role_permissions rp using iam.permissions p where rp.permission_id = p.id and rp.role_id = $1 and p.code = any($2)
            """, ct, roleId, removed).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2) and retired_at is null
            on conflict do nothing
            """, ct, roleId, added).ConfigureAwait(false);
        await AccessDb.TouchRoleAsync(c, tx, roleId, by, now, ct).ConfigureAwait(false);
        await AccessDb.RaiseAsync(c, tx, AccessEvents.RolePermissionsChanged, "iam.role", roleId, by, now, new { code = role.Code, added, removed, reason = change.Reason }, ct).ConfigureAwait(false);
        return AccessResult.Done(roleId);
    }

    private static async Task<AccessResult> ApplyScopesAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var roleId = change.RoleId!.Value;
        if (await AccessDb.LockRoleAsync(c, tx, roleId, ct).ConfigureAwait(false) is not { } role)
        {
            return AccessResult.NotFound;
        }

        foreach (var scope in change.Scopes!)
        {
            if (scope.DataScopeId is { } id)
            {
                await IdentityDb.ExecuteAsync(c, tx, """
                    insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) values ($1, $2, $3)
                    on conflict (role_id, resource_type) do update set data_scope_id = excluded.data_scope_id
                    """, ct, roleId, scope.Resource, id).ConfigureAwait(false);
            }
            else
            {
                await IdentityDb.ExecuteAsync(c, tx, "delete from iam.role_data_scopes where role_id = $1 and resource_type = $2", ct, roleId, scope.Resource).ConfigureAwait(false);
            }
        }

        await AccessDb.TouchRoleAsync(c, tx, roleId, by, now, ct).ConfigureAwait(false);
        await AccessDb.RaiseAsync(c, tx, AccessEvents.RoleDataScopesChanged, "iam.role", roleId, by, now, new { code = role.Code, scopes = change.Scopes, reason = change.Reason }, ct).ConfigureAwait(false);
        return AccessResult.Done(roleId);
    }

    private static async Task<AccessResult> ApplyFieldsAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var roleId = change.RoleId!.Value;
        if (await AccessDb.LockRoleAsync(c, tx, roleId, ct).ConfigureAwait(false) is not { } role)
        {
            return AccessResult.NotFound;
        }

        foreach (var field in change.Fields!)
        {
            if (field.Access is { } level)
            {
                await IdentityDb.ExecuteAsync(c, tx, """
                    insert into iam.field_policies (id, role_id, resource_type, field_key, access, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                    on conflict (role_id, resource_type, field_key) do update set access = excluded.access, updated_at = excluded.updated_at,
                        updated_by = excluded.updated_by, row_version = iam.field_policies.row_version + 1
                    """, ct, Guid.CreateVersion7(now), roleId, field.Resource, field.Field, level, now, by).ConfigureAwait(false);
            }
            else
            {
                await IdentityDb.ExecuteAsync(c, tx, "delete from iam.field_policies where role_id = $1 and resource_type = $2 and field_key = $3", ct, roleId, field.Resource, field.Field).ConfigureAwait(false);
            }
        }

        await AccessDb.TouchRoleAsync(c, tx, roleId, by, now, ct).ConfigureAwait(false);
        await AccessDb.RaiseAsync(c, tx, AccessEvents.RoleFieldPoliciesChanged, "iam.role", roleId, by, now, new { code = role.Code, fields = change.Fields, reason = change.Reason }, ct).ConfigureAwait(false);
        return AccessResult.Done(roleId);
    }

    private static async Task<AccessResult> ApplyAssignAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, bool group, CancellationToken ct)
    {
        if (await AccessDb.LockRoleAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false) is not { } role)
        {
            return AccessResult.Invalid(RoleNotFound);
        }

        var id = Guid.CreateVersion7(now);
        if (group)
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.user_group_roles (id, group_id, role_id, company_id, reason, valid_from, valid_to, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $8, $9)
                """, ct, id, change.GroupId, change.RoleId, change.CompanyId, change.Reason, change.ValidFrom, change.ValidTo, now, by).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.GroupRoleAssigned, "iam.user_group", change.GroupId!.Value, by, now,
                new { assignment_id = id, role = role.Code, company_id = change.CompanyId, valid_from = change.ValidFrom, valid_to = change.ValidTo, reason = change.Reason }, ct).ConfigureAwait(false);
        }
        else
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, valid_to, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $8, $9)
                """, ct, id, change.UserId, change.RoleId, change.CompanyId, change.Reason, change.ValidFrom, change.ValidTo, now, by).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.RoleAssigned, "iam.user", change.UserId!.Value, by, now,
                new { assignment_id = id, role = role.Code, company_id = change.CompanyId, valid_from = change.ValidFrom, valid_to = change.ValidTo, reason = change.Reason }, ct).ConfigureAwait(false);
        }

        return AccessResult.Done(id);
    }

    private static async Task<AccessResult> ApplyEndAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, bool group, CancellationToken ct)
    {
        var table = group ? "iam.user_group_roles" : "iam.user_roles";
        var id = change.AssignmentId!.Value;
        var end = change.EndDate!.Value;
        await using (var command = IdentityDb.Command(c, tx, $"select valid_from, valid_to from {table} where id = $1 for update", id))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return AccessResult.NotFound;
            }

            if (!reader.IsDBNull(1) && reader.GetFieldValue<DateOnly>(1) <= end)
            {
                return AccessResult.Invalid(AlreadyEnded);
            }
        }

        var admins = await TenantAdminGuard.CountAsync(c, tx, Today(now), ct).ConfigureAwait(false);

        // An assignment that would end on or before its first day never applied: it is removed (the event keeps the record).
        var removed = await IdentityDb.ExecuteAsync(c, tx, $"delete from {table} where id = $1 and valid_from >= $2", ct, id, end).ConfigureAwait(false) > 0;
        if (!removed)
        {
            await IdentityDb.ExecuteAsync(c, tx, $"update {table} set valid_to = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", ct, id, end, now, by).ConfigureAwait(false);
        }

        if (await TenantAdminGuard.RemovedLastAsync(c, tx, admins, Today(now), ct).ConfigureAwait(false))
        {
            return AccessResult.Invalid(TenantAdminGuard.LastAdmin);
        }

        await AccessDb.RaiseAsync(c, tx, group ? AccessEvents.GroupRoleEnded : AccessEvents.RoleAssignmentEnded, group ? "iam.user_group_role" : "iam.user_role", id, by, now,
            new { end_date = end, removed, reason = change.Reason }, ct).ConfigureAwait(false);
        return AccessResult.Done(id);
    }

    private static async Task<AccessResult> ApplyAddMemberAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var added = await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.user_group_members (group_id, user_id)
            select g.id, u.id from iam.user_groups g, iam.users u
            where g.id = $1 and not g.is_deleted and u.id = $2 and not u.is_deleted and not u.is_system
            on conflict do nothing
            """, ct, change.GroupId, change.UserId).ConfigureAwait(false);
        if (added == 0)
        {
            return AccessResult.Invalid(MemberExists);
        }

        await AccessDb.RaiseAsync(c, tx, AccessEvents.GroupMemberAdded, "iam.user_group", change.GroupId!.Value, by, now, new { user_id = change.UserId }, ct).ConfigureAwait(false);
        return AccessResult.Done(change.GroupId);
    }

    private static async Task<AccessResult> ApplyRemoveMemberAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var admins = await TenantAdminGuard.CountAsync(c, tx, Today(now), ct).ConfigureAwait(false);
        var removed = await IdentityDb.ExecuteAsync(c, tx, "delete from iam.user_group_members where group_id = $1 and user_id = $2", ct, change.GroupId, change.UserId).ConfigureAwait(false);
        if (removed == 0)
        {
            return AccessResult.NotFound;
        }

        if (await TenantAdminGuard.RemovedLastAsync(c, tx, admins, Today(now), ct).ConfigureAwait(false))
        {
            return AccessResult.Invalid(TenantAdminGuard.LastAdmin);
        }

        await AccessDb.RaiseAsync(c, tx, AccessEvents.GroupMemberRemoved, "iam.user_group", change.GroupId!.Value, by, now, new { user_id = change.UserId }, ct).ConfigureAwait(false);
        return AccessResult.Done(change.GroupId);
    }

    // ================================================================== assignments (FR-ADM-ROL-04)

    public Task<AccessResult> AssignAsync(Guid? userId, Guid? roleId, Guid? companyId, DateOnly? validFrom, DateOnly? validTo, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.AssignRoles, async (c, tx, now, by) =>
        {
            if (await CheckAssignmentAsync(c, tx, roleId, companyId, validFrom, validTo, reason, now, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }

            if (userId is null || !await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.users where id = $1 and not is_deleted and not is_system)", cancellationToken, userId).ConfigureAwait(false))
            {
                return AccessResult.Invalid(UserNotFound);
            }

            var change = new AccessChange { Kind = AccessChange.Assign, UserId = userId, RoleId = roleId, CompanyId = companyId, ValidFrom = validFrom ?? Today(now), ValidTo = validTo, Reason = reason };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AccessResult> EndAsync(Guid assignmentId, DateOnly? endDate, string? reason, CancellationToken cancellationToken) =>
        EndAnyAsync(assignmentId, endDate, reason, group: false, cancellationToken);

    public Task<AccessResult> EndGroupRoleAsync(Guid groupRoleId, DateOnly? endDate, string? reason, CancellationToken cancellationToken) =>
        EndAnyAsync(groupRoleId, endDate, reason, group: true, cancellationToken);

    private Task<AccessResult> EndAnyAsync(Guid id, DateOnly? endDate, string? reason, bool group, CancellationToken cancellationToken) =>
        RunAsync(group ? AccessControlPermissions.ManageGroups : AccessControlPermissions.AssignRoles, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            var end = endDate ?? Today(now);
            if (end < Today(now))
            {
                return AccessResult.Invalid(EndInPast);
            }

            Guid roleId;
            Guid? companyId;
            await using (var command = IdentityDb.Command(c, tx, $"select role_id, company_id, valid_to from {(group ? "iam.user_group_roles" : "iam.user_roles")} where id = $1", id))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return AccessResult.NotFound;
                }

                roleId = reader.GetGuid(0);
                companyId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                if (!reader.IsDBNull(2) && reader.GetFieldValue<DateOnly>(2) <= end)
                {
                    return AccessResult.Invalid(AlreadyEnded);
                }
            }

            if (!await caller.CoversCompanyAsync(AccessControlPermissions.AssignRoles, companyId, cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.Forbidden(CompanyNotCovered);
            }

            var change = new AccessChange { Kind = group ? AccessChange.EndGroupRole : AccessChange.EndAssignment, AssignmentId = id, RoleId = roleId, CompanyId = companyId, EndDate = end, Reason = reason };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Role, company, dates and reason of a new assignment, and the caller's right to give roles for that company.</summary>
    private async Task<AccessResult?> CheckAssignmentAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid? roleId, Guid? companyId, DateOnly? validFrom, DateOnly? validTo, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return AccessResult.Invalid(ReasonRequired);
        }

        var from = validFrom ?? Today(now);
        if (validTo is { } to && to <= from)
        {
            return AccessResult.Invalid(DatesInvalid);
        }

        if (roleId is null || !await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.roles where id = $1 and not is_deleted)", ct, roleId).ConfigureAwait(false))
        {
            return AccessResult.Invalid(RoleNotFound);
        }

        if (companyId is not null && !await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from org.companies where id = $1 and not is_deleted)", ct, companyId).ConfigureAwait(false))
        {
            return AccessResult.Invalid(CompanyNotFound);
        }

        // A role given for company A is given only by someone who may give roles in company A (ruling A).
        return await caller.CoversCompanyAsync(AccessControlPermissions.AssignRoles, companyId, ct).ConfigureAwait(false) ? null : AccessResult.Forbidden(CompanyNotCovered);
    }

    // ================================================================== groups (FR-ADM-USR-06, CR-019)

    public Task<AccessResult> CreateGroupAsync(string? code, IReadOnlyDictionary<string, string>? name, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageGroups, async (c, tx, now, by) =>
        {
            if (code is null || !CodePattern().IsMatch(code))
            {
                return AccessResult.Invalid(CodeInvalid);
            }

            if (!HasText(name))
            {
                return AccessResult.Invalid(NameRequired);
            }

            var id = Guid.CreateVersion7(now);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.user_groups (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4, $5, $4, $5)
                """, cancellationToken, id, code, Json(name!), now, by).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.GroupCreated, "iam.user_group", id, by, now, new { code, name }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(id);
        }, cancellationToken);

    public Task<AccessResult> RenameGroupAsync(Guid groupId, IReadOnlyDictionary<string, string>? name, long? rowVersion, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageGroups, async (c, tx, now, by) =>
        {
            if (!HasText(name))
            {
                return AccessResult.Invalid(NameRequired);
            }

            if (rowVersion is null)
            {
                return AccessResult.Invalid(RowVersionRequired);
            }

            var current = await IdentityDb.ScalarAsync<long?>(c, tx, "select row_version from iam.user_groups where id = $1 and not is_deleted for update", cancellationToken, groupId).ConfigureAwait(false);
            if (current is null)
            {
                return AccessResult.NotFound;
            }

            if (current != rowVersion)
            {
                return AccessResult.Conflict(ConcurrencyConflict);
            }

            await IdentityDb.ExecuteAsync(c, tx, "update iam.user_groups set name_i18n = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", cancellationToken, groupId, Json(name!), now, by).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.GroupRenamed, "iam.user_group", groupId, by, now, new { name }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(groupId);
        }, cancellationToken);

    public Task<AccessResult> ArchiveGroupAsync(Guid groupId, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageGroups, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            var admins = await TenantAdminGuard.CountAsync(c, tx, Today(now), cancellationToken).ConfigureAwait(false);
            var archived = await IdentityDb.ExecuteAsync(c, tx, """
                update iam.user_groups set is_deleted = true, deleted_at = $2, deleted_by = $3, updated_at = $2, updated_by = $3, row_version = row_version + 1
                where id = $1 and not is_deleted
                """, cancellationToken, groupId, now, by).ConfigureAwait(false);
            if (archived == 0)
            {
                return AccessResult.NotFound;
            }

            if (await TenantAdminGuard.RemovedLastAsync(c, tx, admins, Today(now), cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.Invalid(TenantAdminGuard.LastAdmin);
            }

            await AccessDb.RaiseAsync(c, tx, AccessEvents.GroupArchived, "iam.user_group", groupId, by, now, new { reason }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(groupId);
        }, cancellationToken);

    public Task<AccessResult> AddMemberAsync(Guid groupId, Guid? userId, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageGroups, async (c, tx, now, by) =>
        {
            if (!await GroupExistsAsync(c, tx, groupId, cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.NotFound;
            }

            if (userId is null || !await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.users where id = $1 and not is_deleted and not is_system)", cancellationToken, userId).ConfigureAwait(false))
            {
                return AccessResult.Invalid(UserNotFound);
            }

            if (await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.user_group_members where group_id = $1 and user_id = $2)", cancellationToken, groupId, userId).ConfigureAwait(false))
            {
                return AccessResult.Invalid(MemberExists);
            }

            // Joining a group gives its roles: the same rules as assigning them (cannot grant unheld, maker-checker).
            var change = new AccessChange { Kind = AccessChange.AddMember, GroupId = groupId, UserId = userId };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AccessResult> RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageGroups, async (c, tx, now, by) =>
        {
            if (!await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.user_group_members m join iam.user_groups g on g.id = m.group_id and not g.is_deleted where m.group_id = $1 and m.user_id = $2)", cancellationToken, groupId, userId).ConfigureAwait(false))
            {
                return AccessResult.NotFound;
            }

            var change = new AccessChange { Kind = AccessChange.RemoveMember, GroupId = groupId, UserId = userId };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AccessResult> AssignGroupAsync(Guid groupId, Guid? roleId, Guid? companyId, DateOnly? validFrom, DateOnly? validTo, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageGroups, async (c, tx, now, by) =>
        {
            if (!await GroupExistsAsync(c, tx, groupId, cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.NotFound;
            }

            if (await CheckAssignmentAsync(c, tx, roleId, companyId, validFrom, validTo, reason, now, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }

            var change = new AccessChange { Kind = AccessChange.AssignGroup, GroupId = groupId, RoleId = roleId, CompanyId = companyId, ValidFrom = validFrom ?? Today(now), ValidTo = validTo, Reason = reason };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task<bool> GroupExistsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid groupId, CancellationToken ct) =>
        await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.user_groups where id = $1 and not is_deleted)", ct, groupId).ConfigureAwait(false);
}
