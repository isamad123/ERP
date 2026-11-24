using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Positions;

/// <summary>
/// What the strength report is asked (slice S1.22b Build item 7, FR-HRM-POS-03): one company, a date (default today), the dimensions to
/// group by (<c>unit</c>, <c>grade</c>, <c>location</c>; default unit and grade) and optional filters.
/// </summary>
public sealed record StrengthQuery(Guid? CompanyId, DateOnly? AsOf, IReadOnlyList<string>? GroupBy, Guid? UnitId, Guid? GradeId, Guid? LocationId);

/// <summary>What the org chart is asked (slice S1.22b Build item 8, FR-HRM-POS-04): a company, a date (also a future one) and optional unit and location filters.</summary>
public sealed record OrgChartQuery(Guid? CompanyId, DateOnly? AsOf, Guid? UnitId, Guid? LocationId);

/// <summary>An export asked for: <c>pdf</c> or <c>csv</c>; the PDF's language and its texts (the client's own translated labels; the server holds no user-facing text).</summary>
public sealed class ChartExportRequest
{
    public Guid? CompanyId { get; init; }

    public DateOnly? AsOf { get; init; }

    public Guid? UnitId { get; init; }

    public Guid? LocationId { get; init; }

    /// <summary><c>pdf</c> (up to <see cref="OrgChartLimits.PdfBoxes"/> boxes) or <c>csv</c> (reporting lines).</summary>
    public string? Format { get; init; }

    /// <summary><c>en</c> or <c>ur</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Texts for the PDF: <c>title</c>, <c>asOf</c>, <c>vacant</c>, <c>unit</c>, <c>location</c>, <c>page</c> (any missing one is left out).</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

public static class OrgChartLimits
{
    /// <summary>Most boxes the PDF holds (ruling Q7); a bigger chart exports as a CSV of reporting lines.</summary>
    public const int PdfBoxes = 500;

    /// <summary>Most boxes one chart answer holds; beyond it the answer says it was cut and asks for a unit filter.</summary>
    public const int ChartBoxes = 5000;
}

/// <summary>A file the caller downloads.</summary>
public sealed record ExportedFile(string FileName, string ContentType, byte[] Content);

/// <summary>The strength report (slice S1.22b Build item 7): sanctioned, filled, vacant and over per unit, grade and location as of a date.</summary>
public interface IStrengthReportService
{
    Task<PeopleResult> ReportAsync(StrengthQuery query, CancellationToken cancellationToken);

    /// <summary>The positions (position model) or people (headcount model) behind one row of the report.</summary>
    Task<PeopleResult> DrillAsync(StrengthQuery query, CancellationToken cancellationToken);

    /// <summary>The report as a CSV file (<see cref="ExportedFile"/> in the body).</summary>
    Task<PeopleResult> ExportAsync(StrengthQuery query, CancellationToken cancellationToken);
}

/// <summary>The org chart by position (slice S1.22b Build item 8): reporting lines of positions, or of managers in a headcount company.</summary>
public interface IOrgChartService
{
    Task<PeopleResult> ChartAsync(OrgChartQuery query, CancellationToken cancellationToken);

    /// <summary>The chart as a PDF (A4 landscape) or a CSV of reporting lines (<see cref="ExportedFile"/> in the body).</summary>
    Task<PeopleResult> ExportAsync(ChartExportRequest request, CancellationToken cancellationToken);
}
