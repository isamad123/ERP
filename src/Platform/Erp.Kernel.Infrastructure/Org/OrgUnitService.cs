using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Application.Org;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Org;

/// <summary>
/// Organization units over time (slice S1.14b, CR-070 S1.14b detail, FR-KRN-ORG-03, FR-ADM-ORG-03): a hierarchy on a date, the
/// difference between two dates and the move of a unit. A move changes two rows (the old link ends, the new one starts), so
/// it runs in one transaction with the unit locked; the database refuses a cycle and a parent of another company as well
/// (trigger <c>org.tg_org_unit_links_checks</c>), so a path that does not use this service cannot break the structure. Every
/// value is a parameter.
/// </summary>
public sealed class OrgUnitService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    ICallerAccess caller) : IOrgUnitService
{
    public const string FieldsRequired = "errors.org.move_fields_required";
    public const string ReasonTooLong = "errors.org.reason_too_long";
    public const string ParentUnknown = "errors.org.parent_unknown";
    public const string OtherCompany = "errors.org.move_other_company";
    public const string Cycle = "errors.org.move_cycle";
    public const string SameParent = "errors.org.move_same_parent";
    public const string BeforeLinkStart = "errors.org.move_before_link_start";
    public const string MoveConflict = "errors.org.move_conflict";
    public const string DatesRequired = "errors.org.compare_dates_required";

    private const int MaxReasonLength = 500;
    private const string LinksResource = "org.org_unit_links";
    private const string UnitsResource = "org.org_units";

    // ================================================================== reads

    public async Task<(AccessResult Result, OrgTree? Tree)> TreeAsync(Guid hierarchyId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var date = asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        if (await GuardReadAsync(c, hierarchyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return (refused, null);
        }

        var (nodes, unplaced) = await LoadAsync(c, hierarchyId, date, cancellationToken).ConfigureAwait(false);
        return (AccessResult.Done(hierarchyId), new OrgTree(hierarchyId, date, nodes, unplaced));
    }

    public async Task<(AccessResult Result, OrgCompare? Compare)> CompareAsync(Guid hierarchyId, DateOnly? firstDate, DateOnly? secondDate, CancellationToken cancellationToken)
    {
        if (firstDate is null || secondDate is null)
        {
            return (AccessResult.Invalid(DatesRequired), null);
        }

        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        if (await GuardReadAsync(c, hierarchyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return (refused, null);
        }

        var before = (await LoadAsync(c, hierarchyId, firstDate.Value, cancellationToken).ConfigureAwait(false)).Nodes.ToDictionary(n => n.UnitId);
        var after = (await LoadAsync(c, hierarchyId, secondDate.Value, cancellationToken).ConfigureAwait(false)).Nodes.ToDictionary(n => n.UnitId);
        // Parents are named by code from either date (a parent may exist on one date only).
        var codes = before.Values.Concat(after.Values).GroupBy(n => n.UnitId).ToDictionary(g => g.Key, g => g.First().Code);
        string? CodeOf(Guid? id) => id is { } key && codes.TryGetValue(key, out var code) ? code : null;
        OrgCompareChange Change(OrgTreeNode node, Guid? fromParent, Guid? toParent) => new(node.UnitId, node.Code, node.Name, fromParent, CodeOf(fromParent), toParent, CodeOf(toParent));
        var added = after.Values.Where(n => !before.ContainsKey(n.UnitId)).Select(n => Change(n, null, n.ParentUnitId)).OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        var removed = before.Values.Where(n => !after.ContainsKey(n.UnitId)).Select(n => Change(n, n.ParentUnitId, null)).OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        var moved = after.Values.Where(n => before.TryGetValue(n.UnitId, out var b) && b.ParentUnitId != n.ParentUnitId)
            .Select(n => Change(n, before[n.UnitId].ParentUnitId, n.ParentUnitId)).OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        return (AccessResult.Done(hierarchyId), new OrgCompare(hierarchyId, firstDate.Value, secondDate.Value, added, removed, moved));
    }

    /// <summary>404 for an unknown hierarchy, 403 when no role of the caller reads links.</summary>
    private async Task<AccessResult?> GuardReadAsync(NpgsqlConnection c, Guid hierarchyId, CancellationToken ct)
    {
        if (!await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from org.org_hierarchies where id = $1)", ct, hierarchyId).ConfigureAwait(false))
        {
            return AccessResult.NotFound;
        }

        return await caller.HoldsAsync(LinksResource + ".read", ct).ConfigureAwait(false) ? null : AccessResult.Forbidden("errors.access.forbidden");
    }

    /// <summary>Units of the companies the caller may read, placed or not on the date.</summary>
    private async Task<(List<OrgTreeNode> Nodes, List<OrgTreeNode> Unplaced)> LoadAsync(NpgsqlConnection c, Guid hierarchyId, DateOnly date, CancellationToken ct)
    {
        var nodes = new List<OrgTreeNode>();
        var unplaced = new List<OrgTreeNode>();
        var covered = new Dictionary<Guid, bool>();
        await using var command = IdentityDb.Command(c, null, """
            select u.id, l.parent_unit_id, u.code, u.name_i18n::text, u.unit_type_id, u.company_id, u.location_id, u.is_active, l.id, l.valid_from, l.valid_to
            from org.org_units u
            left join org.org_unit_links l on l.unit_id = u.id and l.hierarchy_id = $1 and l.valid_from <= $2 and (l.valid_to is null or l.valid_to > $2)
            where not u.is_deleted
            order by u.code, u.id
            """, hierarchyId, date);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var company = reader.GetGuid(5);
            if (!covered.TryGetValue(company, out var allowed))
            {
                allowed = await caller.CoversCompanyAsync(UnitsResource + ".read", company, ct).ConfigureAwait(false);
                covered[company] = allowed;
            }

            if (!allowed)
            {
                continue;
            }

            var placed = !reader.IsDBNull(8);
            var node = new OrgTreeNode(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), JsonNode.Parse(reader.GetString(3)), reader.GetGuid(4), company,
                reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetBoolean(7), placed ? reader.GetGuid(8) : null,
                placed ? reader.GetFieldValue<DateOnly>(9) : null, placed && !reader.IsDBNull(10) ? reader.GetFieldValue<DateOnly>(10) : null);
            if (placed)
            {
                nodes.Add(node);
            }
            else if (node.IsActive)
            {
                unplaced.Add(node);
            }
        }

        return (nodes, unplaced);
    }

