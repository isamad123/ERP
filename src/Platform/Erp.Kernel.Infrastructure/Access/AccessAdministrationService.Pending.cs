using System.Text.Json;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// "Approvals of changes" in the inbox (slice S1.12d Build item 22, ruling Q15; FR-KRN-IAM-14, FR-XM-32): pending
/// maker-checker requests described in plain words, and whether approving would pass the approval's checks now. The
/// checks run in a transaction that is rolled back, so reading the list changes nothing; the decision itself is still
/// made, and checked again, by <see cref="ApproveAsync"/> and <see cref="RejectAsync"/>.
/// </summary>
public sealed partial class AccessAdministrationService
{
    /// <summary>At most this many requests in each list (oldest first): more waiting is a matter for the administrators.</summary>
    public const int PendingMaxItems = 200;

    public async Task<PendingChanges> PendingAsync(CancellationToken cancellationToken)
    {
        var mayDecide = await permissions.HasPermissionAsync(AccessControlPermissions.ApproveChanges, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var me = actor.UserId;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var days = await AccessDb.SettingIntAsync(c, tx, IdentitySettings.MakerCheckerExpiryDays, 7, cancellationToken).ConfigureAwait(false);
        var (decidableHeld, allHeld) = await HeldKindsAsync(cancellationToken).ConfigureAwait(false); // modules' held changes (S1.20a)

        // Requests older than the expiry can no longer be decided (they expire at the next decision or the daily job).
        var rows = new List<(Guid Id, string Kind, Guid By, string? ByName, DateTimeOffset At, string? Reason, string Payload)>();
        await using (var command = IdentityDb.Command(c, tx, """
            select r.id, r.action_type, r.requested_by, u.display_name, r.created_at, r.reason, r.payload::text
            from iam.maker_checker_requests r left join iam.users u on u.id = r.requested_by
            where r.status = 'pending' and r.created_at > $1
              and (r.requested_by = $2 or ($3 and not r.action_type = any($4)) or r.action_type = any($5))
            order by r.created_at, r.id
            """, now.AddDays(-days), me, mayDecide, allHeld, decidableHeld))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
            }
        }

        var toDecide = new List<ChangeRequestView>();
        var mine = new List<ChangeRequestView>();
        foreach (var row in rows)
        {
            var own = row.By == me;
            if ((own ? mine : toDecide).Count >= PendingMaxItems)
            {
                continue;
            }

            if (HandlerOf(row.Kind) is { } handler)
            {
                if (await DescribeHeldAsync(handler, tx, row.Id, row.Kind, row.By, row.Payload, row.Reason, own, cancellationToken).ConfigureAwait(false) is not { } held)
                {
                    continue; // outside the caller's data scope: not offered (review S1.20a finding 2)
                }

                var (heldSubject, heldDetails, heldProblem) = held;

                (own ? mine : toDecide).Add(new ChangeRequestView(row.Id, row.Kind, row.By, row.ByName, row.At, row.At.AddDays(days), row.Reason, heldSubject, heldDetails, heldProblem is null, heldProblem));
                continue;
            }

            AccessChange? change;
            try
            {
                change = AccessChange.FromJson(row.Payload);
            }
            catch (JsonException)
            {
                change = null; // a request not written by these services: listed, never approvable
            }

            var (subject, details) = change is null ? (null, []) : await DescribeAsync(c, tx, change, cancellationToken).ConfigureAwait(false);
            string? problem = own ? OwnRequest : change is null ? InvalidRequest : await ApproveProblemAsync(c, tx, change, me, cancellationToken).ConfigureAwait(false);
            (own ? mine : toDecide).Add(new ChangeRequestView(row.Id, row.Kind, row.By, row.ByName, row.At, row.At.AddDays(days), row.Reason, subject, details, problem is null, problem));
        }

