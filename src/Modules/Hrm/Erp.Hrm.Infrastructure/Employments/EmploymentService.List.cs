using System.Globalization;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Domain.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The employee list (slice S1.21a Build item 1; FR-HRM-EMP-01, GHR-06). One joined read over employments, their persons and the job
/// details valid on the as-of date, passed through the data scope of the employment, and each row shaped by the field access of the
/// roles covering it (employment, job details and person names each by their own rules). Pay data is not read here at all.
/// </summary>
internal sealed partial class EmploymentService
{
    private const int ListDefault = 50;
    private const int ListMax = 200;
    private const char CursorSeparator = '|';

    public async Task<PeopleResult> EmployeeListAsync(EmployeeListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await HasAsync(EmploymentPermissions.EmploymentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var bySort = string.Equals(query.Sort, "joiningDate", StringComparison.Ordinal);
        if (query.Sort is not null && !bySort && !string.Equals(query.Sort, "employeeNo", StringComparison.Ordinal))
        {
            return Field("sort", UnknownCode);
        }

        var limit = Math.Clamp(query.Limit ?? ListDefault, 1, ListMax);
        var canPersons = await HasAsync(PeoplePermissions.PersonsRead, cancellationToken).ConfigureAwait(false);
        var canAssignments = await HasAsync(EmploymentPermissions.AssignmentsRead, cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var date = query.AsOf ?? today;

        var employments = await scope.ApplyAsync(db.Set<Employment>().AsNoTracking(), EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var assignments = db.Set<EmploymentAssignment>().AsNoTracking();
        var persons = db.Set<Person>().AsNoTracking();
        var rows =
            from e in employments
            join p in persons on e.PersonId equals p.Id
            from a in assignments.Where(a => a.EmploymentId == e.Id && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date)).DefaultIfEmpty()
            select new { e, p, a };

        if (query.AsOf is not null)
        {
            rows = rows.Where(r => r.e.JoiningDate <= date && (r.e.SeparationDate == null || r.e.SeparationDate > date));
        }

        if (query.CompanyId is { } company)
        {
            rows = rows.Where(r => r.e.CompanyId == company);
        }

        if (Clean(query.EmployeeNo) is { } number)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.e.EmployeeNo, Like(number)));
        }

        if (Clean(query.Status) is { } status)
        {
            rows = rows.Where(r => r.e.Status == status);
        }

        if (Clean(query.EmployeeType) is { } type)
        {
            rows = rows.Where(r => r.e.EmployeeType == type);
        }

        if (query.JoinedFrom is { } from)
        {
            rows = rows.Where(r => r.e.JoiningDate >= from);
        }

        if (query.JoinedTo is { } to)
        {
            rows = rows.Where(r => r.e.JoiningDate <= to);
        }

        if (canAssignments)
        {
            if (query.OrgUnitId is { } unit)
            {
                rows = rows.Where(r => r.a != null && r.a.OrgUnitId == unit);
            }

            if (Clean(query.Title) is { } title)
            {
                rows = rows.Where(r => r.a != null && EF.Functions.ILike(r.a.Title, Like(title)));
            }

            if (query.ManagerEmploymentId is { } manager)
            {
                rows = rows.Where(r => r.a != null && r.a.ManagerEmploymentId == manager);
            }
        }
        else if (query.OrgUnitId is not null || Clean(query.Title) is not null || query.ManagerEmploymentId is not null)
        {
            rows = rows.Where(r => false); // job details are not readable by this caller: such a filter would reveal them
        }

        if (Clean(query.Search) is { } text)
        {
            var pattern = Like(text);
            rows = canPersons
                ? rows.Where(r => EF.Functions.ILike(r.e.EmployeeNo, pattern) || (canAssignments && r.a != null && EF.Functions.ILike(r.a.Title, pattern))
                    || EF.Functions.ILike(r.p.FirstName, pattern) || (r.p.MiddleName != null && EF.Functions.ILike(r.p.MiddleName, pattern))
                    || (r.p.LastName != null && EF.Functions.ILike(r.p.LastName, pattern)) || (r.p.NameLocal != null && EF.Functions.ILike(r.p.NameLocal, pattern))
                    || EF.Functions.ILike(r.p.PersonNo, pattern))
                : rows.Where(r => EF.Functions.ILike(r.e.EmployeeNo, pattern) || (canAssignments && r.a != null && EF.Functions.ILike(r.a.Title, pattern)));
        }

