using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// As-of reads and the timeline (slice S1.20 Build item 7, GHR-03, FR-HRM-EMP-11), the catalogue, and the user link (Build item
/// 10, FR-HRM-EMP-17, ruling Q8). Reads pass through the data scope and field security like the generated reads.
/// </summary>
internal sealed partial class EmploymentService
{
    public async Task<PeopleResult> AsOfAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await HasAsync(EmploymentPermissions.EmploymentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (await ReadableAsync(employmentId, cancellationToken).ConfigureAwait(false) is not { } employment)
        {
            return PeopleResult.NotFound;
        }

        var date = asOf ?? await applier.TodayAsync(employment.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var row = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employment.Id && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        JsonObject? assignment = null;
        if (row is not null && await HasAsync(EmploymentPermissions.AssignmentsRead, cancellationToken).ConfigureAwait(false)
            && await AllowsAsync(row, EmploymentAssignmentAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
        {
            assignment = await ShapeAsync(EmploymentAssignmentAccess.Fields, EmploymentAssignmentAccess.Scope, row, AssignmentView(row), cancellationToken).ConfigureAwait(false);
        }

        var view = await ShapeAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, employment, EmploymentView(employment), cancellationToken).ConfigureAwait(false);
        view["asOf"] = Iso(date);
        view["assignment"] = assignment;
        return new PeopleResult(PeopleOutcome.Done, Body: view);
    }

    public async Task<PeopleResult> TimelineAsync(Guid employmentId, string? kind, CancellationToken cancellationToken)
    {
        if (!await HasAsync(EmploymentPermissions.EventsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (await ReadableAsync(employmentId, cancellationToken).ConfigureAwait(false) is not { } employment)
        {
            return PeopleResult.NotFound;
        }

        var all = await db.Set<EmploymentEvent>().AsNoTracking().Where(e => e.EmploymentId == employment.Id)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var replacedBy = EmploymentTimeline.ReplacedBy([.. all.Select(EmploymentApplier.ToTimeline)]);

        // What an event changes is shown as the job details and employment fields themselves would be (review S1.20b finding 4):
        // job details only with the assignment read permission and the scope of the row they concern, each field by its rule.
        var rows = await HasAsync(EmploymentPermissions.AssignmentsRead, cancellationToken).ConfigureAwait(false)
            ? await db.Set<EmploymentAssignment>().AsNoTracking().Where(a => a.EmploymentId == employment.Id).OrderBy(a => a.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var rowMaps = new Dictionary<Guid, FieldAccessMap?>();
        async Task<FieldAccessMap?> AssignmentMapAsync(EmploymentEvent e)
        {
            var row = rows.FirstOrDefault(a => a.EventId == e.Id) ?? rows.LastOrDefault(a => a.ValidFrom <= e.EffectiveDate) ?? rows.FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            if (!rowMaps.TryGetValue(row.Id, out var map))
            {
                rowMaps[row.Id] = map = await AllowsAsync(row, EmploymentAssignmentAccess.Scope, "read", cancellationToken).ConfigureAwait(false)
                    ? await MapAsync(EmploymentAssignmentAccess.Fields, EmploymentAssignmentAccess.Scope, row, cancellationToken).ConfigureAwait(false)
                    : null;
            }

            return map;
        }

        var employmentMap = await MapAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, employment, cancellationToken).ConfigureAwait(false);
        var items = new List<JsonObject>();
        foreach (var e in all.Where(e => kind is null || e.Kind == kind).OrderByDescending(e => e.EffectiveDate).ThenByDescending(e => e.CreatedAt))
        {
            if (!await AllowsAsync(e, EmploymentEventAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var node = await ShapeAsync(EmploymentEventAccess.Fields, EmploymentEventAccess.Scope, e, new JsonObject
            {
                ["id"] = e.Id,
                ["kind"] = e.Kind,
                ["reasonCode"] = e.ReasonCode,
                ["effectiveDate"] = Iso(e.EffectiveDate),
                ["status"] = e.Status,
                ["notes"] = e.Notes,
                ["correctsEventId"] = e.CorrectsEventId,
                ["workflowInstanceId"] = e.WorkflowInstanceId,
                ["createdAt"] = e.CreatedAt,
                ["createdBy"] = e.CreatedBy,
                ["updatedAt"] = e.UpdatedAt,
                ["updatedBy"] = e.UpdatedBy,
            }, cancellationToken).ConfigureAwait(false);
            node["changes"] = e.Changes is null ? null : ShapeChanges(JsonNode.Parse(e.Changes.RootElement.GetRawText())!.AsObject(), await AssignmentMapAsync(e).ConfigureAwait(false), employmentMap);
            node["replacedBy"] = replacedBy.TryGetValue(e.Id, out var by) ? by : null;
            items.Add(node);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { employmentId = employment.Id, items });
    }

    public Task<PeopleResult> CatalogueAsync(CancellationToken cancellationToken) =>
        RunReadAsync(EmploymentPermissions.EmploymentsRead, () => new PeopleResult(PeopleOutcome.Done, Body: new
        {
            status = Catalogue.Status,
            employeeTypes = Catalogue.EmployeeTypes,
            kinds = Catalogue.Kinds.Select(k => new { k.Code, k.Name, k.Reasons, recorded = k.Code == "hire" || ChangeKinds.Contains(k.Code, StringComparer.Ordinal) }),
        }), cancellationToken);

    // ================================================================== user link (FR-HRM-EMP-17, ruling Q8)

    public async Task<PeopleResult> LinkUserAsync(Guid employmentId, UserLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(EmploymentPermissions.UserLink, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.UserId is not { } user)
        {
            return Field("userId", Required);
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        // Nobody links their own user (review S1.20b finding 5): the link decides who is the employee and who the line manager,
        // so making oneself someone's employee or manager needs another person with the permission (FR-XM-32).
        if (user == actor.UserId)
        {
            return PeopleResult.Conflict(OwnUser);
        }

        return await InTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employmentId} for update", cancellationToken).ConfigureAwait(false);
            // One link decision per user at a time, so two requests cannot link one user to two persons (review finding 7).
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended('hrm.user_link:' || {user.ToString()}, 0))", cancellationToken).ConfigureAwait(false);
            var (employment, problem) = await ChangeableAsync(employmentId, expected, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }

            if (!await users.IsLinkableAsync(user, Transaction, cancellationToken).ConfigureAwait(false))
            {
                return Field("userId", UserNotLinkable);
            }

            // One human per user: the user's other current employments must be of the same person; one per company (unique key).
            var others = await db.Set<Employment>().AsNoTracking()
                .Where(e => e.UserRef == user && e.Id != employment.Id && CurrentStatuses.Contains(e.Status)).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (others.Any(o => o.PersonId != employment.PersonId))
            {
                return PeopleResult.Conflict(UserOtherPerson);
            }

            if (others.Any(o => o.CompanyId == employment.CompanyId))
            {
                return PeopleResult.Conflict(UserLinkedElsewhere);
            }

            employment.UserRef = user;
            Touch(employment, expected);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { id = employment.Id, userRef = employment.UserRef, rowVersion = employment.RowVersion });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UnlinkUserAsync(Guid employmentId, long? rowVersion, CancellationToken cancellationToken)
    {
        if (!await HasAsync(EmploymentPermissions.UserLink, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (rowVersion is not { } expected)
        {
            return PeopleResult.Invalid(RowVersionRequired);
        }

        return await InTransactionAsync(async () =>
        {
            var (employment, problem) = await ChangeableAsync(employmentId, expected, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }

            if (employment.UserRef is null)
            {
                return PeopleResult.Conflict(NotLinked);
            }

            // Unlinking keeps the history: the change log holds the user the employment was linked to (ruling Q8).
            employment.UserRef = null;
            Touch(employment, expected);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { id = employment.Id, userRef = (Guid?)null, rowVersion = employment.RowVersion });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The employment, tracked, when the caller may read and change it and the row version matches; else the answer.</summary>
    private async Task<(Employment Employment, PeopleResult? Problem)> ChangeableAsync(Guid id, long expected, CancellationToken ct)
    {
        var employment = await db.Set<Employment>().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (employment is null || !await AllowsAsync(employment, EmploymentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return (null!, PeopleResult.NotFound);
        }

        // The data scope of the roles granting the link permission.
        if (!await AllowsAsync(employment, EmploymentAccess.Scope, "link_user", ct).ConfigureAwait(false))
        {
            return (null!, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        if (employment.RowVersion != expected)
        {
            return (null!, PeopleResult.Conflict(Concurrency));
        }

        if (await NotEditableAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, employment, ["user_ref"], ct).ConfigureAwait(false) is { Count: > 0 } denied)
        {
            return (null!, NotEditable(denied));
        }

        return (employment, null);
    }

    // ================================================================== read helpers

    private async Task<Employment?> ReadableAsync(Guid id, CancellationToken ct)
    {
        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        return employment is not null && await AllowsAsync(employment, EmploymentAccess.Scope, "read", ct).ConfigureAwait(false) ? employment : null;
    }

    private async Task<PeopleResult> RunReadAsync(string permission, Func<PeopleResult> work, CancellationToken ct) =>
        await HasAsync(permission, ct).ConfigureAwait(false) ? work() : PeopleResult.Forbidden();

    /// <summary>
    /// An event's changes as the caller may see them: the job details by the assignment row's field map (left out without one), the
    /// employment fields by the employment's; hidden fields removed, masked ones masked.
    /// </summary>
    private static JsonObject ShapeChanges(JsonObject changes, FieldAccessMap? assignment, FieldAccessMap employment)
    {
        static void Apply(JsonObject section, FieldAccessMap map)
        {
            foreach (var key in section.Select(p => p.Key).ToList())
            {
                switch (map.Of(key))
                {
                    case FieldAccessLevel.Hidden:
                        section.Remove(key);
                        break;
                    case FieldAccessLevel.Masked:
                        section[key] = FieldShaping.Mask(section[key]);
                        break;
                }
            }
        }

        if (changes["assignment"] is JsonObject job)
        {
            if (assignment is null)
            {
                changes.Remove("assignment");
            }
            else
            {
                Apply(job, assignment);
            }
        }

        if (changes["employment"] is JsonObject fields)
        {
            Apply(fields, employment);
        }

        return changes;
    }

    /// <summary>The field access of the roles whose data scope covers the row.</summary>
    private async Task<FieldAccessMap> MapAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, T row, CancellationToken ct)
        where T : class, Erp.BuildingBlocks.Domain.Entities.IEntity
    {
        var plan = await fields.PlanAsync(catalog, target, "read", Source, ct).ConfigureAwait(false);
        return (await plan.ResolveAsync([row], Source, ct).ConfigureAwait(false))[0];
    }

    /// <summary>A view shaped by the field access of the roles whose data scope covers the row; sensitive values shown are access-logged.</summary>
    private async Task<JsonObject> ShapeAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, T row, JsonObject view, CancellationToken ct)
        where T : class, Erp.BuildingBlocks.Domain.Entities.IEntity
    {
        var plan = await fields.PlanAsync(catalog, target, "read", Source, ct).ConfigureAwait(false);
        var map = (await plan.ResolveAsync([row], Source, ct).ConfigureAwait(false))[0];
        var shaped = FieldShaping.Shape(view, catalog, map);
        if (FieldShaping.SensitiveShown([shaped], catalog, [map]) is { Count: > 0 } shown)
        {
            await accessLog.RecordAsync(catalog.Resource, row.Id, shown, "view", 1, ct).ConfigureAwait(false);
        }

        return shaped;
    }

    private static JsonObject EmploymentView(Employment e) => new()
    {
        ["id"] = e.Id,
        ["companyId"] = e.CompanyId,
        ["personId"] = e.PersonId,
        ["employeeNo"] = e.EmployeeNo,
        ["employeeType"] = e.EmployeeType,
        ["joiningDate"] = Iso(e.JoiningDate),
        ["serviceStartDate"] = Iso(e.ServiceStartDate),
        ["confirmationDate"] = e.ConfirmationDate is { } c ? Iso(c) : null,
        ["expectedEndDate"] = e.ExpectedEndDate is { } x ? Iso(x) : null,
        ["separationDate"] = e.SeparationDate is { } s ? Iso(s) : null,
        ["previousEmploymentId"] = e.PreviousEmploymentId,
        ["userRef"] = e.UserRef,
        ["isPrimary"] = e.IsPrimary,
        ["status"] = e.Status,
        ["rowVersion"] = e.RowVersion,
    };

    private static JsonObject AssignmentView(EmploymentAssignment a) => new()
    {
        ["id"] = a.Id,
        ["eventId"] = a.EventId,
        ["orgUnitId"] = a.OrgUnitId,
        ["branchId"] = a.BranchId,
        ["locationId"] = a.LocationId,
        ["positionId"] = a.PositionId,
        ["gradeId"] = a.GradeId,
        ["title"] = a.Title,
        ["managerEmploymentId"] = a.ManagerEmploymentId,
        ["payGroupId"] = a.PayGroupId,
        ["costAllocation"] = JsonNode.Parse(a.CostAllocation.RootElement.GetRawText()),
        ["fte"] = a.Fte,
        ["workSchedule"] = a.WorkSchedule,
        ["isActing"] = a.IsActing,
        ["validFrom"] = Iso(a.ValidFrom),
        ["validTo"] = a.ValidTo is { } t ? Iso(t) : null,
    };
}
