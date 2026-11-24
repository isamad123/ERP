using System.Net;
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
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// The org chart by position (slice S1.22b Build item 8; FR-HRM-POS-04, GHR-03): the reporting lines of positions as of a date (a future
/// date shows the structure then), with holders' names and photos where the caller may read them and vacant boxes marked; in a
/// headcount-model company the lines are the managers' (<c>manager_employment_id</c>). Filters: unit and location. The chart is a flat
/// list of boxes with a parent id; the browser lays it out, searches, zooms and prints. Export: PDF through the document service (A4
/// landscape, up to <see cref="OrgChartLimits.PdfBoxes"/> boxes, ruling Q7) or a CSV of the reporting lines.
/// </summary>
internal sealed class OrgChartService(
    HrmDbContext db,
    IClock clock,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    ISettings settings,
    IFieldAccess fields,
    IOrgStructure org,
    IPrintedPages pages,
    Employments.EmploymentApplier applier) : IOrgChartService
{
    public const string CompanyRequired = "errors.hrm.chart.company_required";
    public const string TooLargeForPdf = "errors.hrm.chart.too_large_for_pdf";
    public const string BadFormat = "errors.hrm.chart.bad_format";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    private StrengthData Data => new(db, scope, fields, org);

    private sealed record Box(string Id, string? ParentId, string Code, string Title, Guid? UnitId, Guid? GradeId, Guid? LocationId, decimal BudgetedFte, decimal FilledFte, bool Vacant, string Status, IReadOnlyList<HolderBox> Holders);

    private sealed record HolderBox(Guid EmploymentId, string EmployeeNo, string? Name, string? NameLocal, Guid? PhotoFileId, decimal Fte, bool IsActing);

    private sealed record Built(Guid CompanyId, DateOnly Day, string Model, List<Box> Boxes, bool Truncated, Dictionary<Guid, OrgRecord> Records);

    public async Task<PeopleResult> ChartAsync(OrgChartQuery query, CancellationToken cancellationToken)
    {
        var (refusal, built) = await BuildAsync(query, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            companyId = built!.CompanyId,
            asOf = built.Day,
            model = built.Model,
            truncated = built.Truncated,
            total = built.Boxes.Count,
            nodes = built.Boxes.Select(b => new
            {
                id = b.Id,
                parentId = b.ParentId,
                code = b.Code,
                title = b.Title,
                unitId = b.UnitId,
                unitCode = b.UnitId is { } u ? built.Records.GetValueOrDefault(u)?.Code : null,
                gradeId = b.GradeId,
                locationId = b.LocationId,
                budgetedFte = b.BudgetedFte,
                filledFte = b.FilledFte,
                vacant = b.Vacant,
                status = b.Status,
                holders = b.Holders,
            }),
        });
    }

    private async Task<(PeopleResult? Refusal, Built? Built)> BuildAsync(OrgChartQuery query, CancellationToken ct)
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

        var company = await db.Database.SqlQuery<Guid>($"select id as \"Value\" from org.companies where id = {companyId} and not is_deleted").ToListAsync(ct).ConfigureAwait(false);
        if (company.Count == 0 || !await scope.AllowsAsync(new Position { CompanyId = companyId }, PositionAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (PeopleResult.NotFound, null);
        }

        var day = query.AsOf ?? await applier.TodayAsync(companyId, clock.UtcNow, ct).ConfigureAwait(false);
        var model = await PositionRules.ModelAsync(settings, companyId, ct).ConfigureAwait(false);
        var boxes = model == OrgModels.Position
            ? await PositionBoxesAsync(companyId, day, query, ct).ConfigureAwait(false)
            : await ManagerBoxesAsync(companyId, day, query, ct).ConfigureAwait(false);
        var truncated = boxes.Count > OrgChartLimits.ChartBoxes;
        if (truncated)
        {
            boxes = [.. boxes.Take(OrgChartLimits.ChartBoxes)];
            var kept = boxes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            boxes = [.. boxes.Select(b => b.ParentId is not null && !kept.Contains(b.ParentId) ? b with { ParentId = null } : b)];
        }

        var records = await Data.OrgRecordsAsync(boxes.Where(b => b.UnitId is not null).Select(b => b.UnitId!.Value), boxes.Where(b => b.LocationId is not null).Select(b => b.LocationId!.Value), ct).ConfigureAwait(false);
        return (null, new Built(companyId, day, model, boxes, truncated, records));
    }

    private async Task<List<Box>> PositionBoxesAsync(Guid companyId, DateOnly day, OrgChartQuery query, CancellationToken ct)
    {
        // The whole company is read so a filtered chart still knows each box's line; the filter only chooses the boxes.
        var all = await Data.PositionsAsync(companyId, day, null, null, null, ct).ConfigureAwait(false);
        var rowsById = new Dictionary<Guid, string>();
        foreach (var rows in await db.Set<Position>().AsNoTracking().Where(p => p.CompanyId == companyId).Select(p => new { p.Id, p.PositionCodeLower }).ToListAsync(ct).ConfigureAwait(false))
        {
            rowsById[rows.Id] = rows.PositionCodeLower;
        }

        var chosen = all.Where(l => !l.Planned && (query.UnitId is null || l.Row.OrgUnitId == query.UnitId) && (query.LocationId is null || l.Row.LocationId == query.LocationId)).ToList();
        var inChart = chosen.Select(l => l.Row.PositionCodeLower).ToHashSet(StringComparer.Ordinal);
        var cards = await Data.CardsAsync(companyId, [.. chosen.SelectMany(l => l.Holders).Select(h => h.PersonId)], ct).ConfigureAwait(false);
        return [.. chosen.Select(l =>
        {
            var parentCode = l.Row.ReportsToPositionId is { } r && rowsById.TryGetValue(r, out var code) && inChart.Contains(code) && code != l.Row.PositionCodeLower ? code : null;
            return new Box(
                l.Row.PositionCodeLower, parentCode, l.Row.PositionCode, l.Row.Title, l.Row.OrgUnitId, l.Row.GradeId, l.Row.LocationId, l.Budget, l.FilledFte,
                !l.Planned && !l.Frozen && l.FilledFte < l.Budget, l.Status,
                [.. l.Holders.Select(h => Holder(h, cards))]);
        })];
    }

    private static HolderBox Holder(Holder h, Dictionary<Guid, PersonCard> cards)
    {
        var card = cards.GetValueOrDefault(h.PersonId);
        return new HolderBox(h.EmploymentId, h.EmployeeNo, card?.Name, card?.NameLocal, card?.PhotoFileId, h.Fte, h.IsActing);
    }

    private async Task<List<Box>> ManagerBoxesAsync(Guid companyId, DateOnly day, OrgChartQuery query, CancellationToken ct)
    {
        var assignments = await scope.ApplyAsync(db.Set<EmploymentAssignment>().AsNoTracking(), EmploymentAssignmentAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var rows = await (from a in assignments
                          join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                          where a.CompanyId == companyId && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day)
                                && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= day && (e.SeparationDate == null || e.SeparationDate >= day)
                          orderby e.EmployeeNo
                          select new { a.EmploymentId, e.PersonId, e.EmployeeNo, a.Title, a.OrgUnitId, a.GradeId, a.LocationId, a.ManagerEmploymentId, a.Fte, a.IsActing }).ToListAsync(ct).ConfigureAwait(false);
        var people = rows.GroupBy(r => r.EmploymentId).Select(g => g.First()).ToList();
        var chosen = people.Where(r => (query.UnitId is null || r.OrgUnitId == query.UnitId) && (query.LocationId is null || r.LocationId == query.LocationId)).ToList();
        var ids = chosen.Select(r => r.EmploymentId).ToHashSet();
        var cards = await Data.CardsAsync(companyId, [.. chosen.Select(r => r.PersonId)], ct).ConfigureAwait(false);
        return [.. chosen.Select(r => new Box(
            r.EmploymentId.ToString(), r.ManagerEmploymentId is { } m && ids.Contains(m) && m != r.EmploymentId ? m.ToString() : null, r.EmployeeNo, r.Title, r.OrgUnitId, r.GradeId, r.LocationId, r.Fte, r.Fte, false, "filled",
            [Holder(new Holder(r.EmploymentId, r.PersonId, r.EmployeeNo, r.Fte, r.IsActing), cards)]))];
    }

    // ================================================================== export

    public async Task<PeopleResult> ExportAsync(ChartExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = (request.Format ?? "pdf").Trim().ToLowerInvariant();
        if (format is not ("pdf" or "csv"))
        {
            return PeopleResult.Invalid(BadFormat);
        }

        var (refusal, built) = await BuildAsync(new OrgChartQuery(request.CompanyId, request.AsOf, request.UnitId, request.LocationId), cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (format == "csv")
        {
            return new PeopleResult(PeopleOutcome.Done, Body: new ExportedFile($"org-chart-{built!.Day:yyyy-MM-dd}.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(Csv(built))));
        }

        if (built!.Boxes.Count > OrgChartLimits.PdfBoxes)
        {
            return PeopleResult.Conflict(TooLargeForPdf, new Dictionary<string, object?> { ["boxes"] = built.Boxes.Count, ["limit"] = OrgChartLimits.PdfBoxes });
        }

        var language = string.Equals(request.Language, "ur", StringComparison.OrdinalIgnoreCase) ? "ur" : "en";
        var pdf = await pages.PdfAsync(Html(built, language, request.Labels), language, landscape: true, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new ExportedFile($"org-chart-{built.Day:yyyy-MM-dd}.pdf", "application/pdf", pdf));
    }

    private static string Csv(Built built)
    {
        var codeOf = built.Boxes.ToDictionary(b => b.Id, b => b.Code, StringComparer.Ordinal);
        var csv = new StringBuilder("code,title,reports_to_code,unit_code,budgeted_fte,filled_fte,vacant,status,holders\r\n");
        foreach (var b in built.Boxes)
        {
            csv.Append(string.Join(',',
                CsvText.Field(b.Code), CsvText.Field(b.Title), CsvText.Field(b.ParentId is { } p ? codeOf.GetValueOrDefault(p) : null),
                CsvText.Field(b.UnitId is { } u ? built.Records.GetValueOrDefault(u)?.Code : null),
                CsvText.Number(b.BudgetedFte), CsvText.Number(b.FilledFte), b.Vacant ? "true" : "false", b.Status,
                CsvText.Field(string.Join("; ", b.Holders.Select(h => h.Name ?? h.EmployeeNo))))).Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>The printable page: the boxes as nested lists (each level indented), vacant boxes marked with the client's own text. Everything shown is HTML-encoded.</summary>
    private static string Html(Built built, string language, IReadOnlyDictionary<string, string>? labels)
    {
        string L(string key) => labels is not null && labels.TryGetValue(key, out var v) ? WebUtility.HtmlEncode(v) : string.Empty;
        var children = built.Boxes.Where(b => b.ParentId is not null).GroupBy(b => b.ParentId!).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var roots = built.Boxes.Where(b => b.ParentId is null).ToList();
        var html = new StringBuilder();
        html.Append("<style>ul{list-style:none;margin:0;padding-inline-start:18px}li{margin:3px 0}.box{display:inline-block;border:1px solid #666;border-radius:4px;padding:2px 8px}.vacant{border-style:dashed}small{color:#555}</style>");
        html.Append("<h1>").Append(L("title")).Append("</h1><p>").Append(L("asOf")).Append(' ').Append(built.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append("</p>");
        void Write(Box b)
        {
            html.Append("<li><span class=\"box").Append(b.Vacant ? " vacant" : string.Empty).Append("\"><strong>").Append(WebUtility.HtmlEncode(b.Title)).Append("</strong> <small>").Append(WebUtility.HtmlEncode(b.Code)).Append("</small>");
            foreach (var h in b.Holders)
            {
                html.Append("<br>").Append(WebUtility.HtmlEncode(language == "ur" && !string.IsNullOrWhiteSpace(h.NameLocal) ? h.NameLocal : h.Name ?? h.EmployeeNo));
            }

            if (b.Vacant)
            {
                html.Append("<br><em>").Append(L("vacant")).Append("</em>");
            }

            html.Append("</span>");
            if (children.TryGetValue(b.Id, out var below))
            {
                html.Append("<ul>");
                foreach (var c in below)
                {
                    Write(c);
                }

                html.Append("</ul>");
            }

            html.Append("</li>");
        }

        html.Append("<ul>");
        foreach (var r in roots)
        {
            Write(r);
        }

        html.Append("</ul>");
        return html.ToString();
    }
}