        await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new PendingChanges(toDecide, mine);
    }

    /// <summary>The first refusal <see cref="ApproveAsync"/> would give for this change now, or null (the same checks, in its order).</summary>
    private async Task<string?> ApproveProblemAsync(NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, Guid approver, CancellationToken ct) =>
        (await ValidateAsync(c, tx, change, ct).ConfigureAwait(false)
            ?? await CheckGrantsAsync(c, tx, change, ct).ConfigureAwait(false)
            ?? await ApproverProblemAsync(c, tx, change, approver, ct).ConfigureAwait(false))?.Error;

    /// <summary>What the change is about (by name in each language) and what it changes, line by line.</summary>
    private static async Task<(IReadOnlyDictionary<string, string>? Subject, IReadOnlyList<ChangeDetail> Details)> DescribeAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, AccessChange change, CancellationToken ct)
    {
        var details = new List<ChangeDetail>();
        Task<IReadOnlyDictionary<string, string>?> Names(string sql, Guid? id) => NamesAsync(c, tx, sql, id, ct);
        async Task Role(Guid? roleId) => details.Add(new ChangeDetail("changes.detail.role", "names", Names: await Names(RoleName, roleId).ConfigureAwait(false)));
        async Task Company(Guid? companyId) => details.Add(companyId is null
            ? new ChangeDetail("changes.detail.company", "key", "changes.detail.allCompanies")
            : new ChangeDetail("changes.detail.company", "names", Names: await Names(CompanyName, companyId).ConfigureAwait(false)));
        void Dates()
        {
            if (change.ValidFrom is { } from)
            {
                details.Add(new ChangeDetail("changes.detail.from", "date", Date: from));
            }

            if (change.ValidTo is { } to)
            {
                details.Add(new ChangeDetail("changes.detail.to", "date", Date: to));
            }
        }

        switch (change.Kind)
        {
            case AccessChange.SetPermissions:
            {
                var current = await AccessDb.RolePermissionsAsync(c, tx, change.RoleId!.Value, ct).ConfigureAwait(false);
                var (added, removed) = change.PermissionDelta(current);
                if (added.Count > 0)
                {
                    details.Add(new ChangeDetail("changes.detail.permissionsAdded", "items", Items: [.. added.Order(StringComparer.Ordinal)]));
                }

                if (removed.Count > 0)
                {
                    details.Add(new ChangeDetail("changes.detail.permissionsRemoved", "items", Items: [.. removed.Order(StringComparer.Ordinal)]));
                }

                return (await Names(RoleName, change.RoleId).ConfigureAwait(false), details);
            }

            case AccessChange.SetDataScopes:
            {
                var items = new List<string>();
                foreach (var scope in change.Scopes ?? [])
                {
                    var code = scope.DataScopeId is { } id ? await IdentityDb.ScalarAsync<string>(c, tx, "select code from iam.data_scopes where id = $1", ct, id).ConfigureAwait(false) : null;
                    items.Add($"{scope.Resource}: {code ?? "-"}");
                }

                details.Add(new ChangeDetail("changes.detail.dataScopes", "items", Items: items));
                return (await Names(RoleName, change.RoleId).ConfigureAwait(false), details);
            }

            case AccessChange.SetFieldPolicies:
                details.Add(new ChangeDetail("changes.detail.fieldRules", "items", Items: [.. (change.Fields ?? []).Select(f => $"{f.Resource}.{f.Field}: {f.Access ?? "-"}")]));
                return (await Names(RoleName, change.RoleId).ConfigureAwait(false), details);

            case AccessChange.Assign:
                await Role(change.RoleId).ConfigureAwait(false);
                await Company(change.CompanyId).ConfigureAwait(false);
                Dates();
                return (await Names(UserName, change.UserId).ConfigureAwait(false), details);

            case AccessChange.EndAssignment:
            case AccessChange.EndGroupRole:
            {
                var group = change.Kind == AccessChange.EndGroupRole;
                var (holder, role, company) = await AssignmentAsync(c, tx, group, change.AssignmentId, ct).ConfigureAwait(false);
                await Role(role).ConfigureAwait(false);
                await Company(company).ConfigureAwait(false);
                if (change.EndDate is { } end)
                {
                    details.Add(new ChangeDetail("changes.detail.endDate", "date", Date: end));
                }

                return (await Names(group ? GroupName : UserName, holder).ConfigureAwait(false), details);
            }

            case AccessChange.AssignGroup:
                await Role(change.RoleId).ConfigureAwait(false);
                await Company(change.CompanyId).ConfigureAwait(false);
                Dates();
                return (await Names(GroupName, change.GroupId).ConfigureAwait(false), details);

            case AccessChange.AddMember:
            case AccessChange.RemoveMember:
                details.Add(new ChangeDetail("changes.detail.member", "names", Names: await Names(UserName, change.UserId).ConfigureAwait(false)));
                return (await Names(GroupName, change.GroupId).ConfigureAwait(false), details);

            case AccessChange.SetSetting:
            case AccessChange.ChangeSetting:
            {
                details.Add(new ChangeDetail("changes.detail.settingKey", "text", change.SettingKey));
                details.Add(new ChangeDetail("changes.detail.layer", "key", "changes.layer." + (change.SettingLayer ?? "tenant")));
                if (change.SettingLayer == "company" && change.SettingScopeId is { } company)
                {
                    details.Add(new ChangeDetail("changes.detail.company", "names", Names: await Names(CompanyName, company).ConfigureAwait(false)));
                }

                details.Add(change.SettingReset
                    ? new ChangeDetail("changes.detail.newValue", "key", "changes.detail.reset")
                    : new ChangeDetail("changes.detail.newValue", "text", change.SettingValue));
                var name = change.SettingKey is { } key
                    ? await IdentityDb.ScalarAsync<string>(c, tx, "select coalesce(name_i18n, description_i18n)::text from cfg.settings where key = $1", ct, key).ConfigureAwait(false)
                    : null;
                return (Parse(name), details);
            }

            case AccessChange.ActivateWorkflowVersion:
            {
                var version = await IdentityDb.ScalarAsync<int?>(c, tx, "select version from wf.workflow_versions where id = $1", ct, change.WorkflowVersionId).ConfigureAwait(false);
                details.Add(new ChangeDetail("changes.detail.version", "text", version?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return (await Names("select d.name_i18n::text from wf.workflow_definitions d join wf.workflow_versions v on v.definition_id = d.id where v.id = $1", change.WorkflowVersionId).ConfigureAwait(false), details);
            }

            case AccessChange.SaveBankAccount:
            case AccessChange.DeleteBankAccount:
            {
                var bank = change.BankAccount!;
                details.Add(new ChangeDetail("changes.detail.operation", "key", "changes.operation." + bank.Operation));
                var stored = bank.Operation == "create" ? null : await IdentityDb.ScalarAsync<string>(c, tx, "select bank_code || ' ' || account_title from org.company_bank_accounts where id = $1", ct, bank.Id).ConfigureAwait(false);
                if (stored is not null)
                {
                    details.Add(new ChangeDetail("changes.detail.bankAccount", "text", stored));
                }

                if (bank.BankCode is not null)
                {
                    details.Add(new ChangeDetail("changes.detail.bankCode", "text", bank.BankCode));
                }

                if (bank.AccountTitle is not null)
                {
                    details.Add(new ChangeDetail("changes.detail.accountTitle", "text", bank.AccountTitle));
                }

                if (bank.AccountNumberLast4 is not null)
                {
                    details.Add(new ChangeDetail("changes.detail.accountNumber", "text", "****" + bank.AccountNumberLast4));
                }

                if (bank.Iban is not null || bank.ClearIban)
                {
                    details.Add(new ChangeDetail("changes.detail.iban", "key", bank.ClearIban ? "changes.detail.removed" : "changes.detail.changed"));
                }

                if (bank.CurrencyCode is not null)
                {
                    details.Add(new ChangeDetail("changes.detail.currency", "text", bank.CurrencyCode));
                }

                details.Add(new ChangeDetail("changes.detail.company", "names", Names: await Names(CompanyName, bank.CompanyId).ConfigureAwait(false)));
                return (await Names(CompanyName, bank.CompanyId).ConfigureAwait(false), details);
            }

            case AccessChange.ChangeUserEmail:
                details.Add(new ChangeDetail("changes.detail.previousEmail", "text", change.PreviousEmail));
                details.Add(new ChangeDetail("changes.detail.newEmail", "text", change.Email));
                return (await Names(UserName, change.UserId).ConfigureAwait(false), details);

            default:
                return (null, details);
        }
    }

    private const string RoleName = "select name_i18n::text from iam.roles where id = $1";
    private const string GroupName = "select name_i18n::text from iam.user_groups where id = $1";
    private const string CompanyName = "select name_i18n::text from org.companies where id = $1";

    /// <summary>A person's display name, the same in every language.</summary>
    private const string UserName = "select jsonb_build_object('en', display_name, 'ur', display_name)::text from iam.users where id = $1";

    private static async Task<IReadOnlyDictionary<string, string>?> NamesAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, Guid? id, CancellationToken ct) =>
        id is null ? null : Parse(await IdentityDb.ScalarAsync<string>(c, tx, sql, ct, id.Value).ConfigureAwait(false));

    private static Dictionary<string, string>? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind != JsonValueKind.Object ? null
            : document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    /// <summary>Who holds an assignment (a user, or a group), its role and company.</summary>
    private static async Task<(Guid? Holder, Guid? Role, Guid? Company)> AssignmentAsync(NpgsqlConnection c, NpgsqlTransaction tx, bool group, Guid? id, CancellationToken ct)
    {
        await using var command = IdentityDb.Command(c, tx, group
            ? "select group_id, role_id, company_id from iam.user_group_roles where id = $1"
            : "select user_id, role_id, company_id from iam.user_roles where id = $1", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2))
            : (null, null, null);
    }
}
