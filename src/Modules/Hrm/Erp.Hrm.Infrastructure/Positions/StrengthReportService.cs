using System.Text;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Positions;
using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// The strength report (slice S1.22b Build item 7; FR-HRM-POS-03, GHR-03): sanctioned, filled, vacant and over strength per unit, grade and
/// location as of a date, with a drill into the positions or people and a CSV export. It counts exactly as the vacancy rule does
/// (<see cref="StrengthGuard"/>): positions by code and FTE in a position-model company; people per unit and grade against the sanctioned
/// count in a headcount-model company. Rows and people are the ones the caller's data scope lets them read; the permission is
/// <c>hrm.positions.read</c>.
/// </summary>
internal sealed class StrengthReportService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    ISettings settings,
    IFieldAccess fields,
    IOrgStructure org,
    Employments.EmploymentApplier applier) : IStrengthReportService
{
    public const string CompanyRequired = "errors.hrm.strength.company_required";
    public const string BadGroupBy = "errors.hrm.strength.group_by";

    private static readonly string[] Dimensions = ["unit", "grade", "location"];
    private static readonly string[] HeadcountGroup = ["unit", "grade"];
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    private StrengthData Data => new(db, scope, fields, org);

    private sealed record Request(Guid CompanyId, DateOnly Day, string Model, string[] Group, StrengthQuery Query);

    private async Task<(PeopleResult? Refusal, Request? Request)> PrepareAsync(StrengthQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await permissions.HasPermissionAsync(PositionPermissions.PositionsRead, ct).ConfigureAwait(false))
        {
            return (PeopleResult.Forbidden(), null);
        }

        if (query.CompanyId is not { } companyId)
        {
            return (PeopleResult.Invalid(CompanyRequired), null);
        }

        var group = (query.GroupBy is { Count: > 0 } g ? g : ["unit", "grade"]).Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (group.Any(x => !Dimensions.Contains(x, StringComparer.Ordinal)))
        {
            return (PeopleResult.Invalid(BadGroupBy), null);
        }

        var company = await db.Database.SqlQuery<Guid>($"select id as \"Value\" from org.companies where id = {companyId} and not is_deleted").ToListAsync(ct).ConfigureAwait(false);
        if (company.Count == 0 || !await scope.AllowsAsync(new Position { CompanyId = companyId }, PositionAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (PeopleResult.NotFound, null);
        }

        var day = query.AsOf ?? await applier.TodayAsync(companyId, clock.UtcNow, ct).ConfigureAwait(false);
        return (null, new Request(companyId, day, await PositionRules.ModelAsync(settings, companyId, ct).ConfigureAwait(false), group, query));
    }

    // ================================================================== the report

    public async Task<PeopleResult> ReportAsync(StrengthQuery query, CancellationToken cancellationToken)
    {
        var (refusal, request) = await PrepareAsync(query, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var rows = request!.Model == OrgModels.Position
            ? await PositionRowsAsync(request, cancellationToken).ConfigureAwait(false)
            : await HeadcountRowsAsync(request, cancellationToken).ConfigureAwait(false);
        var records = await Data.OrgRecordsAsync(rows.Where(r => r.Unit is not null).Select(r => r.Unit!.Value), rows.Where(r => r.Location is not null).Select(r => r.Location!.Value), cancellationToken).ConfigureAwait(false);
        var grades = await Data.GradesAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
        var shaped = rows.Select(r => Shape(r, records, grades)).ToList();
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            companyId = request.CompanyId,
            asOf = request.Day,
            model = request.Model,
            groupBy = request.Model == OrgModels.Position ? request.Group : HeadcountGroup,
            rows = shaped,
            totals = new
            {
                sanctioned = rows.Sum(r => r.Sanctioned),
                filled = rows.Sum(r => r.Filled),
                vacant = rows.Sum(r => r.Vacant),
                over = rows.Sum(r => r.Over),
                planned = rows.Sum(r => r.Planned),
                frozen = rows.Sum(r => r.Frozen),
            },
        });
    }

    private sealed class Row
    {
        public Guid? Unit { get; init; }

        public Guid? Grade { get; init; }

        public Guid? Location { get; init; }

        public decimal Sanctioned { get; set; }

        public decimal Filled { get; set; }

        public decimal Vacant { get; set; }

        public decimal Over { get; set; }

        public decimal Planned { get; set; }

        public decimal Frozen { get; set; }

        public int Positions { get; set; }
    }

    private async Task<List<Row>> PositionRowsAsync(Request r, CancellationToken ct)
    {
        var lines = await Data.PositionsAsync(r.CompanyId, r.Day, r.Query.UnitId, r.Query.GradeId, r.Query.LocationId, ct).ConfigureAwait(false);
        var rows = new Dictionary<(Guid?, Guid?, Guid?), Row>();
        foreach (var line in lines)
        {
            var key = (r.Group.Contains("unit") ? line.Row.OrgUnitId : (Guid?)null, r.Group.Contains("grade") ? line.Row.GradeId : null, r.Group.Contains("location") ? line.Row.LocationId : null);
            if (!rows.TryGetValue(key, out var row))
            {
                row = new Row { Unit = key.Item1, Grade = key.Item2, Location = key.Item3 };
                rows[key] = row;
            }

            row.Positions++;
            if (line.Planned)
            {
                row.Planned += line.Budget;
                continue;
            }

            row.Sanctioned += line.Budget;
            row.Filled += line.FilledFte;
            row.Vacant += line.Vacant;
            row.Over += line.Over;
            if (line.Frozen)
            {
                row.Frozen += line.Budget;
            }
        }

        return [.. rows.Values];
    }

    private async Task<List<Row>> HeadcountRowsAsync(Request r, CancellationToken ct)
    {
        var budgets = await scope.ApplyAsync(db.Set<HeadcountBudget>().AsNoTracking().Where(b => b.CompanyId == r.CompanyId && b.ValidFrom <= r.Day && (b.ValidTo == null || b.ValidTo > r.Day)
            && (r.Query.UnitId == null || b.OrgUnitId == r.Query.UnitId) && (r.Query.GradeId == null || b.GradeId == r.Query.GradeId)), HeadcountBudgetAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var list = await budgets.ToListAsync(ct).ConfigureAwait(false);
        var rows = new List<Row>();
        foreach (var group in list.GroupBy(b => (b.OrgUnitId, b.GradeId)))
        {
            var sanctioned = group.Sum(b => b.SanctionedCount);
            var filled = (await PeopleAsync(r, group.Key.OrgUnitId, group.Key.GradeId, ct).ConfigureAwait(false)).Count;
            rows.Add(new Row
            {
                Unit = group.Key.OrgUnitId,
                Grade = group.Key.GradeId,
                Sanctioned = sanctioned,
                Filled = filled,
                Vacant = Math.Max(0, sanctioned - filled),
                Over = Math.Max(0, filled - sanctioned),
                Positions = 0,
            });
        }

        return [.. rows.OrderBy(x => x.Unit).ThenBy(x => x.Grade)];
    }

    /// <summary>Current people in a unit (and grade) on the day: one per employment, with the assignment row in force.</summary>
    private async Task<List<(Guid EmploymentId, Guid PersonId, string EmployeeNo, decimal Fte, Guid? Grade, string Title)>> PeopleAsync(Request r, Guid unit, Guid? grade, CancellationToken ct)
    {
        var assignments = await scope.ApplyAsync(db.Set<EmploymentAssignment>().AsNoTracking(), EmploymentAssignmentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var rows = await (from a in assignments
                          join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                          where a.CompanyId == r.CompanyId && a.OrgUnitId == unit && (grade == null || a.GradeId == grade)
                                && a.ValidFrom <= r.Day && (a.ValidTo == null || a.ValidTo > r.Day)
                                && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= r.Day && (e.SeparationDate == null || e.SeparationDate >= r.Day)
                          orderby e.EmployeeNo
                          select new { a.EmploymentId, e.PersonId, e.EmployeeNo, a.Fte, a.GradeId, a.Title }).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.GroupBy(x => x.EmploymentId).Select(g => g.First()).Select(x => (x.EmploymentId, x.PersonId, x.EmployeeNo, x.Fte, x.GradeId, x.Title))];
    }

    private static object Shape(Row r, Dictionary<Guid, OrgRecord> records, Dictionary<Guid, Grade> grades) => new
    {
        unitId = r.Unit,
        unitCode = r.Unit is { } u ? records.GetValueOrDefault(u)?.Code : null,
        unitName = r.Unit is { } u2 ? records.GetValueOrDefault(u2)?.Name : null,
        gradeId = r.Grade,
        gradeCode = r.Grade is { } g ? grades.GetValueOrDefault(g)?.Code : null,
        locationId = r.Location,
        locationCode = r.Location is { } l ? records.GetValueOrDefault(l)?.Code : null,
        locationName = r.Location is { } l2 ? records.GetValueOrDefault(l2)?.Name : null,
        sanctioned = r.Sanctioned,
        filled = r.Filled,
        vacant = r.Vacant,
        over = r.Over,
        planned = r.Planned,
        frozen = r.Frozen,
        positions = r.Positions,
    };

    // ================================================================== drill

    public async Task<PeopleResult> DrillAsync(StrengthQuery query, CancellationToken cancellationToken)
    {
        var (refusal, request) = await PrepareAsync(query, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (request!.Model == OrgModels.Position)
        {
            var lines = await Data.PositionsAsync(request.CompanyId, request.Day, query.UnitId, query.GradeId, query.LocationId, cancellationToken).ConfigureAwait(false);
            var cards = await Data.CardsAsync(request.CompanyId, [.. lines.SelectMany(l => l.Holders).Select(h => h.PersonId)], cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new
            {
                asOf = request.Day,
                model = request.Model,
                positions = lines.Select(l => new
                {
                    id = l.Row.Id,
                    code = l.Row.PositionCode,
                    title = l.Row.Title,
                    unitId = l.Row.OrgUnitId,
                    gradeId = l.Row.GradeId,
                    locationId = l.Row.LocationId,
                    budgetedFte = l.Budget,
                    filledFte = l.FilledFte,
                    vacantFte = l.Vacant,
                    overFte = l.Over,
                    status = l.Status,
                    holders = l.Holders.Select(h => new { employmentId = h.EmploymentId, employeeNo = h.EmployeeNo, fte = h.Fte, isActing = h.IsActing, name = cards.GetValueOrDefault(h.PersonId)?.Name }),
                }),
                people = Array.Empty<object>(),
            });
        }

        if (query.UnitId is not { } unit)
        {
            return PeopleResult.Invalid(CompanyRequired);
        }

        var people = await PeopleAsync(request, unit, query.GradeId, cancellationToken).ConfigureAwait(false);
        var names = await Data.CardsAsync(request.CompanyId, [.. people.Select(p => p.PersonId)], cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            asOf = request.Day,
            model = request.Model,
            positions = Array.Empty<object>(),
            people = people.Select(p => new { employmentId = p.EmploymentId, employeeNo = p.EmployeeNo, fte = p.Fte, gradeId = p.Grade, title = p.Title, name = names.GetValueOrDefault(p.PersonId)?.Name }),
        });
    }

    // ================================================================== export

    public async Task<PeopleResult> ExportAsync(StrengthQuery query, CancellationToken cancellationToken)
    {
        var report = await ReportAsync(query, cancellationToken).ConfigureAwait(false);
        if (report.Outcome != PeopleOutcome.Done)
        {
            return report;
        }

        var json = System.Text.Json.JsonSerializer.SerializeToElement(report.Body, System.Text.Json.JsonSerializerOptions.Web);
        var csv = new StringBuilder("unit_code,grade_code,location_code,sanctioned,filled,vacant,over,planned,frozen\r\n");
        foreach (var row in json.GetProperty("rows").EnumerateArray())
        {
            csv.Append(string.Join(',', Cell(row, "unitCode"), Cell(row, "gradeCode"), Cell(row, "locationCode"), Num(row, "sanctioned"), Num(row, "filled"), Num(row, "vacant"), Num(row, "over"), Num(row, "planned"), Num(row, "frozen"))).Append("\r\n");
        }

        var day = json.GetProperty("asOf").GetString();
        return new PeopleResult(PeopleOutcome.Done, Body: new ExportedFile($"strength-{day}.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(csv.ToString())));
    }

    private static string Num(System.Text.Json.JsonElement row, string name) => CsvText.Number(row.GetProperty(name).GetDecimal());

    private static string Cell(System.Text.Json.JsonElement row, string name) => CsvText.Field(row.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.String ? row.GetProperty(name).GetString() : null);
}
