using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// Roles, assignments, groups, maker-checker and previews (slice S1.06 build items 2, 4, 6, 7, 8, 9). The rules that
/// the generated write endpoints could not apply, which CR-020 and CR-021 therefore closed, live here:
/// <list type="bullet">
/// <item>Nobody can give a role a permission they do not hold, or give anyone a role containing one (spec 02 BR); a
/// field rule can never open a field wider than the caller's own access to it.</item>
/// <item>A role given for one company is given only by someone holding <c>iam.roles.assign</c> for that company (or for
/// all companies); only an all-companies assignment gives roles for all companies (ruling A).</item>
/// <item>With <c>iam.maker_checker.sensitive_roles</c> on, a change to, or an assignment of, a role holding a sensitive
/// permission waits for a second person (ruling Q6, CR-021).</item>
/// <item>The last tenant admin cannot be removed (<see cref="TenantAdminGuard"/>).</item>
/// </list>
/// Every change raises an <c>iam.*</c> event in iam.outbox_messages with the actor in <c>headers.actor</c> (S1.07 audit).
/// </summary>
public sealed partial class AccessAdministrationService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess caller,
    IFieldAccess fieldAccess,
    ResourceCatalog resources,
    IServiceProvider services,
    IWorkforceDirectory workforce)
    : IRoleService, IAssignmentService, IMakerCheckerService, IAccessPreviewService
{
    public const string Forbidden = "errors.access.forbidden";
    public const string CannotGrantUnheld = "errors.access.cannot_grant_unheld";
    public const string CompanyNotCovered = "errors.access.company_not_covered";
    public const string ReasonRequired = "errors.access.reason_required";
    public const string CodeInvalid = "errors.role.code_invalid";
    public const string NameRequired = "errors.role.name_required";
    public const string UnknownPermission = "errors.role.unknown_permission";
    public const string UnknownResource = "errors.role.unknown_resource";
    public const string UnknownField = "errors.role.unknown_field";
    public const string UnknownScope = "errors.role.unknown_scope";
    public const string AccessInvalid = "errors.role.access_invalid";
    public const string CopySourceNotFound = "errors.role.copy_source_not_found";
    public const string RowVersionRequired = "errors.data.row_version_required";
    public const string UserNotFound = "errors.assignment.user_not_found";
    public const string RoleNotFound = "errors.assignment.role_not_found";
    public const string CompanyNotFound = "errors.assignment.company_not_found";
    public const string DatesInvalid = "errors.assignment.dates_invalid";
    public const string EndInPast = "errors.assignment.end_in_past";
    public const string AlreadyEnded = "errors.assignment.already_ended";
    public const string MemberExists = "errors.group.member_exists";
    public const string NotPending = "errors.maker_checker.not_pending";
    public const string Expired = "errors.maker_checker.expired";
    public const string OwnRequest = "errors.maker_checker.own_request";
    public const string OwnGain = "errors.maker_checker.own_gain";
    public const string Duplicate = "errors.data.duplicate";
    public const string Overlap = "errors.data.overlap";
    public const string ConcurrencyConflict = "errors.data.concurrency_conflict";

    private static readonly string[] AccessLevels = ["hidden", "masked", "read_only", "editable"];

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    // ================================================================== roles

    public Task<AccessResult> CreateAsync(CreateRole request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (request.Code is not { } code || !CodePattern().IsMatch(code))
            {
                return AccessResult.Invalid(CodeInvalid);
            }

            if (!HasText(request.Name))
            {
                return AccessResult.Invalid(NameRequired);
            }

            Guid? source = null;
            if (request.CopyFromRoleId is { } fromId)
            {
                source = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select id from iam.roles where id = $1 and not is_deleted", cancellationToken, fromId).ConfigureAwait(false);
            }
            else if (request.CopyFromBaseline is { } baseline)
            {
                source = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
                    select id from iam.roles where lower(baseline_code) = lower($1) and source = 'module' and not is_deleted order by created_at, id limit 1
                    """, cancellationToken, baseline).ConfigureAwait(false);
            }

            if ((request.CopyFromRoleId is not null || request.CopyFromBaseline is not null) && source is null)
            {
                return AccessResult.Invalid(CopySourceNotFound);
            }

            var copied = source is { } s ? await AccessDb.RolePermissionsAsync(c, tx, s, cancellationToken).ConfigureAwait(false) : [];
            if (await FirstUnheldAsync(copied, cancellationToken).ConfigureAwait(false) is not null)
            {
                return AccessResult.Forbidden(CannotGrantUnheld);
            }

            var id = Guid.CreateVersion7(now);
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.roles (id, code, name_i18n, description_i18n, source, baseline_code, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, 'tenant', (select baseline_code from iam.roles where id = $5), $6, $7, $6, $7)
                """, cancellationToken, id, code, Json(request.Name!), request.Description is null ? null : Json(request.Description), source, now, by).ConfigureAwait(false);
            if (source is { } from)
            {
                await IdentityDb.ExecuteAsync(c, tx, "insert into iam.role_permissions (role_id, permission_id) select $1, permission_id from iam.role_permissions where role_id = $2", cancellationToken, id, from).ConfigureAwait(false);
                await IdentityDb.ExecuteAsync(c, tx, "insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) select $1, resource_type, data_scope_id from iam.role_data_scopes where role_id = $2", cancellationToken, id, from).ConfigureAwait(false);
                await using (var rules = IdentityDb.Command(c, tx, "select resource_type, field_key, access from iam.field_policies where role_id = $1", from))
                {
                    var copiedRules = new List<(string Resource, string Field, string Access)>();
                    await using (var reader = await rules.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            copiedRules.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                        }
                    }

                    foreach (var (resource, field, level) in copiedRules)
                    {
                        await IdentityDb.ExecuteAsync(c, tx, """
                            insert into iam.field_policies (id, role_id, resource_type, field_key, access, created_at, created_by, updated_at, updated_by)
                            values ($1, $2, $3, $4, $5, $6, $7, $6, $7)
                            """, cancellationToken, Guid.CreateVersion7(now), id, resource, field, level, now, by).ConfigureAwait(false);
                    }
                }
            }

            await AccessDb.RaiseAsync(c, tx, AccessEvents.RoleCreated, "iam.role", id, by, now, new { code, copied_from = source, permissions = copied.Count }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(id);
        }, cancellationToken);
    }

    public Task<AccessResult> RenameAsync(Guid roleId, IReadOnlyDictionary<string, string>? name, IReadOnlyDictionary<string, string>? description, long? rowVersion, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (!HasText(name))
            {
                return AccessResult.Invalid(NameRequired);
            }

            if (rowVersion is null)
            {
                return AccessResult.Invalid(RowVersionRequired);
            }

            if (await AccessDb.LockRoleAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false) is not { } role)
            {
                return AccessResult.NotFound;
            }

            if (role.RowVersion != rowVersion)
            {
                return AccessResult.Conflict(ConcurrencyConflict);
            }

            await IdentityDb.ExecuteAsync(c, tx, """
                update iam.roles set name_i18n = $2, description_i18n = $3, updated_at = $4, updated_by = $5, row_version = row_version + 1 where id = $1
                """, cancellationToken, roleId, Json(name!), description is null ? null : Json(description), now, by).ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, AccessEvents.RoleRenamed, "iam.role", roleId, by, now, new { code = role.Code, name }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(roleId);
        }, cancellationToken);

    public Task<AccessResult> ArchiveAsync(Guid roleId, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            if (await AccessDb.LockRoleAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false) is not { } role)
            {
                return AccessResult.NotFound;
            }

            if (ProtectedRoles.IsProtected(role.Code))
            {
                return AccessResult.Invalid(ProtectedRoles.ProtectedRole); // CR-024: the Tenant Admin role is never archived
            }

            var admins = await TenantAdminGuard.CountAsync(c, tx, Today(now), cancellationToken).ConfigureAwait(false);
            await IdentityDb.ExecuteAsync(c, tx, """
                update iam.roles set is_deleted = true, deleted_at = $2, deleted_by = $3, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
                """, cancellationToken, roleId, now, by).ConfigureAwait(false);
            if (await TenantAdminGuard.RemovedLastAsync(c, tx, admins, Today(now), cancellationToken).ConfigureAwait(false))
            {
                return AccessResult.Invalid(TenantAdminGuard.LastAdmin);
            }

            await AccessDb.RaiseAsync(c, tx, AccessEvents.RoleArchived, "iam.role", roleId, by, now, new { code = role.Code, reason }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(roleId);
        }, cancellationToken);

    public Task<AccessResult> SetPermissionsAsync(Guid roleId, IReadOnlyList<string>? permissions, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (permissions is null)
            {
                return AccessResult.Invalid(UnknownPermission);
            }

            if (await AccessDb.LockRoleAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false) is null)
            {
                return AccessResult.NotFound;
            }

            // Stored as what is added and removed, so a request approved later never undoes a change made meanwhile
            // (review S1.06 finding 7, CR-025).
            var wanted = permissions.Distinct(StringComparer.Ordinal).ToList();
            var current = await AccessDb.RolePermissionsAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false);
            var change = new AccessChange
            {
                Kind = AccessChange.SetPermissions,
                RoleId = roleId,
                Added = wanted.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Removed = current.Except(wanted, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Reason = reason,
            };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AccessResult> SetDataScopesAsync(Guid roleId, IReadOnlyList<RoleScopeChange>? changes, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (changes is not { Count: > 0 })
            {
                return AccessResult.Invalid(UnknownScope);
            }

            if (await AccessDb.LockRoleAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false) is null)
            {
                return AccessResult.NotFound;
            }

            var change = new AccessChange { Kind = AccessChange.SetDataScopes, RoleId = roleId, Scopes = changes, Reason = reason };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AccessResult> SetFieldPoliciesAsync(Guid roleId, IReadOnlyList<RoleFieldChange>? changes, string? reason, CancellationToken cancellationToken) =>
        RunAsync(AccessControlPermissions.ManageRoles, async (c, tx, now, by) =>
        {
            if (changes is not { Count: > 0 })
            {
                return AccessResult.Invalid(UnknownField);
            }

            if (await AccessDb.LockRoleAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false) is null)
            {
                return AccessResult.NotFound;
            }

            var change = new AccessChange { Kind = AccessChange.SetFieldPolicies, RoleId = roleId, Fields = changes, Reason = reason };
            return await ValidateThenSubmitAsync(c, tx, change, now, by, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<RoleSummary?> SummaryAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string code, source;
        string? baseline;
        bool active;
        long rowVersion;
        Dictionary<string, string> name;
        await using (var command = IdentityDb.Command(c, tx, "select code, name_i18n::text, source, baseline_code, is_active, row_version from iam.roles where id = $1 and not is_deleted", roleId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            code = reader.GetString(0);
            name = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1)) ?? [];
            source = reader.GetString(2);
            baseline = reader.IsDBNull(3) ? null : reader.GetString(3);
            active = reader.GetBoolean(4);
            rowVersion = reader.GetInt64(5);
        }

        var perms = await AccessDb.RolePermissionsAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false);
        var scopes = new List<RoleScopeView>();
        await using (var command = IdentityDb.Command(c, tx, """
            select rds.resource_type, ds.id, ds.code, ds.kind from iam.role_data_scopes rds join iam.data_scopes ds on ds.id = rds.data_scope_id
            where rds.role_id = $1 order by rds.resource_type
            """, roleId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                scopes.Add(new RoleScopeView(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var fields = new List<RoleFieldView>();
        await using (var command = IdentityDb.Command(c, tx, "select resource_type, field_key, access from iam.field_policies where role_id = $1 order by resource_type, field_key", roleId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                fields.Add(new RoleFieldView(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var users = await IdentityDb.ScalarAsync<long>(c, tx, """
            select count(distinct x.user_id) from (
                select ur.user_id from iam.user_roles ur where ur.role_id = $1 and ur.valid_from <= $2 and (ur.valid_to is null or ur.valid_to > $2)
                union
                select m.user_id from iam.user_group_roles gr
                join iam.user_groups g on g.id = gr.group_id and not g.is_deleted and g.is_active
                join iam.user_group_members m on m.group_id = gr.group_id
                where gr.role_id = $1 and gr.valid_from <= $2 and (gr.valid_to is null or gr.valid_to > $2)
            ) x join iam.users u on u.id = x.user_id and not u.is_deleted
            """, cancellationToken, roleId, Today(clock.UtcNow)).ConfigureAwait(false);
        var privileged = await AccessDb.IsSensitiveRoleAsync(c, tx, roleId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoleSummary(roleId, code, name, source, baseline, active, rowVersion, privileged, perms, scopes, fields, (int)users);
    }

    public async Task<RoleComparison?> CompareAsync(Guid left, Guid right, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var found = await IdentityDb.ScalarAsync<long>(c, tx, "select count(*) from iam.roles where id = any($1) and not is_deleted", cancellationToken, new[] { left, right }.Distinct().ToArray()).ConfigureAwait(false);
        if (found != (left == right ? 1 : 2))
        {
            return null;
        }

        var a = (await AccessDb.RolePermissionsAsync(c, tx, left, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var b = (await AccessDb.RolePermissionsAsync(c, tx, right, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoleComparison(left, right,
            a.Except(b).Order(StringComparer.Ordinal).ToList(),
            b.Except(a).Order(StringComparer.Ordinal).ToList(),
            a.Intersect(b).Order(StringComparer.Ordinal).ToList());
    }

    // ================================================================== shared machinery

    private delegate Task<AccessResult> Work(NpgsqlConnection c, NpgsqlTransaction tx, DateTimeOffset now, Guid by);

    /// <summary>
    /// Checks the coarse permission, runs the work in one transaction and commits only when the change was applied or
    /// stored for approval. Database refusals become conflict or validation answers (localization keys).
    /// </summary>
    private async Task<AccessResult> RunAsync(string permission, Work work, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
        {
            return AccessResult.Forbidden(Forbidden);
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AccessResult result;
        try
        {
            result = await work(c, tx, now, by).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation)
        {
            return AccessResult.Conflict(Duplicate);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ExclusionViolation)
        {
            return AccessResult.Conflict(Overlap);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            return AccessResult.Invalid(ex.SqlState == PostgresErrorCodes.CheckViolation ? "errors.data.rule_violated" : "errors.data.reference_not_found");
        }

        if (result.Outcome is AccessOutcome.Done or AccessOutcome.Pending)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Validates the change and the caller's right to grant it, then applies it or holds it for a second person.</summary>
    private async Task<AccessResult> ValidateThenSubmitAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        if (await ValidateAsync(c, tx, change, ct).ConfigureAwait(false) is { } invalid)
        {
            return invalid;
        }

        if (await CheckGrantsAsync(c, tx, change, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if (await NeedsSecondPersonAsync(c, tx, change, ct).ConfigureAwait(false))
        {
            return await SubmitAsync(c, tx, change, now, by, ct).ConfigureAwait(false);
        }

        return await ApplyAsync(c, tx, change, now, by, ct).ConfigureAwait(false);
    }

    /// <summary>Checks the parts of a change that do not depend on who makes it (codes, resources, fields, dates).</summary>
    private async Task<AccessResult?> ValidateAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        switch (change.Kind)
        {
            case AccessChange.SetPermissions:
            {
                var current = await AccessDb.RolePermissionsAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false);
                var (added, removed) = change.PermissionDelta(current);
                var known = await IdentityDb.ScalarAsync<long>(c, tx, "select count(*) from iam.permissions where code = any($1) and retired_at is null", ct, (object)added.Distinct(StringComparer.Ordinal).ToArray()).ConfigureAwait(false); // (object): one array parameter, not a params list
                if (known != added.Distinct(StringComparer.Ordinal).Count())
                {
                    return AccessResult.Invalid(UnknownPermission);
                }

                // The protected Tenant Admin role keeps Manage Roles and Assign Roles (CR-024), also when a held change is approved.
                var role = await IdentityDb.ScalarAsync<string>(c, tx, "select code from iam.roles where id = $1", ct, change.RoleId).ConfigureAwait(false);
                var after = current.Concat(added).Except(removed, StringComparer.Ordinal).ToList();
                return role is not null && !ProtectedRoles.Keeps(role, after) ? AccessResult.Invalid(ProtectedRoles.MandatoryPermissionMissing) : null;
            }

            case AccessChange.SetSetting:
                return await ValidateSettingAsync(c, tx, change, ct).ConfigureAwait(false);

            case AccessChange.ChangeSetting:
                return await ValidateHeldSettingAsync(c, tx, change, ct).ConfigureAwait(false);

            case AccessChange.SetDataScopes:
                foreach (var scope in change.Scopes!)
                {
                    if (scope.Resource != RoleRules.AnyResource && resources.Find(scope.Resource) is null)
                    {
                        return AccessResult.Invalid(UnknownResource);
                    }

                    if (scope.DataScopeId is { } id && !await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.data_scopes where id = $1 and is_active)", ct, id).ConfigureAwait(false))
                    {
                        return AccessResult.Invalid(UnknownScope);
                    }
                }

                return null;
            case AccessChange.SetFieldPolicies:
                foreach (var field in change.Fields!)
                {
                    if (field.Access is { } level && !AccessLevels.Contains(level))
                    {
                        return AccessResult.Invalid(AccessInvalid);
                    }

                    var isClass = field.Field.StartsWith(RoleRules.ClassPrefix, StringComparison.Ordinal)
                        && Array.IndexOf(["salary", "personal", "restricted"], field.Field[RoleRules.ClassPrefix.Length..]) >= 0;
                    if (field.Resource == RoleRules.AnyResource)
                    {
                        if (!isClass)
                        {
                            return AccessResult.Invalid(UnknownField);
                        }

                        continue;
                    }

                    if (resources.Find(field.Resource) is not { } resource)
                    {
                        return AccessResult.Invalid(UnknownResource);
                    }

                    var known = isClass || resource.Fields.Find(field.Field) is not null
                        || (field.Field.StartsWith(FieldAccessMap.CustomPrefix, StringComparison.Ordinal) && field.Field.Length > FieldAccessMap.CustomPrefix.Length && resource.Fields.Find("custom") is not null);
                    if (!known)
                    {
                        return AccessResult.Invalid(UnknownField);
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// "Nobody can grant what they do not hold" (spec 02 BR), applied to the requester and again to the approver: every
    /// permission the change grants must be held by the caller; a data scope can only be set for a resource the caller
    /// may read (for <c>*</c>: only by someone holding every permission of the role); a field rule can only open a field
    /// as far as the caller's own access to it.
    /// </summary>
    private async Task<AccessResult?> CheckGrantsAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        var granted = await GrantedPermissionsAsync(c, tx, change, ct).ConfigureAwait(false);
        if (await FirstUnheldAsync(granted, ct).ConfigureAwait(false) is not null)
        {
            return AccessResult.Forbidden(CannotGrantUnheld);
        }

        if (change.Kind == AccessChange.SetFieldPolicies)
        {
            foreach (var field in change.Fields!)
            {
                var wanted = field.Access is { } a ? AccessSnapshot.ParseLevel(a) : DefaultLevel(field.Field);
                foreach (var (resource, key) in AffectedFields(field))
                {
                    var mine = (await fieldAccess.ForAsync(resource.Fields, "read", null, ct).ConfigureAwait(false)).Of(key);
                    if (wanted > mine)
                    {
                        return AccessResult.Forbidden(CannotGrantUnheld);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The permissions a change gives to someone: what the requester and the approver must hold themselves.</summary>
    private async Task<IReadOnlyList<string>> GrantedPermissionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        switch (change.Kind)
        {
            case AccessChange.SetPermissions:
            {
                var current = await AccessDb.RolePermissionsAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false);
                return change.PermissionDelta(current).Added;
            }

            case AccessChange.SetDataScopes:
            case AccessChange.SetFieldPolicies:
            {
                var resourcesTouched = (change.Scopes?.Select(s => s.Resource) ?? change.Fields!.Select(f => f.Resource)).Distinct(StringComparer.Ordinal).ToList();
                var list = resourcesTouched.Where(r => r != RoleRules.AnyResource).Select(r => r + ".read").ToList();
                if (change.Kind == AccessChange.SetDataScopes && resourcesTouched.Contains(RoleRules.AnyResource))
                {
                    list.AddRange(await AccessDb.RolePermissionsAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false));
                }

                return list;
            }

            case AccessChange.Assign:
            case AccessChange.AssignGroup:
                return await AccessDb.RolePermissionsAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false);
            case AccessChange.AddMember:
            {
                var list = new List<string>();
                foreach (var role in await GroupRolesAsync(c, tx, change.GroupId!.Value, ct).ConfigureAwait(false))
                {
                    list.AddRange(await AccessDb.RolePermissionsAsync(c, tx, role, ct).ConfigureAwait(false));
                }

                return list.Distinct(StringComparer.Ordinal).ToList();
            }

            default:
                return [];
        }
    }

    private async Task<string?> FirstUnheldAsync(IEnumerable<string> codes, CancellationToken ct)
    {
        foreach (var code in codes)
        {
            if (!await caller.HoldsAsync(code, ct).ConfigureAwait(false))
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>Removing a rule returns the field to its default: salary hidden (never wider), anything else editable.</summary>
    private static FieldAccessLevel DefaultLevel(string field) =>
        field == RoleRules.ClassPrefix + "salary" ? FieldAccessLevel.Hidden : FieldAccessLevel.Editable;

    /// <summary>The concrete fields a field rule reaches: one field, a class on one resource, or a class on every resource.</summary>
    private IEnumerable<(ResourceAccess Resource, string Key)> AffectedFields(RoleFieldChange change)
    {
        var targets = change.Resource == RoleRules.AnyResource ? resources.All : resources.Find(change.Resource) is { } one ? [one] : [];
        foreach (var resource in targets)
        {
            if (change.Field.StartsWith(RoleRules.ClassPrefix, StringComparison.Ordinal))
            {
                var fieldClass = change.Field[RoleRules.ClassPrefix.Length..];
                foreach (var field in resource.Fields.Fields.Where(f => f.FieldClass == fieldClass))
                {
                    yield return (resource, field.Key);
                }
            }
            else
            {
                yield return (resource, change.Field);
            }
        }
    }

    /// <summary>Maker-checker applies when the setting is on and the change concerns a role holding a sensitive permission (before or after).</summary>
    private async Task<bool> NeedsSecondPersonAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        if (!await AccessDb.SettingOnAsync(c, tx, IdentitySettings.MakerCheckerSensitiveRoles, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (change.Kind is AccessChange.AddMember or AccessChange.RemoveMember)
        {
            foreach (var role in await GroupRolesAsync(c, tx, change.GroupId!.Value, ct).ConfigureAwait(false))
            {
                if (await AccessDb.IsSensitiveRoleAsync(c, tx, role, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }

        if (await AccessDb.IsSensitiveRoleAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false))
        {
            return true;
        }

        if (change.Kind != AccessChange.SetPermissions)
        {
            return false;
        }

        var added = change.PermissionDelta(await AccessDb.RolePermissionsAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false)).Added;
        return await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from iam.permissions where code = any($1) and is_sensitive and retired_at is null)", ct, (object)added.ToArray()).ConfigureAwait(false);
    }

    /// <summary>Roles a group gives today or later (assignments not yet ended).</summary>
    private async Task<List<Guid>> GroupRolesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid groupId, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, "select distinct role_id from iam.user_group_roles where group_id = $1 and (valid_to is null or valid_to > $2)", groupId, Today(clock.UtcNow));
        return await AccessDb.IdsAsync(command, ct).ConfigureAwait(false);
    }

    private static JsonPayload Json(IReadOnlyDictionary<string, string> value) => new(JsonSerializer.Serialize(value));

    private static bool HasText(IReadOnlyDictionary<string, string>? text) => text is not null && text.Values.Any(v => !string.IsNullOrWhiteSpace(v));

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