    // ================================================================== move

    public async Task<AccessResult> MoveAsync(Guid unitId, MoveUnitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HierarchyId is not { } hierarchyId || request.EffectiveFrom is not { } effective)
        {
            return AccessResult.Invalid(FieldsRequired);
        }

        var reason = request.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
        {
            return AccessResult.Invalid(ReasonTooLong);
        }

        var now = clock.UtcNow;
        var by = actor.UserId;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The unit is locked first: two moves of one unit run one after the other, each seeing the link the other left.
            await using (var find = IdentityDb.Command(c, tx, "select company_id from org.org_units where id = $1 and not is_deleted for update", unitId))
            {
                var found = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (found is not Guid companyId)
                {
                    return AccessResult.NotFound;
                }

                if (!await caller.CoversCompanyAsync(LinksResource + ".update", companyId, cancellationToken).ConfigureAwait(false))
                {
                    return AccessResult.Forbidden("errors.access.forbidden");
                }

                if (!await IdentityDb.ScalarAsync<bool>(c, tx, "select exists (select 1 from org.org_hierarchies where id = $1)", cancellationToken, hierarchyId).ConfigureAwait(false))
                {
                    return AccessResult.Invalid("errors.data.reference_not_found");
                }

                if (request.NewParentId is { } parentId)
                {
                    if (parentId == unitId)
                    {
                        return AccessResult.Invalid(Cycle);
                    }

                    var parentCompany = await IdentityDb.ScalarAsync<Guid?>(c, tx, "select company_id from org.org_units where id = $1 and not is_deleted", cancellationToken, parentId).ConfigureAwait(false);
                    if (parentCompany is null)
                    {
                        return AccessResult.Invalid(ParentUnknown);
                    }

                    if (parentCompany != companyId)
                    {
                        return AccessResult.Invalid(OtherCompany);
                    }
                }

                return await MoveLockedAsync(c, tx, unitId, companyId, hierarchyId, request.NewParentId, effective, reason, now, by, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "ck_org_unit_links_no_cycle" })
        {
            return AccessResult.Invalid(Cycle);
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "ck_org_unit_links_same_company" })
        {
            return AccessResult.Invalid(OtherCompany);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            return AccessResult.Conflict(MoveConflict);
        }
    }

    private async Task<AccessResult> MoveLockedAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid unitId, Guid companyId, Guid hierarchyId, Guid? newParentId, DateOnly effective, string? reason, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        (Guid Id, Guid? Parent, DateOnly From, DateOnly? To)? current = null;
        await using (var command = IdentityDb.Command(c, tx, """
            select id, parent_unit_id, valid_from, valid_to from org.org_unit_links
            where hierarchy_id = $1 and unit_id = $2 and valid_from <= $3 and (valid_to is null or valid_to > $3) for update
            """, hierarchyId, unitId, effective))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                current = (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetFieldValue<DateOnly>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3));
            }
        }

        DateOnly? end;
        if (current is { } link)
        {
            if (link.Parent == newParentId)
            {
                return AccessResult.Invalid(SameParent);
            }

            if (effective <= link.From)
            {
                return AccessResult.Invalid(BeforeLinkStart);
            }

            end = link.To;
        }
        else
        {
            // Not in the hierarchy on that date (a first placement, or after a gap): the new link runs up to the next one, if any.
            if (!await caller.CoversCompanyAsync(LinksResource + ".create", companyId, ct).ConfigureAwait(false))
            {
                return AccessResult.Forbidden("errors.access.forbidden");
            }

            end = await IdentityDb.ScalarAsync<DateOnly?>(c, tx, "select min(valid_from) from org.org_unit_links where hierarchy_id = $1 and unit_id = $2 and valid_from > $3", ct, hierarchyId, unitId, effective).ConfigureAwait(false);
        }

        if (newParentId is { } parent
            && await IdentityDb.ScalarAsync<bool>(c, tx, "select org.org_unit_link_would_cycle($1, $2, $3, $4, $5, $6)", ct, hierarchyId, unitId, parent, effective, end, current?.Id).ConfigureAwait(false))
        {
            return AccessResult.Invalid(Cycle);
        }

        var entries = new List<ChangeEntry>();
        if (current is { } old)
        {
            // The old parent still applies up to the day before the effective date (the end of a link is exclusive).
            await IdentityDb.ExecuteAsync(c, tx, "update org.org_unit_links set valid_to = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1", ct, old.Id, effective, now, by).ConfigureAwait(false);
            entries.Add(OrganizationAdministrationService.Entry(LinksResource, old.Id, companyId, "update", now, by, [("valid_to", old.To, effective, false)]));
        }

        var id = Guid.CreateVersion7(now);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into org.org_unit_links (id, hierarchy_id, unit_id, parent_unit_id, valid_from, valid_to, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $7, $8)
            """, ct, id, hierarchyId, unitId, newParentId, effective, end, now, by).ConfigureAwait(false);
        var fields = new List<(string Field, object? Before, object? After, bool Redacted)>
        {
            ("hierarchy_id", null, hierarchyId, false), ("unit_id", null, unitId, false), ("parent_unit_id", current?.Parent, newParentId, false), ("valid_from", null, effective, false), ("valid_to", null, end, false),
        };
        if (!string.IsNullOrEmpty(reason))
        {
            fields.Add(("reason", null, reason, false));
        }

        entries.Add(OrganizationAdministrationService.Entry(LinksResource, id, companyId, "create", now, by, fields));
        await OrganizationAdministrationService.WriteChangesAsync(c, tx, entries, ct).ConfigureAwait(false);

        // The structure changed: HR turns this into hrm.org_structure_changed for cost-centre mapping (S1.22b, FR-HRM-POS-08).
        await IdentityDb.RaiseAsync(c, tx, Erp.Kernel.Contracts.Org.OrgEvents.UnitMoved, unitId, by, now, new
        {
            unitId,
            companyId,
            hierarchyId,
            oldParentId = current?.Parent,
            newParentId,
            effectiveFrom = effective.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }, ct, Erp.Kernel.Contracts.Org.OrgEvents.AggregateType).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return AccessResult.Done(id);
    }
}