        // Keyset paging: the cursor is the last row's sort value and id.
        if (query.After is { Length: > 0 } after)
        {
            var at = after.LastIndexOf(CursorSeparator);
            if (at < 1 || !Guid.TryParse(after[(at + 1)..], out var lastId))
            {
                return Field("after", UnknownCode);
            }

            var key = after[..at];
            if (bySort)
            {
                if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDate))
                {
                    return Field("after", UnknownCode);
                }

                rows = rows.Where(r => r.e.JoiningDate < lastDate || (r.e.JoiningDate == lastDate && r.e.Id.CompareTo(lastId) > 0));
            }
            else
            {
                rows = rows.Where(r => r.e.EmployeeNo.CompareTo(key) > 0 || (r.e.EmployeeNo == key && r.e.Id.CompareTo(lastId) > 0));
            }
        }

        var ordered = bySort ? rows.OrderByDescending(r => r.e.JoiningDate).ThenBy(r => r.e.Id) : rows.OrderBy(r => r.e.EmployeeNo).ThenBy(r => r.e.Id);
        var page = await ordered.Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = page.Count > limit;
        if (more)
        {
            page.RemoveAt(limit);
        }

        // Field access of the roles covering each row, one plan per table for the whole page.
        var employmentRows = page.Select(r => r.e).ToList();
        var employmentMaps = await MapsAsync(EmploymentAccess.Fields, EmploymentAccess.Scope, employmentRows, cancellationToken).ConfigureAwait(false);
        var assignmentRows = page.Where(r => r.a is not null).Select(r => r.a!).ToList();
        var assignmentMaps = canAssignments && assignmentRows.Count > 0
            ? await MapsAsync(EmploymentAssignmentAccess.Fields, EmploymentAssignmentAccess.Scope, assignmentRows, cancellationToken).ConfigureAwait(false)
            : [];
        var personRows = page.Select(r => r.p).DistinctBy(p => p.Id).ToList();
        var personMaps = canPersons && personRows.Count > 0
            ? await MapsAsync(PersonAccess.Fields, PersonAccess.Scope, personRows, cancellationToken).ConfigureAwait(false)
            : [];

        // The manager of each row, by name: the manager's own employment and person, as far as the caller may read them.
        var managerIds = page.Where(r => r.a?.ManagerEmploymentId is not null).Select(r => r.a!.ManagerEmploymentId!.Value).Distinct().ToList();
        var managerNames = managerIds.Count == 0 || !canPersons ? new Dictionary<Guid, string>() : await ManagerNamesAsync(managerIds, cancellationToken).ConfigureAwait(false);

        var items = new List<JsonObject>();
        foreach (var r in page)
        {
            var employmentMap = employmentMaps[r.e.Id];
            var item = FieldShaping.Shape(ListView(r.e), EmploymentAccess.Fields, employmentMap);
            item["asOf"] = Iso(date);
            if (r.a is not null && assignmentMaps.TryGetValue(r.a.Id, out var assignmentMap))
            {
                var job = FieldShaping.Shape(ListAssignmentView(r.a), EmploymentAssignmentAccess.Fields, assignmentMap);
                foreach (var (k, v) in job.ToList())
                {
                    job.Remove(k);
                    item[k] = v;
                }

                item["managerName"] = r.a.ManagerEmploymentId is { } m && managerNames.TryGetValue(m, out var managerName) ? managerName : null;
            }

            if (personMaps.TryGetValue(r.p.Id, out var personMap))
            {
                var person = FieldShaping.Shape(new JsonObject
                {
                    ["personNo"] = r.p.PersonNo,
                    ["firstName"] = r.p.FirstName,
                    ["middleName"] = r.p.MiddleName,
                    ["lastName"] = r.p.LastName,
                    ["nameLocal"] = r.p.NameLocal,
                }, PersonAccess.Fields, personMap);
                item["person"] = person;
            }

            items.Add(item);
        }

        // Anything sensitive that was shown (none of the list fields is, but a customer may classify one) goes to the access log.
        var maps = employmentRows.Select(e => employmentMaps[e.Id]).ToList();
        if (FieldShaping.SensitiveShown(items.Cast<JsonObject>().ToList(), EmploymentAccess.Fields, maps) is { Count: > 0 } shown)
        {
            await accessLog.RecordAsync(EmploymentAccess.Fields.Resource, employmentRows[0].Id, shown, "view", items.Count, cancellationToken).ConfigureAwait(false);
        }

        string? next = null;
        if (more && page.Count > 0)
        {
            var last = page[^1].e;
            next = (bySort ? Iso(last.JoiningDate) : last.EmployeeNo) + CursorSeparator + last.Id.ToString("D", CultureInfo.InvariantCulture);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { items, next, asOf = Iso(date) });
    }

    private async Task<Dictionary<Guid, FieldAccessMap>> MapsAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, IReadOnlyList<T> rows, CancellationToken ct)
        where T : class, Erp.BuildingBlocks.Domain.Entities.IEntity
    {
        var plan = await fields.PlanAsync(catalog, target, "read", Source, ct).ConfigureAwait(false);
        var maps = await plan.ResolveAsync(rows, Source, ct).ConfigureAwait(false);
        var result = new Dictionary<Guid, FieldAccessMap>();
        for (var i = 0; i < rows.Count; i++)
        {
            result[rows[i].Id] = maps[i];
        }

        return result;
    }

    /// <summary>Names of the managers' persons the caller may read (display order: first and last name, else the local-script name).</summary>
    private async Task<Dictionary<Guid, string>> ManagerNamesAsync(IReadOnlyList<Guid> managerEmploymentIds, CancellationToken ct)
    {
        var employments = await scope.ApplyAsync(db.Set<Employment>().AsNoTracking().Where(e => managerEmploymentIds.Contains(e.Id)), EmploymentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var people = await scope.ApplyAsync(db.Set<Person>().AsNoTracking(), PersonAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var found = await (from e in employments join p in people on e.PersonId equals p.Id select new { e.Id, Person = p }).ToListAsync(ct).ConfigureAwait(false);
        var maps = found.Count == 0 ? [] : await MapsAsync(PersonAccess.Fields, PersonAccess.Scope, found.Select(f => f.Person).DistinctBy(p => p.Id).ToList(), ct).ConfigureAwait(false);
        var names = new Dictionary<Guid, string>();
        foreach (var f in found)
        {
            if (!maps.TryGetValue(f.Person.Id, out var map) || map.Of("first_name") != FieldAccessLevel.Editable && map.Of("first_name") != FieldAccessLevel.ReadOnly)
            {
                continue; // a hidden or masked name is not given away through the manager column
            }

            names[f.Id] = string.Join(' ', new[] { f.Person.FirstName, f.Person.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return names;
    }

    private static JsonObject ListView(Employment e) => new()
    {
        ["id"] = e.Id,
        ["companyId"] = e.CompanyId,
        ["personId"] = e.PersonId,
        ["employeeNo"] = e.EmployeeNo,
        ["employeeType"] = e.EmployeeType,
        ["joiningDate"] = Iso(e.JoiningDate),
        ["separationDate"] = e.SeparationDate is { } s ? Iso(s) : null,
        ["status"] = e.Status,
        ["isPrimary"] = e.IsPrimary,
        ["rowVersion"] = e.RowVersion,
    };

    private static JsonObject ListAssignmentView(EmploymentAssignment a) => new()
    {
        ["orgUnitId"] = a.OrgUnitId,
        ["branchId"] = a.BranchId,
        ["title"] = a.Title,
        ["managerEmploymentId"] = a.ManagerEmploymentId,
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A contains pattern for <c>ILIKE</c> with the wildcard characters of the text taken literally.</summary>
    private static string Like(string text) => "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
